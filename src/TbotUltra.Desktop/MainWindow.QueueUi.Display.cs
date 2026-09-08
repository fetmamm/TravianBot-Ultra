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
    // The Queue tab is the authoritative estimate projection. Village Settings reads these same rows
    // so its per-village totals cannot drift from the Queue tab after a cache or selection change.
    private readonly Dictionary<Guid, QueueItemRow> _queueEstimateRowsById = [];
    private IReadOnlyList<QueueItemRow> _allActiveQueueRows = [];
    private IReadOnlyList<QueueItemRow> _allHistoryQueueRows = [];
    private IReadOnlyList<QueueItem> _historyQueueItems = [];
    private bool _historyQueueProjectionDirty;
    private IReadOnlyList<QueueItem> _queueItemsForUiProjection = [];
    private bool _hasQueueDisplayProjection;

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

    private void RefreshQueueUi(Guid? selectId = null, IReadOnlyCollection<Guid>? selectIds = null)
    {
        var requestedSelection = selectIds?.Where(id => id != Guid.Empty).ToHashSet()
            ?? (selectId.HasValue
                ? new HashSet<Guid> { selectId.Value }
                : QueueDataGrid.SelectedItems.OfType<QueueItemRow>().Select(row => row.Id).ToHashSet());
        RefreshDemolishStatusForSelectedVillage();
        _isRefreshingQueueUi = true;
        try
        {
            var ordered = _queuePanelService.GetItems().ToList();
            _queueItemsForUiProjection = ordered;
            ClearStaleBuildingPendingCaches(ordered);

            _queueServerTimeOffset = ResolveQueueServerTimeOffset();
            var projection = BuildQueueDisplayRows(ordered);
            _allActiveQueueRows = projection.ActiveRows;
            _historyQueueItems = projection.HistoryItems;
            _historyQueueProjectionDirty = true;
            _hasQueueDisplayProjection = true;
            _queueEstimateRowsById.Clear();
            foreach (var row in projection.ActiveRows)
            {
                _queueEstimateRowsById[row.Id] = row;
            }
            // Village overview caches a projection of these exact Queue rows. Mark that projection stale
            // only after the authoritative estimates have been replaced, so its next render cannot keep
            // showing old totals with a newly ticking "Updated" timestamp.
            InvalidateVillageOverview();
            UpdateDashboardQueueDurationTooltips(projection.ActiveRows);
            RequestDashboardVillageProjectionRefresh();
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
            var displayedActiveRows = FilterQueueRowsForSelectedVillage(projection.ActiveRows);
            _travianQueueViewModel.ApplyActiveQueueRows(displayedActiveRows);
            if (ShouldProjectQueueHistory())
            {
                EnsureQueueHistoryProjection();
                _travianQueueViewModel.ApplyHistoryQueueRows(
                    FilterQueueRowsForSelectedVillage(_allHistoryQueueRows));
            }
            RefreshTravianBuildQueueUi();
            RefreshTravianSmithyQueueUi();
            UpdateQueueEstimateTotals(displayedActiveRows);
            SyncPendingResourceTargetsInUi();
            if (requestedSelection.Count > 0)
            {
                QueueDataGrid.SelectedItems.Clear();
                foreach (var row in _travianQueueViewModel.ActiveQueueRows.Where(row => requestedSelection.Contains(row.Id)))
                {
                    QueueDataGrid.SelectedItems.Add(row);
                }
            }

            UpdateQueueClearButtonContent();
            if (_queuePopupWindow?.Content is Grid queuePopupRoot && queuePopupRoot.Children.Count >= 2)
            {
                if (queuePopupRoot.Children[0] is DataGrid popupActiveGrid)
                {
                    popupActiveGrid.ItemsSource = _travianQueueViewModel.ActiveQueueRows;
                }

                if (queuePopupRoot.Children[1] is DataGrid popupHistoryGrid)
                {
                    popupHistoryGrid.ItemsSource = _travianQueueViewModel.HistoryQueueRows;
                }
            }
            UpdateExecutionStateIndicator();
            UpdateNextTaskUi();
        }
        catch (Exception ex)
        {
            AppendLog($"Queue load failed: {ex.Message}");
            UpdateExecutionStateIndicator();
        }
        finally
        {
            _isRefreshingQueueUi = false;
        }
    }

    // Produces the one estimate projection consumed by both the Queue tab and Village Settings overview.
    private QueueDisplayRows BuildQueueDisplayRows(IReadOnlyList<QueueItem> ordered)
    {
        var displayRunningId = ResolveDisplayRunningQueueItemId(ordered);
        var serverSpeed = ResolveServerSpeed();
        var mainBuildingLevel = ResolveMainBuildingLevel();
        var queuedCoverage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return QueueDisplayProjection.Build(
            ordered,
            item => QueueItemRowFactory.Create(
                item,
                EstimateForQueueItem(item, serverSpeed, mainBuildingLevel, queuedCoverage),
                displayRunningId,
                GetQueueItemCurrentVillageName,
                GetQueueItemVillageKey,
                BuildQueueDisplayName,
                FormatQueueServerTime));
    }

    private bool ShouldProjectQueueHistory() =>
        ReferenceEquals(QueueSectionTabControl?.SelectedItem, HistoryQueueTabItem)
        || _queuePopupWindow is not null;

    private void EnsureQueueHistoryProjection()
    {
        if (!_historyQueueProjectionDirty)
        {
            return;
        }

        _allHistoryQueueRows = _historyQueueItems
            .Select(item => QueueItemRowFactory.Create(
                item,
                QueueItemEstimate.None,
                displayRunningId: null,
                GetQueueItemCurrentVillageName,
                GetQueueItemVillageKey,
                BuildQueueDisplayName,
                FormatQueueServerTime))
            .ToList();
        _historyQueueProjectionDirty = false;
    }

    private void ApplyCachedQueueRowsForSelectedVillage()
    {
        if (!_hasQueueDisplayProjection)
        {
            RequestQueueUiRefresh();
            return;
        }

        var activeRows = FilterQueueRowsForSelectedVillage(_allActiveQueueRows);
        _travianQueueViewModel.ApplyActiveQueueRows(activeRows);
        if (ShouldProjectQueueHistory())
        {
            EnsureQueueHistoryProjection();
            _travianQueueViewModel.ApplyHistoryQueueRows(
                FilterQueueRowsForSelectedVillage(_allHistoryQueueRows));
        }

        RefreshTravianBuildQueueUi();
        RefreshTravianSmithyQueueUi();
        UpdateQueueEstimateTotals(activeRows);
        SyncPendingResourceTargetsInUi();
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

        UpdateBuildingsQueueDuration();
    }

    private void UpdateBuildingsQueueDuration()
    {
        var villageKey = GetSelectedVillageKey()
            ?? NormalizeVillageName(GetSelectedVillageName());
        if (villageKey is not null
            && _queueEstimateSecondsByVillage.TryGetValue(villageKey, out var seconds)
            && seconds > 0)
        {
            _buildingsViewModel.ApplyQueueDuration(
                FormatBuildDuration(seconds),
                FormatBuildDuration(seconds * 0.75));
            return;
        }

        _buildingsViewModel.ApplyQueueDuration("0h", "0h");
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
        var slotCount = ConstructionSlotCapacity.Resolve(tribe);

        var rows = LiveQueueRowFactory.BuildConstructionRows(
                     activeConstructions,
                     slotCount,
                     snapshot.Knowledge != ConstructionQueueKnowledge.Unknown,
                     nowUtc,
                     FormatQueueFinishTime);
        _travianQueueViewModel.ApplyBuildQueueRows(rows);
    }

    private void RefreshTravianSmithyQueueUi()
    {
        var status = ResolveSelectedVillageBuildingStatus();
        var activeUpgrades = SmithyQueueState.ResolveActiveUpgrades(
            status?.SmithyUpgradeStatus,
            DateTimeOffset.UtcNow);
        var nowUtc = DateTimeOffset.UtcNow;

        var rows = LiveQueueRowFactory.BuildSmithyRows(
                     activeUpgrades,
                     slotCount: 2,
                     status?.SmithyUpgradeStatus is not null,
                     nowUtc,
                     FormatQueueFinishTime);
        _travianQueueViewModel.ApplySmithyQueueRows(rows);
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

        InvalidateVillageOverview();

        _queueUiRefreshPending = true;
        if (_isVillageDropdownOpen)
        {
            _queueUiRefreshTimer.Stop();
            return;
        }

        if (immediate)
        {
            _queueUiRefreshTimer.Stop();
            _queueUiRefreshPending = false;
            var immediateSelectId = _pendingQueueUiSelectId;
            _pendingQueueUiSelectId = null;
            MeasureUiWork("queue refresh", () => RefreshQueueUi(immediateSelectId));
            return;
        }

        _queueUiRefreshTimer.Stop();
        _queueUiRefreshTimer.Start();
    }

    private void VillageComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        _isVillageDropdownOpen = true;
        if (_queueUiRefreshTimer.IsEnabled)
        {
            _queueUiRefreshPending = true;
            _queueUiRefreshTimer.Stop();
        }
    }

    private void VillageComboBox_DropDownClosed(object? sender, EventArgs e)
    {
        _isVillageDropdownOpen = false;
        if (_queueUiRefreshPending)
        {
            _queueUiRefreshTimer.Stop();
            _queueUiRefreshTimer.Start();
        }

        RequestDashboardVillageProjectionRefresh();
    }

    private void QueueSectionTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, QueueSectionTabControl))
        {
            return;
        }

        UpdateQueueClearButtonContent();
        if (ReferenceEquals(QueueSectionTabControl.SelectedItem, HistoryQueueTabItem)
            && _hasQueueDisplayProjection)
        {
            EnsureQueueHistoryProjection();
            _travianQueueViewModel.ApplyHistoryQueueRows(
                FilterQueueRowsForSelectedVillage(_allHistoryQueueRows));
        }
    }
}
