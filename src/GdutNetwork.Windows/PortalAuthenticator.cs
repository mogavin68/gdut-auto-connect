using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GdutNetwork.Core;

namespace GdutNetwork.Windows;

public sealed class PortalAuthenticator(Func<LoginCredential?> credentials, Func<bool> targetConnected,
    Func<HttpClient>? clientFactory = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public async Task<AuthResult> AuthenticateAsync(AutomationSettings settings, CancellationToken token)
    {
        if (!targetConnected()) return Unsupported("当前连接不是 gdut，未提交凭据");
        using var client = clientFactory?.Invoke() ?? CreateClient();
        try
        {
            string currentRoot = await ReadAsync(client, new("http://10.0.3.2/"), token).ConfigureAwait(false);
            if (PortalClassifier.Classify(currentRoot).State == PortalState.SessionIndicated)
                return new(AuthOutcome.AlreadyAuthenticated, "当前会话已存在，未重复提交密码");
            // The current site's own script declares ports 801/802. Try TLS with normal certificate verification.
            (Uri Root, Uri Portal, string Html)? selected = null;
            foreach (string secureRoot in new[] { "https://10.0.3.2/", "https://10.0.3.2:802/" })
            {
                using var tlsBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                tlsBudget.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var rootUri = new Uri(secureRoot);
                    string body = await ReadAsync(client, rootUri, tlsBudget.Token).ConfigureAwait(false);
                    if (!PortalClassifier.Classify(body).VendorMarker) continue;
                    // Verify the advertised HTTPS API port before choosing it for any credential submission.
                    await ReadAsync(client, new("https://10.0.3.2:802/eportal/portal/page/loadConfig?callback=gdut_callback"), tlsBudget.Token).ConfigureAwait(false);
                    selected = (rootUri, new("https://10.0.3.2:802/"), body); break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !token.IsCancellationRequested) { }
            }
            token.ThrowIfCancellationRequested();
            if (selected is null)
            {
                if (!settings.AllowHttpAuthentication) return Unsupported("HTTPS 未能验证，当前设置禁止 HTTP 登录");
                selected = (new("http://10.0.3.2/"), new("http://10.0.3.2:801/"), currentRoot);
            }
            var context = selected.Value;
            if (!PortalClassifier.Classify(context.Html).VendorMarker) return Unsupported("未识别到已核实的校园门户");
            if (PortalClassifier.Classify(context.Html).State == PortalState.SessionIndicated)
                return new(AuthOutcome.AlreadyAuthenticated, "当前会话已存在");

            var configFields = new Dictionary<string, string>
            {
                ["callback"] = "gdut_callback", ["program_index"] = PortalProtocol.Scalar(context.Html, "portalname") ?? "",
                ["wlan_user_ip"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(PortalProtocol.Scalar(context.Html, "v4ip") ?? "")),
                ["wlan_user_ipv6"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(PortalProtocol.Scalar(context.Html, "v6ip") ?? ""))
            };
            using var configDocument = PortalProtocol.ParseJsonp(await ReadAsync(client, Endpoint(context.Portal, "eportal/portal/page/loadConfig", configFields), token).ConfigureAwait(false));
            if (!configDocument.RootElement.TryGetProperty("data", out var config)) return Unsupported("认证配置响应不完整");
            string Value(string key) => PortalProtocol.Value(config, key);
            if (Value("login_method") != "1" || Value("en_md5") != "0" || Value("password_cut") != "0" || Value("enable_r3") != "0")
                return Unsupported("门户认证模式发生变化，已停止提交");
            string program = Value("program_index"), page = Value("page_index");
            if (!Regex.IsMatch(program, @"\A[A-Za-z0-9_-]{1,80}\z") || !Regex.IsMatch(page, @"\A[A-Za-z0-9_-]{1,80}\z"))
                return Unsupported("门户模板标识无效");
            string template = await ReadAsync(client, new(context.Portal, $"eportal/extern/{program}/{page}/pc.js"), token).ConfigureAwait(false);
            string? form = PortalProtocol.Scalar(template, "bodyContent");
            if (form is null || !form.Contains("ee(1)", StringComparison.Ordinal)) return Unsupported("登录模板与已核实流程不一致");
            var evidence = PortalClassifier.Classify("<!--Dr.COMWebLoginID-->" + form);
            if (evidence.State == PortalState.CaptchaRequired) return new(AuthOutcome.Captcha, "需要人工验证码");
            if (!evidence.PasswordField || !evidence.UsernameField) return Unsupported("未找到有效的登录表单");
            var defaults = HiddenDefaults(form);
            if (!defaults.ContainsKey("0MKKey") || !defaults.ContainsKey("para")) return Unsupported("表单默认参数不完整");
            token.ThrowIfCancellationRequested();
            if (!targetConnected()) return Unsupported("网络已切换，未提交凭据");
            var secret = credentials();
            if (secret is null || string.IsNullOrWhiteSpace(secret.UserName) || secret.Password.Length == 0) return Unsupported("尚未配置有效凭据");
            string suffix = Value("account_suffix");
            string account = secret.UserName + suffix;

            if (Value("is_vlan") == "1")
            {
                var check = new Dictionary<string, string> { ["callback"] = "gdut_callback", ["account"] = account, ["r"] = "0" };
                using var checkDocument = PortalProtocol.ParseJsonp(await ReadAsync(client, Endpoint(context.Portal, "eportal/portal/custom/operator", check), token).ConfigureAwait(false));
                var checkResult = checkDocument.RootElement;
                if (PortalProtocol.Value(checkResult, "code") != "1") return PortalProtocol.ClassifyResponse(checkResult);
                if (PortalProtocol.Value(checkResult, "result") == "fail")
                {
                    if (!settings.AutoConfirmBillingCycle) return new(AuthOutcome.BillingConfirmation, "需要确认新的计费周期");
                    token.ThrowIfCancellationRequested();
                    if (!targetConnected()) return Unsupported("网络已切换，未确认计费周期");
                    check["r"] = "1";
                    using var confirm = PortalProtocol.ParseJsonp(await ReadAsync(client, Endpoint(context.Portal, "eportal/portal/custom/operator", check), token).ConfigureAwait(false));
                    if (PortalProtocol.Value(confirm.RootElement, "result") != "ok") return PortalProtocol.ClassifyResponse(confirm.RootElement);
                    await (delay?.Invoke(TimeSpan.FromSeconds(4), token) ?? Task.Delay(TimeSpan.FromSeconds(4), token)).ConfigureAwait(false);
                }
            }
            else if (Value("is_vlan") is not ("" or "0")) return Unsupported("未知的计费预检查模式");

            token.ThrowIfCancellationRequested();
            if (!targetConnected()) return Unsupported("网络已切换，未提交登录");
            // ee(1) -> login.default_login -> custom.vlanOperator -> login.portal_login.
            // The shipped client resets login_method=0 and calls the gateway /drcom/login JSONP route.
            var fields = new Dictionary<string, string>
            {
                ["callback"] = "gdut_callback", ["DDDDD"] = account, ["upass"] = secret.Password,
                ["0MKKey"] = defaults["0MKKey"], ["R1"] = defaults.GetValueOrDefault("R1", ""), ["R2"] = "",
                ["R3"] = defaults.GetValueOrDefault("R3", ""), ["R6"] = defaults.GetValueOrDefault("R6", "0"),
                ["para"] = defaults["para"], ["v6ip"] = PortalProtocol.Scalar(context.Html, "v6ip") ?? "", ["terminal_type"] = "1", ["lang"] = "zh-cn"
            };
            using var loginDocument = PortalProtocol.ParseJsonp(await ReadAsync(client, Endpoint(context.Root, "drcom/login", fields), token).ConfigureAwait(false));
            return PortalProtocol.ClassifyResponse(loginDocument.RootElement);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { return new(AuthOutcome.TransientFailure, "认证请求未完成，先复查网络，下一周期再尝试"); }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        { return Unsupported("认证响应或参数格式尚未识别，已暂停提交"); }
    }

    private static AuthResult Unsupported(string message) => new(AuthOutcome.UnsupportedConfiguration, message);
    private static Uri Endpoint(Uri origin, string path, Dictionary<string, string> values) => new(new Uri(origin, path), "?" + PortalProtocol.Query(values));
    private static Dictionary<string, string> HiddenDefaults(string html)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
        {
            string? name = Attribute(match.Value, "name");
            if (name is "0MKKey" or "R1" or "R2" or "R3" or "R6" or "para") values.TryAdd(name, Attribute(match.Value, "value") ?? "");
        }
        return values;
    }
    private static string? Attribute(string tag, string name)
    {
        var m = Regex.Match(tag, @"\b" + name + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.IgnoreCase);
        return !m.Success ? null : WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
    }
    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(10) };

    private static async Task<string> ReadAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var body = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (body.Length + read > 256 * 1024) throw new FormatException("Oversized portal response");
            body.Write(buffer, 0, read);
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        try { encoding = Encoding.GetEncoding(response.Content.Headers.ContentType?.CharSet?.Trim('"') ?? "utf-8"); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        return encoding.GetString(body.GetBuffer(), 0, (int)body.Length);
    }
}
