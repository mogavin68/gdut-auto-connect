using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GdutNetwork.Core;

public static partial class PortalProtocol
{
    public static string? Scalar(string script, string name)
    {
        var m = Regex.Match(script, @"(?<![\w$])" + Regex.Escape(name) + @"\s*=\s*(?:'((?:\\.|[^'\\])*)'|""((?:\\.|[^""\\])*)""|(-?\d+))", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!m.Success) return null;
        return m.Groups[3].Success ? m.Groups[3].Value : DecodeLiteral(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    private static string DecodeLiteral(string value)
    {
        var output = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { output.Append(value[i]); continue; }
            if (++i == value.Length) throw new FormatException("Invalid script string");
            char c = value[i];
            if (c is 'x' or 'u')
            {
                int count = c == 'x' ? 2 : 4;
                if (i + count >= value.Length) throw new FormatException("Invalid escape");
                output.Append((char)Convert.ToInt32(value.Substring(i + 1, count), 16)); i += count;
            }
            else output.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => c });
        }
        return output.ToString();
    }

    public static JsonDocument ParseJsonp(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith('{')) return JsonDocument.Parse(trimmed);
        var match = Jsonp().Match(trimmed);
        if (!match.Success) throw new FormatException("Unexpected JSONP envelope");
        return JsonDocument.Parse(match.Groups[1].Value);
    }

    public static string Value(JsonElement json, string key) => json.TryGetProperty(key, out var value) ? value.ToString() : "";
    public static AuthResult ClassifyResponse(JsonElement json)
    {
        string result = Value(json, "result");
        if (result is "1" or "ok") return new(AuthOutcome.SubmittedSuccessfully, "认证接口已接受，已重新检查网络");
        string message = Value(json, "msg") + " " + Value(json, "msga") + " " + Value(json, "message");
        if (Regex.IsMatch(message, "验证码|captcha|verification code", RegexOptions.IgnoreCase)) return new(AuthOutcome.Captcha, "需要人工验证");
        if (Regex.IsMatch(message, "密码.*错误|账号或密码|帐号或密码|用户名或密码|password.*(?:error|incorrect)|invalid.*password", RegexOptions.IgnoreCase)) return new(AuthOutcome.BadCredentials, "账号或密码错误");
        if (Regex.IsMatch(message, "欠费|停机|冻结|锁定|禁用|余额不足|超.*(?:终端|设备)|account.*(?:locked|disabled)", RegexOptions.IgnoreCase)) return new(AuthOutcome.AccountProblem, "账号状态异常");
        return new(AuthOutcome.UnsupportedConfiguration, "认证返回尚未识别，请检查后继续");
    }

    public static string Query(IEnumerable<KeyValuePair<string, string>> values) => string.Join('&', values.Select(v => Uri.EscapeDataString(v.Key) + "=" + Uri.EscapeDataString(v.Value)));
    [GeneratedRegex(@"^gdut_callback\s*\(([\s\S]*)\)\s*;?\s*$", RegexOptions.CultureInvariant)] private static partial Regex Jsonp();
}
