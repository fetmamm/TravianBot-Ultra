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
    // Enough to re-enqueue a removed item (Group/DisplayName are re-derived from the task name on Add).
    private sealed record RemovedQueueSnapshot(string TaskName, Dictionary<string, string> Payload, int Priority, int MaxRetries);

    // Last Remove action's removed set (the selected item plus everything the cascades dropped with it),
    // kept so the user can restore an accidental removal. One-shot: cleared once restored.
    private List<RemovedQueueSnapshot> _lastRemovedQueueItems = [];

    private void QueueRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is not QueueItemRow selected)
        {
            AppendLog("Select a queue item first.");
            return;
        }

        var existingItem = _botService.GetQueueItemsForDisplay().FirstOrDefault(item => item.Id == selected.Id);

        // Warn before removing an item that other queued buildings depend on: removing it would cascade-
        // remove those follow-on items too. Show exactly which ones so the user knows the full effect.
        if (existingItem is not null)
        {
            var alsoRemoved = ComputeBuildingQueueRemovalPreview(existingItem);
            if (alsoRemoved.Count > 0 && !ConfirmCascadingQueueRemoval(existingItem, alsoRemoved))
            {
                return;
            }
        }

        // Snapshot the whole queue before removing so the Redo/Restore button can bring back the selected
        // item AND every dependent/higher-level item the cascades drop along with it.
        var snapshotBefore = _botService.GetQueueItemsForDisplay().ToList();
        if (_botService.RemoveQueueItem(selected.Id))
        {
            if (existingItem is not null)
            {
                ForgetBuildingQueueCachesForItem(existingItem);

                // Removing an "upgrade to level N" for a slot cancels the higher-level upgrades queued for
                // that same slot too: the worker loops each upgrade up to its target, so leaving them would
                // silently keep climbing the building past the level the user just removed. Run this before
                // the generic cascade so requirement/orphan re-validation sees the trimmed queue.
                if (string.Equals(existingItem.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    && TryReadBuildingUpgradePayload(existingItem.Payload, out var removedSlotId, out var removedTarget)
                    && removedTarget.HasValue)
                {
                    CascadeRemoveHigherSameSlotBuildingUpgrades(removedSlotId, removedTarget.Value);
                }
            }

            AppendLog($"Queue item removed: {selected.TaskName}.");
            // Removing a prerequisite (e.g. a Main Building upgrade or a prerequisite construct) can leave
            // dependent building tasks that can no longer be built — drop them too. Also refreshes the UI.
            CascadeRemoveUnsatisfiedBuildingQueueItems();
            RememberRemovedQueueItemsForUndo(snapshotBefore);
            RefreshQueueUi();
            return;
        }

        AppendLog("Could not remove queue item.");
    }

    // Records the items present before a Remove but gone after (selected + cascaded) as the restorable set.
    private void RememberRemovedQueueItemsForUndo(IReadOnlyList<QueueItem> snapshotBefore)
    {
        var survivingIds = _botService.GetQueueItemsForDisplay().Select(item => item.Id).ToHashSet();
        _lastRemovedQueueItems = snapshotBefore
            .Where(item => !survivingIds.Contains(item.Id))
            .Select(item => new RemovedQueueSnapshot(
                item.TaskName,
                new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase),
                item.Priority,
                item.MaxRetries))
            .ToList();
    }

    private void QueueRedoButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_lastRemovedQueueItems.Count == 0)
        {
            AppendLog("Nothing to restore — no recently removed queue items.");
            return;
        }

        var restored = 0;
        foreach (var snapshot in _lastRemovedQueueItems)
        {
            try
            {
                _botService.Enqueue(
                    snapshot.TaskName,
                    new Dictionary<string, string>(snapshot.Payload, StringComparer.OrdinalIgnoreCase),
                    snapshot.Priority,
                    snapshot.MaxRetries);
                restored++;
            }
            catch (Exception ex)
            {
                AppendLog($"Could not restore '{snapshot.TaskName}': {ex.Message}");
            }
        }

        // One-shot: the items are back in the queue, so there is nothing left to redo.
        _lastRemovedQueueItems = [];
        AppendLog($"Restored {restored} removed queue item(s).");
        RefreshQueueUi();
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

            var villageItems = _botService
                .GetQueueItemsForDisplay()
                .Where(IsActiveQueueItem)
                .Where(item => string.Equals(
                    NormalizeVillageName(GetQueueItemVillageName(item)),
                    villageName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (villageItems.Count == 0)
            {
                AppendLog($"Clear village queue: '{villageName}' has no queued tasks.");
                return;
            }

            var removed = 0;
            foreach (var item in villageItems)
            {
                if (!_botService.RemoveQueueItem(item.Id))
                {
                    continue;
                }

                ForgetBuildingQueueCachesForItem(item);
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
        // QueueDataGrid is filtered to the selected village. Read the account-scoped store directly so
        // this command always clears every village, including villages added after the UI was built.
        var activeItems = _botService
            .GetQueueItemsForDisplay()
            .Where(IsActiveQueueItem)
            .ToList();
        if (activeItems.Count == 0)
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
        foreach (var item in activeItems)
        {
            if (!_botService.RemoveQueueItem(item.Id))
            {
                continue;
            }

            ForgetBuildingQueueCachesForItem(item);
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

    private static bool IsActiveQueueItem(QueueItem item)
    {
        return item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused
            || (item.Status == QueueStatus.Failed && !item.IsRuntimeOnly);
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
