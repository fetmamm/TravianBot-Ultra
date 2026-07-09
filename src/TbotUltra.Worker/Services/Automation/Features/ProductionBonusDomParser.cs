using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless parsing/formatting for the +15%/+25% production bonus feature (payment wizard
/// Advantages tab). No I/O — pure functions so it can be unit-tested without a browser.
///
/// The Worker reads the four resource boxes in one <c>EvaluateAsync</c> call and hands the raw JSON
/// here; the Desktop reads the compact machine-token result string the client emits. Both live here so
/// the two sides never drift.
/// </summary>
public static class ProductionBonusDomParser
{
    // Ordered so the result string is deterministic (matches the on-screen Wood/Clay/Iron/Crop order).
    public static readonly IReadOnlyList<string> Resources = new[] { "lumber", "clay", "iron", "crop" };

    // +15% re-activates after Travian's daily 09:00 server-time reset, not 24h after activation.
    // Desktop turns this marker into an absolute UTC next-attempt time because it owns server-time offset
    // and user-configured delay settings.
    public const int NextAttemptAfterDailyResetSeconds = -1;

    // While +25% (gold) runs there is no free video, so the next free attempt is when it expires (+buffer).
    public const int NextAttemptAfter25BufferSeconds = 5 * 60;

    // Nothing active and the video was not activatable (missing/disabled/no ad) → back off before retry.
    public const int CooldownRetrySeconds = 4 * 60 * 60;

    /// <summary>One resource box as read from the Advantages tab DOM.</summary>
    public sealed record ProductionBonusBox(
        string Resource,
        bool Active,
        int Percent,
        string Timer,
        bool PurplePresent,
        bool PurpleEnabled);

    /// <summary>Resolved per-resource state used to build the result tokens and drive the UI store.</summary>
    public sealed record ProductionBonusResourceState(
        string Resource,
        int Bonus,
        int RemainingSeconds,
        int NextAttemptSeconds,
        bool CanActivate);

    /// <summary>Parses the raw JSON array produced by the box-reading script. Never throws.</summary>
    public static IReadOnlyList<ProductionBonusBox> ParseBoxesJson(string? json)
    {
        var boxes = new List<ProductionBonusBox>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return boxes;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return boxes;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var resource = GetString(element, "resource").ToLowerInvariant();
                if (!Resources.Contains(resource))
                {
                    continue;
                }

                boxes.Add(new ProductionBonusBox(
                    resource,
                    GetBool(element, "active"),
                    GetInt(element, "percent"),
                    GetString(element, "timer"),
                    GetBool(element, "purplePresent"),
                    GetBool(element, "purpleEnabled")));
            }
        }
        catch (JsonException)
        {
            return new List<ProductionBonusBox>();
        }

        return boxes;
    }

    /// <summary>True when at least one resource currently offers a clickable free +15% video.</summary>
    public static bool AnyActivatable(IReadOnlyList<ProductionBonusBox> boxes)
        => boxes.Any(box => box.PurplePresent && box.PurpleEnabled && !box.Active);

    /// <summary>
    /// Resolves the final state for every known resource (missing boxes count as "none").
    /// <paramref name="afterActivationAttempt"/> is true when called right after watching videos (a
    /// still-activatable resource means the video failed, so back off); false for a plain scan (a
    /// still-activatable resource is simply due now).
    /// </summary>
    public static IReadOnlyList<ProductionBonusResourceState> Classify(
        IReadOnlyList<ProductionBonusBox> boxes,
        bool afterActivationAttempt = true)
    {
        var byResource = boxes
            .GroupBy(box => box.Resource, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var states = new List<ProductionBonusResourceState>();
        foreach (var resource in Resources)
        {
            if (!byResource.TryGetValue(resource, out var box))
            {
                states.Add(new ProductionBonusResourceState(resource, 0, 0, CooldownRetrySeconds, false));
                continue;
            }

            states.Add(ClassifyBox(box, afterActivationAttempt));
        }

        return states;
    }

    private static ProductionBonusResourceState ClassifyBox(ProductionBonusBox box, bool afterActivationAttempt)
    {
        if (box.Active && box.Percent == 25)
        {
            var remaining = ParseTimerToSeconds(box.Timer);
            return new ProductionBonusResourceState(box.Resource, 25, remaining, remaining + NextAttemptAfter25BufferSeconds, false);
        }

        if (box.Active && box.Percent == 15)
        {
            var remaining = ParseTimerToSeconds(box.Timer);
            return new ProductionBonusResourceState(box.Resource, 15, remaining, NextAttemptAfterDailyResetSeconds, false);
        }

        // Nothing active. A video that is offered is due now on a scan, but after a just-failed activation
        // attempt we back off so the loop does not spin. A present but disabled free-video button means the
        // daily 09:00 server-time reset has not happened yet.
        var canActivate = box.PurplePresent && box.PurpleEnabled;
        var nextAttempt = box.PurplePresent && !box.PurpleEnabled
            ? NextAttemptAfterDailyResetSeconds
            : canActivate && !afterActivationAttempt
                ? 0
                : CooldownRetrySeconds;
        return new ProductionBonusResourceState(box.Resource, 0, 0, nextAttempt, canActivate);
    }

    /// <summary>
    /// Parses a Travian React timer ("07:59:53", "71:04:12", or "1:02:03:04") into whole seconds. Strips
    /// bidi/isolate markers and normalizes the Unicode minus. Returns 0 on any parse failure.
    /// </summary>
    public static int ParseTimerToSeconds(string? timer)
    {
        if (string.IsNullOrWhiteSpace(timer))
        {
            return 0;
        }

        var cleaned = StripBidi(timer).Trim();
        if (cleaned.Length == 0)
        {
            return 0;
        }

        var parts = cleaned.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return 0;
        }

        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
            {
                return 0;
            }
        }

        // Interpret from the right: seconds, minutes, hours, days.
        var total = 0L;
        var multipliers = new[] { 1, 60, 3600, 86400 };
        for (var i = 0; i < values.Length && i < multipliers.Length; i++)
        {
            total += (long)values[values.Length - 1 - i] * multipliers[i];
        }

        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>Builds the compact result token, e.g. <c>production_bonus=lumber:25:13935:14235;clay:15:...</c>.</summary>
    public static string BuildResultToken(IReadOnlyList<ProductionBonusResourceState> states)
    {
        var builder = new StringBuilder("production_bonus=");
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (i > 0)
            {
                builder.Append(';');
            }

            var bonus = state.Bonus == 0 ? "none" : state.Bonus.ToString(CultureInfo.InvariantCulture);
            builder.Append(state.Resource)
                .Append(':').Append(bonus)
                .Append(':').Append(state.RemainingSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(state.NextAttemptSeconds.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses the <c>production_bonus=...</c> token out of a worker result string. Returns an empty list
    /// when the token is absent or malformed. Used by the Desktop to update the timer store.
    /// </summary>
    public static IReadOnlyList<ProductionBonusResourceState> ParseResultToken(string? result)
    {
        var states = new List<ProductionBonusResourceState>();
        if (string.IsNullOrWhiteSpace(result))
        {
            return states;
        }

        var marker = "production_bonus=";
        var start = result.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return states;
        }

        var payload = result[(start + marker.Length)..];
        var end = payload.IndexOf(' ');
        if (end >= 0)
        {
            payload = payload[..end];
        }

        foreach (var entry in payload.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = entry.Split(':');
            if (fields.Length != 4)
            {
                continue;
            }

            var resource = fields[0].ToLowerInvariant();
            if (!Resources.Contains(resource))
            {
                continue;
            }

            var bonus = string.Equals(fields[1], "none", StringComparison.OrdinalIgnoreCase)
                ? 0
                : (int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0);
            int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining);
            int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var next);
            states.Add(new ProductionBonusResourceState(resource, bonus, remaining, next, false));
        }

        return states;
    }

    public static string BuildServerUtcOffsetToken(TimeSpan serverUtcOffset)
        => "production_bonus_server_utc_offset_seconds="
           + ((int)serverUtcOffset.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    public static TimeSpan? ParseServerUtcOffsetToken(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        var marker = "production_bonus_server_utc_offset_seconds=";
        var start = result.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var payload = result[(start + marker.Length)..];
        var end = payload.IndexOf(' ');
        if (end >= 0)
        {
            payload = payload[..end];
        }

        return int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static string StripBidi(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            // Directional formatting/isolate marks Travian wraps around numbers.
            if ((ch >= '‪' && ch <= '‮')
                || (ch >= '⁦' && ch <= '⁩')
                || ch == '‎'
                || ch == '‏')
            {
                continue;
            }

            builder.Append(ch == '−' ? '-' : ch);
        }

        return builder.ToString();
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();

    private static int GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }
}
