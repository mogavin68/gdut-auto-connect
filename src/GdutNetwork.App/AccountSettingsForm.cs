using GdutNetwork.Core;
using GdutNetwork.Windows;

namespace GdutNetwork.App;

internal sealed class AccountSettingsForm : Form
{
    public AccountSettingsForm(CredentialStore vault, RecoveryCoordinator coordinator, bool preview = false)
    {
        Text = "账号与自动恢复设置";
        Icon = BrandAssets.LoadIcon();
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(620, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 9 };
        root.ColumnStyles.Add(new(SizeType.Absolute, 110)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (int height in new[] { 42, 42, 36, 64, 54, 54, 110, 50, 28 }) root.RowStyles.Add(new(SizeType.Absolute, height));
        var user = new TextBox { Dock = DockStyle.Top, PlaceholderText = "学号 / 统一认证账号", Text = preview ? "" : vault.ReadMetadata() ?? "" };
        var password = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true, PlaceholderText = "保存新凭据时填写密码" };
        var show = new CheckBox { Text = "显示密码", AutoSize = true };
        show.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !show.Checked;
        var billing = new CheckBox { Text = "自动确认新计费周期（可能触发新周期计费）", AutoSize = true, Checked = coordinator.Settings.AutoConfirmBillingCycle };
        var http = new CheckBox { Text = "HTTPS 不可用时，允许按官方 HTTP 方式登录", AutoSize = true, Checked = coordinator.Settings.AllowHttpAuthentication };
        var startup = new CheckBox { Text = "登录 Windows 后后台运行", AutoSize = true, Checked = coordinator.Settings.StartWithWindows };
        root.Controls.Add(new Label { Text = "账号", AutoSize = true }, 0, 0); root.Controls.Add(user, 1, 0);
        root.Controls.Add(new Label { Text = "密码", AutoSize = true }, 0, 1); root.Controls.Add(password, 1, 1);
        root.Controls.Add(show, 1, 2);
        root.Controls.Add(billing, 0, 3); root.SetColumnSpan(billing, 2);
        root.Controls.Add(http, 0, 4); root.SetColumnSpan(http, 2);
        root.Controls.Add(startup, 0, 5); root.SetColumnSpan(startup, 2);
        var note = new Label { Text = "密码保存在 Windows 凭据管理器中。留空密码可仅保存选项。\n修改账号时需重新填写密码。保存后保持暂停，请在主窗口点击继续。", Dock = DockStyle.Fill, ForeColor = Color.DimGray };
        root.Controls.Add(note, 0, 6); root.SetColumnSpan(note, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var save = new Button { Text = "保存设置", AutoSize = true, Height = 34 };
        var clear = new Button { Text = "删除已保存凭据", AutoSize = true, Height = 34 };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel, Height = 34 };
        buttons.Controls.AddRange([save, clear, cancel]);
        root.Controls.Add(buttons, 0, 7); root.SetColumnSpan(buttons, 2);
        Controls.Add(root);
        CancelButton = cancel;
        save.Click += (_, _) =>
        {
            if (preview) return;
            try
            {
                string userName = user.Text.Trim();
                if (password.Text.Length > 0) vault.Save(userName, password.Text);
                else if (userName != (vault.ReadMetadata() ?? "")) { MessageBox.Show(this, "修改账号时请同时填写密码。", "尚未保存"); return; }
                coordinator.UpdateOptions(http.Checked, billing.Checked, startup.Checked);
                StartupRegistration.SetEnabled(startup.Checked);
                password.Clear();
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
            { MessageBox.Show(this, $"保存未完成（{ex.GetType().Name}）。请检查后重试。", "设置未完成"); }
        };
        clear.Click += (_, _) =>
        {
            if (preview || MessageBox.Show(this, "删除本工具保存的校园网凭据？", "删除凭据", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { vault.Delete(); user.Clear(); password.Clear(); }
            catch (System.ComponentModel.Win32Exception) { MessageBox.Show(this, "无法删除凭据，请检查 Windows 凭据管理器。", "删除未完成"); }
        };
        FormClosed += (_, _) => password.Clear();
    }
}
