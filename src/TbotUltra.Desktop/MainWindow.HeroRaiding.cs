using System;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    internal Task OnHeroRaidingScanClicked()
    {
        if (!RequireTravianLoginForHeroRaiding()) return Task.CompletedTask;
        return GuardUiAsync(async () =>
        {
            if (_heroViewModel.IsHeroRaidingScanRunning) return;
            _heroViewModel.IsHeroRaidingScanRunning = true;
            _heroViewModel.HeroRaidingStatusText = "Scanning nearby oases...";
            try
            {
                BusyOverlay.ShowCancel = true;
                ShowBusyOverlay("Hero raiding: Scan now", "Preparing oasis scan...");
                BusyOverlay.IsIndeterminate = false;
                BusyOverlay.ProgressValue = 0;
                var progress = new Progress<HeroRaidingScanProgress>(value =>
                {
                    if (!BusyOverlay.IsBusy || !BusyOverlay.CancelEnabled) return;
                    var percent = value.TotalAreas == 0
                        ? 0
                        : (double)value.CompletedAreas / value.TotalAreas * 100;
                    BusyOverlay.ProgressValue = percent;
                    BusyOverlay.Text =
                        $"{percent:0}% complete\n" +
                        $"Map area {value.CompletedAreas}/{value.TotalAreas} - {value.TotalAreas - value.CompletedAreas} remaining\n" +
                        $"{value.OasisCount} oases found, {value.SafeCandidateCount} safe";
                });
                var result = await RunManualOperationAsync(
                    "Hero raiding: Scan now",
                    token => _botService.ScanHeroRaidingCandidatesAsync(LoadBotOptions(), AppendLog, progress, token),
                    CancellationToken.None);
                _heroViewModel.ApplyHeroRaidingScan(result);
            }
            catch (OperationCanceledException)
            {
                _heroViewModel.HeroRaidingStatusText = "Oasis scan canceled. Previous results were kept.";
                AppendLog("[hero-raiding] manual oasis scan canceled; previous candidates kept.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _heroViewModel.HeroRaidingStatusText = $"Oasis scan failed: {ex.Message}";
                throw;
            }
            finally
            {
                HideBusyOverlay();
                _heroViewModel.IsHeroRaidingScanRunning = false;
            }
        });
    }

    internal Task OnHeroRaidingOpenInBrowserClicked(int x, int y)
    {
        if (!RequireTravianLoginForHeroRaiding()) return Task.CompletedTask;
        return GuardUiAsync(async () =>
        {
            await RunManualOperationAsync(
                "Open oasis in browser",
                async token =>
                {
                    await _botService.OpenMapTileAsync(LoadBotOptions(), x, y, AppendLog, token);
                    return true;
                },
                CancellationToken.None);
        });
    }

    private bool RequireTravianLoginForHeroRaiding()
    {
        if (_isLoggedIn) return true;
        AppDialog.Show(this,
            "Log in to Travian before using Hero Raiding. Click Login in the Account card, then try again.",
            "Travian login required",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
        return false;
    }

    private void HeroRaidingExclusion_Changed(int x, int y, bool excluded)
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (string.IsNullOrWhiteSpace(accountName))
                throw new InvalidOperationException("No active account is selected.");
            var serverUrl = GetActiveAccountServerUrl();
            var store = new HeroRaidingExclusionStore(_projectRoot, AppendLog);
            var saved = store.Load(accountName, serverUrl).ToHashSet();
            if (excluded) saved.Add((x, y));
            else saved.Remove((x, y));
            store.Save(accountName, serverUrl, saved);
            AppendLog($"[hero-raiding] oasis ({x}|{y}) {(excluded ? "excluded" : "re-enabled")} by user.");
        }
        catch (Exception ex)
        {
            _heroViewModel.HeroRaidingEnabled = false;
            _heroViewModel.HeroRaidingStatusText = $"Could not save oasis exclusion; hero raiding was disabled. {ex.Message}";
            AppendLog($"[hero-raiding] exclusion save failed; automation disabled: {ex}");
        }
    }
}
