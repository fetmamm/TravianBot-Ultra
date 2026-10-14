using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

internal sealed record HeroRaidingOptions(
    bool Enabled,
    int MinimumHpPercent,
    int MaximumDistance,
    int ObservationMinutes,
    string SelectedTypes,
    string Priority);

internal static class HeroRaidingOptionsModule
{
    internal const int NoCandidateRetryMinutes = 60;
    internal const string DefaultSelectedTypes = "Wood,Clay,Iron,Wood+Crop,Clay+Crop,Iron+Crop";

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Wood", "Clay", "Iron", "Crop25", "Crop50", "Wood+Crop", "Clay+Crop", "Iron+Crop",
    };

    internal static IReadOnlyList<string> AccountScopedKeys { get; } =
    [
        BotOptionPayloadKeys.HeroRaidingEnabled,
        BotOptionPayloadKeys.HeroRaidingMinimumHpPercent,
        BotOptionPayloadKeys.HeroRaidingMaximumDistance,
        BotOptionPayloadKeys.HeroRaidingObservationMinutes,
        BotOptionPayloadKeys.HeroRaidingSelectedTypes,
        BotOptionPayloadKeys.HeroRaidingPriority,
    ];

    internal static HeroRaidingOptions FromConfiguration(IConfiguration configuration) => new(
        configuration.GetValue(BotOptionPayloadKeys.HeroRaidingEnabled, false),
        Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.HeroRaidingMinimumHpPercent, 40), 1, 100),
        Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.HeroRaidingMaximumDistance, 20), 1, 50),
        Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.HeroRaidingObservationMinutes, 10), 1, 120),
        NormalizeTypes(configuration[BotOptionPayloadKeys.HeroRaidingSelectedTypes]),
        NormalizePriority(configuration[BotOptionPayloadKeys.HeroRaidingPriority]));

    internal static HeroRaidingOptions FromOptions(BotOptions options) => new(
        options.HeroRaidingEnabled,
        options.HeroRaidingMinimumHpPercent,
        options.HeroRaidingMaximumDistance,
        options.HeroRaidingObservationMinutes,
        options.HeroRaidingSelectedTypes,
        options.HeroRaidingPriority);

    internal static void WriteSettings(JsonObject target, BotOptions options)
    {
        var values = FromOptions(options);
        target[BotOptionPayloadKeys.HeroRaidingEnabled] = values.Enabled;
        target[BotOptionPayloadKeys.HeroRaidingMinimumHpPercent] = Math.Clamp(values.MinimumHpPercent, 1, 100);
        target[BotOptionPayloadKeys.HeroRaidingMaximumDistance] = Math.Clamp(values.MaximumDistance, 1, 50);
        target[BotOptionPayloadKeys.HeroRaidingObservationMinutes] = Math.Clamp(values.ObservationMinutes, 1, 120);
        target[BotOptionPayloadKeys.HeroRaidingSelectedTypes] = NormalizeTypes(values.SelectedTypes);
        target[BotOptionPayloadKeys.HeroRaidingPriority] = NormalizePriority(values.Priority);
    }

    internal static HeroRaidingOptions Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var result = FromOptions(source);
        if (payload is null) return result;

        foreach (var (key, value) in payload)
        {
            if (key.Equals(BotOptionPayloadKeys.HeroRaidingEnabled, StringComparison.OrdinalIgnoreCase)
                && bool.TryParse(value, out var enabled))
                result = result with { Enabled = enabled };
            else if (key.Equals(BotOptionPayloadKeys.HeroRaidingMinimumHpPercent, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var minimumHp))
                result = result with { MinimumHpPercent = Math.Clamp(minimumHp, 1, 100) };
            else if (key.Equals(BotOptionPayloadKeys.HeroRaidingMaximumDistance, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var maximumDistance))
                result = result with { MaximumDistance = Math.Clamp(maximumDistance, 1, 50) };
            else if (key.Equals(BotOptionPayloadKeys.HeroRaidingObservationMinutes, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var observationMinutes))
                result = result with { ObservationMinutes = Math.Clamp(observationMinutes, 1, 120) };
            else if (key.Equals(BotOptionPayloadKeys.HeroRaidingSelectedTypes, StringComparison.OrdinalIgnoreCase))
                result = result with { SelectedTypes = NormalizeTypes(value) };
            else if (key.Equals(BotOptionPayloadKeys.HeroRaidingPriority, StringComparison.OrdinalIgnoreCase))
                result = result with { Priority = NormalizePriority(value) };
        }

        return result;
    }

    internal static BotOptions ApplyTo(this HeroRaidingOptions values, BotOptions source) => source with
    {
        HeroRaidingEnabled = values.Enabled,
        HeroRaidingMinimumHpPercent = Math.Clamp(values.MinimumHpPercent, 1, 100),
        HeroRaidingMaximumDistance = Math.Clamp(values.MaximumDistance, 1, 50),
        HeroRaidingObservationMinutes = Math.Clamp(values.ObservationMinutes, 1, 120),
        HeroRaidingSelectedTypes = NormalizeTypes(values.SelectedTypes),
        HeroRaidingPriority = NormalizePriority(values.Priority),
    };

    private static string NormalizePriority(string? priority) =>
        string.Equals(priority, "adventures", StringComparison.OrdinalIgnoreCase) ? "adventures" : "raiding";

    private static string NormalizeTypes(string? selectedTypes)
    {
        if (selectedTypes is null) return DefaultSelectedTypes;
        return string.Join(',', selectedTypes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(type => string.Equals(type, "Crop", StringComparison.OrdinalIgnoreCase)
                ? new[] { "Crop25", "Crop50" }
                : new[] { type })
            .Where(KnownTypes.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
