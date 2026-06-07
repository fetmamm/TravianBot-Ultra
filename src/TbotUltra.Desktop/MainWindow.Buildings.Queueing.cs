using System;
using System.Collections.Generic;
using System.Linq;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
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
            .Where(item => IsActiveQueueStatus(item.Status))
            .ToList();
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

        if (!BuildingConstructPayload.TryFromDictionary(payload, out var parsed)
            || parsed is null)
        {
            return false;
        }

        slotId = parsed.SlotId;
        gid = parsed.Gid;
        buildingName = parsed.Name ?? string.Empty;
        return true;
    }

    private static bool TryReadBuildingUpgradePayload(
        IReadOnlyDictionary<string, string> payload,
        out int slotId,
        out int? targetLevel)
    {
        slotId = 0;
        targetLevel = null;

        if (!BuildingUpgradePayload.TryFromDictionary(payload, out var parsed)
            || parsed is null)
        {
            return false;
        }

        slotId = parsed.SlotId;
        targetLevel = parsed.TargetLevel;
        return true;
    }

    private static bool IsBuildingConstructForSlot(QueueItem item, out int slotId)
    {
        slotId = 0;
        return string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            && TryReadBuildingConstructPayload(item.Payload, out slotId, out _, out _);
    }

    private static bool IsBuildingUpgradeForSlot(QueueItem item, out int slotId)
    {
        slotId = 0;
        return (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
            && TryReadBuildingUpgradePayload(item.Payload, out slotId, out _);
    }

    // After a queue item is removed, queued buildings can lose a prerequisite (e.g. removing Main
    // Building level 3 leaves a queued Barracks construct that can no longer be built, or removing a
    // construct orphans the upgrades queued for that empty slot). Re-validate the remaining building
    // items for the selected village and remove the ones that are no longer buildable. Cascades: removing
    // one prerequisite can invalidate others, so re-scan until the queue is stable.
    private void CascadeRemoveUnsatisfiedBuildingQueueItems()
    {
        if (_lastBuildingStatus is null)
        {
            return;
        }

        var removedAny = false;
        bool removedThisPass;
        do
        {
            removedThisPass = false;
            var items = GetActiveQueueItems()
                .Where(IsQueueItemForSelectedVillageOrGlobal)
                .ToList();

            foreach (var item in items)
            {
                if (!TryGetUnsatisfiedBuildingRemovalReason(item, out var reason))
                {
                    continue;
                }

                if (_botService.RemoveQueueItem(item.Id))
                {
                    ForgetBuildingQueueCachesForItem(item);
                    AppendLog($"Removed queued {item.TaskName}: {reason}");
                    removedThisPass = true;
                    removedAny = true;
                    break; // queue changed — rebuild the projection and re-scan from the top.
                }
            }
        }
        while (removedThisPass);

        if (removedAny)
        {
            RefreshQueueUi();
        }
    }

    private bool TryGetUnsatisfiedBuildingRemovalReason(QueueItem item, out string reason)
    {
        reason = string.Empty;

        // Construct: its catalog requirements must still be met (by built levels or remaining queued work).
        if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            && TryReadBuildingConstructPayload(item.Payload, out var constructSlotId, out var gid, out _))
        {
            var requirements = BuildingCatalogService.RequirementsFor(gid);
            if (requirements.Count == 0)
            {
                return false;
            }

            var missing = MissingRequirements(_lastBuildingStatus!, requirements);
            if (missing.Count > 0)
            {
                reason = $"slot {constructSlotId} requirement no longer met ({string.Join(", ", missing.Select(req => $"{req.Name} {req.Level}+"))}).";
                return true;
            }

            return false;
        }

        // Upgrade: only an orphaned upgrade (slot is empty AND no construct is queued for it) is removed.
        // Upgrades of already-built buildings don't need their construction requirements re-checked.
        if ((string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
            && TryReadBuildingUpgradePayload(item.Payload, out var upgradeSlotId, out _))
        {
            var slotIsBuilt = _lastBuildingStatus!.Buildings
                .Any(b => b.SlotId == upgradeSlotId && (b.Level ?? 0) > 0);
            if (slotIsBuilt)
            {
                return false;
            }

            var constructQueuedForSlot = GetActiveQueueItems()
                .Where(IsQueueItemForSelectedVillageOrGlobal)
                .Any(other => string.Equals(other.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                    && TryReadBuildingConstructPayload(other.Payload, out var otherSlotId, out _, out _)
                    && otherSlotId == upgradeSlotId);
            if (!constructQueuedForSlot)
            {
                reason = $"slot {upgradeSlotId} is empty and no construct is queued for it.";
                return true;
            }
        }

        return false;
    }

    private void ForgetBuildingQueueCachesForItem(QueueItem item)
    {
        if (TryReadBuildingUpgradePayload(item.Payload, out var upgradeSlotId, out _))
        {
            _buildingLastQueuedTargetBySlot.Remove(upgradeSlotId);
        }

        if (TryReadBuildingConstructPayload(item.Payload, out var constructSlotId, out _, out _))
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

        // Gate NPC trade per village: enabled only when the account-wide master (Auto settings) AND this
        // village's choice are both on. The worker honors this per-task override (BotOptionsPayloadApplier).
        if (!string.IsNullOrWhiteSpace(selectedVillageName) || !string.IsNullOrWhiteSpace(selectedVillageUrl))
        {
            var key = GetVillageKey(selectedVillageUrl, null, null, selectedVillageName);
            payload[BotOptionPayloadKeys.NpcTradeEnabled] = IsNpcTradeEnabledForVillageKey(key) ? "true" : "false";
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
            .Where(item => IsActiveQueueStatus(item.Status))
            // Same-village only: otherwise constructing a slot here would both be blocked by, and even
            // remove (RemoveCoalescedQueueItems below), another village's queued work for the same slot.
            .Where(IsQueueItemForSelectedVillageOrGlobal)
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

        removedCount = RemoveCoalescedQueueItems(relatedItems, ForgetBuildingQueueCachesForItem);

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
        // Regular "upgrade to level N" items are intentionally NOT merged: each keeps its own queue
        // position so intermediate levels that later queued buildings depend on (e.g. Main Building
        // level 3 before a Barracks) are built in order, and the same building can be queued several
        // times (e.g. MB->3 now, MB->10 later). Only "upgrade to max" is deduplicated per slot, since a
        // second max for the same slot would be redundant.
        removedCount = 0;
        effectiveTargetLevel = requestedTargetLevel;

        if (string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
        {
            var existingMax = _botService.GetQueueItemsForDisplay()
                .Where(item => string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
                    && IsActiveQueueStatus(item.Status)
                    && IsQueueItemForSelectedVillageOrGlobal(item))
                .FirstOrDefault(item => TryReadBuildingUpgradePayload(item.Payload, out var existingSlotId, out _)
                    && existingSlotId == slotId);
            if (existingMax is not null)
            {
                enqueued = false;
                return existingMax;
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

        // Only project THIS village's queued work onto its building slots. Otherwise another village's
        // queued construct/upgrade for the same slot number made empty slots look occupied (or faked an
        // "already exists" unique building), so the construct popup showed "no buildings available".
        var effectiveQueueItems = queueItems
            ?? GetActiveQueueItems().Where(IsQueueItemForSelectedVillageOrGlobal).ToList();
        foreach (var item in effectiveQueueItems)
        {
            if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && TryReadBuildingConstructPayload(item.Payload, out var constructSlotId, out var constructGid, out var constructName))
            {
                constructName = string.IsNullOrWhiteSpace(constructName) ? $"gid {constructGid}" : constructName;
                var projected = new Building(constructSlotId, constructName, 0, null, constructGid);
                bySlot[constructSlotId] = projected;
                continue;
            }

            if ((string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                && TryReadBuildingUpgradePayload(item.Payload, out var upgradeSlotId, out var queuedTargetLevel)
                && bySlot.TryGetValue(upgradeSlotId, out var currentProjected))
            {
                var currentLevel = currentProjected.Level ?? 0;
                var targetLevel = currentLevel;
                if (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    && queuedTargetLevel.HasValue)
                {
                    targetLevel = Math.Max(currentLevel, queuedTargetLevel.Value);
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

            if (!TryReadBuildingConstructPayload(item.Payload, out var slotId, out _, out var name)
                || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            result[slotId] = name;
        }

        return result;
    }

    private IReadOnlyDictionary<int, int> GetQueuedBuildingConstructGidsBySlot(IReadOnlyList<QueueItem>? queueItems = null)
    {
        var result = new Dictionary<int, int>();
        foreach (var item in queueItems ?? GetActiveQueueItems())
        {
            if (!string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryReadBuildingConstructPayload(item.Payload, out var slotId, out var gid, out _))
            {
                continue;
            }

            result[slotId] = gid;
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

            if (!TryReadBuildingUpgradePayload(item.Payload, out var slotId, out var targetLevel)
                || !targetLevel.HasValue)
            {
                continue;
            }

            result[slotId] = targetLevel.Value;
        }

        return result;
    }

    private void ClearStaleBuildingPendingCaches(IReadOnlyList<QueueItem>? queueItems = null)
    {
        var activeItems = queueItems ?? GetActiveQueueItems();
        var activeUpgradeSlots = new HashSet<int>();
        var activeConstructSlots = new HashSet<int>();
        var activeDemolishSlots = new HashSet<int>();

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

            if (string.Equals(item.TaskName, "demolish_building_to_level", StringComparison.OrdinalIgnoreCase)
                && item.Payload.TryGetValue(BotOptionPayloadKeys.TargetBuildingSlotOrName, out var demolishSlotText)
                && int.TryParse(demolishSlotText, out var demolishSlotId))
            {
                activeDemolishSlots.Add(demolishSlotId);
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

        // Drop the in-progress demolish highlight (red text) once the demolish task is no
        // longer active in the queue — this covers partial demolitions (target level > 0)
        // where the slot stays occupied and the empty-slot cleanup never fires.
        foreach (var slotId in _buildingDemolishingSlots.Except(activeDemolishSlots).ToList())
        {
            SetDemolishingFlag(slotId, false);
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
            PendingConstructGid = row.PendingConstructGid,
            IsDemolishing = row.IsDemolishing,
            MapLeft = row.MapLeft,
            MapTop = row.MapTop,
            IsWallSlot = row.IsWallSlot,
            IsRallyPointSlot = row.IsRallyPointSlot,
        };
    }

    private void SetPendingBuildingConstruct(int slotId, string buildingName, int gid)
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
            PendingConstructGid = gid,
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
            PendingConstructGid = row.PendingConstructGid,
            IsDemolishing = demolishing,
            MapLeft = row.MapLeft,
            MapTop = row.MapTop,
            IsWallSlot = row.IsWallSlot,
            IsRallyPointSlot = row.IsRallyPointSlot,
        };
    }
}
