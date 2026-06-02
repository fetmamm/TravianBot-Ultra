using TbotUltra.Core.Configuration;

namespace TbotUltra.Core.Tasks;

public sealed record HeroPayload(
    int? MinHpForAdventure = null,
    bool? AutoRevive = null,
    bool? AutoAssignPoints = null,
    bool? AutoUseOintments = null,
    string? StatPriority = null,
    string? AdventurePickOrder = null,
    bool? HideModeEnabled = null,
    string? HideMode = null,
    bool? ContinuousAdventures = null)
{
    public static bool TryFromDictionary(IReadOnlyDictionary<string, string> payload, out HeroPayload? result)
    {
        result = null;

        int? minHp = null;
        if (payload.TryGetValue(BotOptionPayloadKeys.HeroMinHpForAdventure, out var minHpRaw))
        {
            if (!int.TryParse(minHpRaw, out var parsedMinHp) || parsedMinHp < 1 || parsedMinHp > 100)
            {
                return false;
            }

            minHp = parsedMinHp;
        }

        if (!TryReadBool(payload, BotOptionPayloadKeys.HeroAutoRevive, out var autoRevive)
            || !TryReadBool(payload, BotOptionPayloadKeys.HeroAutoAssignPoints, out var autoAssignPoints)
            || !TryReadBool(payload, BotOptionPayloadKeys.HeroAutoUseOintments, out var autoUseOintments)
            || !TryReadBool(payload, BotOptionPayloadKeys.HeroHideModeEnabled, out var hideModeEnabled)
            || !TryReadBool(payload, BotOptionPayloadKeys.HeroContinuousAdventures, out var continuousAdventures))
        {
            return false;
        }

        result = new HeroPayload(
            minHp,
            autoRevive,
            autoAssignPoints,
            autoUseOintments,
            ReadTrimmed(payload, BotOptionPayloadKeys.HeroStatPriority),
            ReadTrimmed(payload, BotOptionPayloadKeys.HeroAdventurePickOrder),
            hideModeEnabled,
            ReadTrimmed(payload, BotOptionPayloadKeys.HeroHideMode),
            continuousAdventures);
        return true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent(result, BotOptionPayloadKeys.HeroMinHpForAdventure, MinHpForAdventure?.ToString());
        AddIfPresent(result, BotOptionPayloadKeys.HeroAutoRevive, FormatBool(AutoRevive));
        AddIfPresent(result, BotOptionPayloadKeys.HeroAutoAssignPoints, FormatBool(AutoAssignPoints));
        AddIfPresent(result, BotOptionPayloadKeys.HeroAutoUseOintments, FormatBool(AutoUseOintments));
        AddIfPresent(result, BotOptionPayloadKeys.HeroStatPriority, StatPriority);
        AddIfPresent(result, BotOptionPayloadKeys.HeroAdventurePickOrder, AdventurePickOrder);
        AddIfPresent(result, BotOptionPayloadKeys.HeroHideModeEnabled, FormatBool(HideModeEnabled));
        AddIfPresent(result, BotOptionPayloadKeys.HeroHideMode, HideMode);
        AddIfPresent(result, BotOptionPayloadKeys.HeroContinuousAdventures, FormatBool(ContinuousAdventures));
        return result;
    }

    private static bool TryReadBool(IReadOnlyDictionary<string, string> payload, string key, out bool? value)
    {
        value = null;
        if (!payload.TryGetValue(key, out var raw))
        {
            return true;
        }

        if (!bool.TryParse(raw, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string? ReadTrimmed(IReadOnlyDictionary<string, string> payload, string key)
    {
        return payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static string? FormatBool(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : null;

    private static void AddIfPresent(Dictionary<string, string> result, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            result[key] = value.Trim();
        }
    }
}
