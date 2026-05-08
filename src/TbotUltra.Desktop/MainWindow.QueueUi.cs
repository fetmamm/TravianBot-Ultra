using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker;
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

        if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
        {
            var slotSuffix = slotId.HasValue ? $" (slot {slotId.Value})" : string.Empty;
            return !string.IsNullOrWhiteSpace(buildingName)
                ? $"Construct {buildingName} to level 1{slotSuffix}"
                : $"Construct building{slotSuffix}";
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
        return (ResourcesDataGrid.ItemsSource as IEnumerable<ResourceFieldRow>)
            ?.FirstOrDefault(row => row.SlotId == slotId)
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

    private void QueueAddButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new AddQueueItemWindow(TbotUltra.Core.Tasks.TaskCatalog.AllowedTaskNames)
            {
                Owner = this,
            };
            if (window.ShowDialog() != true)
            {
                return;
            }

            var payload = window.Payload is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(window.Payload, StringComparer.OrdinalIgnoreCase);
            var selectedVillageName = GetSelectedVillageName();
            var selectedVillageUrl = GetSelectedVillageUrl();
            if (!string.IsNullOrWhiteSpace(selectedVillageName))
            {
                payload[BotOptionPayloadKeys.TargetVillageName] = selectedVillageName;
            }

            if (!string.IsNullOrWhiteSpace(selectedVillageUrl))
            {
                payload[BotOptionPayloadKeys.TargetVillageUrl] = selectedVillageUrl;
            }

            var item = _botService.Enqueue(window.TaskName, payload, window.Priority, window.MaxRetries);
            AppendLog($"Queue item added: {item.TaskName} (priority={item.Priority}).");
            RefreshQueueUi();
            TriggerQueueAutoRunFromEnqueue();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not add queue item: {ex.Message}");
        }
    }

    private void QueueRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is not QueueItemRow selected)
        {
            AppendLog("Select a queue item first.");
            return;
        }

        var existingItem = _botService.GetQueueItemsForDisplay().FirstOrDefault(item => item.Id == selected.Id);
        if (_botService.RemoveQueueItem(selected.Id))
        {
            if (existingItem is not null)
            {
                ForgetBuildingQueueCachesForItem(existingItem);
            }

            AppendLog($"Queue item removed: {selected.TaskName}.");
            RefreshQueueUi();
            return;
        }

        AppendLog("Could not remove queue item.");
    }

    private void QueueMoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is not QueueItemRow selected)
        {
            AppendLog("Select a queue item first.");
            return;
        }

        if (_botService.MoveQueueItemUp(selected.Id))
        {
            RefreshQueueUi(selectId: selected.Id);
            return;
        }

        AppendLog("Move up is only available within the same priority group.");
    }

    private void QueueMoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is not QueueItemRow selected)
        {
            AppendLog("Select a queue item first.");
            return;
        }

        if (_botService.MoveQueueItemDown(selected.Id))
        {
            RefreshQueueUi(selectId: selected.Id);
            return;
        }

        AppendLog("Move down is only available within the same priority group.");
    }

    private void QueueRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is not QueueItemRow selected)
        {
            AppendLog("Select a queue item first.");
            return;
        }

        if (_botService.RetryQueueItem(selected.Id))
        {
            AppendLog($"Queue item reset for retry: {selected.TaskName}.");
            RefreshQueueUi(selectId: selected.Id);
            return;
        }

        AppendLog("Retry is only available for failed or paused items.");
    }

    private void QueueClearButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ReferenceEquals(QueueSectionTabControl?.SelectedItem, HistoryQueueTabItem))
            {
                ClearHistoryQueueItems();
                return;
            }

            ClearActiveQueueItems();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not clear queue: {ex.Message}");
        }
    }

    private void QueueRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshQueueUi();
    }

    private void QueuePopoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_queuePopupWindow is not null)
        {
            _queuePopupWindow.Activate();
            return;
        }

        var activeGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8),
            ItemsSource = QueueDataGrid.ItemsSource,
        };
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding("GroupName"), Width = new DataGridLength(1.15, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Task", Binding = new Binding("DisplayName"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Retries", Binding = new Binding("RetriesText"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var historyGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            BorderThickness = new Thickness(1),
            ItemsSource = QueueHistoryDataGrid.ItemsSource,
        };
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding("GroupName"), Width = new DataGridLength(1.15, DataGridLengthUnitType.Star) });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Completed task", Binding = new Binding("DisplayName"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Created", Binding = new Binding("CreatedAtServer"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });

        var closeButton = new Button
        {
            Content = "Close",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(activeGrid);
        Grid.SetRow(historyGrid, 1);
        root.Children.Add(historyGrid);
        Grid.SetRow(closeButton, 2);
        root.Children.Add(closeButton);

        _queuePopupWindow = new Window
        {
            Title = "Queue",
            Width = 700,
            Height = 400,
            MinWidth = 580,
            MinHeight = 320,
            Content = root,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left + Width + 10,
            Top = Top + 30,
        };
        closeButton.Click += (_, _) => _queuePopupWindow?.Close();
        _queuePopupWindow.Closed += (_, _) => _queuePopupWindow = null;
        _queuePopupWindow.Show();
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

            QueueDataGrid.ItemsSource = activeRows;
            QueueHistoryDataGrid.ItemsSource = historyRows;
            SyncPendingResourceTargetsInUi();
            if (_lastBuildingStatus is not null)
            {
                PopulateBuildingsTab(_lastBuildingStatus);
            }

            if (selectId.HasValue)
            {
                var selected = activeRows.FirstOrDefault(item => item.Id == selectId.Value);
                if (selected is not null)
                {
                    QueueDataGrid.SelectedItem = selected;
                }
            }

            QueueInfoTextBlock.Text = $"Queue active: {activeRows.Count} | done: {historyRows.Count}";
            UpdateQueueClearButtonContent();
            if (_queuePopupWindow?.Content is Grid queuePopupRoot && queuePopupRoot.Children.Count >= 2)
            {
                if (queuePopupRoot.Children[0] is DataGrid popupActiveGrid)
                {
                    popupActiveGrid.ItemsSource = activeRows;
                }

                if (queuePopupRoot.Children[1] is DataGrid popupHistoryGrid)
                {
                    popupHistoryGrid.ItemsSource = historyRows;
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
            : "Clear active queue";
    }

    private void ClearActiveQueueItems()
    {
        var activeRows = (QueueDataGrid.ItemsSource as IEnumerable<QueueItemRow>)?.ToList() ?? [];
        if (activeRows.Count == 0)
        {
            AppendLog("Active queue is already empty.");
            return;
        }

        _loopController.RequestLoopStop();
        _loopController.RequestQueueStop();
        _operationCts?.Cancel();
        _autoQueueRunCts?.Cancel();
        _loopCts?.Cancel();

        var removed = 0;
        foreach (var row in activeRows)
        {
            var existingItem = _botService.GetQueueItemsForDisplay().FirstOrDefault(item => item.Id == row.Id);
            if (!_botService.RemoveQueueItem(row.Id))
            {
                continue;
            }

            if (existingItem is not null)
            {
                ForgetBuildingQueueCachesForItem(existingItem);
            }

            removed += 1;
        }

        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        ClearPendingResourceLevelsFromUi();
        RefreshQueueUi();
        AppendLog(removed > 0
            ? "Active queue cleared and running actions stopped."
            : "Could not clear active queue.");
    }

    private void ClearHistoryQueueItems()
    {
        var historyRows = (QueueHistoryDataGrid.ItemsSource as IEnumerable<QueueItemRow>)?.ToList() ?? [];
        if (historyRows.Count == 0)
        {
            AppendLog("History is already empty.");
            return;
        }

        var removed = 0;
        foreach (var row in historyRows)
        {
            if (_botService.RemoveQueueItem(row.Id))
            {
                removed += 1;
            }
        }

        RefreshQueueUi();
        AppendLog(removed > 0
            ? "Queue history cleared."
            : "Could not clear queue history.");
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
