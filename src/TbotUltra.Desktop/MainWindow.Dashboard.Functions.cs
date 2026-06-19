using System;
using System.Linq;
using System.Threading;
using System.Windows;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void DashboardClearTimersButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedVillageName = NormalizeVillageName(GetSelectedVillageName());
        if (string.IsNullOrWhiteSpace(selectedVillageName))
        {
            AppDialog.Show(
                this,
                "Select a village before clearing timers.",
                "Clear timers",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirmation = AppDialog.Show(
            this,
            $"Clear cached timers for '{selectedVillageName}' and request fresh status reads?\n\n"
                + "This also clears that village's construction build-queue snapshot and resets its deferred "
                + "construction retries, so a stuck \"waiting\" state (when nothing is actually building) can "
                + "be reset. Queue page items will not be removed.",
            "Clear timers",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        // "Clear timers" clears the selected village's cached activity timers + construction snapshot and
        // resets that village's deferred construction retries (a manual escape hatch for a stuck wait). It
        // still must not wake the loop or navigate the browser, so it never starts the bot on its own — if
        // the loop is already running it simply re-checks construction promptly; if stopped, nothing runs.
        ClearSelectedVillageRuntimeTimerCache(selectedVillageName);
        ResetDeferredConstructionTimersForVillage(selectedVillageName);
        RefreshQueueUi();
        RefreshVillageActivityIndicatorsOnDashboard();
        UpdateAutomationLoopRunningIndicators();
        AppendLog($"Cleared cached timers and construction wait for village '{selectedVillageName}'. Queue items were kept and the bot was not started.");
    }

    private void ClearSelectedVillageRuntimeTimerCache(string selectedVillageName)
    {
        _continuousLoopConstructionStatusNeedsSync = true;
        _smithyUpgradeRemainingSeconds.Clear();
        _troopTrainingViewModel.ClearRuntimeTimers();
        _heroViewModel.AdventureStatusText = "Status refresh requested.";

        VillageStatus? selectedStatus = null;
        if (_villageStatusCacheByName.TryGetValue(selectedVillageName, out var cachedStatus))
        {
            selectedStatus = ClearCachedActivityTimers(cachedStatus);
            _villageStatusCacheByName[selectedVillageName] = selectedStatus;
        }

        if (_lastBuildingStatus is not null &&
            string.Equals(
                NormalizeVillageName(_lastBuildingStatus.ActiveVillage),
                selectedVillageName,
                StringComparison.OrdinalIgnoreCase))
        {
            _lastBuildingStatus = ClearCachedActivityTimers(_lastBuildingStatus);
            selectedStatus = _lastBuildingStatus;
        }

        var constructionTimer = ConstructionQueueState.ResolveLiveConstructionTimer(selectedStatus);
        _buildQueueActiveCount = constructionTimer.ActiveCount;
        _buildQueueRemainingSeconds = constructionTimer.RemainingSeconds ?? -1;
        _buildQueueReachedZeroPendingCompletion = false;
        UpdateBuildQueueStatusText();
    }

    // Manual "Clear timers" reset: wipes the cached construction snapshot too so a stale/stuck build
    // belief (e.g. "waiting on Wall 12" while nothing is building) is cleared and the next confirmed
    // dorf1/dorf2 read re-derives reality. Only the user-triggered button calls this; automatic flows
    // (cache-load, UI-tick, partial reads, local FinishUtc) must still never clear ActiveConstructions.
    private static VillageStatus ClearCachedActivityTimers(VillageStatus status)
    {
        return status with
        {
            IsBuildingInProgress = false,
            ActiveBuildCount = 0,
            ActiveConstructions = [],
            ActiveConstructionsFromOverview = false,
            BuildQueueRemainingSeconds = null,
            BuildQueueRemainingText = string.Empty,
            TroopTrainingQueues = null,
            BreweryCelebrationStatus = null,
            FarmLists = null,
            HeroStatus = null,
        };
    }

    // Resets the selected village's deferred construction retries to "now" so a stuck wait (e.g. a stale
    // queue_full retry timed ~30 min out while nothing is building) is cleared. Only Construction-group
    // items that are Pending with a future NextAttemptAt are rescheduled; the loop is not woken, so this
    // does not start the bot — a running loop just re-checks construction on its next pass.
    private void ResetDeferredConstructionTimersForVillage(string villageName)
    {
        var now = DateTimeOffset.UtcNow;
        var deferred = _botService.GetQueueItemsForDisplay()
            .Where(item => item.Group == QueueGroup.Construction
                && item.Status == QueueStatus.Pending
                && item.NextAttemptAt > now
                && string.Equals(
                    NormalizeVillageName(GetQueueItemVillageName(item)),
                    villageName,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        var reset = 0;
        foreach (var item in deferred)
        {
            if (_botService.UpdateDeferredQueueItem(item.Id, null, TimeSpan.Zero))
            {
                reset += 1;
            }
        }

        if (reset > 0)
        {
            AppendLog($"Reset {reset} deferred construction retry timer(s) for village '{villageName}'.");
        }
    }
}
