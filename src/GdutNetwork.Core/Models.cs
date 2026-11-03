namespace GdutNetwork.Core;

public enum PortalState { Unknown, LoginRequired, SessionIndicated, CaptchaRequired }
public enum ProbeOutcome { ExpectedResponse, PortalResponse, Redirect, UnexpectedResponse, Timeout, Failed }
public enum NetworkState { Unknown, WifiUnavailable, WifiDisconnected, OtherWifi, NeedsAuthentication, NeedsAttention, SessionWithoutInternet, InternetReachable, InternetRouteUncertain }

public sealed record WifiInfo(string AdapterName, int InterfaceIndex, string State, string? Ssid, int? ErrorCode = null, bool? RadioOn = null);
public sealed record AdapterInfo(string Name, string Type, string State, int InterfaceIndex, bool HasIpv4, bool HasIpv6);
public sealed record NetworkSnapshot(IReadOnlyList<WifiInfo> Wifi, IReadOnlyList<AdapterInfo> Adapters, string? Warning = null)
{
    public WifiInfo? Target => Wifi.FirstOrDefault(w => w.State == "Connected" && w.Ssid == "gdut");
}

public sealed record RouteObservation(string? InterfaceName, int? InterfaceIndex, bool? MatchesTarget, string? SourceAdapter = null);
public sealed record PortalEvidence(PortalState State, bool VendorMarker, bool PasswordField, bool UsernameField);
public sealed record ProbeResult(string Name, string Endpoint, ProbeOutcome Outcome, int? HttpStatus, PortalEvidence? Portal,
    string? RedirectTarget, RouteObservation? Route, string? Error, long ElapsedMilliseconds);
public sealed record Assessment(NetworkState State, string Summary, string Explanation);
public sealed record DiagnosticReport(DateTimeOffset CheckedAt, long ElapsedMilliseconds, string Mode,
    NetworkSnapshot Network, IReadOnlyList<ProbeResult> Probes, Assessment Assessment, bool NetworkChanged = false);

public sealed record ProbeSpec(string Name, Uri Endpoint, bool IsPortal, string? ExactContent = null, string? ContentPrefix = null)
{
    public static IReadOnlyList<ProbeSpec> Defaults { get; } =
    [
        new("主要认证入口", new("http://10.0.3.2/"), true),
        new("辅助认证入口", new("http://9.9.9.9/"), true),
        new("Microsoft 联网探测", new("http://www.msftconnecttest.com/connecttest.txt"), false, ExactContent: "Microsoft Connect Test"),
        new("百度 HTTPS 探测", new("https://www.baidu.com/robots.txt"), false, ContentPrefix: "User-agent:")
    ];
}
