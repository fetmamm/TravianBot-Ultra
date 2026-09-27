using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless parsing/formatting for the +15%/+25% production bonus feature (payment wizard
/// Advantages tab). No I/O — pure functions so it can be unit-tested without a browser.
///
/// The Worker reads the four resource boxes in one <c>EvaluateAsync</c> call and hands the raw JSON
/// here. The operation returns the classified resource states as a typed outcome.
/// </summary>
public static class ProductionBonusDomParser
{
    // Ordered so the result string is deterministic (matches the on-screen Wood/Clay/Iron/Crop order).
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
                if (!ProductionBonusResources.All.Contains(resource))
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

    /// <summary>True only after Travian has rendered one unique bonus box for every resource.</summary>
    public static bool HasCompleteResourceSet(IReadOnlyList<ProductionBonusBox> boxes)
        => ProductionBonusResources.All.All(resource => boxes.Count(box => string.Equals(box.Resource, resource, StringComparison.OrdinalIgnoreCase)) == 1);

    /// <summary>Returns requested resources whose +15%/+25% activation is not confirmed in a fresh read.</summary>
    public static IReadOnlyList<string> FindUnconfirmedActivations(
        IEnumerable<string> requestedResources,
        IReadOnlyList<ProductionBonusBox> boxes)
    {
        var confirmed = boxes
            .Where(box => box.Active && box.Percent is 15 or 25)
            .Select(box => box.Resource)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return requestedResources
            .Where(resource => ProductionBonusResources.All.Contains(resource, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(resource => !confirmed.Contains(resource))
            .ToList();
    }

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
        foreach (var resource in ProductionBonusResources.All)
        {
            if (!byResource.TryGetValue(resource, out var box))
            {
                states.Add(new ProductionBonusResourceState(
                    resource,
                    0,
                    0,
                    ProductionBonusNextAttemptKind.RelativeDelay,
                    CooldownRetrySeconds,
                    false));
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
            return new ProductionBonusResourceState(
                box.Resource,
                25,
                remaining,
                ProductionBonusNextAttemptKind.RelativeDelay,
                remaining + NextAttemptAfter25BufferSeconds,
                false);
        }

        if (box.Active && box.Percent == 15)
        {
            var remaining = ParseTimerToSeconds(box.Timer);
            return new ProductionBonusResourceState(
                box.Resource,
                15,
                remaining,
                ProductionBonusNextAttemptKind.DailyReset,
                0,
                false);
        }

        // Nothing active. A video that is offered is due now on a scan, but after a just-failed activation
        // attempt we back off so the loop does not spin. A present but disabled free-video button means the
        // daily 09:00 server-time reset has not happened yet.
        var canActivate = box.PurplePresent && box.PurpleEnabled;
        var nextAttemptKind = box.PurplePresent && !box.PurpleEnabled
            ? ProductionBonusNextAttemptKind.DailyReset
            : canActivate && !afterActivationAttempt
                ? ProductionBonusNextAttemptKind.Immediate
                : ProductionBonusNextAttemptKind.RelativeDelay;
        var retryAfterSeconds = nextAttemptKind == ProductionBonusNextAttemptKind.RelativeDelay
            ? CooldownRetrySeconds
            : 0;
        return new ProductionBonusResourceState(
            box.Resource,
            0,
            0,
            nextAttemptKind,
            retryAfterSeconds,
            canActivate);
    }

    /// <summary>
    /// Parses a Travian React timer into whole seconds. Handles both the plain colon form ("07:59:53",
    /// "71:04:12", "1:02:03:04") and the long form where days are rendered separately with a 'd' suffix
    /// ("5d 15:52:56", "6d 10:38:52"). Strips bidi/isolate markers and normalizes the Unicode minus.
    /// Returns 0 on any parse failure.
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

        long total = 0L;

        // Long timers show the day count separately as "5d 15:52:56"; pull it out, then parse the H:M:S rest.
        var dayMatch = System.Text.RegularExpressions.Regex.Match(cleaned, @"(\d+)\s*d");
        if (dayMatch.Success)
        {
            if (int.TryParse(dayMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            {
                total += (long)days * 86400;
            }

            cleaned = cleaned[(dayMatch.Index + dayMatch.Length)..].Trim();
            if (cleaned.Length == 0)
            {
                return total > int.MaxValue ? int.MaxValue : (int)total;
            }
        }

        var parts = cleaned.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
            {
                return 0;
            }
        }

        // Interpret from the right: seconds, minutes, hours, days (added to any 'd'-prefixed days above).
        var multipliers = new[] { 1, 60, 3600, 86400 };
        for (var i = 0; i < values.Length && i < multipliers.Length; i++)
        {
            total += (long)values[values.Length - 1 - i] * multipliers[i];
        }

        return total > int.MaxValue ? int.MaxValue : (int)total;
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
