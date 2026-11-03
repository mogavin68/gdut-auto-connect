using GdutNetwork.Core;

namespace GdutNetwork.Windows;

public sealed class RecoveryDiagnostics : IDiagnosticSource
{
    private readonly DiagnosticService service = new();
    public async Task<DiagnosticReport> CaptureAsync(CancellationToken token) => await service.RunAsync(token).ConfigureAwait(false)
        ?? throw new InvalidOperationException("A diagnostic round is already active");
}

public sealed class RecoveryActions(CredentialStore credentials, NativeWifiController wifi) : IRecoveryActions
{
    public bool HasCredentials => credentials.Exists;
    public Task<bool> ConnectGdutAsync(CancellationToken token) => wifi.ConnectAsync(token);
    public Task<AuthResult> AuthenticateAsync(AutomationSettings settings, CancellationToken token)
        => new PortalAuthenticator(credentials.Read, () => new WindowsNetworkReader().Read().Target is not null).AuthenticateAsync(settings, token);
}
