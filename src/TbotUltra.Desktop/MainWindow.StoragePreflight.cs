using System.Windows;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private bool TryPrepareUpgradeAllStoragePreflight(
        int targetLevel,
        Dictionary<string, string> parentPayload,
        out IReadOnlyList<QueueItemCreateRequest> plannedRequests,
        out IReadOnlyList<StoragePreflightUpgrade> upgrades)
    {
        plannedRequests = [];
        upgrades = [];
        var status = ResolveSelectedVillageBuildingStatus();
        if (status is null
            || status.ResourceFields.Count == 0
            || status.Buildings.Count == 0
            || status.WarehouseCapacity is not > 0
            || status.GranaryCapacity is not > 0)
        {
            AppDialog.Show(
                this,
                "Refresh the selected village before adding this queue item. Current resource fields, buildings, and storage capacity are required for the storage preflight.",
                "Storage capacity check",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var villageName = GetSelectedVillageName() ?? status.ActiveVillage;
        var villageKey = GetSelectedVillageKey();
        var precedingItems = _botService.GetQueueItemsForDisplay()
            .Where(item => IsQueueItemForVillage(item, villageName, villageKey))
            .ToList();
        var result = StorageCapacityQueuePreflightPlanner.PlanUpgradeAllResourcesStepwise(
            status,
            precedingItems,
            targetLevel);
        if (!string.IsNullOrWhiteSpace(result.CannotPlanReason))
        {
            AppDialog.Show(
                this,
                $"The storage requirement could not be planned safely.\n\n{result.CannotPlanReason}\n\nRefresh the village and verify that Warehouse and Granary exist.",
                "Storage capacity check",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        upgrades = result.Upgrades;
        if (upgrades.Count > 0)
        {
            var lines = result.Stages.SelectMany(stage => stage.StorageUpgradesBefore.Select(upgrade =>
                $"  • Before resources to level {stage.ResourceTargetLevel}: {upgrade.Kind} " +
                $"level {upgrade.CurrentLevel} → {upgrade.TargetLevel} " +
                $"(capacity {upgrade.ProjectedCapacity:N0}, required {upgrade.RequiredCapacity:N0})"));
            var choice = AppDialog.ShowCustom(
                this,
                "The resource plan reaches one or more storage-capacity limits. The required storage " +
                "upgrades can be inserted step by step, immediately before the resource stage that needs them.\n\n" +
                string.Join("\n", lines),
                "Storage upgrades required",
                [("Add required storage upgrades", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
                MessageBoxImage.Warning,
                MessageBoxResult.Yes,
                MessageBoxResult.Cancel,
                successResult: MessageBoxResult.Yes);
            if (choice != MessageBoxResult.Yes)
            {
                return false;
            }
        }

        var requests = new List<QueueItemCreateRequest>();
        var planId = Guid.NewGuid().ToString();
        foreach (var stage in result.Stages)
        {
            var batchId = stage.StorageUpgradesBefore.Count > 0 ? Guid.NewGuid().ToString() : null;
            foreach (var upgrade in stage.StorageUpgradesBefore)
            {
                var name = upgrade.Kind == StorageCapacityKind.Warehouse ? "Warehouse" : "Granary";
                var payload = new BuildingUpgradePayload(upgrade.SlotId, upgrade.TargetLevel, name).ToDictionary();
                ApplySelectedVillageToPayload(payload);
                payload[BotOptionPayloadKeys.StoragePreflightPlanId] = planId;
                payload[BotOptionPayloadKeys.StoragePreflightBatchId] = batchId!;
                payload[BotOptionPayloadKeys.AutoAddedBy] = BotOptionPayloadKeys.AutoAddedByStorageCapacityPreflight;
                requests.Add(new QueueItemCreateRequest("upgrade_building_to_level", payload, 0, 3));
            }

            var resourcePayload = new Dictionary<string, string>(parentPayload, StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = stage.ResourceTargetLevel.ToString(),
                [BotOptionPayloadKeys.StoragePreflightPlanId] = planId,
            };
            if (batchId is not null)
            {
                resourcePayload[BotOptionPayloadKeys.StoragePreflightBatchId] = batchId;
            }
            requests.Add(new QueueItemCreateRequest("upgrade_all_resources_to_level", resourcePayload, 0, 3));
        }

        plannedRequests = requests;
        return true;
    }

    private void ApplyStoragePreflightPendingState(IReadOnlyList<StoragePreflightUpgrade> upgrades)
    {
        foreach (var upgrade in upgrades)
        {
            SetPendingBuildingUpgrade(upgrade.SlotId, upgrade.TargetLevel);
        }
    }

    private static bool HasEarlierStoragePreflightDependency(
        IReadOnlyList<QueueItem> orderedItems,
        int itemIndex)
    {
        var item = orderedItems[itemIndex];
        if (item.Payload.TryGetValue(BotOptionPayloadKeys.StoragePreflightPlanId, out var planId)
            && !string.IsNullOrWhiteSpace(planId)
            && orderedItems.Take(itemIndex).Any(candidate =>
                candidate.Payload.TryGetValue(BotOptionPayloadKeys.StoragePreflightPlanId, out var candidatePlanId)
                && string.Equals(candidatePlanId, planId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!item.Payload.TryGetValue(BotOptionPayloadKeys.StoragePreflightBatchId, out var batchId)
            || string.IsNullOrWhiteSpace(batchId))
        {
            return false;
        }

        return orderedItems.Take(itemIndex).Any(candidate =>
            candidate.Payload.TryGetValue(BotOptionPayloadKeys.StoragePreflightBatchId, out var candidateBatchId)
            && string.Equals(candidateBatchId, batchId, StringComparison.OrdinalIgnoreCase)
            && candidate.Payload.TryGetValue(BotOptionPayloadKeys.AutoAddedBy, out var source)
            && string.Equals(
                source,
                BotOptionPayloadKeys.AutoAddedByStorageCapacityPreflight,
                StringComparison.OrdinalIgnoreCase));
    }
}
