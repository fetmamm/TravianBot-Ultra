using TbotUltra.Core.Configuration;

namespace TbotUltra.Core.Tasks;

public sealed record ResourceUpgradePayload(int SlotId, int TargetLevel, string? Name = null)
{
    public static bool TryFromDictionary(
        IReadOnlyDictionary<string, string> payload,
        out ResourceUpgradePayload? result,
        int maxLevel = 18)
    {
        result = null;
        if (!payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeSlotId, out var slotRaw)
            || !int.TryParse(slotRaw, out var slotId)
            || slotId < 1
            || slotId > 18)
        {
            return false;
        }

        if (!payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeTargetLevel, out var targetRaw)
            || !int.TryParse(targetRaw, out var targetLevel)
            || targetLevel <= 0)
        {
            return false;
        }

        var effectiveMax = Math.Max(1, maxLevel);
        payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeName, out var name);
        result = new ResourceUpgradePayload(
            slotId,
            Math.Clamp(targetLevel, 1, effectiveMax),
            string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        return true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeSlotId] = SlotId.ToString(),
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = TargetLevel.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(Name))
        {
            result[BotOptionPayloadKeys.ResourceUpgradeName] = Name.Trim();
        }

        return result;
    }
}
