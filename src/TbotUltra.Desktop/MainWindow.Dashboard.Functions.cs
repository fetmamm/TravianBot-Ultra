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
        var selectedVillageKey = GetSelectedVillageKey();
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
                + "This also clears that village's construction build-queue snapshot and resets all deferred "
                + "group retry timers for the selected village, so stuck \"waiting\" states can retry now. "
                + "Queue page items will not be removed.",
            "Clear timers",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        // "Clear timers" clears the selected village's cached activity timers + construction snapshot and
        // resets that village's deferred group retries (manual escape hatch for a stuck wait). It does not
        // start the bot from stopped, but it wakes an already-running loop so the groups retry promptly.
        ClearSelectedVillageRuntimeTimerCache(selectedVillageName);
        var resetCount = ResetDeferredQueueTimersForVillage(selectedVillageName, selectedVillageKey);
        if (resetCount > 0)
        {
            Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
        }

        RequestQueueUiRefresh(immediate: true);
        RefreshVillageActivityIndicatorsOnDashboard();
        UpdateAutomationLoopRunningIndicators();
        AppendLog($"Cleared cached timers and reset {resetCount} deferred group timer(s) for village '{selectedVillageName}'. Queue items were kept.");
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

    // Resets the selected village's deferred queue timers to "now" across all automation groups. Queue
    // items are kept; only pending future retries for this village are made ready.
    private int ResetDeferredQueueTimersForVillage(string villageName, string? villageKey)
    {
        var now = DateTimeOffset.UtcNow;
        var deferred = _botService.GetQueueItemsForDisplay()
            .Where(item => item.Status == QueueStatus.Pending
                && item.NextAttemptAt > now
                && IsQueueItemForVillage(item, villageName, villageKey))
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
            AppendLog($"Reset {reset} deferred group retry timer(s) for village '{villageName}'.");
        }

        return reset;
    }

    private bool IsQueueItemForVillage(QueueItem item, string villageName, string? villageKey)
    {
        if (!string.IsNullOrWhiteSpace(villageKey)
            && string.Equals(GetQueueItemVillageKey(item), villageKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            NormalizeVillageName(GetQueueItemVillageName(item)),
            villageName,
            StringComparison.OrdinalIgnoreCase);
    }
}
