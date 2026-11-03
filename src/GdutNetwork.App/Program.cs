using System.Text.Json;
using GdutNetwork.Core;
using GdutNetwork.Windows;

namespace GdutNetwork.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--apply-update")
        {
            try { UpdateInstallation.ApplyAsync(args[1]).GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { MessageBox.Show(ex is UpdateException ? ex.Message : $"更新未完成（{ex.GetType().Name}）。请重新启动原安装位置中的程序；旧版备份会保留。", "广工工具更新", MessageBoxButtons.OK, MessageBoxIcon.Warning); return 1; }
        }
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--diagnose")
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var report = new DiagnosticService().RunAsync(timeout.Token).GetAwaiter().GetResult();
                File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(report, DiagnosticService.JsonOptions));
                return report is null ? 1 : 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(new { Mode = "ReadOnly", ErrorType = ex.GetType().Name }));
                return 1;
            }
        }
        if ((args.Length == 2 && args[0] is "--render-preview" or "--render-settings" or "--render-selfservice" or "--render-history" or "--render-update") || (args.Length == 3 && args[0] == "--render-report"))
        {
            using var main = new MainForm(preview: true);
            using Form preview = args[0] == "--render-settings" ? main.CreateSettingsPreview() : args[0] == "--render-selfservice" ? main.CreateSelfServicePreview() : args[0] == "--render-history" ? main.CreateHistoryPreview() : args[0] == "--render-update" ? main.CreateUpdatePreview() : main;
            preview.ShowInTaskbar = false; preview.StartPosition = FormStartPosition.Manual; preview.Location = new(-32000, -32000);
            if (args[0] == "--render-report")
            {
                var report = JsonSerializer.Deserialize<DiagnosticReport>(File.ReadAllText(args[1]), DiagnosticService.JsonOptions);
                if (report is not null) main.ShowReport(report);
            }
            preview.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(preview.Width, preview.Height);
            preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.GetFullPath(args[^1]), System.Drawing.Imaging.ImageFormat.Png);
            preview.Close();
            return 0;
        }
        if (args.Length > 1 || args.Length == 1 && args[0] is not ("--background" or "--account")) return 2;
        using var mutex = new Mutex(true, @"Local\GdutNetwork.Automation", out bool created);
        if (!created)
        {
            if (args.Contains("--background")) return 0;
            MessageBox.Show("工具已经运行，请双击系统托盘中的图标。", "广工自动联网小工具", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        Application.Run(new MainForm(startHidden: args.Contains("--background"), openAccount: args.Contains("--account")));
        return 0;
    }
}
