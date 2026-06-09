using System;
using System.Threading;
using System.Windows;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private async void DashboardClearTimersButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedVillageName = NormalizeVillageName(GetSelectedVillageName());
        if (string.IsNullOrWhiteSpace(selectedVillageName))
        {
            MessageBox.Show(
                this,
                "Select a village before clearing timers.",
                "Clear timers",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(
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

        var resetQueueItems = ResetSelectedVillageDeferredTimers(selectedVillageName);
        ClearSelectedVillageRuntimeTimerCache(selectedVillageName);
        RefreshQueueUi();
        RefreshVillageActivityIndicatorsOnDashboard();
        UpdateAutomationLoopRunningIndicators();

        var loopRunning = _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted);
        if (loopRunning)
        {
            Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
            AppendLog(
                $"Cleared timers for village '{selectedVillageName}' ({resetQueueItems} deferred queue item(s)); " +
                "the running automation loop will refresh status.");
            return;
        }

        if (!_isLoggedIn || !_browserSessionLikelyOpen || _uiBusy)
        {
            AppendLog(
                $"Cleared timers for village '{selectedVillageName}' ({resetQueueItems} deferred queue item(s)); " +
                "status will refresh on the next automation run.");
            return;
        }

        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var status = await ReadVillageStatusWithRetryAsync(
                options,
                CancellationToken.None,
                resourceOnly: false,
                forceCurrentVillage: false);

            CacheVillageStatus(status, selectedVillageName);
            SetActiveWorkingVillageFromStatus(status);
            ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
            _resourcesViewModel.ApplyStorageForecasts(status);
            _lastBuildingStatus = status;
            PopulateBuildingsTab(status);
            ApplyConstructionTimerFromStatus(status);
            RefreshVillageActivityIndicatorsOnDashboard();
            AppendLog(
                $"Cleared timers and refreshed status for village '{selectedVillageName}' " +
                $"({resetQueueItems} deferred queue item(s)).");
        }
        catch (Exception ex)
        {
            AppendLog($"Timers were cleared for village '{selectedVillageName}', but immediate status refresh failed: {ex.Message}");
        }
        finally
        {
            ToggleUiBusy(false);
        }
    }

    private int ResetSelectedVillageDeferredTimers(string selectedVillageName)
    {
        var resetCount = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var item in _botService.GetQueueItemsForDisplay())
        {
            if (item.Status != QueueStatus.Pending || item.NextAttemptAt <= now)
            {
                continue;
            }

            var itemVillageName = NormalizeVillageName(GetQueueItemVillageName(item));
            var belongsToSelectedVillage = string.Equals(
                itemVillageName,
                selectedVillageName,
                StringComparison.OrdinalIgnoreCase);
            var isAccountHeroTimer = string.IsNullOrWhiteSpace(itemVillageName) &&
                                     item.Group == QueueGroup.Hero;
            if (!belongsToSelectedVillage && !isAccountHeroTimer)
            {
                continue;
            }

            if (_botService.UpdateDeferredQueueItem(item.Id, item.Payload, TimeSpan.Zero))
            {
                resetCount++;
            }
        }

        return resetCount;
    }

    private void ClearSelectedVillageRuntimeTimerCache(string selectedVillageName)
    {
        _buildQueueRemainingSeconds = -1;
        _buildQueueActiveCount = 0;
        _buildQueueReachedZeroPendingCompletion = false;
        _continuousLoopConstructionStatusNeedsSync = true;
        _smithyUpgradeRemainingSeconds.Clear();
        _troopTrainingViewModel.ClearRuntimeTimers();
        _heroViewModel.AdventureStatusText = "Status refresh requested.";

        if (_villageStatusCacheByName.TryGetValue(selectedVillageName, out var cachedStatus))
        {
            _villageStatusCacheByName[selectedVillageName] = ClearCachedActivityTimers(cachedStatus);
        }

        if (_lastBuildingStatus is not null &&
            string.Equals(
                NormalizeVillageName(_lastBuildingStatus.ActiveVillage),
                selectedVillageName,
                StringComparison.OrdinalIgnoreCase))
        {
            _lastBuildingStatus = ClearCachedActivityTimers(_lastBuildingStatus);
        }
    }

    private static VillageStatus ClearCachedActivityTimers(VillageStatus status)
    {
        return status with
        {
            BuildQueue = [],
            IsBuildingInProgress = false,
            ActiveBuildCount = 0,
            BuildQueueRemainingSeconds = null,
            BuildQueueRemainingText = string.Empty,
            ActiveConstructions = [],
        };
    }
}
