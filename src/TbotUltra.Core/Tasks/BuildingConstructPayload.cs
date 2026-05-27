using TbotUltra.Core.Configuration;

namespace TbotUltra.Core.Tasks;

public sealed record BuildingConstructPayload(int SlotId, int Gid, string? Name = null)
{
    public static bool TryFromDictionary(
        IReadOnlyDictionary<string, string> payload,
        out BuildingConstructPayload? result)
    {
        result = null;
        if (!payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructSlotId, out var slotRaw)
            || !int.TryParse(slotRaw, out var slotId)
            || slotId <= 0)
        {
            return false;
        }

        if (!payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructGid, out var gidRaw)
            || !int.TryParse(gidRaw, out var gid)
            || gid <= 0)
        {
            return false;
        }

        payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructName, out var name);
        result = new BuildingConstructPayload(
            slotId,
            gid,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        return true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingConstructSlotId] = SlotId.ToString(),
            [BotOptionPayloadKeys.BuildingConstructGid] = Gid.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(Name))
        {
            result[BotOptionPayloadKeys.BuildingConstructName] = Name.Trim();
        }

        return result;
    }
}
