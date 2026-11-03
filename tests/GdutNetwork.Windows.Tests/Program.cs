using System.Net;
using System.Text.Json;
using GdutNetwork.Core;
using GdutNetwork.Windows;

var results = new List<object>(); int failures = 0;
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Test(string name, Func<Task> action)
{
    try { await action(); results.Add(new { Name = name, Passed = true }); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; results.Add(new { Name = name, Passed = false, ErrorType = ex.GetType().Name }); Console.WriteLine($"FAIL {name}: {ex.GetType().Name}"); }
}
Task Sync(Action action) { action(); return Task.CompletedTask; }
const string password = "test-only-&?/#=密 码";
var defaults = new AutomationSettings();
PortalAuthenticator Adapter(PortalFake fake, Func<bool>? connected = null) => new(() => new("test-account", password), connected ?? (() => true),
    () => new HttpClient(fake, disposeHandler: false), (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });

await Test("Windows vault can round-trip and delete an isolated synthetic credential", () => Sync(() =>
{
    var vault = new CredentialStore(Guid.NewGuid());
    try
    {
        Assert(!vault.Exists, "test target already exists"); vault.Save("test-account", password);
        Assert(vault.Exists && vault.ReadMetadata() == "test-account", "metadata");
        var value = vault.Read(); Assert(value?.UserName == "test-account" && value.Password == password, "roundtrip");
        Assert(!JsonSerializer.Serialize(value).Contains("test-only"), "serialization");
    }
    finally { vault.Delete(); }
    Assert(!vault.Exists, "isolated entry not removed");
}));
await Test("Pause and disabled billing option persist across settings-store instances", () => Sync(() =>
{
    string path = Path.Combine(AppContext.BaseDirectory, "state-tests", Guid.NewGuid().ToString("N"));
    var store = new SettingsStore(path); Assert(store.Load().AutoConfirmBillingCycle, "default");
    store.Save(new() { Pause = PauseReason.BadCredentials, AutoConfirmBillingCycle = false, StartWithWindows = false });
    var reloaded = new SettingsStore(path).Load();
    Assert(reloaded.Pause == PauseReason.BadCredentials && !reloaded.Enabled && !reloaded.AutoConfirmBillingCycle && !reloaded.StartWithWindows, "state reload");
    File.WriteAllText(Path.Combine(path, "settings.json"), "{broken");
    Assert(new SettingsStore(path).Load().Pause == PauseReason.Configuration, "corrupt state must not auto-start");
}));
await Test("Existing authenticated session never reads a password or submits anything", async () =>
{
    using var fake = new PortalFake { AlreadySession = true }; int reads = 0;
    var adapter = new PortalAuthenticator(() => { reads++; return new("test-account", password); }, () => true, () => new HttpClient(fake, false));
    var result = await adapter.AuthenticateAsync(defaults, default);
    Assert(result.Outcome == AuthOutcome.AlreadyAuthenticated && reads == 0 && fake.LoginRequests == 0 && fake.ConfirmRequests == 0, "already authenticated");
});
await Test("Other Wi-Fi never receives a portal request", async () =>
{
    using var fake = new PortalFake(); var result = await Adapter(fake, () => false).AuthenticateAsync(defaults, default);
    Assert(result.Outcome == AuthOutcome.UnsupportedConfiguration && fake.Requests == 0, "wrong network");
});
await Test("HTTP fallback is used only when enabled after TLS verification fails", async () =>
{
    using var fake = new PortalFake(); var result = await Adapter(fake).AuthenticateAsync(defaults, default);
    Assert(result.Outcome == AuthOutcome.SubmittedSuccessfully && fake.LoginRequests == 1 && fake.TlsRequests == 2, "HTTP fallback");
    Assert(fake.LastPassword == password && fake.LastAccount == "test-account", "credential escaping");
});
await Test("Disabling HTTP prevents password submission when TLS is unavailable", async () =>
{
    using var fake = new PortalFake(); var result = await Adapter(fake).AuthenticateAsync(defaults with { AllowHttpAuthentication = false }, default);
    Assert(result.Outcome == AuthOutcome.UnsupportedConfiguration && fake.LoginRequests == 0 && fake.ConfirmRequests == 0, "HTTP disabled");
});
await Test("Billing auto-confirm defaults on and sends exactly one confirmation and one login", async () =>
{
    using var fake = new PortalFake { BillingRequired = true }; var result = await Adapter(fake).AuthenticateAsync(defaults, default);
    Assert(result.Outcome == AuthOutcome.SubmittedSuccessfully && fake.OperatorChecks == 1 && fake.ConfirmRequests == 1 && fake.LoginRequests == 1, "billing enabled");
});
await Test("Turning billing auto-confirm off stops before confirmation and login", async () =>
{
    using var fake = new PortalFake { BillingRequired = true };
    var result = await Adapter(fake).AuthenticateAsync(defaults with { AutoConfirmBillingCycle = false }, default);
    Assert(result.Outcome == AuthOutcome.BillingConfirmation && fake.OperatorChecks == 1 && fake.ConfirmRequests == 0 && fake.LoginRequests == 0, "billing disabled");
});
await Test("Visible CAPTCHA stops authentication before any billing or login request", async () =>
{
    using var fake = new PortalFake { VisibleCaptcha = true }; var result = await Adapter(fake).AuthenticateAsync(defaults, default);
    Assert(result.Outcome == AuthOutcome.Captcha && fake.OperatorChecks == 0 && fake.LoginRequests == 0, "captcha");
});
await Test("Unknown MD5 configuration stops before credentials are submitted", async () =>
{
    using var fake = new PortalFake { Md5Changed = true }; var result = await Adapter(fake).AuthenticateAsync(defaults, default);
    Assert(result.Outcome == AuthOutcome.UnsupportedConfiguration && fake.OperatorChecks == 0 && fake.LoginRequests == 0, "config change");
});
await Test("Network change before billing confirmation prevents financial and login actions", async () =>
{
    bool connected = true;
    using var fake = new PortalFake { BillingRequired = true, OnOperatorCheck = () => connected = false };
    var result = await Adapter(fake, () => connected).AuthenticateAsync(defaults, default);
    Assert(result.Outcome == AuthOutcome.UnsupportedConfiguration && fake.ConfirmRequests == 0 && fake.LoginRequests == 0, "network change");
});
await Test("Password errors return a fixed safe category without echoing the secret", async () =>
{
    using var fake = new PortalFake { LoginMessage = "账号或密码错误" }; var result = await Adapter(fake).AuthenticateAsync(defaults, default);
    Assert(result.Outcome == AuthOutcome.BadCredentials && !result.Message.Contains(password) && fake.LoginRequests == 1, "password error");
});
await Test("Unknown authentication errors pause rather than repeatedly retrying passwords", async () =>
{
    using var fake = new PortalFake { LoginMessage = "unexpected response" }; var result = await Adapter(fake).AuthenticateAsync(defaults, default);
    Assert(result.Outcome == AuthOutcome.UnsupportedConfiguration && fake.LoginRequests == 1, "unknown error");
});
await Test("Cancellation during a request propagates to the coordinator", async () =>
{
    using var fake = new PortalFake { DelayRoot = true }; using var cancel = new CancellationTokenSource(20);
    bool canceled = false;
    try { await Adapter(fake).AuthenticateAsync(defaults, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
    Assert(canceled && fake.LoginRequests == 0 && fake.ConfirmRequests == 0, "cancellation");
});

if (args.Length == 1) File.WriteAllText(Path.GetFullPath(args[0]), JsonSerializer.Serialize(new { Total = results.Count, Failed = failures, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
await Test("Official account months and unused gift months are independent", () => Sync(() =>
{
    var value=SelfServiceParser.Balance("<p><label>账号剩余月数：</label><span>5</span></p><p><label>还没使用赠送月数：</label><span>2</span></p>");
    Assert(value.AccountMonths==5&&value.GiftMonths==2,"independent official balances");
}));
await Test("Zero months is distinct from an unavailable official field", () => Sync(() =>
{
    var value=SelfServiceParser.Balance("<p><label>账号剩余月数:</label><span>0</span></p>");
    Assert(value.AccountMonths==0&&value.GiftMonths is null,"zero vs unknown");
}));
await Test("Money and gift promotions are never converted into remaining months", () => Sync(() =>
{
    var value=SelfServiceParser.Balance("<p><label>账户余额</label><span>300</span></p><div>充值5个月送1个月</div><script>var leftMonths='10';</script>");
    Assert(value.AccountMonths is null&&value.GiftMonths is null,"no inferred balance");
}));
await Test("Conflicting desktop and mobile balances remain unknown", () => Sync(() =>
{
    var value=SelfServiceParser.Balance("<p><label>账号剩余月数:</label><span>3</span></p><p><label>账号剩余月数:</label><span>4</span></p>");
    Assert(value.AccountMonths is null,"conflict");
}));
await Test("Identical responsive copies do not double-count months", () => Sync(() =>
{
    var value=SelfServiceParser.Balance(string.Concat(Enumerable.Repeat("<p><label>账号剩余月数:</label><span>5</span></p>",2)));
    Assert(value.AccountMonths==5,"duplicate not sum");
}));
await Test("Unexpected or overflowing month values remain unknown", () => Sync(() =>
{
    foreach(var text in new[]{"9999999999999999999999999999999999999999","-1","5元","NaN"})
        Assert(SelfServiceParser.Balance($"<p><label>账号剩余月数:</label><span>{text}</span></p>").AccountMonths is null,"invalid number");
}));
await Test("Only exact HTTPS service and verified payment hosts are navigable", () => Sync(() =>
{
    Assert(SelfServiceNavigation.IsSelfService("https://selfmanager.gdut.edu.cn/dashboard"),"official self service");
    Assert(SelfServiceNavigation.IsOfficial("https://tyzf.gdut.edu.cn/zhifu/PayMain.aspx"),"verified payment host");
    foreach(var url in new[]{"http://selfmanager.gdut.edu.cn/","https://selfmanager.gdut.edu.cn.evil.test/","https://selfmanager.gdut.edu.cn@evil.test/","https://evil@selfmanager.gdut.edu.cn/","https://selfmanager.gdut.edu.cn:801/","https://other.gdut.edu.cn/","file:///C:/test"})
        Assert(!SelfServiceNavigation.IsOfficial(url),"unsafe navigation");
    Assert(!SelfServiceNavigation.IsSelfService("https://tyzf.gdut.edu.cn/"),"payment host must never receive credentials");
}));
const string loginFixture="""
<form id="loginform" method="post" action="/login/verify"><input id="account" name="account"><input type="password" id="password" name="password"><div id="randomDiv" style="display: none"></div></form>
<script>var codeflag = 'false';var trytimes='0';</script>
""";
await Test("Verified official form can use the real frontend without emulating password encryption", () => Sync(() =>
{
    SelfServiceParser.ValidateLoginForm(loginFixture,new Uri("https://selfmanager.gdut.edu.cn/login/"));
}));
await Test("Captcha and changed credential destinations stop automatic self-service login", () => Sync(() =>
{
    foreach(var changed in new[]{loginFixture.Replace("'false'","'true'"),loginFixture.Replace("trytimes='0'","trytimes='3'"),loginFixture.Replace("display: none","display: block"),loginFixture.Replace("action=\"/login/verify\"","action=\"https://tyzf.gdut.edu.cn/login/verify\""),loginFixture.Replace("method=\"post\"","method=\"get\"")})
    {
        bool stopped=false;try{SelfServiceParser.ValidateLoginForm(changed,new Uri("https://selfmanager.gdut.edu.cn/login/"));}catch(SelfServiceException){stopped=true;}
        Assert(stopped,"unsafe or interactive login must stop");
    }
}));
await Test("Login error messages do not reproduce account or server content", () => Sync(() =>
{
    string message=SelfServiceParser.LoginError("<form id=loginform></form><div id=error>账号 PRIVATE-TEST-ACCOUNT 密码错误</div>");
    Assert(!message.Contains("PRIVATE-TEST-ACCOUNT")&&message.Contains("已停止"),"private message redaction");
}));

Console.WriteLine($"{results.Count - failures}/{results.Count} passed");
return failures == 0 ? 0 : 1;

sealed class PortalFake : HttpMessageHandler
{
    public bool AlreadySession, BillingRequired, VisibleCaptcha, Md5Changed, DelayRoot;
    public string? LoginMessage;
    public Action? OnOperatorCheck;
    public int Requests, TlsRequests, LoginRequests, OperatorChecks, ConfirmRequests;
    public string? LastPassword, LastAccount;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++;
        if (request.RequestUri is not { } uri || uri.Host != "10.0.3.2" || request.Method != HttpMethod.Get || request.Headers.Contains("Cookie")) throw new InvalidOperationException("Unexpected destination or request mode");
        if (uri.Scheme == "https") { TlsRequests++; throw new HttpRequestException("Synthetic TLS rejection"); }
        string path = uri.AbsolutePath;
        if (path == "/")
        {
            if (DelayRoot) await Task.Delay(5000, token);
            string title = AlreadySession ? "注销页" : "登录页";
            return Html($"<!--Dr.COMWebLoginID_3.htm--><title>{title}</title><script>portalname='';v4ip='192.0.2.2';v6ip='';</script>");
        }
        if (path.EndsWith("page/loadConfig"))
            return Json(new { data = new { login_method = "1", en_md5 = Md5Changed ? "1" : "0", password_cut = "0", enable_r3 = "0", program_index = "test", page_index = "one", account_suffix = "", is_vlan = "1" } });
        if (path.EndsWith("/pc.js"))
        {
            string display = VisibleCaptcha ? "block" : "none";
            return Html("var bodyContent='<form name=\"f0\"><input name=\"0MKKey\" value=\"123456\"><input name=\"para\" value=\"00\"><input name=\"R1\" value=\"0\"><input name=\"R3\" value=\"0\"><input name=\"R6\" value=\"0\"></form><form name=\"f1\"><input name=\"DDDDD\"><input type=\"password\" name=\"upass\"><input name=\"captcha\" style=\"display:" + display + "\"><input type=\"button\" onclick=\"javascript:ee(1)\"></form>';");
        }
        var query = uri.Query.TrimStart('?').Split('&').Select(s => s.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length == 2 ? Uri.UnescapeDataString(p[1]) : "");
        if (path.EndsWith("custom/operator"))
        {
            if (query["r"] == "0") { OperatorChecks++; OnOperatorCheck?.Invoke(); return Json(new { code = "1", result = BillingRequired ? "fail" : "ok" }); }
            ConfirmRequests++; return Json(new { code = "1", result = "ok" });
        }
        if (path == "/drcom/login")
        {
            LoginRequests++; LastPassword = query.GetValueOrDefault("upass"); LastAccount = query.GetValueOrDefault("DDDDD");
            return LoginMessage is null ? Json(new { result = 1 }) : Json(new { result = 0, msg = LoginMessage });
        }
        throw new InvalidOperationException("Unexpected endpoint");
    }
    private static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, System.Text.Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Json(object data) => Html("gdut_callback(" + JsonSerializer.Serialize(data) + ");");
}
