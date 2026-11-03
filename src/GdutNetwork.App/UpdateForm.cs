using System.Diagnostics;
using GdutNetwork.Windows;

namespace GdutNetwork.App;

internal sealed class UpdateForm : Form
{
    private readonly Version current = UpdateRelease.Normalize(typeof(MainForm).Assembly.GetName().Version!);
    private readonly GitHubUpdateClient client = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Label state = new() { Dock = DockStyle.Fill, Text = "正在检查公开正式版…" };
    private readonly TextBox notes = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill };
    private readonly Button check = new() { Text = "重新检查", AutoSize = true };
    private readonly Button download = new() { Text = "下载更新", AutoSize = true, Enabled = false };
    private readonly Button install = new() { Text = "安装并重启", AutoSize = true, Enabled = false };
    private readonly Button cancel = new() { Text = "取消下载", AutoSize = true, Enabled = false };
    private UpdateRelease? release;
    private DownloadedUpdate? package;
    private CancellationTokenSource? transfer;
    private bool busy, closed;
    internal bool InstallStarted { get; private set; }
    internal UpdateForm(bool preview = false)
    {
        Text = "检查更新"; Icon = BrandAssets.LoadIcon(); Font = new("Microsoft YaHei UI", 10);
        ClientSize = new(780, 570); MinimumSize = new(740, 520); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), ColumnCount = 1, RowCount = 6 };
        root.RowStyles.Add(new(SizeType.Absolute, 36)); root.RowStyles.Add(new(SizeType.Absolute, 66)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 30)); root.RowStyles.Add(new(SizeType.Absolute, 50)); root.RowStyles.Add(new(SizeType.Absolute, 44));
        root.Controls.Add(new Label { Text = $"当前版本：{current.Major}.{current.Minor}.{current.Build} · Windows x64", Dock = DockStyle.Fill }, 0, 0);
        root.Controls.Add(state, 0, 1); root.Controls.Add(notes, 0, 2); root.Controls.Add(progress, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(0, 8, 0, 0) };
        var web = new Button { Text = "打开发布页", AutoSize = true };
        buttons.Controls.AddRange([check, download, install, cancel, web]); root.Controls.Add(buttons, 0, 4);
        root.Controls.Add(new Label { Text = "点击时检查和下载公开正式版；校验通过后由你确认安装。账号、设置和充值记录不会发送到 GitHub。", Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 5);
        Controls.Add(root);
        check.Click += async (_, _) => { if (!preview) await CheckAsync(); };
        download.Click += async (_, _) => { if (!preview) await DownloadAsync(); };
        cancel.Click += (_, _) => transfer?.Cancel();
        web.Click += (_, _) => { if (!preview) try { Process.Start(new ProcessStartInfo(GitHubUpdateClient.ReleasesPage.AbsoluteUri) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { state.Text = "无法打开浏览器，请手动访问 GitHub 发布页。"; } };
        install.Click += (_, _) =>
        {
            if (preview || package is null || busy) return;
            if (MessageBox.Show(this, $"将安装 {package.Release.Tag} 并重启程序。\n\n打开的缴费会话会结束，请先完成或核对付款。账号和设置会保留。\n安装失败时会尝试恢复旧版。\n\n现在安装？", "确认安装更新", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { UpdateInstallation.StartInstall(package, current, background: false); InstallStarted = true; DialogResult = DialogResult.OK; Close(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { state.Text = ex is UpdateException ? ex.Message : $"未能启动安装（{ex.GetType().Name}），当前版本保持运行。"; install.Enabled = false; package = null; download.Enabled = true; }
        };
        Shown += async (_, _) => { if (!preview) await CheckAsync(); };
        FormClosed += (_, _) => { closed = true; lifetime.Cancel(); client.Dispose(); lifetime.Dispose(); };
        if (preview) { state.Text = "界面预览 · 将显示最新正式版、下载进度和校验结果。"; notes.Text = "新版说明将在此处显示。\r\n\r\n下载文件通过 SHA-256、大小和产品版本校验后，才可以确认安装并重启。"; }
    }
    private async Task CheckAsync()
    {
        if (busy) return; busy = true; check.Enabled = download.Enabled = install.Enabled = false;
        state.Text = "正在检查 GitHub 公开正式版…";
        try
        {
            release = await client.GetLatestAsync(lifetime.Token);
            if (closed) return;
            notes.Text = release.Notes.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
            bool newer = release.Version > current;
            state.Text = newer ? $"发现新版 {release.Tag} · {release.Size / 1048576.0:0.0} MB\n可下载并校验，确认后再安装。" : $"当前已是最新公开版本，或比公开版本更新。\n公开版本：{release.Tag}";
            download.Enabled = newer;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!closed) state.Text = ex is UpdateException ? ex.Message : $"更新检查未完成（{ex.GetType().Name}），请稍后重试。"; }
        finally { busy = false; if (!closed) check.Enabled = true; }
    }
    private async Task DownloadAsync()
    {
        if (busy || release is null) return;
        busy = true; check.Enabled = download.Enabled = install.Enabled = false; cancel.Enabled = true; progress.Value = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); transfer = cancellation;
        try
        {
            var expected = release;
            var reporting = new Progress<long>(bytes => { if (!closed && busy && ReferenceEquals(expected, release)) { progress.Value = (int)Math.Min(100, bytes * 100 / expected.Size); state.Text = $"已下载 {bytes / 1048576.0:0.0} / {expected.Size / 1048576.0:0.0} MB"; } });
            package = await client.DownloadAsync(release, reporting, cancellation.Token);
            if (!closed) { install.Enabled = true; progress.Value = 100; state.Text = $"{release.Tag} 下载完成，文件校验通过。\n点击“安装并重启”确认更新。"; }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!closed) { state.Text = ex is OperationCanceledException ? "下载已取消或连接超时，当前程序未改变。" : ex is UpdateException ? ex.Message : $"下载未完成（{ex.GetType().Name}），当前程序未改变。"; download.Enabled = true; } }
        finally { transfer = null; busy = false; if (!closed) { check.Enabled = true; cancel.Enabled = false; } }
    }
}
