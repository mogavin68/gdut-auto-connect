using System.Text.Json;
using GdutNetwork.Core;
using GdutNetwork.Windows;

namespace GdutNetwork.App;

public sealed class MainForm : Form
{
    private readonly CredentialStore vault = new();
    private readonly NativeWifiController? wifi;
    private readonly RecoveryCoordinator coordinator;
    private readonly NotifyIcon tray = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly bool isPreview;
    private bool exiting;
    private PauseReason lastNotice;
    private readonly Label status = new() { Text = "准备检测", AutoSize = false, Dock = DockStyle.Fill, Font = new("Microsoft YaHei UI", 20, FontStyle.Bold) };
    private readonly Label explanation = new() { Text = "点击立即检测，查看当前网络、认证入口与外网探测结果。", Dock = DockStyle.Fill, AutoSize = false };
    private readonly Label network = new() { Text = "当前 Wi-Fi：待读取", Dock = DockStyle.Fill, AutoSize = false };
    private readonly Label timing = new() { Text = "尚未检测 · 定时检测未开启", Dock = DockStyle.Fill, AutoSize = false };
    private readonly Button detect = new() { Text = "立即检测", AutoSize = true, Height = 38 };
    private readonly Button monitoring = new() { Text = "继续自动恢复", AutoSize = true, Height = 38 };
    private readonly Button accountSettings = new() { Text = "账号与设置", AutoSize = true, Height = 38 };
    private readonly Label automation = new() { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(88, 100, 120) };
    private readonly Button export = new() { Text = "导出诊断", AutoSize = true, Enabled = false, Height = 38 };
    private readonly DataGridView grid = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 60_000 };
    private DiagnosticReport? latest;
    private bool busy;
    private bool pendingRecovery;
    private bool closed;
    private DateTimeOffset? nextCheck;

    public MainForm(bool preview = false, bool startHidden = false, bool openAccount = false)
    {
        isPreview = preview;
        wifi = preview ? null : new NativeWifiController();
        coordinator = new(new RecoveryDiagnostics(), preview ? new PreviewActions() : new RecoveryActions(vault, wifi!),
            preview ? new PreviewSettingsStore() : new SettingsStore());
        Text = "广工自动联网小工具";
        Icon = BrandAssets.LoadIcon();
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1020, 710);
        MinimumSize = new Size(900, 650);
        BackColor = Color.FromArgb(245, 247, 251);
        ForeColor = Color.FromArgb(28, 39, 56);
        StartPosition = FormStartPosition.CenterScreen;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 9 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 28, 62, 64, 36, 48 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        root.Controls.Add(new Label { Text = "GDUT  /  大学城校区", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(47, 89, 178), Font = new(Font, FontStyle.Bold) }, 0, 0);
        root.Controls.Add(status, 0, 1);
        root.Controls.Add(explanation, 0, 2);
        root.Controls.Add(network, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        detect.BackColor = Color.FromArgb(38, 86, 189);
        detect.ForeColor = Color.White;
        detect.FlatStyle = FlatStyle.Flat;
        foreach (var button in new[] { detect, monitoring, accountSettings, export }) { button.Padding = new Padding(12, 3, 12, 3); button.Margin = new Padding(0, 0, 12, 0); buttons.Controls.Add(button); }
        root.Controls.Add(buttons, 0, 4);
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.None;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.DefaultCellStyle.Padding = new Padding(6);
        grid.ColumnHeadersHeight = 42;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 237, 247);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = ForeColor;
        grid.Columns.Add("Target", "检测项目");
        grid.Columns.Add("Result", "结果");
        grid.Columns.Add("Portal", "认证状态");
        grid.Columns.Add("Route", "系统选路");
        grid.Columns[0].FillWeight = 110;
        grid.Columns[1].FillWeight = 110;
        grid.Columns[2].FillWeight = 95;
        grid.Columns[3].FillWeight = 130;
        foreach (var spec in ProbeSpec.Defaults) grid.Rows.Add(spec.Name, "尚未检测", "—", "—");
        root.Controls.Add(grid, 0, 5);
        root.Controls.Add(timing, 0, 6);
        root.Controls.Add(automation, 0, 7);
        root.Controls.Add(new Label { Text = "v0.2.1 测试版 · AI 生成，不维护 · 关闭窗口收起到托盘；停止请在托盘退出", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(110, 120, 138) }, 0, 8);
        Controls.Add(root);
        detect.Click += async (_, _) => await DetectAsync(false);
        monitoring.Click += async (_, _) => await ToggleRecoveryAsync();
        accountSettings.Click += (_, _) => OpenSettings();
        timer.Tick += async (_, _) => { nextCheck = DateTimeOffset.Now.AddMinutes(1); RefreshTiming(); await DetectAsync(true); };
        export.Click += (_, _) => Export();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开窗口", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("暂停 / 继续自动恢复", null, async (_, _) => await ToggleRecoveryAsync());
        menu.Items.Add("账号与设置", null, (_, _) => { Show(); OpenSettings(); });
        menu.Items.Add("退出", null, (_, _) => { exiting = true; Close(); });
        tray.Icon = Icon;
        tray.Text = "广工自动联网小工具";
        tray.ContextMenuStrip = menu;
        tray.Visible = !preview;
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        coordinator.Updated += update => PostUpdate(() => ApplyUpdate(update));
        if (wifi is not null) wifi.ConnectionChanged += (manual, disconnected) => PostUpdate(async () => await WifiChangedAsync(manual, disconnected));
        FormClosing += (_, e) =>
        {
            if (!preview && !exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            closed = true; timer.Stop(); lifetime.Cancel();
        };
        FormClosed += (_, _) => { tray.Visible = false; tray.Dispose(); menu.Dispose(); wifi?.Dispose(); timer.Dispose(); lifetime.Dispose(); };
        Shown += async (_, _) =>
        {
            if (preview) return;
            if (startHidden) Hide();
            if (openAccount) OpenSettings();
            if (coordinator.Settings.Enabled) { StartTimer(); await DetectAsync(true); }
        };
        ApplyUpdate(new(RecoveryCoordinator.PauseText(coordinator.Settings.Pause), null, coordinator.Settings));
        if (preview)
        {
            status.Text = "准备检测";
            explanation.Text = "这是界面预览。检测后将在这里显示网络判断、证据与下一步说明。";
        }
    }

    private async Task DetectAsync(bool recovery)
    {
        if (busy || closed) return;
        busy = true;
        detect.Enabled = false;
        status.Text = "正在检测网络…";
        explanation.Text = "正在读取 Wi-Fi，并检查两个认证入口与外网探测目标。";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await coordinator.RunRoundAsync(recovery, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!closed) { status.Text = "检测已停止"; explanation.Text = "本轮检测已取消或超时；未执行网络恢复操作。"; }
        }
        catch (Exception ex)
        {
            if (!closed) { status.Text = "本轮未完成"; explanation.Text = $"未取得完整结果（{ex.GetType().Name}），请检查设置与网络。"; }
        }
        finally
        {
            busy = false;
            if (!closed)
            {
                detect.Enabled = true; RefreshTiming();
                if (pendingRecovery && coordinator.Settings.Enabled)
                { pendingRecovery = false; BeginInvoke(new Action(async () => await DetectAsync(true))); }
            }
        }
    }

    private void StartTimer()
    {
        if (isPreview) return;
        nextCheck = DateTimeOffset.Now.AddMinutes(1); timer.Start(); RefreshTiming();
    }

    private async Task ToggleRecoveryAsync()
    {
        if (isPreview) return;
        try
        {
            if (coordinator.Settings.Enabled) coordinator.Pause(PauseReason.User);
            else
            {
                if (!vault.Exists) { OpenSettings(); if (!vault.Exists) return; }
                coordinator.Resume(); StartTimer();
                if (busy) pendingRecovery = true;
                else await DetectAsync(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(this, $"无法更新运行状态（{ex.GetType().Name}）。", "自动恢复未开启"); }
    }

    private void OpenSettings()
    {
        if (isPreview) return;
        try
        {
            coordinator.Pause(PauseReason.User);
            using var form = new AccountSettingsForm(vault, coordinator);
            form.ShowDialog(this);
            ApplyUpdate(new(RecoveryCoordinator.PauseText(coordinator.Settings.Pause), null, coordinator.Settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(this, $"无法打开账号设置（{ex.GetType().Name}）。", "账号设置"); }
    }

    internal Form CreateSettingsPreview() => new AccountSettingsForm(vault, coordinator, preview: true);

    private async Task WifiChangedAsync(bool manual, bool disconnected)
    {
        if (closed || isPreview) return;
        try
        {
            if (manual && coordinator.Settings.Enabled) { coordinator.Pause(PauseReason.ManualDisconnect); return; }
            if (coordinator.Settings.Pause is PauseReason.RadioOff or PauseReason.ManualDisconnect
                && new WindowsNetworkReader().Read().Target is not null)
            { coordinator.Resume(); StartTimer(); }
            if (coordinator.Settings.Enabled)
            {
                if (disconnected) { coordinator.InterruptCurrent(); pendingRecovery = true; }
                if (!busy) { pendingRecovery = false; await DetectAsync(true); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = "无线状态变化后未能更新，请检查后继续"; }
    }

    private void ApplyUpdate(RecoveryUpdate update)
    {
        if (update.Report is not null) ShowReport(update.Report);
        else status.Text = update.Status;
        monitoring.Text = update.Settings.Enabled ? "暂停自动恢复" : "继续自动恢复";
        automation.Text = (update.Settings.Enabled ? "自动恢复已开启 · 检测与失败重试周期均为 60 秒" : RecoveryCoordinator.PauseText(update.Settings.Pause))
            + "\n新计费周期自动确认：" + (update.Settings.AutoConfirmBillingCycle ? "开启（可在设置关闭）" : "关闭（遇到时暂停提醒）");
        if (!update.Settings.Enabled) { pendingRecovery = false; timer.Stop(); nextCheck = null; }
        if (!isPreview && update.Settings.Pause is not (PauseReason.None or PauseReason.User) && update.Settings.Pause != lastNotice)
        { tray.ShowBalloonTip(7000, "广工自动联网小工具", RecoveryCoordinator.PauseText(update.Settings.Pause), ToolTipIcon.Warning); }
        lastNotice = update.Settings.Pause;
        RefreshTiming();
    }

    private void PostUpdate(Action action)
    {
        if (closed || !IsHandleCreated) return;
        if (!InvokeRequired) action();
        else try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    private sealed class PreviewSettingsStore : ISettingsStore
    {
        public AutomationSettings Load() => new();
        public void Save(AutomationSettings settings) { }
    }
    private sealed class PreviewActions : IRecoveryActions
    {
        public bool HasCredentials => false;
        public Task<bool> ConnectGdutAsync(CancellationToken token) => Task.FromResult(false);
        public Task<AuthResult> AuthenticateAsync(AutomationSettings settings, CancellationToken token) => Task.FromResult(new AuthResult(AuthOutcome.UnsupportedConfiguration, "预览模式"));
    }

    internal void ShowReport(DiagnosticReport report)
    {
        latest = report;
        status.Text = report.Assessment.Summary;
        explanation.Text = report.Assessment.Explanation;
        string wifi = string.Join(" / ", report.Network.Wifi.Select(w => $"{w.AdapterName}：{w.Ssid ?? WifiState(w.State)}{(w.ErrorCode is null ? "" : $"（读取错误 {w.ErrorCode}）")}"));
        network.Text = "当前 Wi-Fi：" + (wifi.Length > 0 ? wifi : "未读取到") + (report.Network.Warning is null ? "" : $" · {report.Network.Warning}");
        grid.Rows.Clear();
        foreach (var probe in report.Probes)
        {
            string outcome = probe.Outcome switch
            {
                ProbeOutcome.ExpectedResponse => "预期响应匹配", ProbeOutcome.PortalResponse => $"HTTP {probe.HttpStatus}",
                ProbeOutcome.Redirect => "收到跳转（未跟随）", ProbeOutcome.Timeout => "请求超时",
                ProbeOutcome.Failed => $"访问失败：{probe.Error}", _ => $"响应不匹配（{probe.HttpStatus}）"
            };
            string portal = probe.Portal?.State switch
            {
                PortalState.SessionIndicated => "页面显示已登录", PortalState.LoginRequired => "发现登录表单",
                PortalState.CaptchaRequired => "需要人工验证", _ => "未确认"
            };
            string route = probe.Route?.InterfaceName ?? "未取得路径";
            if (probe.Route?.MatchesTarget == false) route += "（非 gdut 网卡）";
            grid.Rows.Add(probe.Name, outcome, portal, route);
        }
        export.Enabled = true;
        RefreshTiming();
    }

    private void RefreshTiming()
    {
        string last = latest is null ? "尚无完整检测结果" : $"最近检测 {latest.CheckedAt:HH:mm:ss} · 耗时 {latest.ElapsedMilliseconds / 1000.0:F1} 秒";
        timing.Text = last + (timer.Enabled && nextCheck is not null ? $" · 下次检测 {nextCheck:HH:mm:ss}" : " · 定时检测未开启");
    }

    private void Export()
    {
        if (latest is null) return;
        using var dialog = new SaveFileDialog { Filter = "诊断报告 (*.json)|*.json", FileName = $"gdut-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(latest, DiagnosticService.JsonOptions)); }
        catch (IOException) { MessageBox.Show(this, "无法写入所选文件，请选择其他位置。", "导出未完成"); }
        catch (UnauthorizedAccessException) { MessageBox.Show(this, "没有权限写入所选位置，请选择其他位置。", "导出未完成"); }
    }

    private static string WifiState(string value) => value switch
    {
        "Disconnected" => "未连接", "NotReady" => "网卡未就绪", "Connected" => "已连接（SSID 未读取）",
        "Associating" => "正在连接", "Authenticating" => "正在认证", _ => value
    };
}
