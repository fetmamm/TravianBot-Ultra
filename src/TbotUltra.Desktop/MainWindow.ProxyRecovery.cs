using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private const int AutomaticProxyRecoveryFailureThreshold = 3;
    private int _automaticProxyRecoveryScheduled;

    private bool TryScheduleAutomaticProxyRecovery(BotOptions options)
    {
        if (_consecutiveTransientNavigationFailures < AutomaticProxyRecoveryFailureThreshold
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
            var recoveryService = new ProxyFailoverService(new ProxyListTester(log: AppendLog));
            var result = await recoveryService.FindRecoveryAsync(
                account,
                library,
                account.ServerUrl,
                AppendLog,
                recoveryCts.Token);
            libraryStore.Save(library);
            AppendLog($"[proxy-recovery] {result.Message}");

            if (result.Kind == ProxyFailoverKind.CurrentProxyHealthy)
            {
                MarkNetworkConnectionHealthy();
                StatusTextBlock.Text = "Proxy recovered; resuming automation.";
                StartContinuousLoopRunner();
                return;
            }

            if (result.Kind == ProxyFailoverKind.Unavailable)
            {
                StatusTextBlock.Text = "Proxy unavailable. Automation stopped.";
                AppendLog($"[ALARM] [proxy-recovery] {result.Message} Automation remains stopped for safety.");
                return;
            }

            var changedAccount = CloneAccount(account);
            if (result.Kind == ProxyFailoverKind.ReplacementProxy && result.Proxy is not null)
            {
                changedAccount.ProxyEnabled = true;
                changedAccount.ProxyServer = result.Proxy.Server;
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
            AppendLog("[proxy-recovery] recovery timed out or was cancelled; automation remains stopped.");
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
}
