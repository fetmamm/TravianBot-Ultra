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

    internal static BuildQueueItem? FindNewTargetBuilding(
        IReadOnlyList<BuildQueueItem> before,
        IReadOnlyList<BuildQueueItem> after,
        string buildingName,
        int slotId,
        int? gid,
        int targetLevel)
    {
        var beforeCount = before.Count(item => TargetMatches(item, buildingName, slotId, gid, targetLevel));
        return after
            .Where(item => TargetMatches(item, buildingName, slotId, gid, targetLevel))
            .Skip(beforeCount)
            .FirstOrDefault();
    }

    internal static BuildQueueItem? FindTargetBuilding(
        IReadOnlyList<BuildQueueItem> queue,
        string buildingName,
        int slotId,
        int? gid,
        int targetLevel)
    {
        return queue.FirstOrDefault(item => TargetMatches(item, buildingName, slotId, gid, targetLevel));
    }

    internal static BuildQueueItem? FindNewBuildingByName(
        IReadOnlyList<BuildQueueItem> before,
        IReadOnlyList<BuildQueueItem> after,
        string buildingName)
    {
        var beforeCount = before.Count(item => TextMatchesBuilding(item.Text, buildingName));
        var afterMatches = after
            .Where(item => TextMatchesBuilding(item.Text, buildingName))
            .ToList();

        return afterMatches.Count > beforeCount
            ? afterMatches.FirstOrDefault()
            : null;
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

    internal static int? TryReadLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text, @"\b(?:level|lvl)\s*(\d{1,3})\b", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var level)
            ? level
            : null;
    }

    private static bool TargetMatches(
        BuildQueueItem item,
        string buildingName,
        int slotId,
        int? gid,
        int targetLevel)
    {
        if (item.SlotId is int actualSlotId && actualSlotId != slotId)
        {
            return false;
        }

        // Current Official queue rows contain name, level and timer but no slot/gid link.
        // When slot identity is absent, require the normalized name instead. The before/after
        // occurrence count in FindNewTargetBuilding still proves that a matching row was added.
        if (item.SlotId is null && !TextMatchesBuilding(item.Text, buildingName))
        {
            return false;
        }

        if (gid is int expectedGid && item.Gid is int actualGid && actualGid != expectedGid)
        {
            return false;
        }

        var level = TryReadLevel(item.Text);
        if (level is int parsedLevel)
        {
            return parsedLevel >= targetLevel;
        }

        return TextMatchesBuilding(item.Text, buildingName);
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
