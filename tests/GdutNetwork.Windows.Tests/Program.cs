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

await Test("Half-year history uses calendar months including both boundary dates", () => Sync(() =>
{
    var range=RechargeHistoryPeriod.LastSixMonths(new(2026,8,31));
    Assert(range.Start==new DateOnly(2026,2,28)&&range.End==new DateOnly(2026,8,31),"calendar month clamp");
    Assert(RechargeHistoryPeriod.LastSixMonths(new(2024,8,31)).Start==new DateOnly(2024,2,29),"leap calendar");
}));
await Test("Half-year slices cover every day exactly once and never exceed sixty days", () => Sync(() =>
{
    var range=RechargeHistoryPeriod.LastSixMonths(new(2026,11,3));var slices=range.Split();
    var days=slices.SelectMany(s=>Enumerable.Range(s.Start.DayNumber,s.End.DayNumber-s.Start.DayNumber+1)).ToArray();
    Assert(slices.All(s=>s.End.DayNumber-s.Start.DayNumber<60)&&days.Distinct().Count()==days.Length,"window overlap or size");
    Assert(days.SequenceEqual(Enumerable.Range(range.Start.DayNumber,range.End.DayNumber-range.Start.DayNumber+1)),"window coverage");
}));
await Test("Official null-total empty history differs from malformed data", () => Sync(() =>
{
    Assert(RechargeHistoryClient.ParsePage("{\"total\":null,\"rows\":[]}").Total==0,"official empty");
    foreach(var json in new[]{"<html>login</html>","{\"total\":0}","{\"total\":null,\"rows\":[[1]]}","{\"total\":-1,\"rows\":[]}"})
    {
        bool stopped=false;try{RechargeHistoryClient.ParsePage(json);}catch(SelfServiceException){stopped=true;}
        Assert(stopped,"malformed history must not become zero records");
    }
}));
await Test("History preserves official types notes negative amounts and China-time day boundaries", () => Sync(() =>
{
    long time=new DateTimeOffset(2026,5,3,0,0,0,TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
    string json=JsonSerializer.Serialize(new{total=1,rows=new[]{new object?[]{time,"冲正",-30.25m,1,"<b>synthetic</b>"}}});
    var row=RechargeHistoryClient.ParsePage(json).Entries[0];
    Assert(row.OccurredAt.Day==3&&row.OccurredAt.Hour==0&&row.Amount==-30.25m&&row.Terminal=="1"&&row.Remark=="<b>synthetic</b>","raw fields");
    Assert(!row.ToString().Contains("synthetic"),"private ToString");
}));
await Test("All history windows and pages load without losing identical transactions", async () =>
{
    var today=new DateOnly(2026,11,3);var first=RechargeHistoryPeriod.LastSixMonths(today).Split()[0];int requests=0;
    object?[] Row(DateOnly day,string note)=>[new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue),TimeSpan.FromHours(8)).ToUnixTimeMilliseconds(),"合成测试",30m,1,note];
    using var fake=new HistoryFake(uri=>
    {
        requests++;var q=HistoryFake.Query(uri);int page=int.Parse(q["pageNumber"]);
        if(q["startTime"]!=first.Start.ToString("yyyy-MM-dd"))return HistoryFake.Json(new{total=(int?)null,rows=Array.Empty<object?[]>()});
        var rows=page==1?Enumerable.Range(0,100).Select(i=>Row(first.Start,"row "+i)).ToArray():new[]{Row(first.End,"same"),Row(first.End,"same")};
        return HistoryFake.Json(new{total=102,rows});
    });
    using var client=new RechargeHistoryClient(new CookieContainer(),fake);var snapshot=await client.LoadAsync(today,default);
    Assert(requests==5&&snapshot.Entries.Count==102,"all pages and empty windows");
    Assert(snapshot.Entries.Count(r=>r.Remark=="same")==2&&snapshot.Entries[0].OccurredAt>=snapshot.Entries[^1].OccurredAt,"preserve real duplicates and order");
});
await Test("Expired history sessions stop without following redirects or submitting credentials", async () =>
{
    int calls=0;using var fake=new HistoryFake(_=>{calls++;return new(HttpStatusCode.Redirect){Headers={Location=new Uri("https://other.test/")}};});
    using var client=new RechargeHistoryClient(new CookieContainer(),fake);bool stopped=false;
    try{await client.LoadAsync(new(2026,11,3),default);}catch(SelfServiceException){stopped=true;}
    Assert(stopped&&calls==1,"no retry or redirect");
});
await Test("History pagination changes cannot silently produce a partial result", async () =>
{
    var today=new DateOnly(2026,11,3);long stamp=new DateTimeOffset(RechargeHistoryPeriod.LastSixMonths(today).Start.ToDateTime(TimeOnly.MinValue),TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
    using var fake=new HistoryFake(uri=>HistoryFake.Query(uri)["pageNumber"]=="1"?HistoryFake.Json(new{total=2,rows=new[]{new object?[]{stamp,"测试",30,1,"first"}}}):HistoryFake.Json(new{total=1,rows=Array.Empty<object?[]>()}));
    using var client=new RechargeHistoryClient(new CookieContainer(),fake);bool stopped=false;
    try{await client.LoadAsync(today,default);}catch(SelfServiceException){stopped=true;}
    Assert(stopped,"changing total must not be claimed complete");
});
await Test("Out-of-range history and missing pages are rejected rather than shown as complete", async () =>
{
    foreach(bool outside in new[]{true,false})
    {
        using var fake=new HistoryFake(_=>HistoryFake.Json(new{total=1,rows=outside?new[]{new object?[]{0,"测试",30,1,"old"}}:Array.Empty<object?[]>()}));
        using var client=new RechargeHistoryClient(new CookieContainer(),fake);bool stopped=false;
        try{await client.LoadAsync(new(2026,11,3),default);}catch(SelfServiceException){stopped=true;}
        Assert(stopped,"incomplete or outside");
    }
});
await Test("History cancellation prevents any HTTP requests", async () =>
{
    int calls=0;using var fake=new HistoryFake(_=>{calls++;return HistoryFake.Json(new{total=0,rows=Array.Empty<object?[]>()});});
    using var client=new RechargeHistoryClient(new CookieContainer(),fake);using var cts=new CancellationTokenSource();cts.Cancel();bool cancelled=false;
    try{await client.LoadAsync(new(2026,11,3),cts.Token);}catch(OperationCanceledException){cancelled=true;}
    Assert(cancelled&&calls==0,"cancelled before I/O");
});

string UpdateJson(bool draft=false,bool prerelease=false,string? digest=null,string? download=null,string tag="v0.4.0")=>JsonSerializer.Serialize(new{draft,prerelease,tag_name=tag,html_url=$"https://github.com/mogavin68/gdut-auto-connect/releases/tag/{tag}",body="Synthetic release notes",assets=new[]{new{name="GdutNetwork.exe",state="uploaded",size=2048,digest=digest??("sha256:"+new string('a',64)),browser_download_url=download??$"https://github.com/mogavin68/gdut-auto-connect/releases/download/{tag}/GdutNetwork.exe"}}});
await Test("Stable update metadata pins the repository and normalizes three-part versions",()=>Sync(()=>
{
    var release=GitHubUpdateClient.ParseLatest(UpdateJson());
    Assert(release.Version==new Version(0,4,0,0)&&release.Size==2048&&release.Sha256==new string('a',64),"release metadata");
    Assert(UpdateRelease.Normalize(new(0,4,0))==UpdateRelease.Normalize(new(0,4,0,0)),"equal current version");
}));
await Test("Drafts prereleases missing hashes and foreign download destinations are rejected",()=>Sync(()=>
{
    foreach(var json in new[]{UpdateJson(draft:true),UpdateJson(prerelease:true),UpdateJson(digest:""),UpdateJson(download:"https://evil.test/GdutNetwork.exe"),UpdateJson(tag:"v0.4.0-beta"),"<html>login</html>"})
    {bool stopped=false;try{GitHubUpdateClient.ParseLatest(json);}catch(UpdateException){stopped=true;}Assert(stopped,"untrusted release");}
}));
await Test("Only verified HTTPS GitHub release CDN redirects are accepted",()=>Sync(()=>
{
    Assert(GitHubUpdateClient.AllowedDownloadRedirect(new("https://release-assets.githubusercontent.com/file?token=synthetic")),"official CDN");
    foreach(var url in new[]{"http://release-assets.githubusercontent.com/file","https://release-assets.githubusercontent.com.evil.test/file","https://evil@release-assets.githubusercontent.com/file","https://release-assets.githubusercontent.com:801/file","https://github.com/login"})
        Assert(!GitHubUpdateClient.AllowedDownloadRedirect(new(url)),"unsafe redirect");
}));
await Test("Update checks send no authorization or campus cookies",async()=>
{
    bool seen=false;using var fake=new UpdateFake(request=>
    {
        Assert(request.Method==HttpMethod.Get&&request.RequestUri==GitHubUpdateClient.LatestApi,"fixed API");
        Assert(!request.Headers.Contains("Authorization")&&!request.Headers.Contains("Cookie"),"no credentials");seen=true;
        return new(HttpStatusCode.OK){Content=new StringContent(UpdateJson())};
    });
    using var client=new GitHubUpdateClient(fake);await client.GetLatestAsync(default);Assert(seen,"request observed");
});
await Test("Unavailable and rate-limited update APIs return actionable safe errors",async()=>
{
    foreach(var code in new[]{HttpStatusCode.NotFound,HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests})
    {
        using var fake=new UpdateFake(_=>new(code));using var client=new GitHubUpdateClient(fake);bool stopped=false;
        try{await client.GetLatestAsync(default);}catch(UpdateException){stopped=true;}Assert(stopped,"HTTP error");
    }
});
await Test("A hash-mismatched update is never accepted as an executable package",async()=>
{
    using var fake=new UpdateFake(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[2048])});
    using var client=new GitHubUpdateClient(fake);bool stopped=false;
    try{await client.DownloadAsync(GitHubUpdateClient.ParseLatest(UpdateJson()),null,default);}catch(UpdateException ex){stopped=ex.Message.Contains("SHA-256");}
    Assert(stopped,"digest mismatch");
});
await Test("Download size mismatch and untrusted redirect stop before installation",async()=>
{
    foreach(bool redirect in new[]{false,true})
    {
        int count=0;using var fake=new UpdateFake(_=>{count++;return redirect?new(HttpStatusCode.Redirect){Headers={Location=new Uri("https://evil.test/file")}}:new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[4096])};});
        using var client=new GitHubUpdateClient(fake);bool stopped=false;
        try{await client.DownloadAsync(GitHubUpdateClient.ParseLatest(UpdateJson()),null,default);}catch(UpdateException){stopped=true;}
        Assert(stopped&&count==1,"invalid download");
    }
});
await Test("Cancelling an in-progress download aborts the stream and yields no install package",async()=>
{
    using var fake=new UpdateFake(_=>{var content=new StreamContent(new BlockingUpdateStream());content.Headers.ContentLength=2048;return new(HttpStatusCode.OK){Content=content};});
    using var client=new GitHubUpdateClient(fake);using var cts=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));bool stopped=false;
    try{await client.DownloadAsync(GitHubUpdateClient.ParseLatest(UpdateJson()),null,cts.Token);}catch(OperationCanceledException){stopped=true;}
    Assert(stopped,"active download cancellation");
});
await Test("Cancelled update checks do not send requests",async()=>
{
    int calls=0;using var fake=new UpdateFake(_=>{calls++;return new(HttpStatusCode.OK){Content=new StringContent(UpdateJson())};});
    using var client=new GitHubUpdateClient(fake);using var cts=new CancellationTokenSource();cts.Cancel();bool stopped=false;
    try{await client.GetLatestAsync(cts.Token);}catch(OperationCanceledException){stopped=true;}Assert(stopped&&calls==0,"cancelled I/O");
});
string UpdateTestDirectory(){string path=Path.Combine(AppContext.BaseDirectory,"update-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
await Test("Verified replacement preserves the previous executable and unrelated settings",()=>Sync(()=>
{
    string dir=UpdateTestDirectory(),target=Path.Combine(dir,"GdutNetwork.exe"),source=Path.Combine(dir,"candidate.exe"),settings=Path.Combine(dir,"settings.json");
    File.WriteAllText(target,"synthetic old");File.WriteAllText(source,"synthetic new");File.WriteAllText(settings,"synthetic settings");
    string backup=UpdateInstallation.ReplaceVerified(source,target,UpdateInstallation.Sha256(target),UpdateInstallation.Sha256(source));
    Assert(File.ReadAllText(target)=="synthetic new"&&File.ReadAllText(backup)=="synthetic old"&&File.ReadAllText(settings)=="synthetic settings","replacement preserves data");
    UpdateInstallation.Restore(target,backup);Assert(File.ReadAllText(target)=="synthetic old","rollback");
}));
await Test("Changed install files and non-tool targets are rejected without modification",()=>Sync(()=>
{
    string dir=UpdateTestDirectory(),target=Path.Combine(dir,"GdutNetwork.exe"),source=Path.Combine(dir,"candidate.exe");File.WriteAllText(target,"old");File.WriteAllText(source,"new");
    bool stopped=false;try{UpdateInstallation.ReplaceVerified(source,target,new string('a',64),UpdateInstallation.Sha256(source));}catch(UpdateException){stopped=true;}
    Assert(stopped&&File.ReadAllText(target)=="old","old hash binding");
    stopped=false;try{UpdateInstallation.ReplaceVerified(source,source,UpdateInstallation.Sha256(source),UpdateInstallation.Sha256(source));}catch(UpdateException){stopped=true;}
    Assert(stopped&&File.ReadAllText(source)=="new","alias protected");
}));
await Test("Rollback can restore a previous executable when the new target is missing",()=>Sync(()=>
{
    string dir=UpdateTestDirectory(),target=Path.Combine(dir,"GdutNetwork.exe"),source=Path.Combine(dir,"candidate.exe");File.WriteAllText(target,"old");File.WriteAllText(source,"new");
    string backup=UpdateInstallation.ReplaceVerified(source,target,UpdateInstallation.Sha256(target),UpdateInstallation.Sha256(source));
    File.Delete(target);UpdateInstallation.Restore(target,backup);Assert(File.ReadAllText(target)=="old","missing target recovery");
}));
await Test("A locked installation cannot corrupt or remove the original executable",()=>Sync(()=>
{
    string dir=UpdateTestDirectory(),target=Path.Combine(dir,"GdutNetwork.exe"),source=Path.Combine(dir,"candidate.exe");File.WriteAllText(target,"old");File.WriteAllText(source,"new");
    string oldHash=UpdateInstallation.Sha256(target),newHash=UpdateInstallation.Sha256(source);bool stopped=false;
    using(var locked=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.None))try{UpdateInstallation.ReplaceVerified(source,target,oldHash,newHash);}catch(IOException){stopped=true;}
    Assert(stopped&&File.ReadAllText(target)=="old"&&!Directory.EnumerateFiles(dir,"*.previous").Any(),"locked target safety");
}));
await Test("An arbitrary downloaded file cannot pass product/version validation",()=>Sync(()=>
{
    string dir=UpdateTestDirectory(),file=Path.Combine(dir,"candidate.exe");File.WriteAllText(file,"synthetic, not a PE");bool stopped=false;
    try{UpdateInstallation.VerifyBinary(file,new(0,4,0));}catch(UpdateException){stopped=true;}Assert(stopped,"product identity");
}));

Console.WriteLine($"{results.Count - failures}/{results.Count} passed");
return failures == 0 ? 0 : 1;

sealed class BlockingUpdateStream : MemoryStream
{
    private bool first=true;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
    { if(first){first=false;buffer.Span[..512].Clear();return 512;}await Task.Delay(Timeout.Infinite,token);return 0; }
}
sealed class UpdateFake(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {token.ThrowIfCancellationRequested();return Task.FromResult(response(request));}
}

sealed class HistoryFake(Func<Uri,HttpResponseMessage> response) : HttpMessageHandler
{
    public static Dictionary<string,string> Query(Uri uri)=>uri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>x[1]);
    public static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),System.Text.Encoding.UTF8,"application/json")};
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(request.Method!=HttpMethod.Get||request.RequestUri is not {} uri||uri.Scheme!="https"||uri.Host!="selfmanager.gdut.edu.cn"||uri.AbsolutePath!="/bill/getPayMent"||request.Headers.Referrer?.AbsolutePath!="/bill/payMent")throw new InvalidOperationException("Unexpected history request");
        return Task.FromResult(response(uri));
    }
}

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
