using System.Net;
using System.Text;
using System.Text.Json;
using GdutNetwork.Core;

var results = new List<object>();
int failures = 0;
const string login = "<!--Dr.COMWebLoginID_1.htm--><title>上网认证</title><input name='DDDDD'><input type='password' name='upass'>";
const string session = "<!--Dr.COMWebLoginID_1.htm--><title>注销页</title><script>let login=true,captcha=true;</script>";
var wifi = new NetworkSnapshot([new("WLAN", 12, "Connected", "gdut")], []);
ProbeResult Portal(PortalState state, string name = "主要认证入口") => new(name, "http://10.0.3.2/", ProbeOutcome.PortalResponse, 200, new(state, true, false, false), null, null, null, 1);
ProbeResult External(bool? match, ProbeOutcome outcome = ProbeOutcome.ExpectedResponse) => new("Microsoft 联网探测", "http://www.msftconnecttest.com/", outcome, 200, null, null, new("test", 12, match), null, 1);

void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Test(string name, Func<Task> action)
{
    try { await action(); results.Add(new { Name = name, Passed = true }); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; results.Add(new { Name = name, Passed = false, Error = ex.Message }); Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Task Sync(Action action) { action(); return Task.CompletedTask; }

await Test("Live logout title is a session indication, not login success", () => Sync(() => Assert(PortalClassifier.Classify(session).State == PortalState.SessionIndicated, "session state")));
await Test("All Dr.COM marker numbers in comments do not imply a live login form", () => Sync(() => Assert(PortalClassifier.Classify("<!--Dr.COMWebLoginID_1.htm Dr.COMWebLoginID_2.htm Dr.COMWebLoginID_3.htm-->").State == PortalState.Unknown, "comment is not state")));
await Test("Static script strings cannot trigger a CAPTCHA or login state", () => Sync(() => Assert(PortalClassifier.Classify("<script>let s=\"验证码 <input type='password' name='account'>\";</script>").State == PortalState.Unknown, "script is not UI")));
await Test("Recognized visible credential form indicates login required", () => Sync(() => Assert(PortalClassifier.Classify(login).State == PortalState.LoginRequired, "login form")));
await Test("Unknown vendor form is not treated as a trusted login form", () => Sync(() => Assert(PortalClassifier.Classify("<input name='account'><input type='password'>").State == PortalState.Unknown, "unknown vendor")));
await Test("Hidden password input does not trigger login", () => Sync(() => Assert(PortalClassifier.Classify("drcom <input name='account'><input type='hidden' name='password'>").State == PortalState.Unknown, "hidden field")));
await Test("Disabled password input does not trigger login", () => Sync(() => Assert(PortalClassifier.Classify("drcom <input name='account'><input type='password' disabled>").State == PortalState.Unknown, "disabled field")));
await Test("Duplicate attributes cannot crash portal parsing", () => Sync(() => Assert(PortalClassifier.Classify("drcom <input name='account' name='other'><input type='password'>").State == PortalState.LoginRequired, "duplicate attrs")));
await Test("CAPTCHA form requests human attention", () => Sync(() => Assert(PortalClassifier.Classify(login + "<input name='captcha'>").State == PortalState.CaptchaRequired, "captcha state")));
await Test("Conflicting login and session evidence on one page stays unknown", () => Sync(() => Assert(PortalClassifier.Classify(login.Replace("上网认证", "注销页")).State == PortalState.Unknown, "conflict")));
await Test("Session without a successful external probe never means online", () => Sync(() => Assert(NetworkEvaluator.Evaluate(wifi, [Portal(PortalState.SessionIndicated)]).State == NetworkState.SessionWithoutInternet, "false online")));
await Test("Conflicting portal entrances stay unknown", () => Sync(() => Assert(NetworkEvaluator.Evaluate(wifi, [Portal(PortalState.SessionIndicated), Portal(PortalState.LoginRequired, "辅助认证入口")]).State == NetworkState.Unknown, "entrance conflict")));
await Test("VPN route success does not prove gdut recovery", () => Sync(() => Assert(NetworkEvaluator.Evaluate(wifi, [External(false)]).State == NetworkState.InternetRouteUncertain, "VPN route")));
await Test("Missing route information does not prove gdut recovery", () => Sync(() => Assert(NetworkEvaluator.Evaluate(wifi, [External(null)]).State == NetworkState.InternetRouteUncertain, "unknown route")));
await Test("Verified target route and expected response mean probe reachability", () => Sync(() => Assert(NetworkEvaluator.Evaluate(wifi, [External(true)]).State == NetworkState.InternetReachable, "target route")));
await Test("Other online Wi-Fi is retained", () => Sync(() => Assert(NetworkEvaluator.Evaluate(new([new("WLAN", 12, "Connected", "Other")], []), [External(false)]).State == NetworkState.InternetReachable, "other online wifi")));
await Test("Wi-Fi permission failures stay unknown", () => Sync(() => Assert(NetworkEvaluator.Evaluate(new([new("WLAN", 12, "Connected", null, 5)], []), []).State == NetworkState.Unknown, "permission")));
await Test("Plain access errors cannot be called a bad password", () => Sync(() => Assert(NetworkEvaluator.Evaluate(wifi, [External(false, ProbeOutcome.Failed)]).State == NetworkState.Unknown, "transport != bad password")));
await Test("Endpoint redaction removes userinfo, path, query and fragment", () => Sync(() => Assert(SafeEndpoint.Display(new("https://user:password@example.com/account/123?token=secret#secret")) == "https://example.com/", "redaction")));
await Test("Non-web redirect targets are not exposed", () => Sync(() => Assert(SafeEndpoint.Display(new("file:///C:/secret")) == "[非 HTTP(S) 地址]", "scheme")));

await Test("Probe only sends GET and matches the exact expected content", async () =>
{
    using var handler = new FakeHandler((request, _) =>
    {
        Assert(request.Method == HttpMethod.Get, "not GET");
        Assert(!request.Headers.Contains("Cookie") && request.Content is null, "credentials sent");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Microsoft Connect Test") });
    });
    using var client = new HttpClient(handler);
    var result = await HttpProbe.RunAsync(client, ProbeSpec.Defaults[2], () => null, TimeSpan.FromSeconds(1), default);
    Assert(result.Outcome == ProbeOutcome.ExpectedResponse && handler.Count == 1, "GET result");
});
await Test("Redirect is reported without following it or exporting its token", async () =>
{
    using var handler = new FakeHandler((_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri("http://10.0.3.2/private-account?token=secret");
        return Task.FromResult(response);
    });
    using var client = new HttpClient(handler);
    var result = await HttpProbe.RunAsync(client, ProbeSpec.Defaults[0], () => null, TimeSpan.FromSeconds(1), default);
    Assert(result.Outcome == ProbeOutcome.Redirect && handler.Count == 1 && result.RedirectTarget == "http://10.0.3.2/", "redirect safety");
});
await Test("Wrong body with HTTP 200 does not mean internet access", async () =>
{
    using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(login) }));
    using var client = new HttpClient(handler);
    var result = await HttpProbe.RunAsync(client, ProbeSpec.Defaults[2], () => null, TimeSpan.FromSeconds(1), default);
    Assert(result.Outcome == ProbeOutcome.UnexpectedResponse && result.Portal?.State == PortalState.LoginRequired, "body mismatch");
    Assert(!JsonSerializer.Serialize(result).Contains("upass"), "raw body exported");
});
await Test("Timeouts are distinct from user cancellation", async () =>
{
    using var handler = new FakeHandler(async (_, token) => { await Task.Delay(5000, token); return new HttpResponseMessage(HttpStatusCode.OK); });
    using var client = new HttpClient(handler);
    var result = await HttpProbe.RunAsync(client, ProbeSpec.Defaults[0], () => null, TimeSpan.FromMilliseconds(20), default);
    Assert(result.Outcome == ProbeOutcome.Timeout, "timeout");
});
await Test("Cancellation propagates and cannot trigger later recovery", async () =>
{
    using var handler = new FakeHandler(async (_, token) => { await Task.Delay(5000, token); return new HttpResponseMessage(HttpStatusCode.OK); });
    using var client = new HttpClient(handler);
    using var cancel = new CancellationTokenSource(20);
    bool canceled = false;
    try { await HttpProbe.RunAsync(client, ProbeSpec.Defaults[0], () => null, TimeSpan.FromSeconds(2), cancel.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Assert(canceled, "cancellation swallowed");
});
await Test("Oversized responses are bounded", async () =>
{
    using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 300_000)) }));
    using var client = new HttpClient(handler);
    var result = await HttpProbe.RunAsync(client, ProbeSpec.Defaults[0], () => null, TimeSpan.FromSeconds(1), default);
    Assert(result.Error == "ResponseTooLarge", "body cap");
});
await Test("GBK portal text is decoded correctly", async () =>
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(session, Encoding.GetEncoding("gbk"), "text/html") }));
    using var client = new HttpClient(handler);
    var result = await HttpProbe.RunAsync(client, ProbeSpec.Defaults[0], () => null, TimeSpan.FromSeconds(1), default);
    Assert(result.Portal?.State == PortalState.SessionIndicated, "GBK decode");
});

