using System;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    internal Task OnHeroRaidingScanClicked() => GuardUiAsync(async () =>
    {
        if (_heroViewModel.IsHeroRaidingScanRunning) return;
        _heroViewModel.IsHeroRaidingScanRunning = true;
        _heroViewModel.HeroRaidingStatusText = "Scanning nearby oases...";
        try
        {
            var result = await RunManualOperationAsync(
                "Hero raiding: Scan now",
                token => _botService.ScanHeroRaidingCandidatesAsync(LoadBotOptions(), AppendLog, token),
                CancellationToken.None);
            _heroViewModel.ApplyHeroRaidingScan(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _heroViewModel.HeroRaidingStatusText = $"Oasis scan failed: {ex.Message}";
            throw;
        }
        finally
        {
            _heroViewModel.IsHeroRaidingScanRunning = false;
        }
    });

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
