using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GdutNetwork.Windows;

public sealed class UpdateException(string message) : Exception(message);
public sealed record UpdateRelease(Version Version, string Tag, string Notes, Uri Page, Uri Download, long Size, string Sha256)
{
    public static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
}
public sealed record DownloadedUpdate(UpdateRelease Release, string SessionDirectory, string FilePath);

public sealed class GitHubUpdateClient : IDisposable
{
    public const string Repository = "mogavin68/gdut-auto-connect";
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases");
    public static readonly Uri LatestApi = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private readonly HttpClient client;
    public GitHubUpdateClient(HttpMessageHandler? testHandler = null)
    {
        client = new(testHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GdutAutoConnect/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public async Task<UpdateRelease> GetLatestAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(LatestApi, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new UpdateException("暂时无法取得公开版本，请稍后重试或打开发布页。");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new UpdateException("GitHub 请求暂受限制，请稍后重试或打开发布页。");
        if (response.StatusCode != HttpStatusCode.OK) throw new UpdateException("更新服务器暂不可用，请稍后重试。");
        await response.Content.LoadIntoBufferAsync(512_000, timeout.Token);
        return ParseLatest(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    public static UpdateRelease ParseLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new FormatException();
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Regex.IsMatch(tag, @"^v?[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$")) throw new FormatException();
            if (!Version.TryParse(tag.TrimStart('v'), out var version)) throw new FormatException();
            string pagePath = $"/{Repository}/releases/tag/{tag}";
            var page = new Uri(root.GetProperty("html_url").GetString() ?? "");
            if (!IsGitHubPath(page, pagePath)) throw new FormatException();
            var assets = root.GetProperty("assets").EnumerateArray().Where(x => x.GetProperty("name").GetString() == "GdutNetwork.exe").ToArray();
            if (assets.Length != 1) throw new FormatException();
            var asset = assets[0];
            if (asset.GetProperty("state").GetString() != "uploaded") throw new FormatException();
            long size = asset.GetProperty("size").GetInt64();
            string digest = asset.GetProperty("digest").GetString() ?? "";
            if (size < 1024 || size > 512L * 1024 * 1024 || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) throw new FormatException();
            var download = new Uri(asset.GetProperty("browser_download_url").GetString() ?? "");
            if (!IsGitHubPath(download, $"/{Repository}/releases/download/{tag}/GdutNetwork.exe")) throw new FormatException();
            string notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
            if (notes.Length > 16000) notes = notes[..16000] + "\n（更多说明请打开发布页）";
            return new(UpdateRelease.Normalize(version), tag, notes, page, download, size, digest[7..].ToLowerInvariant());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or UriFormatException or OverflowException)
        { throw new UpdateException("发布版本信息或校验值未能确认，已停止自动更新。请打开发布页核对。"); }
    }

    private static bool IsGitHubPath(Uri uri, string path) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Host == "github.com" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.AbsolutePath == path && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    public static bool AllowedDownloadRedirect(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    public async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release, IProgress<long>? progress, CancellationToken token)
    {
        if (!IsGitHubPath(release.Download, $"/{Repository}/releases/download/{release.Tag}/GdutNetwork.exe") || !Regex.IsMatch(release.Sha256, "^[0-9a-f]{64}$") || release.Size < 1024 || release.Size > 512L * 1024 * 1024) throw new UpdateException("下载来源或校验信息无效。");
        string directory = UpdateInstallation.CreateSessionDirectory();
        string partial = Path.Combine(directory, "package.part");
        string file = Path.Combine(directory, "GdutNetwork.exe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            Uri uri = release.Download;
            HttpResponseMessage? response = null;
            for (int i = 0; i < 6; i++)
            {
                using var headers = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                headers.CancelAfter(TimeSpan.FromSeconds(30));
                response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, headers.Token);
                headers.CancelAfter(Timeout.InfiniteTimeSpan);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var next = response.Headers.Location;
                    response.Dispose(); response = null;
                    if (next is null) throw new UpdateException("下载跳转无效，已停止更新。");
                    uri = new Uri(uri, next);
                    if (!AllowedDownloadRedirect(uri)) throw new UpdateException("下载跳转来源未能确认，已停止更新。");
                    continue;
                }
                break;
            }
            using (response)
            {
                if (response is null || response.StatusCode != HttpStatusCode.OK) throw new UpdateException("新版下载未完成，请稍后重试。");
                if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw new UpdateException("下载文件大小与发布信息不一致，已停止更新。");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
                {
                    byte[] buffer = new byte[131072]; long received = 0; var watch = Stopwatch.StartNew();
                    while (true)
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(90));
                        int count = await input.ReadAsync(buffer, idle.Token);
                        if (count == 0) break;
                        received += count;
                        if (received > release.Size) throw new UpdateException("下载文件超过声明大小，已停止更新。");
                        await output.WriteAsync(buffer.AsMemory(0, count), idle.Token); hash.AppendData(buffer, 0, count);
                        if (watch.ElapsedMilliseconds >= 100) { progress?.Report(received); watch.Restart(); }
                    }
                    string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                    if (received != release.Size || actual != release.Sha256) throw new UpdateException("下载文件 SHA-256 或大小校验失败，未执行安装。");
                    progress?.Report(received);
                }
            }
            UpdateInstallation.VerifyBinary(partial, release.Version);
            File.Move(partial, file);
            return new(release, directory, file);
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
    public void Dispose() => client.Dispose();
}
