using System.Text.RegularExpressions;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless build-queue fingerprinting extracted from <see cref="TravianClient"/>.
/// <see cref="Full"/> captures the full row text (incl. timers) for change detection;
/// <see cref="Identity"/> strips live countdown timers so it only reflects WHICH items
/// are queued (name + level), not their remaining time.
/// </summary>
internal static class BuildQueueFingerprints
{
    internal static string Full(IReadOnlyList<BuildQueueItem> queue)
    {
        if (queue.Count == 0)
        {
            return "empty";
        }

        return string.Join(
            " || ",
            queue
                .Take(5)
                .Select(item => $"{item.Text.Trim()}|{item.TimeLeft?.Trim() ?? string.Empty}"));
    }

    internal static string Identity(IReadOnlyList<BuildQueueItem> queue)
    {
        if (queue.Count == 0)
        {
            return "empty";
        }

        return string.Join(
            " || ",
            queue
                .Take(5)
                .Select(item => StripQueueTimerTokens(item.Text)));
    }

    internal static bool ContainsBuilding(IReadOnlyList<BuildQueueItem> queue, string buildingName)
    {
        return queue.Any(item => TextMatchesBuilding(item.Text, buildingName));
    }

    internal static bool TextMatchesBuilding(string? text, string buildingName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(buildingName))
        {
            return false;
        }

        var normalizedName = BuildingNames.Normalize(buildingName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return false;
        }

        var cleaned = StripQueueTimerTokens(text);
        cleaned = Regex.Replace(cleaned, @"\b(?:level|lvl)\s*\d+\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        var normalizedText = BuildingNames.Normalize(cleaned);

        return normalizedText.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)
            || normalizedText.StartsWith(normalizedName + " ", StringComparison.OrdinalIgnoreCase);
    }

    // The build-queue row text contains a live countdown timer (e.g. "0:08:12") and/or a completion
    // clock that change on every read. Including them made the queue fingerprint differ each poll,
    // which WaitForResourceLevelAdvanceAsync misread as "queue changed" -> a false queued=True after a
    // click that actually did nothing (queue full), spinning the upgrade loop. Strip time-like tokens
    // so the fingerprint only reflects WHICH items are queued (name + level), not their remaining time.
    private static readonly Regex QueueTimerTokenRegex = new(
        @"\b\d{1,3}:\d{1,2}(?::\d{1,2})?\b|\b\d{1,4}\s*(?:h|hours?|m|min|mins?|minutes?|s|sec|secs?|seconds?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string StripQueueTimerTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var stripped = QueueTimerTokenRegex.Replace(text, " ");
        return Regex.Replace(stripped, @"\s+", " ").Trim();
    }
}
