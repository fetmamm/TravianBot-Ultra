namespace TbotUltra.Core.Configuration;

internal sealed record ActionPacingPayloadValues(
    bool Enabled,
    double TaskMinSeconds,
    double TaskMaxSeconds,
    double PageLoadMinSeconds,
    double PageLoadMaxSeconds,
    double ClickMinSeconds,
    double ClickMaxSeconds,
    double LoopMinSeconds,
    double LoopMaxSeconds,
    double FarmListStepMinSeconds,
    double FarmListStepMaxSeconds,
    int ShortVillageDeferSeconds,
    int VillageRoundSleepExtensionMinutes);

/// <summary>
/// Applies action-pacing payload keys without owning unrelated bot options.
/// </summary>
internal static class ActionPacingPayloadApplier
{
    internal static ActionPacingPayloadValues Apply(
        BotOptions source,
        IReadOnlyDictionary<string, string>? payload)
    {
        var result = new ActionPacingPayloadValues(
            PacingDefaults.ActionPacingEnabled,
            source.ActionPacingTaskMinSeconds,
            source.ActionPacingTaskMaxSeconds,
            source.ActionPacingPageLoadMinSeconds,
            source.ActionPacingPageLoadMaxSeconds,
            source.ActionPacingClickMinSeconds,
            source.ActionPacingClickMaxSeconds,
            source.ActionPacingLoopMinSeconds,
            source.ActionPacingLoopMaxSeconds,
            source.FarmListStepDelayMinSeconds,
            source.FarmListStepDelayMaxSeconds,
            PacingDefaults.NormalizeShortVillageDeferSeconds(source.ShortVillageDeferSeconds),
            PacingDefaults.NormalizeVillageRoundSleepExtensionMinutes(source.VillageRoundSleepExtensionMinutes));

        if (payload is null)
        {
            return result;
        }

        foreach (var pair in payload)
        {
            var key = pair.Key.Trim();
            var value = pair.Value.Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            if (key.Equals(BotOptionPayloadKeys.ActionPacingEnabled, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingTaskMinSeconds, out var taskMin))
            {
                result = result with { TaskMinSeconds = taskMin };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingTaskMaxSeconds, out var taskMax))
            {
                result = result with { TaskMaxSeconds = taskMax };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds, out var pageMin))
            {
                result = result with { PageLoadMinSeconds = pageMin };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds, out var pageMax))
            {
                result = result with { PageLoadMaxSeconds = pageMax };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingClickMinSeconds, out var clickMin))
            {
                result = result with { ClickMinSeconds = clickMin };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingClickMaxSeconds, out var clickMax))
            {
                result = result with { ClickMaxSeconds = clickMax };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingLoopMinSeconds, out var loopMin))
            {
                result = result with { LoopMinSeconds = loopMin };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingLoopMaxSeconds, out var loopMax))
            {
                result = result with { LoopMaxSeconds = loopMax };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.FarmListStepDelayMinSeconds, out var farmMin))
            {
                result = result with { FarmListStepMinSeconds = farmMin };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.FarmListStepDelayMaxSeconds, out var farmMax))
            {
                result = result with { FarmListStepMaxSeconds = farmMax };
            }
            else if (key.Equals(BotOptionPayloadKeys.ShortVillageDeferSeconds, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var shortVillageDeferSeconds))
            {
                result = result with
                {
                    ShortVillageDeferSeconds = PacingDefaults.NormalizeShortVillageDeferSeconds(shortVillageDeferSeconds),
                };
            }
            else if (key.Equals(BotOptionPayloadKeys.VillageRoundSleepExtensionMinutes, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var extensionMinutes))
            {
                result = result with
                {
                    VillageRoundSleepExtensionMinutes = PacingDefaults.NormalizeVillageRoundSleepExtensionMinutes(extensionMinutes),
                };
            }
        }

        return result;
    }

    private static bool TryReadDelay(string key, string value, string expectedKey, out double delay)
    {
        delay = 0;
        if (!key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase)
            || !double.TryParse(value, out var parsed))
        {
            return false;
        }

        delay = ClampDelaySeconds(parsed);
        return true;
    }

    private static double ClampDelaySeconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 3600);
    }
}
