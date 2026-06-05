using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private string BuildQueueDisplayName(QueueItem item)
    {
        if (item is null)
        {
            return "-";
        }

        var payload = item.Payload ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var slotId = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeSlotId)
            ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.BuildingUpgradeSlotId)
            ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.BuildingConstructSlotId);
        var targetLevel = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeTargetLevel)
            ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.BuildingUpgradeTargetLevel)
            ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.TargetLevel);
        var resourceName = GetPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeName);
        var buildingName = GetPayloadValue(payload, BotOptionPayloadKeys.BuildingUpgradeName)
            ?? GetPayloadValue(payload, BotOptionPayloadKeys.BuildingConstructName);

        if (string.Equals(item.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase)
            && targetLevel.HasValue)
        {
            return $"Upgrade all resources to level {targetLevel.Value}";
        }

        if (string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
            && ResourceUpgradePayload.TryFromDictionary(payload, out var resourcePayload, ResourceFieldMaxLevel)
            && resourcePayload is not null)
        {
            var name = !string.IsNullOrWhiteSpace(resourcePayload.Name)
                ? resourcePayload.Name
                : ResolveResourceName(resourcePayload.SlotId);
            return !string.IsNullOrWhiteSpace(name)
                ? $"Upgrade {name} slot {resourcePayload.SlotId} to level {resourcePayload.TargetLevel}"
                : $"Upgrade resource slot {resourcePayload.SlotId} to level {resourcePayload.TargetLevel}";
        }

        if (string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
            && targetLevel.HasValue)
        {
            var name = !string.IsNullOrWhiteSpace(resourceName)
                ? resourceName
                : (slotId.HasValue ? ResolveResourceName(slotId.Value) : null);
            return !string.IsNullOrWhiteSpace(name) && slotId.HasValue
                ? $"Upgrade {name} slot {slotId.Value} to level {targetLevel.Value}"
                : !string.IsNullOrWhiteSpace(name)
                    ? $"Upgrade {name} to level {targetLevel.Value}"
                    : $"Upgrade resource slot {slotId ?? 0} to level {targetLevel.Value}";
        }

        if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            && BuildingConstructPayload.TryFromDictionary(payload, out var constructPayload)
            && constructPayload is not null)
        {
            var slotSuffix = $" (slot {constructPayload.SlotId})";
            return !string.IsNullOrWhiteSpace(constructPayload.Name)
                ? $"Construct {constructPayload.Name} to level 1{slotSuffix}"
                : $"Construct building{slotSuffix}";
        }

        if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
        {
            var slotSuffix = slotId.HasValue ? $" (slot {slotId.Value})" : string.Empty;
            return !string.IsNullOrWhiteSpace(buildingName)
                ? $"Construct {buildingName} to level 1{slotSuffix}"
                : $"Construct building{slotSuffix}";
        }

        if (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
            && BuildingUpgradePayload.TryFromDictionary(payload, out var buildingPayload)
            && buildingPayload is { TargetLevel: not null })
        {
            var name = !string.IsNullOrWhiteSpace(buildingPayload.Name)
                ? buildingPayload.Name
                : ResolveBuildingName(buildingPayload.SlotId);
            return !string.IsNullOrWhiteSpace(name)
                ? $"Upgrade {name} to level {buildingPayload.TargetLevel.Value}{BuildSlotSuffix(buildingPayload.SlotId)}"
                : $"Upgrade building slot {buildingPayload.SlotId} to level {buildingPayload.TargetLevel.Value}";
        }

        if (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
            && targetLevel.HasValue)
        {
            var name = !string.IsNullOrWhiteSpace(buildingName)
                ? buildingName
                : (slotId.HasValue ? ResolveBuildingName(slotId.Value) : null);
            return !string.IsNullOrWhiteSpace(name)
                ? $"Upgrade {name} to level {targetLevel.Value}{BuildSlotSuffix(slotId)}"
                : $"Upgrade building slot {slotId ?? 0} to level {targetLevel.Value}";
        }

        if (string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
            && BuildingUpgradePayload.TryFromDictionary(payload, out var buildingMaxPayload)
            && buildingMaxPayload is not null)
        {
            var name = !string.IsNullOrWhiteSpace(buildingMaxPayload.Name)
                ? buildingMaxPayload.Name
                : ResolveBuildingName(buildingMaxPayload.SlotId);
            return !string.IsNullOrWhiteSpace(name)
                ? $"Upgrade {name} to max level{BuildSlotSuffix(buildingMaxPayload.SlotId)}"
                : $"Upgrade building slot {buildingMaxPayload.SlotId} to max level";
        }

        if (string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
        {
            var name = !string.IsNullOrWhiteSpace(buildingName)
                ? buildingName
                : (slotId.HasValue ? ResolveBuildingName(slotId.Value) : null);
            return !string.IsNullOrWhiteSpace(name)
                ? $"Upgrade {name} to max level{BuildSlotSuffix(slotId)}"
                : $"Upgrade building slot {slotId ?? 0} to max level";
        }

        if (string.Equals(item.TaskName, "demolish_building_to_level", StringComparison.OrdinalIgnoreCase)
            && targetLevel.HasValue)
        {
            var targetBuilding = GetPayloadValue(payload, BotOptionPayloadKeys.TargetBuildingSlotOrName);
            return !string.IsNullOrWhiteSpace(targetBuilding)
                ? $"Demolish {targetBuilding} to level {targetLevel.Value}"
                : $"Demolish building to level {targetLevel.Value}";
        }

        if (string.Equals(item.TaskName, "send_farmlists", StringComparison.OrdinalIgnoreCase))
        {
            var names = (GetPayloadValue(payload, BotOptionPayloadKeys.ContinuousFarmListNames) ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return names.Length > 0
                ? $"Send farmlists: {string.Join(", ", names)}"
                : "Send selected farmlists";
        }

        return string.IsNullOrWhiteSpace(item.DisplayName) ? HumanizeTaskName(item.TaskName) : item.DisplayName;
    }

    private static string? GetPayloadValue(IReadOnlyDictionary<string, string> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static int? TryGetIntPayloadValue(IReadOnlyDictionary<string, string> payload, string key)
    {
        return payload.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : null;
    }

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

    private static string BuildSlotSuffix(int? slotId)
    {
        return slotId.HasValue ? $" (slot {slotId.Value})" : string.Empty;
    }

    private void RefreshQueueUi(Guid? selectId = null)
    {
        try
        {
            var ordered = _botService.GetQueueItemsForDisplay().ToList();
            ClearStaleBuildingPendingCaches(ordered);
            _queueServerTimeOffset = ResolveQueueServerTimeOffset();
            var displayRunningId = ResolveDisplayRunningQueueItemId(ordered);
            var rows = ordered
                .Select(item => new QueueItemRow
                {
                    Id = item.Id,
                    Group = item.Group,
                    GroupName = QueueGroupCatalog.GetTitle(item.Group),
                    VillageName = GetQueueItemVillageName(item) ?? "-",
                    VillageKey = GetQueueItemVillageKey(item) ?? string.Empty,
                    DisplayName = BuildQueueDisplayName(item),
                    TaskName = item.TaskName,
                    Status = item.Id == displayRunningId ? QueueStatus.Running : item.Status,
                    Retries = item.Retries,
                    MaxRetries = item.MaxRetries,
                    IsRuntimeOnly = item.IsRuntimeOnly,
                    CreatedAt = item.CreatedAt,
                    NextAttemptAtServer = FormatQueueServerTime(item.NextAttemptAt),
                    CreatedAtServer = FormatQueueServerTime(item.CreatedAt),
                })
                .ToList();
            var activeRows = rows
                .Where(row =>
                    row.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused
                    || (row.Status == QueueStatus.Failed && !row.IsRuntimeOnly))
                .ToList();
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
            SyncPendingResourceTargetsInUi();
            if (_lastBuildingStatus is not null)
            {
                PopulateBuildingsTab(_lastBuildingStatus);
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
