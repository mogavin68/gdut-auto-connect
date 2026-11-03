using System.Net;
using System.Text.RegularExpressions;

namespace GdutNetwork.Core;

public static partial class PortalClassifier
{
    public static PortalEvidence Classify(string html)
    {
        // Static scripts may mention login, logout and CAPTCHA together; they are not live state.
        string withoutComments = Comments().Replace(html, " ");
        string markup = Scripts().Replace(withoutComments, " ");
        string visibleText = WebUtility.HtmlDecode(Tags().Replace(markup, " "));
        bool vendor = withoutComments.Contains("dr.com", StringComparison.OrdinalIgnoreCase)
            || withoutComments.Contains("drcom", StringComparison.OrdinalIgnoreCase)
            || withoutComments.Contains("eportal", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Dr.COMWebLoginID", StringComparison.OrdinalIgnoreCase);
        bool password = false, username = false, captcha = false;
        foreach (Match match in Inputs().Matches(markup))
        {
            var attrs = Attributes().Matches(match.Value).GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key.ToLowerInvariant(),
                g => WebUtility.HtmlDecode(g.First().Groups[2].Success ? g.First().Groups[2].Value : g.First().Groups[3].Success ? g.First().Groups[3].Value : g.First().Groups[4].Value),
                StringComparer.OrdinalIgnoreCase);
            attrs.TryGetValue("type", out string? type);
            attrs.TryGetValue("style", out string? style);
            if (string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase) || Disabled().IsMatch(match.Value)
                || style is not null && Regex.IsMatch(style, @"display\s*:\s*none", RegexOptions.IgnoreCase)) continue;
            string identity = string.Join(' ', attrs.Where(a => a.Key is "name" or "id" or "placeholder").Select(a => a.Value));
            password |= string.Equals(type, "password", StringComparison.OrdinalIgnoreCase);
            username |= UserField().IsMatch(identity);
            captcha |= CaptchaField().IsMatch(identity);
        }
        captcha |= password && (visibleText.Contains("验证码", StringComparison.Ordinal) || visibleText.Contains("captcha", StringComparison.OrdinalIgnoreCase));
        bool session = SessionTitle().IsMatch(markup);
        PortalState state = captcha ? PortalState.CaptchaRequired
            : session && password ? PortalState.Unknown
            : password && username && vendor ? PortalState.LoginRequired
            : session ? PortalState.SessionIndicated
            : PortalState.Unknown;
        return new(state, vendor, password, username);
    }

    [GeneratedRegex(@"<!--[\s\S]*?-->", RegexOptions.CultureInvariant)] private static partial Regex Comments();
    [GeneratedRegex(@"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Scripts();
    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)] private static partial Regex Tags();
    [GeneratedRegex(@"<input\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Inputs();
    [GeneratedRegex(@"\sdisabled(?:\s|=|/?>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Disabled();
    [GeneratedRegex("([\\w:-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.CultureInvariant)] private static partial Regex Attributes();
    [GeneratedRegex(@"username|user_name|user_account|account|DDDDD|账号|用户名", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex UserField();
    [GeneratedRegex(@"captcha|verify_?code|verification|验证码", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CaptchaField();
    [GeneratedRegex(@"<title\b[^>]*>\s*(?:注销页|注销页面|登录成功|认证成功|logout)\s*</title>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex SessionTitle();
}

public static class SafeEndpoint
{
    public static string Display(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) return "[非 HTTP(S) 地址]";
        // Redirect paths and query strings can contain account IDs and tokens; omit both.
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port) { Path = "/", UserName = "", Password = "" };
        return builder.Uri.GetLeftPart(UriPartial.Authority) + "/";
    }
}
