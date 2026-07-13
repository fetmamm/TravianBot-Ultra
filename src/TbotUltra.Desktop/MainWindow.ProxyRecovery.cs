using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private const int AutomaticProxyRecoveryFailureThreshold = 3;
    private int _automaticProxyRecoveryScheduled;
    private int _automaticProxyRecoveryRetryAttempt;
    private DateTimeOffset _automaticProxyRecoveryRetryAtUtc;

    private bool TryScheduleAutomaticProxyRecovery(BotOptions options)
    {
        if (_consecutiveTransientConnectionFailures < AutomaticProxyRecoveryFailureThreshold
            || DateTimeOffset.UtcNow < _automaticProxyRecoveryRetryAtUtc
            || Interlocked.CompareExchange(ref _automaticProxyRecoveryScheduled, 1, 0) != 0)
        {
            return false;
        }

        var account = FindAccount(_accountStore.ActiveAccountName());
        if (account?.ProxyEnabled != true
            || string.IsNullOrWhiteSpace(account.ProxyServer)
            || !ProxyParser.TryBuild(account.ProxyServer, out _, out _))
        {
            Interlocked.Exchange(ref _automaticProxyRecoveryScheduled, 0);
            return false;
        }

        AppendLog(
            $"[proxy-recovery] {AutomaticProxyRecoveryFailureThreshold} consecutive navigation failures; "
            + "stopping the loop to verify the active proxy and find a safe replacement.");
        _ = Dispatcher.BeginInvoke(new Action(() =>
            _ = GuardUiAsync(() => RecoverFailedProxyAsync(CloneAccount(account), options))));
        return true;
    }

    private async Task RecoverFailedProxyAsync(AccountEntry account, BotOptions previousOptions)
    {
        try
        {
            var endingLoop = _loopTask;
            if (endingLoop is not null && !endingLoop.IsCompleted)
            {
                try
                {
                    await endingLoop.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    AppendLog("[proxy-recovery] loop shutdown wait timed out; continuing with controlled session stop.");
                }
            }

            using var recoveryCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var libraryStore = new ProxyLibraryStore();
            var library = libraryStore.Load();
            IReadOnlyList<string>? plannedProxyIds = null;
            var plan = ProxyPlanStore.LoadActive(account.Name);
            if (plan?.Enabled == true)
            {
                var runtime = ProxyPlanStore.LoadRuntime(account.Name);
                var scheduled = AccountProxyPlanResolver.Resolve(plan, account.Name, DateTimeOffset.Now, runtime.ActiveProxyId);
                plannedProxyIds = plan.Assignments
                    .Select(item => item.ProxyId)
                    .OrderByDescending(id => string.Equals(id, scheduled.ProxyId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            var recoveryService = new ProxyFailoverService(new ProxyListTester(log: AppendLog));
            var result = await recoveryService.FindRecoveryAsync(
                account,
                library,
                account.ServerUrl,
                AppendLog,
                recoveryCts.Token,
                plannedProxyIds);
            libraryStore.Save(library);
            AppendLog($"[proxy-recovery] {result.Message}");

            if (result.Kind == ProxyFailoverKind.CurrentProxyHealthy)
            {
                MarkNetworkConnectionHealthy();
                StatusTextBlock.Text = "Proxy recovered; resuming automation.";
                StartContinuousLoopRunner();
                return;
            }

            if (result.Kind == ProxyFailoverKind.RetryLater)
            {
                ScheduleAutomaticProxyRecoveryRetry(result.Message);
                return;
            }

            if (result.Kind == ProxyFailoverKind.Unavailable)
            {
                ResetAutomaticProxyRecoveryRetry();
                StatusTextBlock.Text = "Proxy unavailable. Automation stopped.";
                AppendLog($"[ALARM] [proxy-recovery] {result.Message} Automation remains stopped for safety.");
                return;
            }

            var changedAccount = CloneAccount(account);
            if (result.Kind == ProxyFailoverKind.ReplacementProxy && result.Proxy is not null)
            {
                changedAccount.ProxyEnabled = true;
                changedAccount.ProxyServer = result.Proxy.Server;
                if (plan?.Enabled == true)
                {
                    var runtime = ProxyPlanStore.LoadRuntime(account.Name);
                    runtime.ActiveProxyId = result.Proxy.Id;
                    runtime.LastSuccessfulProxyId = result.Proxy.Id;
                    runtime.ActivatedAtUtc = DateTimeOffset.UtcNow;
                    ProxyPlanStore.SaveRuntime(account.Name, runtime);
                }
                AppendLog($"[proxy-recovery] switching to {ProxyParser.MaskForLog(result.Proxy.Server)}.");
            }
            else
            {
                changedAccount.ProxyEnabled = false;
                AppendLog("[proxy-recovery] no replacement proxy passed; switching to the allowed direct connection.");
            }

            MarkNetworkConnectionHealthy();
            _pendingProxyChangeAtSleep = null;
            var recovered = await ApplyProxyChangeWithImmediateReloginAsync(
                changedAccount,
                previousOptions,
                resumeContinuousLoopOverride: true);
            if (!recovered)
            {
                StatusTextBlock.Text = "Proxy recovery failed. Automation stopped.";
                AppendLog("[ALARM] [proxy-recovery] controlled relogin failed; automation remains stopped.");
            }
        }
        catch (OperationCanceledException)
        {
            ScheduleAutomaticProxyRecoveryRetry("The proxy checks timed out before a safe decision could be made.");
        }
        catch (Exception ex)
        {
            AppendLog($"[ALARM] [proxy-recovery] recovery failed: {ex.Message}");
            StatusTextBlock.Text = "Proxy recovery failed. Automation stopped.";
        }
        finally
        {
            Interlocked.Exchange(ref _automaticProxyRecoveryScheduled, 0);
        }
    }

    private void ScheduleAutomaticProxyRecoveryRetry(string reason)
    {
        if (_loopController.IsClosing)
        {
            return;
        }

        _automaticProxyRecoveryRetryAttempt++;
        var retryDelay = ResolveAutomaticProxyRecoveryRetryDelay(_automaticProxyRecoveryRetryAttempt);
        _automaticProxyRecoveryRetryAtUtc = DateTimeOffset.UtcNow + retryDelay;
        MarkTransientNetworkUnavailable(retryDelay);
        StatusTextBlock.Text = $"Connection unavailable. Retrying in {retryDelay.TotalMinutes:F0} min.";
        AppendLog(
            $"[proxy-recovery] {reason} Retry {_automaticProxyRecoveryRetryAttempt} scheduled in "
            + $"{retryDelay.TotalMinutes:F0} min without changing the proxy or raising an alarm.");
        StartContinuousLoopRunner();
    }

    private void ResetAutomaticProxyRecoveryRetry()
    {
        _automaticProxyRecoveryRetryAttempt = 0;
        _automaticProxyRecoveryRetryAtUtc = DateTimeOffset.MinValue;
    }

    internal static TimeSpan ResolveAutomaticProxyRecoveryRetryDelay(int attempt)
        => attempt switch
        {
            <= 1 => TimeSpan.FromMinutes(2),
            2 => TimeSpan.FromMinutes(5),
            _ => TimeSpan.FromMinutes(10),
        };
}
