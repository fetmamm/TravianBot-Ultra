using System;
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
            $"Clear cached timers and request fresh status reads for '{selectedVillageName}'?\n\nQueue page items will not be removed.",
            "Clear timers",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        // "Clear timers" must ONLY clear the selected village's cached activity timers and refresh the UI.
        // It must not resume work: don't reset deferred queue items to retry-now, don't wake the loop, and
        // don't navigate the browser — otherwise pressing it would effectively start the bot.
        ClearSelectedVillageRuntimeTimerCache(selectedVillageName);
        RefreshQueueUi();
        RefreshVillageActivityIndicatorsOnDashboard();
        UpdateAutomationLoopRunningIndicators();
        AppendLog($"Cleared cached timers for village '{selectedVillageName}'. Queue items were kept and the bot was not started.");
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

    private static VillageStatus ClearCachedActivityTimers(VillageStatus status)
    {
        var activeConstructions = status.ActiveConstructions ?? [];
        var activeBuildCount = activeConstructions.Count;
        return status with
        {
            IsBuildingInProgress = activeBuildCount > 0,
            ActiveBuildCount = activeBuildCount,
            BuildQueueRemainingSeconds = null,
            BuildQueueRemainingText = string.Empty,
            TroopTrainingQueues = null,
            BreweryCelebrationStatus = null,
            FarmLists = null,
            HeroStatus = null,
        };
    }
}
