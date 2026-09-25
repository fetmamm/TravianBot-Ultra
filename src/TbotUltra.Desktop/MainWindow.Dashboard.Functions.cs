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
        var choice = AppDialog.ShowCustom(
            this,
            "Choose which cached timers to clear. Queue page items will not be removed.",
            "Clear timers",
            [("Clear village timers", MessageBoxResult.Yes), ("Clear account timers", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel,
            successResult: MessageBoxResult.Yes,
            dangerResult: MessageBoxResult.No);
        if (choice == MessageBoxResult.Cancel)
        {
            return;
        }

        if (choice == MessageBoxResult.Yes)
        {
            if (string.IsNullOrWhiteSpace(selectedVillageName))
            {
                AppDialog.Show(
                    this,
                    "Select a village before clearing village timers.",
                    "Clear timers",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ClearVillageTimers(selectedVillageName, selectedVillageKey);
            return;
        }

        ClearAccountTimers();
    }

    private void ClearVillageTimers(string selectedVillageName, string? selectedVillageKey)
    {
        // "Clear timers" clears the selected village's cached activity timers + construction snapshot and
        // resets that village's deferred group retries (manual escape hatch for a stuck wait). It does not
        // start the bot from stopped, but it wakes an already-running loop so the groups retry promptly.
        ClearSelectedVillageRuntimeTimerCache(selectedVillageName, selectedVillageKey);
        ClearAutomationLoopCardBlocks();
        var resetCount = ResetDeferredQueueTimersForVillage(selectedVillageName, selectedVillageKey);
        PrepareConstructionLoginFill("manual-clear-timers", selectedVillageName, selectedVillageKey);
        _automationDesk.ResetVillageBatch();
        if (IsContinuousLoopRunning() || _autoQueueRunning)
        {
        RequestContinuousAutomationWake();
        }

        RequestQueueUiRefresh(immediate: true);
        RefreshVillageActivityIndicatorsOnDashboard();
        UpdateAutomationLoopRunningIndicators();
        AppendLog($"Cleared cached timers and reset {resetCount} deferred group timer(s) for village '{selectedVillageName}'. Construction was prepared for immediate queue fill. Queue items were kept.");
    }

    private void ClearAccountTimers()
    {
        _automationDesk.RequestConstructionStatusSync();
        _smithyUpgradeRemainingSeconds.Clear();
        _troopTrainingViewModel.ClearRuntimeTimers();
        _heroViewModel.AdventureStatusText = "Status refresh requested.";
        ClearAutomationLoopCardBlocks();

        var clearedStatuses = _villageStatusCache.Snapshot
            .ToDictionary(pair => pair.Key, pair => ClearCachedActivityTimers(pair.Value), StringComparer.OrdinalIgnoreCase);
        _villageStatusCache.LoadFrom(clearedStatuses);
        _villageCacheStore.Save(_villageStatusCache.Snapshot);
        if (_lastBuildingStatus is not null)
        {
            _lastBuildingStatus = ClearCachedActivityTimers(_lastBuildingStatus);
        }

        var resetCount = ResetAllDeferredQueueTimers();
        PrepareConstructionLoginFill("manual-clear-timers");
        _automationDesk.ResetVillageBatch();
        if (IsContinuousLoopRunning() || _autoQueueRunning)
        {
        RequestContinuousAutomationWake();
        }

        _buildQueueActiveCount = 0;
        _buildQueueRemainingSeconds = -1;
        _buildQueueReachedZeroPendingCompletion = false;
        UpdateBuildQueueStatusText();
        RequestQueueUiRefresh(immediate: true);
        RefreshVillageActivityIndicatorsOnDashboard();
        UpdateAutomationLoopRunningIndicators();
        AppendLog($"Cleared cached timers and reset {resetCount} deferred group timer(s) for the active account. Construction was prepared for immediate queue fill. Queue items were kept.");
    }

    private void ClearSelectedVillageRuntimeTimerCache(string selectedVillageName, string? selectedVillageKey)
    {
        _automationDesk.RequestConstructionStatusSync();
        _smithyUpgradeRemainingSeconds.Clear();
        _troopTrainingViewModel.ClearRuntimeTimers();
        _heroViewModel.AdventureStatusText = "Status refresh requested.";

        VillageStatus? selectedStatus = null;
        if (!string.IsNullOrWhiteSpace(selectedVillageKey)
            && _villageStatusCache.TryGetByKey(selectedVillageKey, out var cachedStatus))
        {
            selectedStatus = ClearCachedActivityTimers(cachedStatus);
            StoreVillageStatusCacheEntry(selectedVillageName, selectedStatus);
        }

        if (_lastBuildingStatus is not null && IsStatusForSelectedVillage(_lastBuildingStatus))
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

    // Manual timer reset is also a manual retry of every automation card. Clear only automatic
    // blocked states; a group that the user disabled manually remains disabled.
    private void ClearAutomationLoopCardBlocks()
    {
        ClearTroopsBlockedState();
        ClearFarmingBlockedState();
        ClearHeroBlockedState();
        ClearBreweryBlockedState();
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
                && (item.Group == QueueGroup.Hero
                    || IsQueueItemForVillage(item, villageName, villageKey)))
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

    private int ResetAllDeferredQueueTimers()
    {
        var now = DateTimeOffset.UtcNow;
        var deferred = _botService.GetQueueItemsForDisplay()
            .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
            .ToList();
        var reset = 0;
        foreach (var item in deferred)
        {
            if (_botService.UpdateDeferredQueueItem(item.Id, null, TimeSpan.Zero))
            {
                reset += 1;
            }
        }

        return reset;
    }

    private bool IsQueueItemForVillage(QueueItem item, string villageName, string? villageKey)
    {
        if (!string.IsNullOrWhiteSpace(villageKey))
        {
            return string.Equals(GetQueueItemVillageKey(item), villageKey, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            NormalizeVillageName(GetQueueItemVillageName(item)),
            villageName,
            StringComparison.OrdinalIgnoreCase);
    }
}
