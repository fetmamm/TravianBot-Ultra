using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class QueueDisplayNameFormatter
{
    public static string Format(
        QueueItem item,
        Func<int, string?> resolveResourceName,
        Func<int, string?> resolveBuildingName,
        int resourceFieldMaxLevel)
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
            && ResourceUpgradePayload.TryFromDictionary(payload, out var resourcePayload, resourceFieldMaxLevel)
            && resourcePayload is not null)
        {
            var name = !string.IsNullOrWhiteSpace(resourcePayload.Name)
                ? resourcePayload.Name
                : resolveResourceName(resourcePayload.SlotId);
            return !string.IsNullOrWhiteSpace(name)
                ? $"Upgrade {name} slot {resourcePayload.SlotId} to level {resourcePayload.TargetLevel}"
                : $"Upgrade resource slot {resourcePayload.SlotId} to level {resourcePayload.TargetLevel}";
        }

        if (string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
            && targetLevel.HasValue)
        {
            var name = !string.IsNullOrWhiteSpace(resourceName)
                ? resourceName
                : (slotId.HasValue ? resolveResourceName(slotId.Value) : null);
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
                : resolveBuildingName(buildingPayload.SlotId);
            return !string.IsNullOrWhiteSpace(name)
                ? $"Upgrade {name} to level {buildingPayload.TargetLevel.Value}{BuildSlotSuffix(buildingPayload.SlotId)}"
                : $"Upgrade building slot {buildingPayload.SlotId} to level {buildingPayload.TargetLevel.Value}";
        }

        if (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
            && targetLevel.HasValue)
        {
            var name = !string.IsNullOrWhiteSpace(buildingName)
                ? buildingName
                : (slotId.HasValue ? resolveBuildingName(slotId.Value) : null);
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
                : resolveBuildingName(buildingMaxPayload.SlotId);
            return !string.IsNullOrWhiteSpace(name)
                ? $"Upgrade {name} to max level{BuildSlotSuffix(buildingMaxPayload.SlotId)}"
                : $"Upgrade building slot {buildingMaxPayload.SlotId} to max level";
        }

        if (string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
        {
            var name = !string.IsNullOrWhiteSpace(buildingName)
                ? buildingName
                : (slotId.HasValue ? resolveBuildingName(slotId.Value) : null);
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
            if (!string.IsNullOrWhiteSpace(item.DisplayName)
                && item.DisplayName.Contains("all farmlists", StringComparison.OrdinalIgnoreCase))
            {
                return item.DisplayName;
            }

            var names = (GetPayloadValue(payload, BotOptionPayloadKeys.ContinuousFarmListNames) ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return names.Length > 0
                ? $"Send farmlists: {string.Join(", ", names)}"
                : string.IsNullOrWhiteSpace(item.DisplayName) ? "Send selected farmlists" : item.DisplayName;
        }

        return string.IsNullOrWhiteSpace(item.DisplayName) ? HumanizeTaskName(item.TaskName) : item.DisplayName;
    }

    public static string? GetPayloadValue(IReadOnlyDictionary<string, string> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    public static int? TryGetIntPayloadValue(IReadOnlyDictionary<string, string> payload, string key)
    {
        return payload.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : null;
    }

    private static string BuildSlotSuffix(int? slotId)
    {
        return slotId.HasValue ? $" (slot {slotId.Value})" : string.Empty;
    }

    private static string HumanizeTaskName(string taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return "Task";
        }

        return string.Join(
            " ",
            taskName.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
