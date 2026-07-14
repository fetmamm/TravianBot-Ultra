using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;

namespace TbotUltra.Worker.Services;

internal static class UpgradeResourceWaitCalculator
{
    internal static UpgradeResourceWaitSnapshot BuildSnapshot(
        string blockedLabel,
        IReadOnlyDictionary<string, long?> required,
        IReadOnlyDictionary<string, string> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        int fallbackWaitSeconds,
        string waitReasonWhenEstimated,
        long? warehouseCapacity,
        long? granaryCapacity,
        string? serverStorageBlockKind = null)
    {
        var values = new Dictionary<string, UpgradeResourceWaitValue>(StringComparer.OrdinalIgnoreCase);
        var longestFiniteSeconds = 0;
        var hasUnknownWait = false;

        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            required.TryGetValue(key, out var requiredValue);
            currentResources.TryGetValue(key, out var currentRaw);
            productionByHour.TryGetValue(key, out var productionValue);
            var currentValue = TravianParsing.TryParseResourceValue(currentRaw);
            var missingValue = requiredValue.HasValue && currentValue.HasValue
                ? Math.Max(0, requiredValue.Value - currentValue.Value)
                : (long?)null;

            int? waitSeconds = null;
            string waitReason;
            if (missingValue is null || missingValue <= 0)
            {
                waitSeconds = 0;
                waitReason = "enough";
            }
            else if (productionValue is > 0)
            {
                var computedSeconds = (int)Math.Ceiling((missingValue.Value / productionValue.Value) * 3600d);
                waitSeconds = Math.Max(1, computedSeconds);
                longestFiniteSeconds = Math.Max(longestFiniteSeconds, waitSeconds.Value);
                waitReason = waitReasonWhenEstimated == "cached_production" ? "from_cached_production" : "from_production";
            }
            else
            {
                hasUnknownWait = true;
                waitReason = productionValue is null ? "production_unknown" : "production_non_positive";
            }

            values[key] = new UpgradeResourceWaitValue(
                Required: requiredValue,
                Current: currentValue,
                Missing: missingValue,
                ProductionPerHour: productionValue,
                WaitSeconds: waitSeconds,
                WaitReason: waitReason);
        }

        var resolvedWaitSeconds = fallbackWaitSeconds > 0
            ? fallbackWaitSeconds
            : longestFiniteSeconds > 0
                ? longestFiniteSeconds
                : 60;
        if (hasUnknownWait && fallbackWaitSeconds <= 0)
        {
            resolvedWaitSeconds = Math.Max(30, Math.Min(resolvedWaitSeconds, 60));
        }

        var resolvedWaitReason = fallbackWaitSeconds > 0
            ? "page_timer"
            : waitReasonWhenEstimated;
        var storageCapacityKind = ResolveStorageCapacityBlockKind(
            required,
            warehouseCapacity,
            granaryCapacity,
            serverStorageBlockKind);
        if (storageCapacityKind is not null)
        {
            resolvedWaitReason = "storage_capacity";
        }

