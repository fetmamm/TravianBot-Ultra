using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TbotUltra.Desktop.Services.Licensing;
using TbotUltra.Desktop.Services.Orchestration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static readonly TimeSpan LicenseReleaseGracePeriod = TimeSpan.FromSeconds(10);
    private TbotLicenseClient? _licenseClient;
    private LicenseCopyIdentity? _licenseCopy;
    private TbotLicenseStatus? _licenseStatus;
    private DateTimeOffset _licenseStatusReceivedAt;
    private DateTimeOffset _licenseBoundaryRefreshNotBefore;
    private readonly SemaphoreSlim _licenseRunGate = new(1, 1);
    private readonly DispatcherTimer _licenseDisplayTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Guid? _licenseRunId;
    private int _licenseScopeCount;
    private volatile bool _licenseRunUsable;
    private CancellationTokenSource? _licenseRenewalCancellation;
    private CancellationTokenSource? _licenseReleaseDelayCancellation;

    private void InitializeLicensing()
    {
        try
        {
            var configPath = Path.Combine(_projectRoot, "config", "license.json");
            if (!File.Exists(configPath))
            {
                configPath = Path.Combine(_projectRoot, ".release-template", "config", "license.json");
            }

            _licenseCopy = LicenseCopyIdentityStore.LoadOrCreate(_projectRoot);
            _licenseClient = new TbotLicenseClient(TbotLicenseSettings.Load(configPath),
                Path.Combine(_projectRoot, "config", "license-session.dat"), log: AppendLog);
            _licenseClient.SetLocalCopy(_licenseCopy.CopyId);
            _licenseDisplayTimer.Tick += (_, _) => UpdateLicenseCountdown();
            _licenseDisplayTimer.Start();
            Loaded += async (_, _) => await RefreshLicenseStatusAsync();
            AppendLog("[license] Client and copy identity initialized.");
        }
        catch (Exception ex)
        {
            LicenseCountdownText.Text = "License setup unavailable";
            LicenseCountdownText.ToolTip = ex.Message;
            AppendLog($"[license] Initialization failed: {FormatExceptionForLog(ex)}");
        }
    }

    private async void LicenseAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_licenseClient is null || _licenseCopy is null)
        {
            MessageBox.Show(this, "License setup is unavailable. Check config/license.json and restart.",
                "Tbot account", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = new LicenseAccountWindow(_licenseClient, _licenseCopy) { Owner = this };
        window.SessionChanged += () => _ = RefreshLicenseStatusAsync();
        window.SigningOut += async () =>
        {
            if (_licenseRunId is not null)
            {
                await StopAutomationSafelyAsync(AutomationStopMode.AfterCurrentAction);
                await ReleaseLicenseRunAsync(force: true);
            }
        };
        window.ShowDialog();
        await RefreshLicenseStatusAsync();
    }

    private async Task RefreshLicenseStatusAsync()
    {
        if (_licenseClient?.IsSignedIn != true)
        {
            RunOrPostToUi(() =>
            {
                _licenseStatus = null;
                UpdateLicenseCountdown();
            });
            return;
        }

        try
        {
            var status = await _licenseClient.GetStatusAsync();
            RunOrPostToUi(() =>
            {
                _licenseStatus = status;
                _licenseStatusReceivedAt = DateTimeOffset.UtcNow;
                _licenseBoundaryRefreshNotBefore = default;
                UpdateLicenseCountdown();
            });
        }
        catch (Exception ex)
        {
            AppendLog($"[license] Status refresh failed: {FormatExceptionForLog(ex)}");
            RunOrPostToUi(() =>
            {
                LicenseCountdownText.Text = "License status unavailable";
                LicenseCountdownText.ToolTip = ex.Message;
            });
        }
    }

    private void UpdateLicenseCountdown()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(UpdateLicenseCountdown);
            return;
        }

        if (_licenseClient?.IsSignedIn != true)
        {
            LicenseCountdownText.Text = "Sign in to start automation";
            return;
        }

        var status = _licenseStatus;
        if (status is null) return;
        var elapsed = DateTimeOffset.UtcNow - _licenseStatusReceivedAt;
        if (status.Tier == "pending")
        {
            LicenseCountdownText.Text = status.PendingPaidMonths > 0
                ? "Paid access starts with first automation"
                : status.PromoActivationAvailable
                    ? "Three free months start with first automation"
                    : status.PromoRedeemed
                        ? "Three-month code activation expired"
                        : "Trial starts with first automation";
        }
        else if (status.Tier == "full" && status.LifetimeAccess)
        {
            LicenseCountdownText.Text = "Lifetime access";
        }
        else if (status.Tier == "full" && status.FullAccessUntil is { } until)
        {
            var remaining = until - status.ServerNow - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            var label = status.OfflineEligible ? "Full access"
                : status.PromoRedeemed ? "Free access" : "Trial";
            LicenseCountdownText.Text = $"{label}: {remaining.Days}d {remaining.Hours}h {remaining.Minutes}m left";
            if (remaining == TimeSpan.Zero && DateTimeOffset.UtcNow >= _licenseBoundaryRefreshNotBefore)
            {
                _licenseBoundaryRefreshNotBefore = DateTimeOffset.UtcNow.AddSeconds(30);
                _ = RefreshLicenseStatusAsync();
            }
        }
        else
        {
            var activeSeconds = Math.Max(0, status.ActiveFreeRuns) * Math.Max(0, elapsed.TotalSeconds);
            var remaining = Math.Max(0, status.SecondsRemaining - (int)activeSeconds);
            LicenseCountdownText.Text = $"Free time: {TimeSpan.FromSeconds(remaining):h\\:mm} left today";
        }
    }

    private async Task<Guid?> AcquireLicenseRunAsync(CancellationToken cancellationToken = default)
    {
        if (_licenseClient is null || _licenseCopy is null)
        {
            AppendLog("[license] Automation blocked: license setup is unavailable.");
            return null;
        }

        await _licenseRunGate.WaitAsync(cancellationToken);
        try
        {
            CancelPendingLicenseRelease();
            if (_licenseRunId is not null)
            {
                if (!_licenseRunUsable) return null;
                _licenseScopeCount++;
                return _licenseRunId;
            }
            var runId = Guid.NewGuid();
            try
            {
                var lease = await _licenseClient.StartOrRenewAsync(_licenseCopy.CopyId, runId,
                    cancellationToken);
                _licenseRunId = runId;
                _licenseScopeCount = 1;
                _licenseRunUsable = true;
                _licenseRenewalCancellation = new CancellationTokenSource();
                _ = RenewLicenseRunAsync(runId, lease, _licenseRenewalCancellation.Token);
                AppendLog($"[license] Automation lease started: tier={lease.Tier}, ends={lease.LeaseUntil:O}.");
                await RefreshLicenseStatusAsync();
                return runId;
            }
            catch (Exception ex)
            {
                AppendLog($"[license] Automation blocked: {FormatExceptionForLog(ex)}");
                RunOrPostToUi(() =>
                {
                    LicenseCountdownText.Text = "Open Tbot account to check access";
                    LicenseCountdownText.ToolTip = ex.Message;
                });
                return null;
            }
        }
        finally
        {
            _licenseRunGate.Release();
        }
    }

    private async Task RenewLicenseRunAsync(Guid runId, TbotLicenseLease lease, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = lease.LeaseUntil - lease.ServerNow - TimeSpan.FromSeconds(20);
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            try
            {
                await Task.Delay(delay, cancellationToken);
                if (_licenseClient is null || _licenseCopy is null) return;
                lease = await _licenseClient.StartOrRenewAsync(_licenseCopy.CopyId, runId, cancellationToken);
                _licenseRunUsable = true;
                await RefreshLicenseStatusAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _licenseRunUsable = false;
                AppendLog($"[license] Lease renewal failed; stopping automation: {FormatExceptionForLog(ex)}");
                await Dispatcher.InvokeAsync(() => RequestAutomationStop(AutomationStopMode.AfterCurrentAction));
                return;
            }
        }
    }

    private async Task ReleaseLicenseRunAsync(bool force = false)
    {
        Guid runId;
        await _licenseRunGate.WaitAsync();
        try
        {
            if (_licenseRunId is not { } activeRunId) return;
            runId = activeRunId;
            if (force) _licenseScopeCount = 1;
            _licenseScopeCount = Math.Max(0, _licenseScopeCount - 1);
            if (_licenseScopeCount > 0) return;

            if (!force)
            {
                ScheduleLicenseRelease(runId);
                return;
            }

            CancelPendingLicenseRelease();
            DetachLicenseRun();
        }
        finally
        {
            _licenseRunGate.Release();
        }

        await StopLicenseRunOnServerAsync(runId);
    }

    private void ScheduleLicenseRelease(Guid runId)
    {
        if (_licenseReleaseDelayCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        _licenseReleaseDelayCancellation = cancellation;
        _ = CompleteScheduledLicenseReleaseAsync(runId, cancellation);
    }

    private async Task CompleteScheduledLicenseReleaseAsync(Guid runId,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(LicenseReleaseGracePeriod, cancellation.Token);
            await _licenseRunGate.WaitAsync(cancellation.Token);
            try
            {
                if (!ReferenceEquals(_licenseReleaseDelayCancellation, cancellation)
                    || _licenseRunId != runId || _licenseScopeCount > 0)
                {
                    return;
                }

                _licenseReleaseDelayCancellation = null;
                DetachLicenseRun();
            }
            finally
            {
                _licenseRunGate.Release();
            }

            await StopLicenseRunOnServerAsync(runId);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A new browser operation reused the active lease before the grace period ended.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void CancelPendingLicenseRelease()
    {
        var cancellation = _licenseReleaseDelayCancellation;
        _licenseReleaseDelayCancellation = null;
        cancellation?.Cancel();
    }

    private void DetachLicenseRun()
    {
        _licenseRunId = null;
        _licenseRunUsable = false;
        _licenseRenewalCancellation?.Cancel();
        _licenseRenewalCancellation?.Dispose();
        _licenseRenewalCancellation = null;
    }

    private async Task StopLicenseRunOnServerAsync(Guid runId)
    {
        try
        {
            if (_licenseClient is not null) await _licenseClient.StopAsync(runId);
            AppendLog("[license] Automation lease stopped after the active browser-operation burst.");
        }
        catch (Exception ex)
        {
            AppendLog($"[license] Lease stop failed; it will expire on the server: {FormatExceptionForLog(ex)}");
        }
        await RefreshLicenseStatusAsync();
    }

    private async ValueTask<IAsyncDisposable> AcquireBotUseLeaseAsync(CancellationToken cancellationToken)
    {
        if (await AcquireLicenseRunAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException("Sign in to your Tbot account and check your license before using the bot.");
        }
        return new AsyncActionScope(() => new ValueTask(ReleaseLicenseRunAsync()));
    }

    private sealed class AsyncActionScope(Func<ValueTask> release) : IAsyncDisposable
    {
        private Func<ValueTask>? _release = release;

        public ValueTask DisposeAsync()
        {
            var action = Interlocked.Exchange(ref _release, null);
            return action is null ? ValueTask.CompletedTask : action();
        }
    }

}
