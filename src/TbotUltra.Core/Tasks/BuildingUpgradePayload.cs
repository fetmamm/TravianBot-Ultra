using TbotUltra.Core.Configuration;

namespace TbotUltra.Core.Tasks;

public sealed record BuildingUpgradePayload(int SlotId, int? TargetLevel = null, string? Name = null)
{
    public static bool TryFromDictionary(
        IReadOnlyDictionary<string, string> payload,
        out BuildingUpgradePayload? result)
    {
        result = null;
        if (!payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeSlotId, out var slotRaw)
            || !int.TryParse(slotRaw, out var slotId)
            || slotId <= 0)
        {
            return false;
        }

        int? targetLevel = null;
        if (payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeTargetLevel, out var targetRaw))
        {
            if (!int.TryParse(targetRaw, out var parsedTargetLevel) || parsedTargetLevel <= 0)
            {
                return false;
            }

            targetLevel = parsedTargetLevel;
        }

        payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeName, out var name);
        result = new BuildingUpgradePayload(
            slotId,
            targetLevel,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        return true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = SlotId.ToString(),
        };

        if (TargetLevel.HasValue)
        {
            result[BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = TargetLevel.Value.ToString();
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            result[BotOptionPayloadKeys.BuildingUpgradeName] = Name.Trim();
        }

        return result;
    }
}
