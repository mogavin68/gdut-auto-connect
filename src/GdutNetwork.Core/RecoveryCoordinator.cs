namespace GdutNetwork.Core;

public sealed class RecoveryCoordinator(IDiagnosticSource diagnostics, IRecoveryActions actions, ISettingsStore store)
{
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? current;
    private long generation;
    private AutomationSettings settings = store.Load();
    public AutomationSettings Settings { get { lock (sync) return settings; } }
    public event Action<RecoveryUpdate>? Updated;

    public void Resume()
    {
        lock (sync)
        {
            var next = settings with { Enabled = true, Pause = PauseReason.None };
            store.Save(next);
            settings = next;
            generation++;
        }
        Publish("自动恢复已开启", null);
    }

    public void Pause(PauseReason reason)
    {
        lock (sync)
        {
            generation++;
            current?.Cancel();
            settings = settings with { Enabled = false, Pause = reason };
            store.Save(settings);
        }
        Publish(PauseText(reason), null);
    }

    public void InterruptCurrent()
    {
        lock (sync) { generation++; current?.Cancel(); }
    }

    public void UpdateOptions(bool allowHttp, bool confirmBilling, bool? startWithWindows = null)
    {
        lock (sync)
        {
            generation++;
            current?.Cancel();
            var next = settings with { AllowHttpAuthentication = allowHttp, AutoConfirmBillingCycle = confirmBilling,
                StartWithWindows = startWithWindows ?? settings.StartWithWindows };
            store.Save(next);
            settings = next;
        }
    }

    public async Task RunRoundAsync(bool recovery, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        long round;
        lock (sync) { round = generation; current = cancellation; }
        DiagnosticReport? report = null;
        bool Active() { lock (sync) return round == generation && settings.Enabled && !cancellation.IsCancellationRequested; }
        try
        {
            report = await diagnostics.CaptureAsync(cancellation.Token).ConfigureAwait(false);
            if (!recovery || !Active()) { Publish(report.Assessment.Summary, report); return; }
            var action = RecoveryPolicy.Decide(report);
            if (action == RecoveryAction.PauseRadioOff) { Pause(PauseReason.RadioOff); return; }
            if (action == RecoveryAction.PauseCaptcha) { Pause(PauseReason.Captcha); return; }
            if (action == RecoveryAction.ConnectGdut)
            {
                if (!Active()) return;
                Publish("正在尝试连接 gdut…", report);
                if (!await actions.ConnectGdutAsync(cancellation.Token).ConfigureAwait(false))
                { Publish("本轮未连接成功，60 秒周期继续检测", report); return; }
                if (!Active()) return;
                report = await diagnostics.CaptureAsync(cancellation.Token).ConfigureAwait(false);
                action = RecoveryPolicy.Decide(report);
            }
            if (action == RecoveryAction.PauseRadioOff) { Pause(PauseReason.RadioOff); return; }
            if (action == RecoveryAction.PauseCaptcha) { Pause(PauseReason.Captcha); return; }
            if (action != RecoveryAction.Authenticate) { Publish(report.Assessment.Summary, report); return; }
            if (!Active()) return;
            if (!actions.HasCredentials) { Pause(PauseReason.MissingCredentials); return; }
            Publish("正在检查认证并按需登录…", report);
            var result = await actions.AuthenticateAsync(Settings, cancellation.Token).ConfigureAwait(false);
            if (!Active()) return;
            var pause = result.Outcome switch
            {
                AuthOutcome.BadCredentials => PauseReason.BadCredentials,
                AuthOutcome.Captcha => PauseReason.Captcha,
                AuthOutcome.AccountProblem => PauseReason.AccountProblem,
                AuthOutcome.BillingConfirmation => PauseReason.BillingConfirmation,
                AuthOutcome.UnsupportedConfiguration => PauseReason.Configuration,
                _ => PauseReason.None
            };
            if (pause != PauseReason.None) { Pause(pause); return; }
            // A timeout may still have logged in successfully; always recheck before the next attempt.
            report = await diagnostics.CaptureAsync(cancellation.Token).ConfigureAwait(false);
            if (Active()) Publish(report.Assessment.Summary + " · " + result.Message, report);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Http.HttpRequestException)
        { Publish("本轮未完成，将在下一检测周期重试", report); }
        finally
        {
            lock (sync) { if (ReferenceEquals(current, cancellation)) current = null; }
            gate.Release();
        }
    }

    private void Publish(string text, DiagnosticReport? report) => Updated?.Invoke(new(text, report, Settings));
    public static string PauseText(PauseReason reason) => reason switch
    {
        PauseReason.User => "已手动暂停自动恢复", PauseReason.RadioOff => "Wi-Fi 已关闭，自动恢复已暂停",
        PauseReason.ManualDisconnect => "检测到主动断开，自动恢复已暂停", PauseReason.BadCredentials => "账号或密码错误，请修改后继续",
        PauseReason.Captcha => "需要验证码，请手动认证后继续", PauseReason.AccountProblem => "账号状态异常，请处理后继续",
        PauseReason.BillingConfirmation => "需要确认新计费周期，请手动处理后继续",
        PauseReason.MissingCredentials => "请先在账号设置中保存凭据，再点击继续",
        PauseReason.Configuration => "认证配置变化或尚未核实，请先检查后继续", _ => "自动恢复未开启"
    };
}
