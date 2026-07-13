using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private async Task<bool> TryHandleStorageCapacityDependencyAsync(
        QueueItem item,
        Dictionary<string, string> updatedPayload)
    {
        var status = ResolveStorageDependencyStatus(item);
        var payloadWarehouseCapacity =
            ReadNullableLongPayload(updatedPayload, BotOptionPayloadKeys.UpgradeWarehouseCapacity);
        var payloadGranaryCapacity =
            ReadNullableLongPayload(updatedPayload, BotOptionPayloadKeys.UpgradeGranaryCapacity);
        if (status is null
            || status.Buildings.Count == 0
            || (payloadWarehouseCapacity is not > 0 && status.WarehouseCapacity is not > 0)
            || (payloadGranaryCapacity is not > 0 && status.GranaryCapacity is not > 0))
        {
            try
            {
                await RefreshConstructionStatusAsync(_loopController.AcquireSessionScopeToken());
            }
            catch (Exception ex)
            {
                AppendLog($"Storage dependency building refresh skipped: {ex.Message}");
            }

            status = ResolveStorageDependencyStatus(item);
        }

        var block = ResolveStorageCapacityBlock(updatedPayload, status);
        if (block is null)
        {
            block = ResolveExplicitStorageCapacityBlock(updatedPayload, status);
            if (block is null)
            {
                return false;
            }
        }

        if (status is null || status.Buildings.Count == 0)
        {
            AppendLog(
                $"Storage dependency not queued for task '{item.TaskName}': building snapshot unavailable.");
            return false;
        }

        var queueItems = _botService.GetQueueItemsForDisplay();
        if (updatedPayload.TryGetValue(BotOptionPayloadKeys.StorageDependencyItemId, out var dependencyIdRaw)
            && Guid.TryParse(dependencyIdRaw, out var dependencyId))
        {
            var dependency = queueItems.FirstOrDefault(candidate => candidate.Id == dependencyId);
            if (dependency is { Status: QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused })
            {
                PersistStorageCapacityWait(item, updatedPayload);
                return true;
            }

            if (dependency?.Status == QueueStatus.Failed)
            {
                PersistStorageCapacityWait(item, updatedPayload);
                PauseStorageCapacityParent(
                    item,
                    $"automatic {block.Kind} dependency {dependency.Id} failed");
                return true;
            }

            updatedPayload.Remove(BotOptionPayloadKeys.StorageDependencyItemId);
        }

        var villageName = NormalizeVillageName(GetQueueItemVillageName(item));
        var queuedConstructSlots = queueItems
            .Where(candidate => ConstructionQueueState.IsActiveQueueStatus(candidate.Status))
            .Where(candidate => villageName is null
                || string.Equals(
                    NormalizeVillageName(GetQueueItemVillageName(candidate)),
                    villageName,
                    StringComparison.OrdinalIgnoreCase))
            .Where(candidate => IsBuildingConstructForSlot(candidate, out _))
            .Select(candidate =>
            {
                IsBuildingConstructForSlot(candidate, out var slotId);
                return slotId;
            })
            .ToHashSet();
        var plan = StorageCapacityDependencyPlanner.Plan(
            block.Kind,
            status,
            queuedConstructSlots,
            DateTimeOffset.UtcNow);

        PersistStorageCapacityWait(item, updatedPayload);
        if (plan.Action == StorageDependencyAction.Wait)
        {
            _botService.UpdateDeferredQueueItem(
                item.Id,
                updatedPayload,
                TimeSpan.FromSeconds(plan.WaitSeconds));
            AppendLog(
                $"[storage-capacity] {block.Kind} capacity {block.CurrentCapacity:N0}/{block.RequiredCapacity:N0}. " +
                $"{plan.Reason}; original task retries in {plan.WaitSeconds}s.");
            return true;
        }

        if (plan.Action == StorageDependencyAction.Pause)
        {
            PauseStorageCapacityParent(
                item,
                $"{block.Kind} capacity {block.CurrentCapacity:N0}/{block.RequiredCapacity:N0}; {plan.Reason}");
            return true;
        }

        var dependencyPayload = StorageCapacityDependencyPlanner.BuildDependencyPayload(
            plan,
            item.Id,
            updatedPayload);
        var taskName = plan.Action == StorageDependencyAction.Upgrade
            ? "upgrade_building_to_level"
            : "construct_building";
        var priority = item.Priority < int.MaxValue ? item.Priority + 1 : item.Priority;
        var dependencyItem = _botService.Enqueue(
            taskName,
            dependencyPayload,
            priority,
            maxRetries: 3);

        updatedPayload[BotOptionPayloadKeys.StorageDependencyItemId] = dependencyItem.Id.ToString();
        if (!_botService.UpdateDeferredQueueItem(item.Id, updatedPayload))
        {
            _botService.RemoveQueueItem(dependencyItem.Id);
            AppendLog(
                $"ALARM: could not link automatic {block.Kind} dependency to task '{item.TaskName}'.");
            return false;
        }

        item.Payload = updatedPayload;
        AppendLog(
            $"[storage-capacity] {block.Kind} capacity {block.CurrentCapacity:N0}/{block.RequiredCapacity:N0}. " +
            $"Queued next: {plan.Reason} (dependency {dependencyItem.Id}).");
        RequestQueueUiRefresh(selectId: dependencyItem.Id);
        return true;
    }

    private async Task HandleStorageDependencySucceededAsync(QueueItem dependencyItem)
    {
        if (!TryReadStorageDependencyParentId(dependencyItem, out var parentId))
        {
            return;
        }

        var parent = _botService.GetQueueItemsForDisplay()
            .FirstOrDefault(item => item.Id == parentId);
        if (parent is not { Status: QueueStatus.Pending })
        {
            return;
        }

        var status = ResolveStorageDependencyStatus(dependencyItem);
        var kind = ReadStorageDependencyKind(dependencyItem.Payload);
        if (status is not null && kind.HasValue)
        {
            var plan = StorageCapacityDependencyPlanner.Plan(
                kind.Value,
                status,
                [],
                DateTimeOffset.UtcNow);
            if (plan.Action == StorageDependencyAction.Wait)
            {
                _botService.UpdateDeferredQueueItem(
                    parent.Id,
                    parent.Payload,
                    TimeSpan.FromSeconds(plan.WaitSeconds));
                AppendLog(
                    $"[storage-capacity] dependency queued in Travian; original task retries after {plan.WaitSeconds}s.");
                return;
            }
        }

        if (_botService.UpdateDeferredQueueItem(parent.Id, parent.Payload, TimeSpan.Zero))
        {
            AppendLog(
                $"[storage-capacity] dependency complete; original task '{parent.TaskName}' retries now.");
        }

        await Task.CompletedTask;
    }

    private void HandleStorageDependencyFailed(QueueItem dependencyItem, string errorMessage)
    {
        if (!TryReadStorageDependencyParentId(dependencyItem, out var parentId))
        {
            return;
        }

        var latestDependency = _botService.GetQueueItemsForDisplay()
            .FirstOrDefault(item => item.Id == dependencyItem.Id);
        if (latestDependency?.Status != QueueStatus.Failed)
        {
            return;
        }

        var parent = _botService.GetQueueItemsForDisplay()
            .FirstOrDefault(item => item.Id == parentId);
        if (parent is { Status: QueueStatus.Pending })
        {
            _botService.PauseQueueItem(parent.Id);
        }

        AppendLog(
            $"ALARM: automatic storage dependency failed. Original task '{parent?.TaskName ?? parentId.ToString()}' paused. " +
            $"Error: {errorMessage}");
    }

    private VillageStatus? ResolveStorageDependencyStatus(QueueItem item)
    {
        var villageName = NormalizeVillageName(GetQueueItemVillageName(item));
        if (villageName is not null
            && _villageStatusCacheByName.TryGetValue(villageName, out var cached))
        {
            return cached;
        }

        return _lastBuildingStatus is not null
            && (villageName is null
                || string.Equals(
                    NormalizeVillageName(_lastBuildingStatus.ActiveVillage),
                    villageName,
                    StringComparison.OrdinalIgnoreCase))
                ? _lastBuildingStatus
                : null;
    }

    private static StorageCapacityBlock? ResolveStorageCapacityBlock(
        IReadOnlyDictionary<string, string> payload,
        VillageStatus? status,
        bool preferLiveStatus = false)
    {
        var payloadWarehouseCapacity =
            ReadNullableLongPayload(payload, BotOptionPayloadKeys.UpgradeWarehouseCapacity);
        var payloadGranaryCapacity =
            ReadNullableLongPayload(payload, BotOptionPayloadKeys.UpgradeGranaryCapacity);
        return StorageCapacityDependencyPlanner.ResolveBlock(
            ReadLongPayload(payload, BotOptionPayloadKeys.UpgradeRequiredWood),
            ReadLongPayload(payload, BotOptionPayloadKeys.UpgradeRequiredClay),
            ReadLongPayload(payload, BotOptionPayloadKeys.UpgradeRequiredIron),
            ReadLongPayload(payload, BotOptionPayloadKeys.UpgradeRequiredCrop),
            preferLiveStatus
                ? status?.WarehouseCapacity ?? payloadWarehouseCapacity
                : payloadWarehouseCapacity ?? status?.WarehouseCapacity,
            preferLiveStatus
                ? status?.GranaryCapacity ?? payloadGranaryCapacity
                : payloadGranaryCapacity ?? status?.GranaryCapacity);
    }

    private static StorageCapacityBlock? ResolveExplicitStorageCapacityBlock(
        IReadOnlyDictionary<string, string> payload,
        VillageStatus? status)
    {
        if (!payload.TryGetValue(BotOptionPayloadKeys.UpgradeStorageCapacityKind, out var rawKind)
            || !Enum.TryParse<StorageCapacityKind>(rawKind, true, out var kind))
        {
            return null;
        }

        var currentCapacity = kind == StorageCapacityKind.Warehouse
            ? status?.WarehouseCapacity
            : status?.GranaryCapacity;
        return new StorageCapacityBlock(
            kind,
            (currentCapacity ?? 0) + 1,
            currentCapacity ?? 0);
    }

    private void PersistStorageCapacityWait(
        QueueItem item,
        Dictionary<string, string> payload)
    {
        payload[BotOptionPayloadKeys.UpgradeDeferReason] =
            BotOptionPayloadKeys.UpgradeDeferReasonStorageCapacity;
        payload[BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
            ConstructionQueueState.CurrentDeferClassificationVersion;
        _botService.UpdateDeferredQueueItem(item.Id, payload);
        item.Payload = payload;
    }

    private void PauseStorageCapacityParent(QueueItem item, string reason)
    {
        _botService.PauseQueueItem(item.Id);
        var villageName = GetQueueItemVillageName(item);
        var villagePart = string.IsNullOrWhiteSpace(villageName) ? string.Empty : $" village='{villageName}'";
        AppendLog(
            $"ALARM: task '{item.TaskName}'{villagePart} paused: {reason}.");
        RequestQueueUiRefresh(selectId: item.Id);
    }

    private static bool TryReadStorageDependencyParentId(QueueItem item, out Guid parentId)
    {
        parentId = Guid.Empty;
        return item.Payload.TryGetValue(BotOptionPayloadKeys.StorageDependencyParentId, out var raw)
            && Guid.TryParse(raw, out parentId);
    }

    private static StorageCapacityKind? ReadStorageDependencyKind(
        IReadOnlyDictionary<string, string> payload)
    {
        return payload.TryGetValue(BotOptionPayloadKeys.StorageDependencyKind, out var raw)
            && Enum.TryParse<StorageCapacityKind>(raw, true, out var kind)
                ? kind
                : null;
    }

    private static long ReadLongPayload(
        IReadOnlyDictionary<string, string> payload,
        string key)
    {
        return ReadNullableLongPayload(payload, key) ?? 0;
    }

    private static long? ReadNullableLongPayload(
        IReadOnlyDictionary<string, string> payload,
        string key)
    {
        return payload.TryGetValue(key, out var raw)
            && long.TryParse(raw, out var value)
                ? value
                : null;
    }
}
