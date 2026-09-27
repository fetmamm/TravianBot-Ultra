using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

internal static class GeneralSettingsOptionsModule
{
    private const string AllowSilverSpendingKey = "allow_silver_spending";

    internal static GeneralSettingsConfiguration FromConfiguration(IConfiguration configuration)
        => new(
            configuration.GetValue(BotOptionPayloadKeys.DontNotifyNewVersion, false),
            configuration.GetValue(BotOptionPayloadKeys.PostLoginQuickReloginEnabled, true),
            configuration.GetValue(BotOptionPayloadKeys.DailyServerResetManualOverrideEnabled, false),
            Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.DailyServerResetManualHour, 0), 0, 23));

    internal static void WriteSettings(
        JsonObject target,
        GeneralSettingsConfiguration general,
        BotOptions options)
    {
        target[BotOptionPayloadKeys.DontNotifyNewVersion] = general.DontNotifyNewVersion;
        target[BotOptionPayloadKeys.PostLoginQuickReloginEnabled] = general.QuickReloginEnabled;
        target[BotOptionPayloadKeys.DailyServerResetManualOverrideEnabled] = general.DailyServerResetOverrideEnabled;
        target[BotOptionPayloadKeys.DailyServerResetManualHour] = Math.Clamp(general.DailyServerResetHour, 0, 23);
        target[AllowSilverSpendingKey] = options.AllowSilverSpending;
        target[BotOptionPayloadKeys.AllowGoldSpending] = options.AllowGoldSpending;
        target[BotOptionPayloadKeys.GoldLimit] = Math.Max(0, options.GoldLimit);
        target[BotOptionPayloadKeys.DailyGoldSpendingLimit] = Math.Max(0, options.DailyGoldSpendingLimit);
        target[BotOptionPayloadKeys.SilverLimit] = Math.Max(0, options.SilverLimit);
        target[BotOptionPayloadKeys.DailySilverSpendingLimit] = Math.Max(0, options.DailySilverSpendingLimit);
    }
}