        return new UpgradeResourceWaitSnapshot(
            blockedLabel,
            values,
            resolvedWaitSeconds,
            storageCapacityKind is not null
                ? resolvedWaitReason
                : hasUnknownWait && fallbackWaitSeconds <= 0
                    ? "recheck_needed"
                    : resolvedWaitReason,
            warehouseCapacity,
            granaryCapacity,
            storageCapacityKind);
    }

    private static string? ResolveStorageCapacityBlockKind(
        IReadOnlyDictionary<string, long?> required,
        long? warehouseCapacity,
        long? granaryCapacity,
        string? serverStorageBlockKind)
    {
        var normalizedServerKind = NormalizeStorageCapacityKind(serverStorageBlockKind);
        if (normalizedServerKind is not null
            && (warehouseCapacity is not > 0 || granaryCapacity is not > 0))
        {
            return normalizedServerKind;
        }

        required.TryGetValue("wood", out var wood);
        required.TryGetValue("clay", out var clay);
        required.TryGetValue("iron", out var iron);
        required.TryGetValue("crop", out var crop);
        var requiredWarehouse = Math.Max(wood ?? 0, Math.Max(clay ?? 0, iron ?? 0));
        if (warehouseCapacity is > 0 && requiredWarehouse > warehouseCapacity.Value)
        {
            return "warehouse";
        }

        if (granaryCapacity is > 0 && crop is long requiredCrop && requiredCrop > granaryCapacity.Value)
        {
            return "granary";
        }

        return normalizedServerKind;
    }

    private static string? NormalizeStorageCapacityKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("warehouse", StringComparison.OrdinalIgnoreCase))
        {
            return "warehouse";
        }

        if (normalized.Contains("granary", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("silo", StringComparison.OrdinalIgnoreCase))
        {
            return "granary";
        }

        return null;
    }

    internal static string FormatLog(UpgradeResourceWaitSnapshot snapshot)
    {
        static string FormatValue(long? value) => value?.ToString() ?? "?";
        static string FormatProduction(double? value) => value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "?";
        static string FormatWait(int? value) => value is null ? "?" : value.Value.ToString();

        var parts = new List<string>();
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            if (!snapshot.Values.TryGetValue(key, out var value))
            {
                continue;
            }

            parts.Add($"{key}: req={FormatValue(value.Required)}, cur={FormatValue(value.Current)}, miss={FormatValue(value.Missing)}, prod/h={FormatProduction(value.ProductionPerHour)}, wait_s={FormatWait(value.WaitSeconds)}, reason={value.WaitReason}");
        }

        // Clear human-readable headline first, then the per-resource diagnostics for debugging.
        var headline = string.Equals(snapshot.WaitReason, "storage_capacity", StringComparison.OrdinalIgnoreCase)
            ? $"Storage capacity too low for {FriendlyUpgradeTarget(snapshot.BlockedLabel)} ({snapshot.StorageCapacityKind ?? "storage"}). Waiting {snapshot.WaitSeconds}s."
            : $"Not enough resources to build {FriendlyUpgradeTarget(snapshot.BlockedLabel)}. Waiting {snapshot.WaitSeconds}s.";
        var details = parts.Count > 0 ? $" | {string.Join(" | ", parts)}" : string.Empty;
        return $"{headline}{details} | wait_reason={snapshot.WaitReason}";
    }

    // Turns an internal blocked label ("Building slot 19 (Warehouse) upgrade to level 7",
    // "Resource slot 9 (Cropland) upgrade to level 10", "Building slot 31 construct Marketplace") into a
    // short "Name level N" / "Name" phrase for the user-facing wait log.
    private static string FriendlyUpgradeTarget(string blockedLabel)
    {
        if (string.IsNullOrWhiteSpace(blockedLabel))
        {
            return "the building";
        }

        var nameMatch = Regex.Match(blockedLabel, @"\(([^)]+)\)");
        var name = nameMatch.Success
            ? nameMatch.Groups[1].Value.Trim()
            : blockedLabel.Trim();

        var levelMatch = Regex.Match(blockedLabel, @"level\s+(\d+)", RegexOptions.IgnoreCase);
        return levelMatch.Success ? $"{name} level {levelMatch.Groups[1].Value}" : name;
    }

    internal static string BuildBlockedResultMessage(UpgradeResourceWaitSnapshot snapshot)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(snapshot.BlockedLabel);
        builder.Append(": blocked by resources. ");
        builder.Append("queue_wait_seconds=");
        builder.Append(snapshot.WaitSeconds);
        builder.Append(' ');
        builder.Append(BotOptionPayloadKeys.UpgradeBlockedLabel);
        builder.Append('=');
        builder.Append(SanitizePayloadToken(snapshot.BlockedLabel));
        builder.Append(' ');
        builder.Append(BotOptionPayloadKeys.UpgradeWaitReason);
        builder.Append('=');
        builder.Append(snapshot.WaitReason);
        builder.Append(' ');
        builder.Append(BotOptionPayloadKeys.UpgradeWaitSeconds);
        builder.Append('=');
        builder.Append(snapshot.WaitSeconds);
        AppendUpgradeWaitValueTokens(builder, "wood", snapshot.Values);
        AppendUpgradeWaitValueTokens(builder, "clay", snapshot.Values);
        AppendUpgradeWaitValueTokens(builder, "iron", snapshot.Values);
        AppendUpgradeWaitValueTokens(builder, "crop", snapshot.Values);
        AppendLongToken(builder, BotOptionPayloadKeys.UpgradeWarehouseCapacity, snapshot.WarehouseCapacity);
        AppendLongToken(builder, BotOptionPayloadKeys.UpgradeGranaryCapacity, snapshot.GranaryCapacity);
        AppendStringToken(builder, BotOptionPayloadKeys.UpgradeStorageCapacityKind, snapshot.StorageCapacityKind);
        return builder.ToString();
    }

    private static void AppendUpgradeWaitValueTokens(
        System.Text.StringBuilder builder,
        string key,
        IReadOnlyDictionary<string, UpgradeResourceWaitValue> values)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return;
        }

        AppendLongToken(builder, RequiredKeyFor(key), value.Required);
        AppendLongToken(builder, CurrentKeyFor(key), value.Current);
        AppendDoubleToken(builder, ProductionKeyFor(key), value.ProductionPerHour);
    }

    private static string RequiredKeyFor(string key) => key switch
    {
        "wood" => BotOptionPayloadKeys.UpgradeRequiredWood,
        "clay" => BotOptionPayloadKeys.UpgradeRequiredClay,
        "iron" => BotOptionPayloadKeys.UpgradeRequiredIron,
        _ => BotOptionPayloadKeys.UpgradeRequiredCrop,
    };

    private static string CurrentKeyFor(string key) => key switch
    {
        "wood" => BotOptionPayloadKeys.UpgradeCurrentWood,
        "clay" => BotOptionPayloadKeys.UpgradeCurrentClay,
        "iron" => BotOptionPayloadKeys.UpgradeCurrentIron,
        _ => BotOptionPayloadKeys.UpgradeCurrentCrop,
    };

    private static string ProductionKeyFor(string key) => key switch
    {
        "wood" => BotOptionPayloadKeys.UpgradeProductionWood,
        "clay" => BotOptionPayloadKeys.UpgradeProductionClay,
        "iron" => BotOptionPayloadKeys.UpgradeProductionIron,
        _ => BotOptionPayloadKeys.UpgradeProductionCrop,
    };

    private static void AppendLongToken(System.Text.StringBuilder builder, string key, long? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        builder.Append(' ');
        builder.Append(key);
        builder.Append('=');
        builder.Append(value.Value);
    }

    private static void AppendDoubleToken(System.Text.StringBuilder builder, string key, double? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        builder.Append(' ');
        builder.Append(key);
        builder.Append('=');
        builder.Append(value.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendStringToken(System.Text.StringBuilder builder, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(' ');
        builder.Append(key);
        builder.Append('=');
        builder.Append(SanitizePayloadToken(value));
    }

    private static string SanitizePayloadToken(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", "_");
    }

    /// <summary>
    /// Computes the post-action wait in milliseconds for any server speed.
    /// We always honour the page-reported duration; the 200ms minimum only kicks in when the
    /// page reports 0s (typical for 50000x / 1M servers) so the click has time to register.
    /// No upper cap — slow servers (1x, 3x) are expected to wait the full build duration.
    /// Use the cancellation token to interrupt long waits via Stop.
    /// </summary>
    internal static int ComputePostActionWaitMs(int durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            return 200;
        }

        // Add a small buffer so we re-read the page just after the build finishes server-side.
        return durationSeconds * 1000 + 300;
    }


}

internal sealed record UpgradeResourceWaitSnapshot(
    string BlockedLabel,
    IReadOnlyDictionary<string, UpgradeResourceWaitValue> Values,
    int WaitSeconds,
    string WaitReason,
    long? WarehouseCapacity,
    long? GranaryCapacity,
    string? StorageCapacityKind);

internal sealed record UpgradeResourceWaitValue(
    long? Required,
    long? Current,
    long? Missing,
    double? ProductionPerHour,
    int? WaitSeconds,
    string WaitReason);
