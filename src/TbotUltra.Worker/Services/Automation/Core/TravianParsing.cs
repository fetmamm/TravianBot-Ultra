using System.Text.RegularExpressions;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless text parsing/formatting primitives extracted from <see cref="TravianClient"/>:
/// numeric/resource/duration parsing, duration formatting, shortest-queue duration, and
/// upgrade-outcome mapping. Pure functions so they can be unit-tested in isolation.
/// </summary>
internal static class TravianParsing
{
    internal static long? TryParseResourceValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var parsed) ? parsed : null;
    }

    internal static int? ParseNumericTextToInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = Regex.Replace(value, @"\s+", " ").Trim();
        var match = Regex.Match(normalized, @"(\d[\d\s\.,']*)");
        if (!match.Success)
        {
            return null;
        }

        var digits = Regex.Replace(match.Groups[1].Value, @"\D", string.Empty);
        if (digits.Length == 0)
        {
            return null;
        }

        return int.TryParse(digits, out var parsed) ? parsed : null;
    }

    internal static int? ParseDurationToSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        var hms = Regex.Match(value, @"(?:(?<h>\d{1,3})\s*:)?(?<m>\d{1,2})\s*:\s*(?<s>\d{1,2})");
        if (hms.Success)
        {
            var h = hms.Groups["h"].Success ? int.Parse(hms.Groups["h"].Value) : 0;
            var m = int.Parse(hms.Groups["m"].Value);
            var s = int.Parse(hms.Groups["s"].Value);
            return Math.Max(0, h * 3600 + m * 60 + s);
        }

        var minutes = Regex.Match(value, @"(?<m>\d{1,4})\s*m(?:in|inute)?s?", RegexOptions.IgnoreCase);
        var seconds = Regex.Match(value, @"(?<s>\d{1,6})\s*s(?:ec|econd)?s?", RegexOptions.IgnoreCase);
        if (minutes.Success || seconds.Success)
        {
            var m = minutes.Success ? int.Parse(minutes.Groups["m"].Value) : 0;
            var s = seconds.Success ? int.Parse(seconds.Groups["s"].Value) : 0;
            return Math.Max(0, m * 60 + s);
        }

        return null;
    }

    internal static string FormatDuration(int seconds)
    {
        var clamped = Math.Max(0, seconds);
        var ts = TimeSpan.FromSeconds(clamped);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        }

        return $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    internal static int? ResolveShortestQueueDurationSeconds(IReadOnlyList<BuildQueueItem> items)
    {
        var candidates = items
            .Select(item => ParseDurationToSeconds(item.TimeLeft))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.Min();
    }

    internal static TravianClient.UpgradeAttemptOutcome ParseUpgradeOutcome(string? value)
    {
        return value?.Trim() switch
        {
            "CanUpgrade" => TravianClient.UpgradeAttemptOutcome.CanUpgrade,
            "BlockedByResources" => TravianClient.UpgradeAttemptOutcome.BlockedByResources,
            "BlockedByQueue" => TravianClient.UpgradeAttemptOutcome.BlockedByQueue,
            "BlockedByMaxLevel" => TravianClient.UpgradeAttemptOutcome.BlockedByMaxLevel,
            _ => TravianClient.UpgradeAttemptOutcome.BlockedUnknown,
        };
    }
}
