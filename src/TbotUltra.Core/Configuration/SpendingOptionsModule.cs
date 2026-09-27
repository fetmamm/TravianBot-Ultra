using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

internal sealed record SpendingOptions(
    bool AllowGoldSpending,
    bool AllowSilverSpending,
    int GoldLimit,
    int DailyGoldSpendingLimit,
    int SilverLimit,
    int DailySilverSpendingLimit);

internal static class SpendingOptionsModule
{
    private const string AllowSilverSpendingKey = "allow_silver_spending";

    internal static IReadOnlyList<string> AccountScopedKeys { get; } =
    [
        BotOptionPayloadKeys.AllowGoldSpending,
        BotOptionPayloadKeys.GoldLimit,
        BotOptionPayloadKeys.DailyGoldSpendingLimit,
        BotOptionPayloadKeys.SilverLimit,
        BotOptionPayloadKeys.DailySilverSpendingLimit,
        AllowSilverSpendingKey,
    ];

    internal static SpendingOptions FromConfiguration(IConfiguration configuration)
        => new(
            configuration.GetValue(BotOptionPayloadKeys.AllowGoldSpending, false),
            configuration.GetValue(AllowSilverSpendingKey, false),
            Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.GoldLimit, 100)),
            Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.DailyGoldSpendingLimit, 20)),
            Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.SilverLimit, 100)),
            Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.DailySilverSpendingLimit, 10000)));

    internal static SpendingOptions FromOptions(BotOptions options)
        => new(
            options.AllowGoldSpending,
            options.AllowSilverSpending,
            options.GoldLimit,
            options.DailyGoldSpendingLimit,
            options.SilverLimit,
            options.DailySilverSpendingLimit);

    internal static BotOptions ApplyTo(this SpendingOptions values, BotOptions source)
        => source with
        {
            AllowGoldSpending = values.AllowGoldSpending,
            AllowSilverSpending = values.AllowSilverSpending,
            GoldLimit = Math.Max(0, values.GoldLimit),
            DailyGoldSpendingLimit = Math.Max(0, values.DailyGoldSpendingLimit),
            SilverLimit = Math.Max(0, values.SilverLimit),
            DailySilverSpendingLimit = Math.Max(0, values.DailySilverSpendingLimit),
        };

    internal static void WriteSettings(JsonObject target, SpendingOptions values)
    {
        target[AllowSilverSpendingKey] = values.AllowSilverSpending;
        target[BotOptionPayloadKeys.AllowGoldSpending] = values.AllowGoldSpending;
        target[BotOptionPayloadKeys.GoldLimit] = Math.Max(0, values.GoldLimit);
        target[BotOptionPayloadKeys.DailyGoldSpendingLimit] = Math.Max(0, values.DailyGoldSpendingLimit);
        target[BotOptionPayloadKeys.SilverLimit] = Math.Max(0, values.SilverLimit);
        target[BotOptionPayloadKeys.DailySilverSpendingLimit] = Math.Max(0, values.DailySilverSpendingLimit);
    }

    internal static void WriteGoldSettings(JsonObject target, bool allowGoldSpending, int goldLimit)
    {
        target[BotOptionPayloadKeys.AllowGoldSpending] = allowGoldSpending;
        target[BotOptionPayloadKeys.GoldLimit] = Math.Max(0, goldLimit);
    }
}
