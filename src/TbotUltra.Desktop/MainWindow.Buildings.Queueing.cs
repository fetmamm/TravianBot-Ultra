using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static bool IsBuildingMutationTask(string taskName) =>
        string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
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
        var removedAny = false;
        bool removedThisPass;
        do
        {
            removedThisPass = false;
            // Validate every village's building items against ITS OWN status and queue — building
            // requirements are per-village. Removals come from the selected-village-filtered grid, but a
            // multi-village queue can still hold dependents in other villages; checking each against its own
            // cached status keeps them from being wrongly kept (or wrongly removed by another village's queue).
            var items = GetActiveQueueItems()
                .Where(item => IsBuildingMutationTask(item.TaskName))
                .ToList();

            foreach (var item in items)
            {
                // No snapshot for this item's village (never loaded/cached) → can't validate, leave it.
                // Selected/village-less items fall back to the cached selected-village status (risk: removing
                // a prerequisite before buildings were read would otherwise skip cleanup entirely).
                var villageStatus = ResolveBuildingStatusForQueueItem(item);
                if (villageStatus is null)
                {
                    continue;
                }

                var queueFilter = BuildSameVillageQueueFilter(item);
                if (!TryGetUnsatisfiedBuildingRemovalReason(item, villageStatus, queueFilter, out var reason))
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

    // Non-mutating dry run of what removing `selected` would drop besides itself: the same-slot higher-level
    // upgrades plus everything the requirement/orphan cascade would then remove. Mirrors the real removal
    // order (same-slot first, then cascade fixpoint) so the preview matches what actually happens. Returns
    // the extra items (selected excluded), in queue order.
    private List<QueueItem> ComputeBuildingQueueRemovalPreview(QueueItem selected)
    {
        var active = GetActiveQueueItems();
        var removedIds = new HashSet<Guid> { selected.Id };

        // Same-slot higher upgrades (mirrors CascadeRemoveHigherSameSlotBuildingUpgrades).
        if (string.Equals(selected.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
            && TryReadBuildingUpgradePayload(selected.Payload, out var selectedSlotId, out var selectedTarget)
            && selectedTarget.HasValue)
        {
            foreach (var item in active.Where(IsQueueItemForSelectedVillageOrGlobal))
            {
                if (removedIds.Contains(item.Id))
                {
                    continue;
                }

                if (string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
                    && TryReadBuildingUpgradePayload(item.Payload, out var maxSlotId, out _)
                    && maxSlotId == selectedSlotId)
                {
                    removedIds.Add(item.Id);
                }
                else if (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    && TryReadBuildingUpgradePayload(item.Payload, out var upgradeSlotId, out var target)
                    && upgradeSlotId == selectedSlotId && target.HasValue && target.Value > selectedTarget.Value)
                {
                    removedIds.Add(item.Id);
                }
            }
        }

        // Requirement/orphan cascade fixpoint (mirrors CascadeRemoveUnsatisfiedBuildingQueueItems). The
        // queue filter excludes already-doomed items so each pass validates against the trimmed queue.
        bool changed;
        do
        {
            changed = false;
            foreach (var item in active.Where(i => IsBuildingMutationTask(i.TaskName) && !removedIds.Contains(i.Id)))
            {
                var status = ResolveBuildingStatusForQueueItem(item);
                if (status is null)
                {
                    continue;
                }

                var baseFilter = BuildSameVillageQueueFilter(item);
                bool Filter(QueueItem other) => baseFilter(other) && !removedIds.Contains(other.Id);
                if (TryGetUnsatisfiedBuildingRemovalReason(item, status, Filter, out _))
                {
                    removedIds.Add(item.Id);
                    changed = true;
                    break;
                }
            }
        }
        while (changed);

        return active.Where(item => item.Id != selected.Id && removedIds.Contains(item.Id)).ToList();
    }

    // Confirmation popup listing the follow-on items a removal would also drop. Returns true to proceed.
    private bool ConfirmCascadingQueueRemoval(QueueItem selected, IReadOnlyList<QueueItem> alsoRemoved)
    {
        const int maxListed = 15;
        var listed = alsoRemoved.Take(maxListed).Select(item => $"  • {BuildQueueDisplayName(item)}");
        var body = string.Join("\n", listed);
        if (alsoRemoved.Count > maxListed)
        {
            body += $"\n  • … and {alsoRemoved.Count - maxListed} more";
        }

        var message =
            $"'{BuildQueueDisplayName(selected)}' is a prerequisite for {alsoRemoved.Count} other queued " +
            $"item(s). Removing it will also remove the following, since they could no longer be built:\n\n" +
            $"{body}\n\nRemove all of them?";

        var choice = AppDialog.ShowCustom(
            this,
            message,
            "Remove dependent queue items",
            new (string, MessageBoxResult)[]
            {
                ("Remove all", MessageBoxResult.Yes),
                ("Cancel", MessageBoxResult.Cancel),
            },
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel);
        return choice == MessageBoxResult.Yes;
    }

    // When an "upgrade to level N" item is removed for a slot, the higher-level upgrades queued for the
    // SAME slot are part of the same progression the user is cancelling. The worker loops each upgrade up
    // to its target, so leaving them would keep climbing the building past the removed level. Remove every
    // queued upgrade for this slot with a higher target (and any "upgrade to max", which always exceeds an
    // explicit level). Lower-target upgrades are independent goals and are left untouched. Selected-village
    // (or village-less) items only, matching the rest of the queue-cascade logic.
    private void CascadeRemoveHigherSameSlotBuildingUpgrades(int slotId, int removedTargetLevel)
    {
        var higherUpgrades = GetActiveQueueItems()
            .Where(IsQueueItemForSelectedVillageOrGlobal)
            .Where(item =>
            {
                if (string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
                    && TryReadBuildingUpgradePayload(item.Payload, out var maxSlotId, out _))
                {
                    return maxSlotId == slotId;
                }

                if (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    && TryReadBuildingUpgradePayload(item.Payload, out var upgradeSlotId, out var target))
                {
                    return upgradeSlotId == slotId && target.HasValue && target.Value > removedTargetLevel;
                }

                return false;
            })
            .ToList();

        foreach (var item in higherUpgrades)
        {
            if (_botService.RemoveQueueItem(item.Id))
            {
                ForgetBuildingQueueCachesForItem(item);
                AppendLog($"Removed queued {item.TaskName}: slot {slotId} upgrade above the removed level {removedTargetLevel}.");
            }
        }
    }

    // Resolves the selected village's building status for queue-cascade validation: the live snapshot when
    // loaded, otherwise the cached status for that village. Null only when neither exists.
    private VillageStatus? ResolveSelectedVillageBuildingStatus()
    {
        var name = NormalizeVillageName(GetSelectedVillageName());
        if (name is null)
        {
            return _lastBuildingStatus;
        }

        if (_lastBuildingStatus is not null
            && string.Equals(NormalizeVillageName(_lastBuildingStatus.ActiveVillage), name, StringComparison.OrdinalIgnoreCase))
        {
            return _lastBuildingStatus;
        }

        return _villageStatusCacheByName.TryGetValue(name, out var cached) ? cached : null;
    }

    // Building status for the village a queue item targets: live snapshot for the selected (or village-less)
    // item, otherwise that village's cached status. Null when the village has no snapshot to validate against.
    private VillageStatus? ResolveBuildingStatusForQueueItem(QueueItem item)
    {
        var villageName = NormalizeVillageName(GetQueueItemVillageName(item));
        var selectedName = NormalizeVillageName(GetSelectedVillageName());
        if (villageName is null
            || (selectedName is not null && string.Equals(villageName, selectedName, StringComparison.OrdinalIgnoreCase)))
        {
            return ResolveSelectedVillageBuildingStatus();
        }

        return _villageStatusCacheByName.TryGetValue(villageName, out var cached) ? cached : null;
    }

    // Queue filter scoping requirement checks to the item's own village (plus village-less/global items).
    // Prevents another village's queued work from falsely satisfying — or blocking — this village.
    private Func<QueueItem, bool> BuildSameVillageQueueFilter(QueueItem item)
    {
        var villageName = NormalizeVillageName(GetQueueItemVillageName(item));
        if (villageName is null)
        {
            return IsQueueItemForSelectedVillageOrGlobal;
        }

        return other =>
        {
            var otherVillage = NormalizeVillageName(GetQueueItemVillageName(other));
            return otherVillage is null
                || string.Equals(otherVillage, villageName, StringComparison.OrdinalIgnoreCase);
        };
    }

    private bool TryGetUnsatisfiedBuildingRemovalReason(
        QueueItem item,
        VillageStatus status,
        Func<QueueItem, bool> queueFilter,
        out string reason)
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

            // UI resource-tab rows only reflect the selected village, so include them only for it.
            var missing = MissingRequirements(
                status,
                requirements,
                queueFilter,
                includeUiResourceRows: IsQueueItemForSelectedVillageOrGlobal(item));
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
            var slotIsBuilt = status.Buildings
                .Any(b => b.SlotId == upgradeSlotId && (b.Level ?? 0) > 0);
            if (slotIsBuilt)
            {
                return false;
            }

            var constructQueuedForSlot = GetActiveQueueItems()
                .Where(queueFilter)
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

        // Stamp the stable coordinate key so the item's village identity survives renames and a
        // lost-then-refounded village that shares a name (name/url alone resolve to the wrong village).
        var selectedVillageKey = GetSelectedVillageKey();
        if (!string.IsNullOrWhiteSpace(selectedVillageKey))
        {
            payload[BotOptionPayloadKeys.TargetVillageKey] = selectedVillageKey;
        }

        ApplyConstructFasterSettingsToPayload(payload, selectedVillageKey, selectedVillageName);

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
