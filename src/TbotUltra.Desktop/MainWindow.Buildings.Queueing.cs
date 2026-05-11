using System;
using System.Collections.Generic;
using System.Linq;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static bool IsBuildingMutationTask(string taskName) =>
        string.Equals(taskName, "load_buildings_snapshot", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "demolish_building_to_level", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<QueueItem> GetActiveQueueItems()
    {
        return _botService.GetQueueItemsForDisplay()
            .Where(item => item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused)
            .ToList();
    }

    private static bool IsActiveBuildingQueueStatus(QueueStatus status)
    {
        return status is QueueStatus.Pending or QueueStatus.Paused or QueueStatus.Running;
    }

    private static bool TryReadBuildingConstructPayload(
        IReadOnlyDictionary<string, string> payload,
        out int slotId,
        out int gid,
        out string buildingName)
    {
        slotId = 0;
        gid = 0;
        buildingName = string.Empty;

        if (!payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructSlotId, out var slotRaw)
            || !int.TryParse(slotRaw, out slotId))
        {
            return false;
        }

        if (!payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructGid, out var gidRaw)
            || !int.TryParse(gidRaw, out gid))
        {
            return false;
        }

        if (payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructName, out var nameRaw))
        {
            buildingName = nameRaw?.Trim() ?? string.Empty;
        }

        return true;
    }

    private static bool TryReadBuildingUpgradePayload(
        IReadOnlyDictionary<string, string> payload,
        out int slotId,
        out int? targetLevel)
    {
        slotId = 0;
        targetLevel = null;

        if (!payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeSlotId, out var slotRaw)
            || !int.TryParse(slotRaw, out slotId))
        {
            return false;
        }

        if (payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeTargetLevel, out var targetRaw)
            && int.TryParse(targetRaw, out var parsedTargetLevel))
        {
            targetLevel = parsedTargetLevel;
        }

        return true;
    }

    private void ForgetBuildingQueueCachesForItem(QueueItem item)
    {
        if (item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeSlotId, out var upgradeSlotRaw)
            && int.TryParse(upgradeSlotRaw, out var upgradeSlotId))
        {
            _buildingLastQueuedTargetBySlot.Remove(upgradeSlotId);
        }

        if (item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructSlotId, out var constructSlotRaw)
            && int.TryParse(constructSlotRaw, out var constructSlotId))
        {
            _buildingLastQueuedConstructBySlot.Remove(constructSlotId);
        }
    }

    private void ApplySelectedVillageToPayload(Dictionary<string, string> payload)
    {
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
    }

    private QueueItem? EnqueueBuildingConstructTaskCoalesced(
        Dictionary<string, string> payload,
        int slotId,
        int gid,
        out bool enqueued,
        out int removedCount)
    {
        var relatedItems = _botService.GetQueueItemsForDisplay()
            .Where(item => IsActiveBuildingQueueStatus(item.Status))
            .Where(item =>
            {
                if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                    && TryReadBuildingConstructPayload(item.Payload, out var existingSlotId, out _, out _))
                {
                    return existingSlotId == slotId;
                }

                if ((string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                    && TryReadBuildingUpgradePayload(item.Payload, out var existingUpgradeSlotId, out _))
                {
                    return existingUpgradeSlotId == slotId;
                }

                return false;
            })
            .ToList();

        var matchingConstruct = relatedItems
            .Where(item => string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(item =>
                TryReadBuildingConstructPayload(item.Payload, out _, out var existingGid, out _)
                && existingGid == gid);
        if (matchingConstruct is not null)
        {
            enqueued = false;
            removedCount = 0;
            return matchingConstruct;
        }

        removedCount = 0;
        foreach (var related in relatedItems.Where(item => item.Status is QueueStatus.Pending or QueueStatus.Paused))
        {
            if (_botService.RemoveQueueItem(related.Id))
            {
                ForgetBuildingQueueCachesForItem(related);
                removedCount += 1;
            }
        }

        ApplySelectedVillageToPayload(payload);
        var created = _botService.Enqueue("construct_building", payload, priority: 0, maxRetries: 3);
        enqueued = true;
        return created;
    }

    private QueueItem? EnqueueBuildingUpgradeTaskCoalesced(
        string taskName,
        Dictionary<string, string> payload,
        int slotId,
        int? requestedTargetLevel,
        out int? effectiveTargetLevel,
        out bool enqueued,
        out int removedCount)
    {
        var relatedItems = _botService.GetQueueItemsForDisplay()
            .Where(item =>
                (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                && IsActiveBuildingQueueStatus(item.Status))
            .Select(item =>
            {
                var parsed = TryReadBuildingUpgradePayload(item.Payload, out var parsedSlotId, out var parsedTargetLevel);
                return new
                {
                    Item = item,
                    Parsed = parsed,
                    SlotId = parsedSlotId,
                    TargetLevel = parsedTargetLevel,
                    IsMax = string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase),
                };
            })
            .Where(item => item.Parsed && item.SlotId == slotId)
            .ToList();

        var existingMax = relatedItems.FirstOrDefault(item => item.IsMax);
        var highestExistingTarget = relatedItems
            .Where(item => item.TargetLevel.HasValue)
            .Select(item => item.TargetLevel!.Value)
            .DefaultIfEmpty(0)
            .Max();
        effectiveTargetLevel = requestedTargetLevel.HasValue
            ? Math.Max(requestedTargetLevel.Value, highestExistingTarget)
            : highestExistingTarget > 0 ? highestExistingTarget : null;

        if (string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
        {
            if (existingMax is not null)
            {
                enqueued = false;
                removedCount = 0;
                return existingMax.Item;
            }
        }
        else if (requestedTargetLevel.HasValue)
        {
            if (existingMax is not null || highestExistingTarget >= requestedTargetLevel.Value)
            {
                enqueued = false;
                removedCount = 0;
                return relatedItems
                    .OrderByDescending(item => item.IsMax)
                    .ThenByDescending(item => item.TargetLevel ?? 0)
                    .ThenBy(item => item.Item.CreatedAt)
                    .Select(item => item.Item)
                    .FirstOrDefault();
            }
        }

        removedCount = 0;
        foreach (var related in relatedItems.Where(item => item.Item.Status is QueueStatus.Pending or QueueStatus.Paused))
        {
            if (_botService.RemoveQueueItem(related.Item.Id))
            {
                ForgetBuildingQueueCachesForItem(related.Item);
                removedCount += 1;
            }
        }

        if (effectiveTargetLevel.HasValue)
        {
            payload[BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = effectiveTargetLevel.Value.ToString();
        }

        ApplySelectedVillageToPayload(payload);
        var created = _botService.Enqueue(taskName, payload, priority: 0, maxRetries: 3);
        enqueued = true;
        return created;
    }

    private VillageStatus BuildProjectedBuildingStatus(VillageStatus status, IReadOnlyList<QueueItem>? queueItems = null)
    {
        var projectedBuildings = status.Buildings
            .Select(item => item with { })
            .ToList();
        var bySlot = projectedBuildings
            .Where(item => item.SlotId is not null)
            .GroupBy(item => item.SlotId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Level ?? 0).First());

        foreach (var item in queueItems ?? GetActiveQueueItems())
        {
            if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructSlotId, out var constructSlotRaw)
                && int.TryParse(constructSlotRaw, out var constructSlotId)
                && item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructGid, out var constructGidRaw)
                && int.TryParse(constructGidRaw, out var constructGid))
            {
                var constructName = item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructName, out var queuedConstructName)
                    ? queuedConstructName
                    : $"gid {constructGid}";
                var projected = new Building(constructSlotId, constructName, 0, null, constructGid);
                bySlot[constructSlotId] = projected;
                continue;
            }

            if ((string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                && item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeSlotId, out var upgradeSlotRaw)
                && int.TryParse(upgradeSlotRaw, out var upgradeSlotId)
                && bySlot.TryGetValue(upgradeSlotId, out var currentProjected))
            {
                var currentLevel = currentProjected.Level ?? 0;
                var targetLevel = currentLevel;
                if (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    && item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeTargetLevel, out var upgradeTargetRaw)
                    && int.TryParse(upgradeTargetRaw, out var parsedTargetLevel))
                {
                    targetLevel = Math.Max(currentLevel, parsedTargetLevel);
                }
                else if (currentProjected.Gid is int currentGid)
                {
                    targetLevel = Math.Max(currentLevel, BuildingCatalogService.MaxLevelFor(currentGid));
                }

                bySlot[upgradeSlotId] = currentProjected with { Level = targetLevel };
            }
        }

        return status with { Buildings = bySlot.Values.ToList() };
    }

    private IReadOnlyDictionary<int, string> GetQueuedBuildingConstructsBySlot(IReadOnlyList<QueueItem>? queueItems = null)
    {
        var result = new Dictionary<int, string>();
        foreach (var item in queueItems ?? GetActiveQueueItems())
        {
            if (!string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructSlotId, out var slotRaw)
                || !int.TryParse(slotRaw, out var slotId))
            {
                continue;
            }

            if (item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructName, out var name)
                && !string.IsNullOrWhiteSpace(name))
            {
                result[slotId] = name;
            }
        }

        return result;
    }

    private IReadOnlyDictionary<int, int> GetQueuedBuildingTargetsBySlot(IReadOnlyList<QueueItem>? queueItems = null)
    {
        var result = new Dictionary<int, int>();
        foreach (var item in queueItems ?? GetActiveQueueItems())
        {
            if (!string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeSlotId, out var slotRaw)
                || !int.TryParse(slotRaw, out var slotId))
            {
                continue;
            }

            if (item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeTargetLevel, out var targetRaw)
                && int.TryParse(targetRaw, out var targetLevel))
            {
                result[slotId] = targetLevel;
            }
        }

        return result;
    }

    private void ClearStaleBuildingPendingCaches(IReadOnlyList<QueueItem>? queueItems = null)
    {
        var activeItems = queueItems ?? GetActiveQueueItems();
        var activeUpgradeSlots = new HashSet<int>();
        var activeConstructSlots = new HashSet<int>();

        foreach (var item in activeItems)
        {
            if ((string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                && TryReadBuildingUpgradePayload(item.Payload, out var slotId, out _))
            {
                activeUpgradeSlots.Add(slotId);
            }

            if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && TryReadBuildingConstructPayload(item.Payload, out var constructSlotId, out _, out _))
            {
                activeConstructSlots.Add(constructSlotId);
            }
        }

        foreach (var slotId in _buildingLastQueuedTargetBySlot.Keys.Except(activeUpgradeSlots).ToList())
        {
            _buildingLastQueuedTargetBySlot.Remove(slotId);
        }

        foreach (var slotId in _buildingLastQueuedConstructBySlot.Keys.Except(activeConstructSlots).ToList())
        {
            _buildingLastQueuedConstructBySlot.Remove(slotId);
        }
    }

    private void SetPendingBuildingUpgrade(int slotId, int targetLevel)
    {
        var index = -1;
        for (var i = 0; i < _buildingRows.Count; i++)
        {
            if (_buildingRows[i].SlotId == slotId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var row = _buildingRows[index];
        _buildingRows[index] = new BuildingSlotRow
        {
            SlotId = row.SlotId,
            Name = row.Name,
            Level = row.Level,
            Gid = row.Gid,
            Category = row.Category,
            Requirements = row.Requirements,
            PendingTargetLevel = targetLevel,
            PendingConstructName = row.PendingConstructName,
            IsDemolishing = row.IsDemolishing,
            MapLeft = row.MapLeft,
            MapTop = row.MapTop,
            IsWallSlot = row.IsWallSlot,
            IsRallyPointSlot = row.IsRallyPointSlot,
        };
    }

    private void SetPendingBuildingConstruct(int slotId, string buildingName)
    {
        var index = -1;
        for (var i = 0; i < _buildingRows.Count; i++)
        {
            if (_buildingRows[i].SlotId == slotId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var row = _buildingRows[index];
        _buildingRows[index] = new BuildingSlotRow
        {
            SlotId = row.SlotId,
            Name = row.Name,
            Level = row.Level,
            Gid = row.Gid,
            Category = row.Category,
            Requirements = row.Requirements,
            PendingTargetLevel = row.PendingTargetLevel,
            PendingConstructName = buildingName,
            IsDemolishing = row.IsDemolishing,
            MapLeft = row.MapLeft,
            MapTop = row.MapTop,
            IsWallSlot = row.IsWallSlot,
            IsRallyPointSlot = row.IsRallyPointSlot,
        };
    }

    private void SetDemolishingFlag(int slotId, bool demolishing)
    {
        if (demolishing)
        {
            _buildingDemolishingSlots.Add(slotId);
        }
        else
        {
            _buildingDemolishingSlots.Remove(slotId);
        }

        var index = -1;
        for (var i = 0; i < _buildingRows.Count; i++)
        {
            if (_buildingRows[i].SlotId == slotId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var row = _buildingRows[index];
        if (row.IsDemolishing == demolishing)
        {
            return;
        }

        _buildingRows[index] = new BuildingSlotRow
        {
            SlotId = row.SlotId,
            Name = row.Name,
            Level = row.Level,
            Gid = row.Gid,
            Category = row.Category,
            Requirements = row.Requirements,
            PendingTargetLevel = row.PendingTargetLevel,
            PendingConstructName = row.PendingConstructName,
            IsDemolishing = demolishing,
            MapLeft = row.MapLeft,
            MapTop = row.MapTop,
            IsWallSlot = row.IsWallSlot,
            IsRallyPointSlot = row.IsRallyPointSlot,
        };
    }
}
