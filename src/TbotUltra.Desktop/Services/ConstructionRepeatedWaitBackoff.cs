using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public sealed record ConstructionRepeatedWaitBackoff(
    TimeSpan Delay,
    string Signature,
    int Count);

public static class ConstructionRepeatedWaitBackoffPolicy
{
    private static readonly int[] BackoffSeconds = [60, 120, 300, 600, 1200, 1800];
    public static IReadOnlyList<string> RuntimePayloadKeys { get; } =
    [
        BotOptionPayloadKeys.ConstructionDeferBackoffSignature,
        BotOptionPayloadKeys.ConstructionDeferBackoffCount,
        BotOptionPayloadKeys.ConstructionDeferBackoffResourceFingerprint,
        BotOptionPayloadKeys.ConfirmedEmptyResourceValidationFingerprint,
    ];
    private static readonly string[] CurrentResourceKeys =
    [
        BotOptionPayloadKeys.UpgradeCurrentWood,
        BotOptionPayloadKeys.UpgradeCurrentClay,
        BotOptionPayloadKeys.UpgradeCurrentIron,
        BotOptionPayloadKeys.UpgradeCurrentCrop,
    ];

    public static ConstructionRepeatedWaitBackoff? Evaluate(
        QueueItem item,
        IReadOnlyDictionary<string, string> deferredPayload,
        TimeSpan requestedDelay,
        Func<int, int, int> randomInt)
    {
        if (!deferredPayload.TryGetValue(BotOptionPayloadKeys.UpgradeWaitReason, out var waitReason)
            || !string.Equals(waitReason, "page_timer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var blockedTarget = deferredPayload.GetValueOrDefault(BotOptionPayloadKeys.UpgradeBlockedLabel)
            ?? item.Payload.GetValueOrDefault(BotOptionPayloadKeys.BuildingConstructSlotId)
            ?? "unknown";
        var signature = $"{item.TaskName.Trim().ToLowerInvariant()}|resources|page_timer|{blockedTarget.Trim().ToLowerInvariant()}";
        var previousSignature = item.Payload.GetValueOrDefault(BotOptionPayloadKeys.ConstructionDeferBackoffSignature);
        var previousCount = item.Payload.TryGetValue(BotOptionPayloadKeys.ConstructionDeferBackoffCount, out var rawCount)
            && int.TryParse(rawCount, out var parsedCount)
                ? Math.Max(0, parsedCount)
                : 0;
        var count = string.Equals(previousSignature, signature, StringComparison.Ordinal)
            ? previousCount + 1
            : 1;
        var baseSeconds = BackoffSeconds[Math.Min(count - 1, BackoffSeconds.Length - 1)];
        var maxJitterSeconds = baseSeconds >= BackoffSeconds[^1]
            ? 0
            : Math.Max(1, baseSeconds * 15 / 100);
        var jitterSeconds = maxJitterSeconds == 0
            ? 0
            : randomInt(0, maxJitterSeconds + 1);
        var guardedDelay = TimeSpan.FromSeconds(Math.Min(BackoffSeconds[^1], baseSeconds + jitterSeconds));

        return new ConstructionRepeatedWaitBackoff(
            requestedDelay > guardedDelay ? requestedDelay : guardedDelay,
            signature,
            count);
    }

    public static string CreateResourceObservationFingerprint(IReadOnlyDictionary<string, string> payload)
    {
        return string.Join('|', CurrentResourceKeys.Select(key =>
            payload.TryGetValue(key, out var value) && long.TryParse(value, out var parsed)
                ? parsed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "-"));
    }

    public static bool ShouldReleaseForChangedFullResourceObservation(
        IReadOnlyDictionary<string, string> deferredPayload,
        IReadOnlyDictionary<string, long> currentResources)
    {
        if (!AllowsEarlyResourceObservationRelease(deferredPayload))
        {
            return false;
        }

        if (!deferredPayload.ContainsKey(BotOptionPayloadKeys.ConstructionDeferBackoffSignature)
            || !deferredPayload.TryGetValue(
                BotOptionPayloadKeys.ConstructionDeferBackoffResourceFingerprint,
                out var deferredFingerprint))
        {
            return true;
        }

        var currentPayload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resourceKeys = new[] { "wood", "clay", "iron", "crop" };
        for (var index = 0; index < resourceKeys.Length; index++)
        {
            if (currentResources.TryGetValue(resourceKeys[index], out var value))
            {
                currentPayload[CurrentResourceKeys[index]] =
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return !string.Equals(
            deferredFingerprint,
            CreateResourceObservationFingerprint(currentPayload),
            StringComparison.Ordinal);
    }

    public static bool AllowsEarlyResourceObservationRelease(
        IReadOnlyDictionary<string, string> deferredPayload)
    {
        if (!deferredPayload.ContainsKey(BotOptionPayloadKeys.ConstructionDeferBackoffSignature))
        {
            return true;
        }

        return deferredPayload.TryGetValue(
                BotOptionPayloadKeys.ConstructionDeferBackoffCount,
                out var rawCount)
            && int.TryParse(rawCount, out var count)
            && count <= 1;
    }
}
