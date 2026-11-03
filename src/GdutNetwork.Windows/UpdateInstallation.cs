using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GdutNetwork.Windows;

public sealed record UpdateInstallPlan(int Schema, string TargetPath, string PackagePath, string OldSha256, string NewSha256, string OldVersion, string NewVersion, int ParentProcessId, long ParentStartedUtcTicks, bool Background);
public sealed record UpdateCompletion(int HelperProcessId, long HelperStartedUtcTicks, bool Succeeded);

public static class UpdateInstallation
{
    public static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GDUTAutoNetwork", "updates");
    public static string CreateSessionDirectory()
    {
        string directory = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); EnsureNoLinks(directory); return directory;
    }
    public static string Sha256(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public static void VerifyBinary(string file, Version expected)
    {
        var info = FileVersionInfo.GetVersionInfo(file);
        if (info.ProductName != "GdutNetwork" || !Version.TryParse(info.ProductVersion, out var actual) || UpdateRelease.Normalize(actual) != UpdateRelease.Normalize(expected))
            throw new UpdateException("下载的程序版本或产品标识与发布信息不一致，未执行安装。");
    }
    public static void StartInstall(DownloadedUpdate package, Version current, bool background)
    {
        string target = Environment.ProcessPath ?? throw new UpdateException("无法确定程序安装位置。");
        if (!Path.GetFileName(target).Equals("GdutNetwork.exe", StringComparison.OrdinalIgnoreCase)) throw new UpdateException("请使用正式发布的 GdutNetwork.exe 执行自动更新。");
        if (File.Exists(Path.ChangeExtension(target, ".dll"))) throw new UpdateException("当前目录包含外置程序组件，请使用发布页的单文件 EXE 手动更新。");
        EnsureSession(package.SessionDirectory);
        if (!Path.GetFullPath(package.FilePath).Equals(Path.Combine(Path.GetFullPath(package.SessionDirectory), "GdutNetwork.exe"), StringComparison.OrdinalIgnoreCase)) throw new UpdateException("更新文件位置未能确认。");
        if (package.Release.Version <= UpdateRelease.Normalize(current)) throw new UpdateException("所选版本不是更新的正式版。");
        VerifyBinary(target, current); VerifyBinary(package.FilePath, package.Release.Version);
        if (Sha256(package.FilePath) != package.Release.Sha256) throw new UpdateException("安装前文件校验失败，请重新下载。");
        using var process = Process.GetCurrentProcess();
        var plan = new UpdateInstallPlan(1, Path.GetFullPath(target), package.FilePath, Sha256(target), package.Release.Sha256,
            UpdateRelease.Normalize(current).ToString(), package.Release.Version.ToString(), process.Id, process.StartTime.ToUniversalTime().Ticks, background);
        string planPath = Path.Combine(package.SessionDirectory, "install-plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        var start = new ProcessStartInfo(package.FilePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = package.SessionDirectory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planPath);
        using var helper = Process.Start(start) ?? throw new UpdateException("无法启动更新程序，当前版本保持运行。");
    }
    public static async Task ApplyAsync(string planPath)
    {
        string fullPlan = Path.GetFullPath(planPath);
        string directory = Path.GetDirectoryName(fullPlan) ?? throw new UpdateException("更新计划位置无效。");
        EnsureSession(directory);
        if (Path.GetFileName(fullPlan) != "install-plan.json" || new FileInfo(fullPlan).Length > 16000) throw new UpdateException("更新计划无效。");
        EnsureNoLinks(fullPlan);
        var plan = JsonSerializer.Deserialize<UpdateInstallPlan>(File.ReadAllText(fullPlan)) ?? throw new UpdateException("更新计划无效。");
        if (plan.Schema != 1 || plan.ParentProcessId <= 0 || plan.ParentProcessId == Environment.ProcessId || !Regex.IsMatch(plan.OldSha256, "^[a-f0-9]{64}$") || !Regex.IsMatch(plan.NewSha256, "^[a-f0-9]{64}$")) throw new UpdateException("更新计划校验失败。");
        string own = Path.GetFullPath(Environment.ProcessPath ?? "");
        if (!own.Equals(Path.Combine(directory, "GdutNetwork.exe"), StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(plan.PackagePath).Equals(own, StringComparison.OrdinalIgnoreCase)) throw new UpdateException("更新程序来源位置未能确认。");
        string target = Path.GetFullPath(plan.TargetPath);
        if (!Path.GetFileName(target).Equals("GdutNetwork.exe", StringComparison.OrdinalIgnoreCase) || target == own) throw new UpdateException("安装目标无效。");
        EnsureNoLinks(target);
        var oldVersion = Version.Parse(plan.OldVersion); var newVersion = Version.Parse(plan.NewVersion);
        if (newVersion <= oldVersion) throw new UpdateException("更新版本顺序无效。");
        VerifyBinary(own, newVersion);
        if (Sha256(own) != plan.NewSha256) throw new UpdateException("更新文件再次校验失败。");
        try
        {
            using var parent = Process.GetProcessById(plan.ParentProcessId);
            if (!parent.HasExited)
            {
                if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedUtcTicks || !string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) throw new UpdateException("原程序进程信息已变化，更新已取消。");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (ArgumentException) { /* Confirmed parent already exited; never terminate other processes. */ }
        VerifyBinary(target, oldVersion);
        string backup = ReplaceVerified(own, target, plan.OldSha256, plan.NewSha256);
        try
        {
            VerifyBinary(target, newVersion);
            using var helper = Process.GetCurrentProcess();
            using var started = Launch(target, plan.Background);
            if (started.WaitForExit(1500) && started.ExitCode != 0) throw new UpdateException("新版启动未完成。");
            try { File.WriteAllText(Path.Combine(directory, "completed.json"), JsonSerializer.Serialize(new UpdateCompletion(helper.Id, helper.StartTime.ToUniversalTime().Ticks, true))); }
            catch (IOException) { } catch (UnauthorizedAccessException) { /* Cleanup failure must not roll back a running new version. */ }
        }
        catch
        {
            Restore(target, backup);
            try { using var restarted = Launch(target, plan.Background); } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            throw new UpdateException("新版安装或启动未完成，已尝试恢复旧版。请保留旧版备份并稍后重试。");
        }
    }
    private static Process Launch(string target, bool background)
    {
        var start = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)!, WindowStyle = background ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal };
        if (background) start.ArgumentList.Add("--background");
        return Process.Start(start) ?? throw new UpdateException("无法启动程序。");
    }
    public static string ReplaceVerified(string package, string target, string oldHash, string newHash)
    {
        package = Path.GetFullPath(package); target = Path.GetFullPath(target);
        if (package.Equals(target, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).Equals("GdutNetwork.exe", StringComparison.OrdinalIgnoreCase)) throw new UpdateException("更新目标无效。");
        EnsureNoLinks(package); EnsureNoLinks(target);
        if (Sha256(package) != newHash || Sha256(target) != oldHash) throw new UpdateException("安装文件已变化，未替换程序。");
        string id = Guid.NewGuid().ToString("N");
        string staged = Path.Combine(Path.GetDirectoryName(target)!, $"GdutNetwork.{id}.new");
        string backup = Path.Combine(Path.GetDirectoryName(target)!, $"GdutNetwork.{id}.previous");
        bool moved = false;
        try
        {
            File.Copy(package, staged, overwrite: false);
            if (Sha256(staged) != newHash) throw new UpdateException("待安装文件校验失败。");
            File.Move(target, backup); moved = true; File.Move(staged, target);
            return backup;
        }
        catch { if (moved && !File.Exists(target)) File.Move(backup, target); throw; }
        finally { try { File.Delete(staged); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public static void Restore(string target, string backup)
    {
        target = Path.GetFullPath(target); backup = Path.GetFullPath(backup);
        if (!Path.GetFileName(target).Equals("GdutNetwork.exe", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(target) != Path.GetDirectoryName(backup) || !Regex.IsMatch(Path.GetFileName(backup), "^GdutNetwork\\.[a-f0-9]{32}\\.previous$")) throw new UpdateException("旧版备份位置无效。");
        EnsureNoLinks(target); EnsureNoLinks(backup);
        string failed = backup + ".failed";
        bool moved = File.Exists(target);
        if (moved) File.Move(target, failed);
        try { File.Move(backup, target); } catch { if (moved) File.Move(failed, target); throw; }
        try { File.Delete(failed); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static void EnsureSession(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(directory), Path.GetFullPath(CacheRoot), StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) throw new UpdateException("更新缓存目录无效。");
        EnsureNoLinks(directory);
    }
    private static void EnsureNoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UpdateException("更新路径包含链接，请选择普通安装目录。");
            current = Path.GetDirectoryName(current);
        }
    }
    public static async Task CleanupCompletedAsync()
    {
        await Task.Delay(3000).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(CacheRoot)) return;
            foreach (string directory in Directory.EnumerateDirectories(CacheRoot))
            {
                try
                {
                    EnsureSession(directory);
                    string marker = Path.Combine(directory, "completed.json");
                    if (!File.Exists(marker) || new FileInfo(marker).Length > 2048) continue;
                    EnsureNoLinks(marker);
                    var done = JsonSerializer.Deserialize<UpdateCompletion>(File.ReadAllText(marker));
                    if (done is null || !done.Succeeded) continue;
                    try { using var process = Process.GetProcessById(done.HelperProcessId); if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == done.HelperStartedUtcTicks) continue; } catch (ArgumentException) { }
                    foreach (string name in new[] { "GdutNetwork.exe", "install-plan.json", "package.part", "completed.json" })
                    { string file = Path.Combine(directory, name); EnsureNoLinks(file); File.Delete(file); }
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UpdateException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
