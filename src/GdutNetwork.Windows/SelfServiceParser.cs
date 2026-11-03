using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace GdutNetwork.Windows;

public sealed class SelfServiceException(string message) : Exception(message);
public sealed record SubscriptionBalance(decimal? AccountMonths, decimal? GiftMonths, DateTimeOffset CheckedAt);

public static class SelfServiceNavigation
{
    public static bool IsSelfService(string url) => Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Port==443&&uri.UserInfo.Length==0&&uri.Host=="selfmanager.gdut.edu.cn";
    public static bool IsOfficial(string url) => Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Port==443&&uri.UserInfo.Length==0&&uri.Host is "selfmanager.gdut.edu.cn" or "tyzf.gdut.edu.cn";
}

public static class SelfServiceParser
{
    private static IDocument Parse(string html) => new HtmlParser().ParseDocument(html);
    public static bool IsLogin(string html) => Parse(html).QuerySelector("form#loginform") is not null;

    public static void ValidateLoginForm(string html, Uri uri)
    {
        if(!SelfServiceNavigation.IsSelfService(uri.AbsoluteUri))throw new SelfServiceException("已阻止向非官方自助服务地址填写凭据。");
        var doc=Parse(html);
        var form=doc.QuerySelector("form#loginform")??throw new SelfServiceException("未找到官网登录表单。");
        string scripts=string.Join("\n",doc.QuerySelectorAll("script").Select(x=>x.TextContent));
        if(!Regex.IsMatch(scripts,"var\\s+codeflag\\s*=\\s*['\"]false['\"]")||Regex.IsMatch(scripts,"var\\s+trytimes\\s*=\\s*['\"](?:[3-9]|\\d{2,})"))
            throw new SelfServiceException("官网需要验证码，请在官方页面手动登录。自动尝试已停止。");
        if(doc.QuerySelector("#randomDiv") is {} captcha&&!Regex.IsMatch(captcha.GetAttribute("style")??"",@"display\s*:\s*none",RegexOptions.IgnoreCase))
            throw new SelfServiceException("官网需要验证码，请在官方页面手动登录。自动尝试已停止。");
        if(!Uri.TryCreate(uri,form.GetAttribute("action"),out var action)||!SelfServiceNavigation.IsSelfService(action.AbsoluteUri)||!Regex.IsMatch(action.AbsolutePath,@"^/login/verify(?:;jsessionid=[A-Za-z0-9]+)?$")||!string.Equals(form.GetAttribute("method"),"post",StringComparison.OrdinalIgnoreCase))
            throw new SelfServiceException("官网登录表单地址已改变，请手动核对。");
        if(form.QuerySelectorAll("#account[name=account]").Length!=1||form.QuerySelectorAll("#password[name=password][type=password]").Length!=1)
            throw new SelfServiceException("官网登录字段已改变，请手动核对。");
    }

    public static string LoginError(string html)
    {
        var doc=Parse(html);
        string error=string.Join(" ",doc.QuerySelectorAll("#fielderror1,#fielderror2,#error").Select(x=>x.TextContent));
        if(error.Contains("验证码"))return "官网需要验证码，请在官方页面手动登录。自动尝试已停止。";
        if(error.Contains("密码")||error.Contains("账号")||error.Contains("用户"))return "官网未接受已保存的账号密码，请核对后再试。自动尝试已停止。";
        return "官网登录未完成，请在官方页面查看原因。自动尝试已停止。";
    }

    public static SubscriptionBalance Balance(string html)
    {
        var doc=Parse(html);
        foreach(var node in doc.QuerySelectorAll("script,style,noscript"))node.Remove();
        // The school's two labels are separate balances. Do not add, subtract, or derive months from money or promotions.
        var pairs=new List<(string Label,string Value)>();
        foreach(var label in doc.QuerySelectorAll("label,dt"))
            if(label.NextElementSibling is {} value)pairs.Add((Clean(label.TextContent),Clean(value.TextContent)));
        foreach(var row in doc.QuerySelectorAll("tr,.form-group"))
        {
            var cells=row.Children.ToArray();
            if(cells.Length==2)pairs.Add((Clean(cells[0].TextContent),Clean(cells[1].TextContent)));
        }
        decimal? Read(string label)
        {
            var candidates=pairs.Where(x=>x.Label==label).Select(x=>Regex.Match(x.Value,@"^([0-9]+(?:\.[0-9]+)?)\s*(?:个?月)?$")).ToArray();
            if(candidates.Length==0||candidates.Any(x=>!x.Success))return null;
            var values=new List<decimal>();
            foreach(var m in candidates)
            {
                if(!decimal.TryParse(m.Groups[1].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value))return null;
                values.Add(value);
            }
            return values.Distinct().Count()==1?values[0]:null;
        }
        return new(Read("账号剩余月数"),Read("还没使用赠送月数"),DateTimeOffset.Now);
    }
    private static string Clean(string text)=>Regex.Replace(text,@"\s+"," ").Trim().TrimEnd(':','：');
}