await Test("Hidden CAPTCHA input does not force manual verification", () => Sync(() => Assert(PortalClassifier.Classify(login + "<input name='captcha' style='display: none'>").State == PortalState.LoginRequired, "hidden CAPTCHA")));
await Test("Credential objects do not serialize or print secrets", () => Sync(() =>
{
    var value = new LoginCredential("sample-account", "sample-secret");
    Assert(!JsonSerializer.Serialize(value).Contains("sample-") && !value.ToString().Contains("sample-"), "credential leakage");
}));
await Test("Static JS literals are decoded without evaluation", () => Sync(() => Assert(PortalProtocol.Scalar("var bodyContent='a\\\'b\\n\\u4E2D\\x41';", "bodyContent") == "a'b\n中A", "literal decode")));
await Test("JSONP parser accepts the fixed callback and rejects executable wrappers", () => Sync(() =>
{
    using var parsed = PortalProtocol.ParseJsonp("gdut_callback({\"result\":1});");
    Assert(PortalProtocol.Value(parsed.RootElement, "result") == "1", "callback parse");
    bool rejected = false;
    try { using var ignored = PortalProtocol.ParseJsonp("evil({\"result\":1});other()"); } catch (FormatException) { rejected = true; }
    Assert(rejected, "arbitrary code accepted");
}));
await Test("Billing auto-confirm is enabled by default and can be disabled", () => Sync(() =>
{
    var saved = new MemoryStore(); var engine = new RecoveryCoordinator(new ReportSource(wifi), new FakeActions(), saved);
    Assert(engine.Settings.AutoConfirmBillingCycle, "default billing"); engine.UpdateOptions(true, false);
    Assert(!saved.Value.AutoConfirmBillingCycle, "billing option persistence");
}));
await Test("Online target network never calls recovery actions", async () =>
{
    var source = new ReportSource(wifi, [External(true)]); var actions = new FakeActions();
    var engine = new RecoveryCoordinator(source, actions, MemoryStore.Active());
    await engine.RunRoundAsync(true, default); Assert(actions.AuthCalls == 0 && actions.ConnectCalls == 0, "online action");
});
await Test("VPN internet success never triggers a speculative login", async () =>
{
    var actions = new FakeActions(); var engine = new RecoveryCoordinator(new ReportSource(wifi, [External(false)]), actions, MemoryStore.Active());
    await engine.RunRoundAsync(true, default); Assert(actions.AuthCalls == 0 && actions.ConnectCalls == 0, "VPN speculative action");
});
await Test("Already-authenticated session is preserved when probes fail", async () =>
{
    var actions = new FakeActions(); var engine = new RecoveryCoordinator(new ReportSource(wifi, [Portal(PortalState.SessionIndicated)]), actions, MemoryStore.Active());
    await engine.RunRoundAsync(true, default); Assert(actions.AuthCalls == 0, "session resubmitted");
});
await Test("Radio off pauses persistently without attempting connection", async () =>
{
    var source = new ReportSource(new([new("WLAN", 12, "Disconnected", null, RadioOn: false)], []));
    var actions = new FakeActions(); var store = MemoryStore.Active(); var engine = new RecoveryCoordinator(source, actions, store);
    await engine.RunRoundAsync(true, default);
    Assert(store.Value.Pause == PauseReason.RadioOff && !store.Value.Enabled && actions.ConnectCalls == 0, "radio handling");
});
await Test("Missing credentials pause before any authentication", async () =>
{
    var actions = new FakeActions { CredentialsPresent = false }; var store = MemoryStore.Active();
    var engine = new RecoveryCoordinator(new ReportSource(wifi), actions, store); await engine.RunRoundAsync(true, default);
    Assert(actions.AuthCalls == 0 && store.Value.Pause == PauseReason.MissingCredentials, "missing credential handling");
});
await Test("Other Wi-Fi is not switched based only on missing or ambiguous probes", () => Sync(() =>
{
    var snapshot = new NetworkSnapshot([new("WLAN", 12, "Connected", "Other")], []);
    var report = new DiagnosticReport(DateTimeOffset.Now, 1, "Test", snapshot, [External(false, ProbeOutcome.UnexpectedResponse)], new(NetworkState.Unknown, "unknown", "test"));
    Assert(RecoveryPolicy.Decide(report) == RecoveryAction.None, "speculative network switch");
}));
await Test("Other Wi-Fi with two failed external probes may switch to gdut", () => Sync(() =>
{
    var snapshot = new NetworkSnapshot([new("WLAN", 12, "Connected", "Other")], []);
    var first = External(false, ProbeOutcome.Timeout); var second = first with { Name = "百度 HTTPS 探测" };
    var report = new DiagnosticReport(DateTimeOffset.Now, 1, "Test", snapshot, [first, second], new(NetworkState.OtherWifi, "limited", "test"));
    Assert(RecoveryPolicy.Decide(report) == RecoveryAction.ConnectGdut, "unavailable network switch");
}));
await Test("A changed network invalidates even a disconnected snapshot", () => Sync(() =>
{
    var snapshot = new NetworkSnapshot([new("WLAN", 12, "Disconnected", null)], []);
    var report = new DiagnosticReport(DateTimeOffset.Now, 1, "Test", snapshot, [], new(NetworkState.Unknown, "changed", "test"), NetworkChanged: true);
    Assert(RecoveryPolicy.Decide(report) == RecoveryAction.None, "stale network action");
}));
foreach (var pair in new[] { (AuthOutcome.BadCredentials, PauseReason.BadCredentials), (AuthOutcome.Captcha, PauseReason.Captcha), (AuthOutcome.AccountProblem, PauseReason.AccountProblem), (AuthOutcome.BillingConfirmation, PauseReason.BillingConfirmation), (AuthOutcome.UnsupportedConfiguration, PauseReason.Configuration) })
    await Test($"Authentication {pair.Item1} pauses and remains paused after coordinator restart", async () =>
    {
        var actions = new FakeActions { Result = new(pair.Item1, "test") }; var store = MemoryStore.Active(); var source = new ReportSource(wifi);
        var engine = new RecoveryCoordinator(source, actions, store); await engine.RunRoundAsync(true, default);
        var restarted = new RecoveryCoordinator(source, actions, store);
        Assert(!restarted.Settings.Enabled && restarted.Settings.Pause == pair.Item2 && actions.AuthCalls == 1, "pause persistence");
    });
