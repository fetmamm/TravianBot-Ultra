using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<string, double> _queueEstimateSecondsByVillage =
        new(StringComparer.OrdinalIgnoreCase);

    private string BuildQueueDisplayName(QueueItem item)
    {
        return QueueDisplayNameFormatter.Format(
            item,
            ResolveResourceName,
            ResolveBuildingName,
            ResourceFieldMaxLevel);
    }

    private static string? GetPayloadValue(IReadOnlyDictionary<string, string> payload, string key)
        => QueueDisplayNameFormatter.GetPayloadValue(payload, key);

    private static int? TryGetIntPayloadValue(IReadOnlyDictionary<string, string> payload, string key)
        => QueueDisplayNameFormatter.TryGetIntPayloadValue(payload, key);

    private string? ResolveResourceName(int slotId)
    {
        return _resourcesViewModel.AllFields
            .FirstOrDefault(row => row.SlotId == slotId)
            ?.Name;
    }

    private string? ResolveBuildingName(int slotId)
    {
        return _buildingRows.FirstOrDefault(row => row.SlotId == slotId)?.Name;
    }

    // Guards against re-entrancy: RefreshQueueUi repopulates the Buildings tab, and PopulateBuildingsTab
    // requests a queue refresh (so estimates pick up a newly detected Main Building level). Without this
    // flag those two would trigger each other in an endless loop.
    private bool _isRefreshingQueueUi;

    private void RefreshQueueUi(Guid? selectId = null)
    {
        _isRefreshingQueueUi = true;
        try
        {
            var ordered = _botService.GetQueueItemsForDisplay().ToList();
            ClearStaleBuildingPendingCaches(ordered);
            // Drop construction upgrades whose target level is already reached (built manually or covered
            // by an earlier queued step) so the queue only shows real remaining work. Re-fetch after a prune.
            if (PruneCompletedConstructionQueueItems(ordered))
            {
                ordered = _botService.GetQueueItemsForDisplay().ToList();
            }

            _queueServerTimeOffset = ResolveQueueServerTimeOffset();
            var displayRunningId = ResolveDisplayRunningQueueItemId(ordered);
            var serverSpeed = ResolveServerSpeed();
            var mainBuildingLevel = ResolveMainBuildingLevel();
            // Tracks the highest level already covered by earlier queued upgrades of the same building/
            // field (in queue order) so each row only counts its own step, not the shared lower levels.
            var queuedCoverage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rows = ordered
                .Select(item =>
                {
                    var estimate = EstimateForQueueItem(item, serverSpeed, mainBuildingLevel, queuedCoverage);
                    return QueueItemRowFactory.Create(
                        item,
                        estimate,
                        displayRunningId,
                        GetQueueItemCurrentVillageName,
                        GetQueueItemVillageKey,
                        BuildQueueDisplayName,
                        FormatQueueServerTime);
                })
                .ToList();
            var activeRows = rows
                .Where(row =>
                    row.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused
                    || (row.Status == QueueStatus.Failed && !row.IsRuntimeOnly))
                .ToList();
            UpdateDashboardQueueDurationTooltips(activeRows);
            var historyRows = rows
                .Where(row =>
                    row.Status == QueueStatus.Succeeded
                    || row.Status == QueueStatus.Canceled
                    || (row.Status == QueueStatus.Failed && row.IsRuntimeOnly))
                .ToList();
            var nowUtc = DateTimeOffset.UtcNow;
            var hasRunningQueueItems = ordered.Any(item => item.Status == QueueStatus.Running);
            var hasDeferredQueueItems = ordered.Any(item =>
                item.Status == QueueStatus.Pending &&
                item.NextAttemptAt > nowUtc);
            var hasPausedQueueItems = ordered.Any(item => item.Status == QueueStatus.Paused);
            var hasInlineWait = _inlineWaitUntilUtc > nowUtc;

            // Filter the displayed rows to the village selected in the dropdown so the Queue tab shows
            // that village's queue. Village-less (global) tasks are always shown. State flags above use
            // the unfiltered list so execution status stays account-wide.
            var displayedActiveRows = FilterQueueRowsForSelectedVillage(activeRows);
            var displayedHistoryRows = FilterQueueRowsForSelectedVillage(historyRows);

            QueueDataGrid.ItemsSource = displayedActiveRows;
            QueueHistoryDataGrid.ItemsSource = displayedHistoryRows;
            RefreshTravianBuildQueueUi();
            RefreshTravianSmithyQueueUi();
            UpdateQueueEstimateTotals(displayedActiveRows);
            SyncPendingResourceTargetsInUi();
            if (ResolveSelectedVillageBuildingStatus() is { } selectedBuildingStatus)
            {
                PopulateBuildingsTab(selectedBuildingStatus);
            }

            if (selectId.HasValue)
            {
                var selected = displayedActiveRows.FirstOrDefault(item => item.Id == selectId.Value);
                if (selected is not null)
                {
                    QueueDataGrid.SelectedItem = selected;
                }
            }

            QueueInfoTextBlock.Text = $"Queue active: {displayedActiveRows.Count} | done: {displayedHistoryRows.Count}";
            UpdateQueueClearButtonContent();
            if (_queuePopupWindow?.Content is Grid queuePopupRoot && queuePopupRoot.Children.Count >= 2)
            {
                if (queuePopupRoot.Children[0] is DataGrid popupActiveGrid)
                {
                    popupActiveGrid.ItemsSource = displayedActiveRows;
                }

                if (queuePopupRoot.Children[1] is DataGrid popupHistoryGrid)
                {
                    popupHistoryGrid.ItemsSource = displayedHistoryRows;
                }
            }
            UpdateExecutionStateIndicator();
        }
        catch (Exception ex)
        {
            QueueInfoTextBlock.Text = $"Queue load failed: {ex.Message}";
            AppendLog($"Queue load failed: {ex.Message}");
            UpdateExecutionStateIndicator();
        }
        finally
        {
            _isRefreshingQueueUi = false;
        }
    }

    private void UpdateDashboardQueueDurationTooltips(IReadOnlyList<QueueItemRow> rows)
    {
        _queueEstimateSecondsByVillage.Clear();
        var queuedVillages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!IsConstructionQueueTask(row.TaskName)
                || row.Status is not (QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused))
            {
                continue;
            }

            var villageKey = string.IsNullOrWhiteSpace(row.VillageKey)
                ? NormalizeVillageName(row.VillageName)
                : row.VillageKey;
            if (villageKey is null)
            {
                continue;
            }

            queuedVillages.Add(villageKey);
            if (!row.HasEstimate)
            {
                continue;
            }

            _queueEstimateSecondsByVillage.TryGetValue(villageKey, out var seconds);
            _queueEstimateSecondsByVillage[villageKey] = seconds + row.EstimateSeconds;
        }

        if (DashboardVillageList.ItemsSource is IEnumerable<VillageSelectionItem> villages)
        {
            foreach (var village in villages)
            {
                var villageKey = GetVillageKey(village);
                village.HasQueue = queuedVillages.Contains(villageKey);
                ApplyDashboardQueueTooltip(village);
            }
        }
    }

    private void ApplyDashboardQueueTooltip(VillageSelectionItem village)
    {
        if (!village.HasQueue)
        {
            village.QueueTooltip = "No construction queued here — consider queuing more";
            return;
        }

        var villageKey = GetVillageKey(village);
        village.QueueTooltip = _queueEstimateSecondsByVillage.TryGetValue(villageKey, out var seconds)
            && seconds > 0
                ? QueueItemRowFactory.FormatQueueDurationTooltip(seconds)
                : "Construction queued in this village\nTime: unavailable\nTime (25%): unavailable";
    }

    private void RefreshTravianBuildQueueUi()
    {
        var status = ResolveSelectedVillageBuildingStatus();
        var nowUtc = DateTimeOffset.UtcNow;
        var snapshot = ConstructionQueueState.ResolveSnapshot(status, nowUtc);
        var activeConstructions = snapshot.Knowledge == ConstructionQueueKnowledge.Active
            ? ConstructionQueueState.ResolveCurrentActiveConstructions(status, nowUtc)
            : [];
        var tribe = !string.IsNullOrWhiteSpace(status?.Tribe)
            && !string.Equals(status.Tribe, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? status.Tribe
                : ResolveStoredTroopTrainingTribe();
        var slotCount = tribe.Contains("Roman", StringComparison.OrdinalIgnoreCase)
            || ResolveIsRomansTribe()
                ? 3
                : 2;

        _travianBuildQueueRows.Clear();
        foreach (var row in LiveQueueRowFactory.BuildConstructionRows(
                     activeConstructions,
                     slotCount,
                     snapshot.Knowledge != ConstructionQueueKnowledge.Unknown,
                     nowUtc,
                     FormatQueueFinishTime))
        {
            _travianBuildQueueRows.Add(row);
        }
    }

    private void RefreshTravianSmithyQueueUi()
    {
        var status = ResolveSelectedVillageBuildingStatus();
        var activeUpgrades = SmithyQueueState.ResolveActiveUpgrades(
            status?.SmithyUpgradeStatus,
            DateTimeOffset.UtcNow);
        var nowUtc = DateTimeOffset.UtcNow;

        _travianSmithyQueueRows.Clear();
        foreach (var row in LiveQueueRowFactory.BuildSmithyRows(
                     activeUpgrades,
                     slotCount: 2,
                     status?.SmithyUpgradeStatus is not null,
                     nowUtc,
                     FormatQueueFinishTime))
        {
            _travianSmithyQueueRows.Add(row);
        }
    }

    private static Guid? ResolveDisplayRunningQueueItemId(IReadOnlyList<QueueItem> ordered)
    {
        if (ordered.Any(item => item.Status == QueueStatus.Running))
        {
            return null;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        return ordered
            .FirstOrDefault(item =>
                !item.IsRuntimeOnly &&
                item.Status == QueueStatus.Pending &&
                item.NextAttemptAt > nowUtc)?.Id;
    }

    private void UpdateQueueClearButtonContent()
    {
        if (QueueClearButton is null)
        {
            return;
        }

        QueueClearButton.Content = ReferenceEquals(QueueSectionTabControl?.SelectedItem, HistoryQueueTabItem)
            ? "Clear history"
            : "Clear account queue";
    }

    private void RefreshQueueUiOnUiThread(Guid? selectId = null)
    {
        RequestQueueUiRefresh(selectId);
    }

    private void RequestQueueUiRefresh(Guid? selectId = null, bool immediate = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => RequestQueueUiRefresh(selectId, immediate));
            return;
        }

        if (selectId.HasValue)
        {
            _pendingQueueUiSelectId = selectId;
        }

        if (immediate)
        {
            _queueUiRefreshTimer.Stop();
            var immediateSelectId = _pendingQueueUiSelectId;
            _pendingQueueUiSelectId = null;
            RefreshQueueUi(immediateSelectId);
            return;
        }

        _queueUiRefreshTimer.Stop();
        _queueUiRefreshTimer.Start();
    }

    private void QueueSectionTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, QueueSectionTabControl))
        {
            return;
        }

        UpdateQueueClearButtonContent();
    }
}
