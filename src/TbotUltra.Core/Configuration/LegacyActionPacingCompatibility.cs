using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

/// <summary>
/// Preserves the one-way compatibility mapping from the retired human-like settings
/// to current action-pacing defaults. New action-pacing keys remain authoritative.
/// </summary>
internal static class LegacyActionPacingCompatibility
{
    internal static LegacyActionPacingSettings Resolve(IConfiguration configuration)
    {
        var legacyEnabled = configuration.GetValue("human_like_enabled", false);
        var legacySpeed = configuration["human_like_speed"] ?? "medium";
        var actionPacingEnabled = configuration[BotOptionPayloadKeys.ActionPacingEnabled] is not null
            ? configuration.GetValue(BotOptionPayloadKeys.ActionPacingEnabled, PacingDefaults.ActionPacingEnabled)
            : legacyEnabled || PacingDefaults.ActionPacingEnabled;

        return new LegacyActionPacingSettings(
            legacyEnabled,
            legacySpeed,
            actionPacingEnabled,
            ResolveFallbacks(legacyEnabled, legacySpeed));
    }

    private static ActionPacingFallbacks ResolveFallbacks(bool legacyEnabled, string? legacySpeed)
    {
        if (!legacyEnabled)
        {
            return ActionPacingFallbacks.Default;
        }

        var (legacyMin, legacyMax) = legacySpeed?.Trim().ToLowerInvariant() switch
        {
            "slow" => (2.5, 5.0),
            "fast" => (0.3, 1.0),
            _ => (1.0, 2.5),
        };

        return new ActionPacingFallbacks(
            Math.Max(PacingDefaults.ActionPacingTaskMinSeconds, legacyMin),
            Math.Max(PacingDefaults.ActionPacingTaskMaxSeconds, legacyMax),
            Math.Max(PacingDefaults.ActionPacingPageLoadMinSeconds, legacyMin),
            Math.Max(PacingDefaults.ActionPacingPageLoadMaxSeconds, legacyMax),
            Math.Max(PacingDefaults.ActionPacingClickMinSeconds, legacyMin),
            Math.Max(PacingDefaults.ActionPacingClickMaxSeconds, legacyMax),
            PacingDefaults.ActionPacingLoopMinSeconds,
            PacingDefaults.ActionPacingLoopMaxSeconds,
            Math.Max(PacingDefaults.FarmListStepDelayMinSeconds, legacyMin),
            Math.Max(PacingDefaults.FarmListStepDelayMaxSeconds, legacyMax));
    }
}

internal sealed record LegacyActionPacingSettings(
    bool HumanLikeEnabled,
    string HumanLikeSpeed,
    bool ActionPacingEnabled,
    ActionPacingFallbacks Fallbacks);

internal sealed record ActionPacingFallbacks(
    double TaskMinSeconds,
    double TaskMaxSeconds,
    double PageLoadMinSeconds,
    double PageLoadMaxSeconds,
    double ClickMinSeconds,
    double ClickMaxSeconds,
    double LoopMinSeconds,
    double LoopMaxSeconds,
    double FarmListMinSeconds,
    double FarmListMaxSeconds)
{
    internal static ActionPacingFallbacks Default { get; } = new(
        PacingDefaults.ActionPacingTaskMinSeconds,
        PacingDefaults.ActionPacingTaskMaxSeconds,
        PacingDefaults.ActionPacingPageLoadMinSeconds,
        PacingDefaults.ActionPacingPageLoadMaxSeconds,
        PacingDefaults.ActionPacingClickMinSeconds,
        PacingDefaults.ActionPacingClickMaxSeconds,
        PacingDefaults.ActionPacingLoopMinSeconds,
        PacingDefaults.ActionPacingLoopMaxSeconds,
        PacingDefaults.FarmListStepDelayMinSeconds,
        PacingDefaults.FarmListStepDelayMaxSeconds);
}
