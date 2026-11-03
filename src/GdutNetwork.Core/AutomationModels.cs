using System.Text.Json.Serialization;

namespace GdutNetwork.Core;

public enum PauseReason { None, User, ManualDisconnect, RadioOff, BadCredentials, Captcha, AccountProblem, BillingConfirmation, MissingCredentials, Configuration }
public enum AuthOutcome { SubmittedSuccessfully, AlreadyAuthenticated, BadCredentials, Captcha, AccountProblem, BillingConfirmation, UnsupportedConfiguration, TransientFailure }
public enum RecoveryAction { None, ConnectGdut, Authenticate, PauseRadioOff, PauseCaptcha }

public sealed record AutomationSettings
{
    public int Version { get; init; } = 1;
    public bool Enabled { get; init; }
    public PauseReason Pause { get; init; } = PauseReason.User;
    public bool AllowHttpAuthentication { get; init; } = true;
    public bool AutoConfirmBillingCycle { get; init; } = true;
    public bool StartWithWindows { get; init; } = true;
}

public sealed class LoginCredential(string userName, string password)
{
    [JsonIgnore] public string UserName { get; } = userName;
    [JsonIgnore] public string Password { get; } = password;
    public override string ToString() => "[校园网凭据]";
}

public sealed record AuthResult(AuthOutcome Outcome, string Message);
public sealed record RecoveryUpdate(string Status, DiagnosticReport? Report, AutomationSettings Settings);

public interface IDiagnosticSource { Task<DiagnosticReport> CaptureAsync(CancellationToken token); }
public interface IRecoveryActions
{
    bool HasCredentials { get; }
    Task<bool> ConnectGdutAsync(CancellationToken token);
    Task<AuthResult> AuthenticateAsync(AutomationSettings settings, CancellationToken token);
}
public interface ISettingsStore
{
    AutomationSettings Load();
    void Save(AutomationSettings settings);
}

public static class RecoveryPolicy
{
    public static RecoveryAction Decide(DiagnosticReport report)
    {
        if (report.NetworkChanged) return RecoveryAction.None;
        if (report.Network.Wifi.Any(w => w.RadioOn == false)) return RecoveryAction.PauseRadioOff;
        if (report.Probes.Any(p => p.Outcome == ProbeOutcome.ExpectedResponse)) return RecoveryAction.None;
        if (report.Network.Warning is not null || report.Network.Wifi.Any(w => w.ErrorCode is not null)) return RecoveryAction.None;
        if (report.Network.Target is null)
        {
            if (report.Network.Wifi.Any(w => w.State == "Connected"))
            {
                var external = report.Probes.Where(p => p.Name is "Microsoft 联网探测" or "百度 HTTPS 探测").ToArray();
                bool limited = external.Length >= 2 && (external.All(p => p.Outcome is ProbeOutcome.Failed or ProbeOutcome.Timeout)
                    || external.Any(p => p.Outcome == ProbeOutcome.Redirect || p.Portal?.State == PortalState.LoginRequired));
                return limited ? RecoveryAction.ConnectGdut : RecoveryAction.None;
            }
            return report.Network.Wifi.Count > 0 ? RecoveryAction.ConnectGdut : RecoveryAction.None;
        }
        if (report.Probes.Any(p => p.Portal?.State == PortalState.CaptchaRequired)) return RecoveryAction.PauseCaptcha;
        if (report.Probes.Any(p => p.Portal?.State == PortalState.SessionIndicated)) return RecoveryAction.None;
        return RecoveryAction.Authenticate;
    }
}
