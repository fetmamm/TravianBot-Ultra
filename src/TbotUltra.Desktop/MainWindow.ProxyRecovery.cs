using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private const int AutomaticProxyRecoveryFailureThreshold = 3;
    private bool TryScheduleAutomaticProxyRecovery(BotOptions options)
    {
        if (!_automationProxyRecoveryRuntime.TryReserve(
                _automationNetworkBackoff.ConsecutiveFailures,
                AutomaticProxyRecoveryFailureThreshold))
        {
            return false;
        }

        var account = FindAccount(_accountStore.ActiveAccountName());
        if (account?.ProxyEnabled != true
            || string.IsNullOrWhiteSpace(account.ProxyServer)
            || !ProxyParser.TryBuild(account.ProxyServer, out _, out _))
        {
            _automationProxyRecoveryRuntime.Release();
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
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _automationDesk.StopAsync(
                    AutomationStopMode.AfterCurrentAction,
                    stopTimeout.Token);
            }
            catch (OperationCanceledException) when (stopTimeout.IsCancellationRequested)
            {
                AppendLog("[proxy-recovery] loop shutdown wait timed out; continuing with controlled session stop.");
            }

            using var recoveryCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var libraryStore = new ProxyLibraryStore();
            var library = libraryStore.Load();
            IReadOnlyList<string>? plannedProxyIds = null;
            var plan = ProxyPlanStore.LoadActive(account.Name);
            if (plan?.Enabled == true)
            {
                var runtime = ProxyPlanStore.LoadRuntime(account.Name);
                var scheduled = AccountProxyPlanResolver.Resolve(plan, account.Name, DateTimeOffset.Now, runtime);
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
                try
                {
                    // A standalone proxy request can succeed while the existing Chromium page is still
                    // stranded on chrome-error://. Verify the real browser session before resuming work.
                    await _botService.ExecuteLoginAsync(
                        previousOptions,
                        AppendLog,
                        keepBrowserOpenAfterLogin: true,
                        recoveryCts.Token);
                    MarkNetworkConnectionHealthy();
                    ResetAutomaticProxyRecoveryRetry();
                    StatusTextBlock.Text = "Proxy recovered; resuming automation.";
                    StartContinuousLoopRunner();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ScheduleAutomaticProxyRecoveryRetry(
                        $"The proxy test passed, but the browser session still could not reach Travian: {ex.Message}");
                }

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
                changedAccount.ProxyId = result.Proxy.Id;
                changedAccount.ProxyServer = result.Proxy.Server;
                if (plan?.Enabled == true)
                {
                    var runtime = ProxyPlanStore.LoadRuntime(account.Name);
                    runtime.ActiveProxyId = result.Proxy.Id;
                    runtime.LastSuccessfulProxyId = result.Proxy.Id;
                    runtime.ActivatedAtUtc = DateTimeOffset.UtcNow;
                    runtime.RecoveryOverrideProxyId = result.Proxy.Id;
                    runtime.RecoveryOverrideUntilUtc = AccountProxyPlanResolver
                        .Resolve(plan, account.Name, DateTimeOffset.Now, runtime.ActiveProxyId)
                        .NextTransitionAt;
                    ProxyPlanStore.SaveRuntime(account.Name, runtime);
                }
                AppendLog($"[proxy-recovery] switching to {ProxyParser.MaskForLog(result.Proxy.Server)}.");
            }
            else
            {
                changedAccount.ProxyEnabled = false;
                changedAccount.ProxyId = string.Empty;
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
            _automationProxyRecoveryRuntime.Release();
        }
    }

    private void ScheduleAutomaticProxyRecoveryRetry(string reason)
    {
        if (_loopController.IsClosing)
        {
            return;
        }

        var retry = _automationProxyRecoveryRuntime.ScheduleRetry();
        var retryDelay = retry.Delay;
        _automationNetworkBackoff.MarkUnavailable(retryDelay);
        StatusTextBlock.Text = $"Connection unavailable. Retrying in {retryDelay.TotalMinutes:F0} min.";
        AppendLog(
            $"[proxy-recovery] {reason} Retry {retry.Attempt} scheduled in "
            + $"{retryDelay.TotalMinutes:F0} min without changing the proxy or raising an alarm.");
        StartContinuousLoopRunner();
    }

    private void ResetAutomaticProxyRecoveryRetry()
    {
        _automationProxyRecoveryRuntime.ResetRetry();
    }
}