await Test("A timed-out login is rechecked without a second submission in the round", async () =>
{
    var actions = new FakeActions { Result = new(AuthOutcome.TransientFailure, "timeout") }; var source = new ReportSource(wifi);
    var engine = new RecoveryCoordinator(source, actions, MemoryStore.Active()); await engine.RunRoundAsync(true, default);
    Assert(actions.AuthCalls == 1 && source.Calls == 2, "post-timeout verification");
});
await Test("Failed connection does not proceed to authentication", async () =>
{
    var actions = new FakeActions { ConnectResult = false }; var source = new ReportSource(new([new("WLAN", 12, "Disconnected", null)], []));
    var engine = new RecoveryCoordinator(source, actions, MemoryStore.Active()); await engine.RunRoundAsync(true, default);
    Assert(actions.ConnectCalls == 1 && actions.AuthCalls == 0, "connect failure");
});
await Test("Pause during network detection cancels subsequent authentication", async () =>
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var source = new ReportSource(wifi) { BeforeCapture = async token => { started.TrySetResult(); await Task.Delay(5000, token); } };
    var actions = new FakeActions(); var store = MemoryStore.Active(); var engine = new RecoveryCoordinator(source, actions, store);
    var round = engine.RunRoundAsync(true, default); await started.Task; engine.Pause(PauseReason.User); await round;
    Assert(actions.AuthCalls == 0 && !store.Value.Enabled, "pause race");
});
await Test("Overlapping triggers cannot start duplicate rounds", async () =>
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var source = new ReportSource(wifi) { BeforeCapture = async token => { started.TrySetResult(); await release.Task.WaitAsync(token); } };
    var actions = new FakeActions(); var engine = new RecoveryCoordinator(source, actions, MemoryStore.Active());
    var first = engine.RunRoundAsync(true, default); await started.Task; await engine.RunRoundAsync(true, default); release.TrySetResult(); await first;
    Assert(actions.AuthCalls == 1 && source.Calls == 2, "overlap");
});

