using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

internal static class GeneralSettingsOptionsModule
{
    internal static GeneralSettingsConfiguration FromConfiguration(
        IConfiguration configuration,
        bool quickReloginEnabled)
        => new(
            configuration.GetValue(BotOptionPayloadKeys.DontNotifyNewVersion, false),
            quickReloginEnabled,
            configuration.GetValue(BotOptionPayloadKeys.StartBrowserMinimized, false),
            configuration.GetValue(BotOptionPayloadKeys.DailyServerResetManualOverrideEnabled, false),
            Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.DailyServerResetManualHour, 0), 0, 23));

    internal static bool ReadStartBrowserMinimized(IConfiguration configuration)
        => configuration.GetValue(BotOptionPayloadKeys.StartBrowserMinimized, false);

    internal static void WriteSettings(
        JsonObject target,
        GeneralSettingsConfiguration general)
    {
        target[BotOptionPayloadKeys.DontNotifyNewVersion] = general.DontNotifyNewVersion;
        target[BotOptionPayloadKeys.StartBrowserMinimized] = general.StartBrowserMinimized;
        target[BotOptionPayloadKeys.DailyServerResetManualOverrideEnabled] = general.DailyServerResetOverrideEnabled;
        target[BotOptionPayloadKeys.DailyServerResetManualHour] = Math.Clamp(general.DailyServerResetHour, 0, 23);
    }
}
