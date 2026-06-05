using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
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

        if (WouldMoveBuildingUpgradeBeforeConstruct(selected.Id))
        {
            AppendLog("Building upgrades must stay after their construction item.");
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

        if (WouldMoveBuildingConstructAfterUpgrade(selected.Id))
        {
            AppendLog("Building construction must stay before its queued upgrades.");
            return;
        }

        if (_botService.MoveQueueItemDown(selected.Id))
        {
            RefreshQueueUi(selectId: selected.Id);
            return;
        }

        AppendLog("Move down is only available within the same priority group.");
    }

    private bool WouldMoveBuildingUpgradeBeforeConstruct(Guid selectedId)
    {
        var ordered = _botService.GetQueueItemsForDisplay().ToList();
        var index = ordered.FindIndex(item => item.Id == selectedId);
        if (index <= 0)
        {
            return false;
        }

        var current = ordered[index];
        var previous = ordered[index - 1];
        return current.Group == previous.Group
            && current.Priority == previous.Priority
            && IsBuildingUpgradeForSlot(current, out var upgradeSlotId)
            && IsBuildingConstructForSlot(previous, out var constructSlotId)
            && upgradeSlotId == constructSlotId;
    }

    private bool WouldMoveBuildingConstructAfterUpgrade(Guid selectedId)
    {
        var ordered = _botService.GetQueueItemsForDisplay().ToList();
        var index = ordered.FindIndex(item => item.Id == selectedId);
        if (index < 0 || index >= ordered.Count - 1)
        {
            return false;
        }

        var current = ordered[index];
        var next = ordered[index + 1];
        return current.Group == next.Group
            && current.Priority == next.Priority
            && IsBuildingConstructForSlot(current, out var constructSlotId)
            && IsBuildingUpgradeForSlot(next, out var upgradeSlotId)
            && constructSlotId == upgradeSlotId;
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

            // Clearing the whole account queue (all villages) is destructive — confirm like Stop.
            var choice = AppDialog.ShowCustom(
                this,
                "This clears the queued tasks for ALL villages on this account. Are you sure?",
                "Clear account queue",
                new (string, MessageBoxResult)[]
                {
                    ("Yes", MessageBoxResult.Yes),
                    ("Cancel", MessageBoxResult.Cancel),
                },
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel,
                MessageBoxResult.Cancel);
            if (choice != MessageBoxResult.Yes)
            {
                return;
            }

            ClearActiveQueueItems();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not clear queue: {ex.Message}");
        }
    }

    // Clears only the queued (active) tasks for the village currently selected in the dropdown. Other
    // villages' queues are untouched. No global stop — just removes that village's items.
    private void ClearVillageQueueButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            var villageName = NormalizeVillageName(GetSelectedVillageName());
            if (villageName is null)
            {
                AppendLog("Clear village queue: no village selected.");
                return;
            }

            var rows = (QueueDataGrid.ItemsSource as IEnumerable<QueueItemRow>)?.ToList() ?? [];
            var villageRows = rows
                .Where(row => string.Equals((row.VillageName ?? string.Empty).Trim(), villageName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (villageRows.Count == 0)
            {
                AppendLog($"Clear village queue: '{villageName}' has no queued tasks.");
                return;
            }

            var removed = 0;
            foreach (var row in villageRows)
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

            ClearPendingResourceLevelsFromUi();
            RefreshQueueUi();
            AppendLog($"Cleared {removed} queued task(s) for village '{villageName}'.");
        }
        catch (Exception ex)
        {
            AppendLog($"Could not clear village queue: {ex.Message}");
        }
    }

    private void QueueRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshQueueUi();
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
        _loopController.CancelOperation();
        _loopController.CancelAutoQueueRun();
        _loopController.CancelLoop();

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
}
