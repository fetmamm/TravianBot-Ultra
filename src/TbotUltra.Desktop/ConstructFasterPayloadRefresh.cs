using System;
using System.Collections.Generic;
using System.Globalization;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop;

internal static class ConstructFasterPayloadRefresh
{
    public static void Apply(
        Dictionary<string, string> payload,
        BotOptions options,
        string? villageKey,
        string? villageName,
        Func<string, bool, bool> isConstructFasterEnabledByKey)
    {
        var villageEnabled = !string.IsNullOrWhiteSpace(villageKey)
            && isConstructFasterEnabledByKey(villageKey, false);
        if (!villageEnabled && !string.IsNullOrWhiteSpace(villageName))
        {
            villageEnabled = isConstructFasterEnabledByKey($"name:{villageName.Trim()}", false);
        }

        payload[BotOptionPayloadKeys.ConstructFasterEnabled] = villageEnabled ? "true" : "false";
        payload[BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled] =
            options.ConstructFasterMinBuildTimeEnabled ? "true" : "false";
        payload[BotOptionPayloadKeys.ConstructFasterMinBuildMinutes] =
            Math.Max(0, options.ConstructFasterMinBuildMinutes).ToString(CultureInfo.InvariantCulture);
        payload[BotOptionPayloadKeys.ConstructFasterRandomEnabled] =
            options.ConstructFasterRandomEnabled ? "true" : "false";
        payload[BotOptionPayloadKeys.ConstructFasterRandomChancePercent] =
            Math.Clamp(options.ConstructFasterRandomChancePercent, 0, 100).ToString(CultureInfo.InvariantCulture);
    }
}