if (args.Length == 1) File.WriteAllText(Path.GetFullPath(args[0]), JsonSerializer.Serialize(new { Total = results.Count, Failed = failures, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed");
return failures == 0 ? 0 : 1;

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Count++;
        return send(request, cancellationToken);
    }
}

sealed class MemoryStore : ISettingsStore
{
    public AutomationSettings Value = new();
    public AutomationSettings Load() => Value;
    public void Save(AutomationSettings value) => Value = value;
    public static MemoryStore Active() => new() { Value = new() { Enabled = true, Pause = PauseReason.None } };
}
sealed class FakeActions : IRecoveryActions
{
    public bool CredentialsPresent = true;
    public bool HasCredentials => CredentialsPresent;
    public bool ConnectResult = true;
    public int ConnectCalls, AuthCalls;
    public AuthResult Result = new(AuthOutcome.SubmittedSuccessfully, "test");
    public Task<bool> ConnectGdutAsync(CancellationToken token) { ConnectCalls++; token.ThrowIfCancellationRequested(); return Task.FromResult(ConnectResult); }
    public Task<AuthResult> AuthenticateAsync(AutomationSettings settings, CancellationToken token) { AuthCalls++; token.ThrowIfCancellationRequested(); return Task.FromResult(Result); }
}
sealed class ReportSource(NetworkSnapshot network, IReadOnlyList<ProbeResult>? probes = null) : IDiagnosticSource
{
    public int Calls;
    public Func<CancellationToken, Task>? BeforeCapture;
    public async Task<DiagnosticReport> CaptureAsync(CancellationToken token)
    {
        Calls++;
        if (BeforeCapture is not null) await BeforeCapture(token);
        var results = probes ?? [];
        return new(DateTimeOffset.Now, 1, "Test", network, results, NetworkEvaluator.Evaluate(network, results));
    }
}
