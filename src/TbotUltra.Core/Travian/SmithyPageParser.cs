using System.Text.Json;
using System.Text.RegularExpressions;
using TbotUltra.Core.Tasks;

namespace TbotUltra.Core.Travian;

/// <summary>
/// What should happen to one targeted troop, given the current Smithy page state.
/// </summary>
public enum SmithyTroopOutcome
{
    /// <summary>Resources available and the smithy is free — click Improve.</summary>
    Improve,
    /// <summary>The troop is already at (or above) the requested target level.</summary>
    AlreadyAtTarget,
    /// <summary>The troop is at the maximum level (20) and cannot be improved further.</summary>
    Maxed,
    /// <summary>The troop sits at the Smithy's level cap and cannot be improved until the Smithy building
    /// itself is upgraded (Travian shows "Smithy level too low"). Terminal for the current run.</summary>
    SmithyLevelTooLow,
    /// <summary>Not enough resources right now (Travian shows "Exchange resources"/an "Enough resources on…" hint).</summary>
    NoResources,
    /// <summary>The smithy queue is busy ("Research is already being conducted.").</summary>
    InProgress,
    /// <summary>The troop is not listed on the Smithy page — most likely not researched in the Academy yet.</summary>
    NotResearched,
}

/// <summary>
/// A single troop row parsed from the Smithy page (<c>.build_details.researches .research</c>).
/// </summary>
public sealed record SmithyTroopRow(
    string Name,
    int? UnitId,
    string? TKey,
    int CurrentLevel,
    bool CanImprove,
    bool ResearchInProgress,
    bool NoResources,
    bool Maxed,
    int? ResourceWaitSeconds = null,
    bool SmithyLevelTooLow = false)
{
    /// <summary>Stable identity, preferring the unit id ("u21") and falling back to the troop slot ("t1").</summary>
    public string Key => UnitId is int id ? $"u{id}" : (TKey ?? string.Empty);
}

/// <summary>One active troop improvement read from the Smithy under-progress table.</summary>
public sealed record SmithyQueueEntry(
    string Name,
    int? TargetLevel,
    int RemainingSeconds);

/// <summary>
/// Stateless parsing + classification for the Smithy troop-upgrade page. The Worker performs a single
/// browser eval that emits one raw object per troop row; this parser turns that JSON into
/// <see cref="SmithyTroopRow"/> values and decides, per user target, what should happen. Kept browser-free
/// so it can be unit-tested (see ENGINEERING_NOTES §4).
/// </summary>
public static class SmithyPageParser
{
    public const int MaxLevel = SmithyUpgradePayload.MaxLevel;

    private static readonly Regex LevelRegex = new(@"(\d+)", RegexOptions.Compiled);
    private static readonly Regex UnitClassRegex = new(@"\bu(\d+)\b", RegexOptions.Compiled);
    private static readonly Regex TKeyRegex = new(@"[?&]t=(t\d+)\b", RegexOptions.Compiled);

    /// <summary>
    /// Parses the worker eval JSON (array of raw row objects) into troop rows. Never throws on malformed
    /// input — a bad row is skipped so a single page glitch can't crash the task.
    /// </summary>
    public static IReadOnlyList<SmithyTroopRow> ParseRows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<SmithyTroopRow> rows = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var row = ParseRow(element);
                if (row is not null)
                {
                    rows.Add(row);
                }
            }
        }
        catch (JsonException)
        {
            return rows;
        }

        return rows;
    }

    /// <summary>
    /// Parses raw rows from the Smithy under-progress table. Rows without a positive timer are ignored,
    /// so normal troop-row duration labels can never become false active queue entries.
    /// </summary>
    public static IReadOnlyList<SmithyQueueEntry> ParseQueueEntries(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<SmithyQueueEntry> entries = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var remainingSeconds = ParseDurationSeconds(
                    GetString(element, "timerValue"),
                    GetString(element, "timerText"));
                if (remainingSeconds is not > 0)
                {
                    continue;
                }

                var rowText = GetString(element, "rowText").Trim();
                var name = FirstNonEmpty(
                    GetString(element, "name"),
                    GetString(element, "imageAlt"),
                    ParseQueueName(rowText),
                    "Smithy upgrade");
                var targetLevel = ParseQueueTargetLevel(rowText);
                entries.Add(new SmithyQueueEntry(name, targetLevel, remainingSeconds.Value));
            }
        }
        catch (JsonException)
        {
            return entries;
        }

        return entries
            .OrderBy(entry => entry.RemainingSeconds)
            .ToList();
    }

    private static SmithyTroopRow? ParseRow(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = GetString(element, "name").Trim();
        var unitId = ParseUnitId(GetString(element, "unitClass"));
        var tKey = ParseTKey(GetString(element, "buttonOnClick"));
        var currentLevel = ParseLevel(GetString(element, "levelText"));
        var buttonValue = GetString(element, "buttonValue").Trim().ToLowerInvariant();
        var errorText = GetString(element, "errorText").Trim().ToLowerInvariant();

        // A clickable troop-research button: Official renders value="Improve" with an onclick that
        // navigates to action=research; SS/legacy renders an "Upgrade" button. Either way the worker only
        // flags hasResearchButton when the button is enabled and is not a gold/exchange/video button.
        var canImprove = GetBool(element, "hasResearchButton");

        var researchInProgress = errorText.Contains("research is already being conducted")
            || errorText.Contains("already being researched");

        var noResources = !canImprove
            && (buttonValue.Contains("exchange")
                || errorText.Contains("enough resources on")
                || errorText.Contains("not enough"));

        var maxed = currentLevel >= MaxLevel
            || GetBool(element, "fullyDeveloped");

        // The troop is at the Smithy's level cap: Travian shows "Smithy level too low" and offers no
        // Improve button until the Smithy building is upgraded. Distinct from Maxed (max level 20).
        var smithyLevelTooLow = errorText.Contains("smithy level too low")
            || errorText.Contains("blacksmith level too low");

        // Travian renders a hidden countdown inside the resource shortage message
        // (".errorMessage .timer[value=seconds]") with the exact seconds until enough resources exist.
        var resourceWaitSeconds = GetInt(element, "errorWaitSeconds");

        return new SmithyTroopRow(name, unitId, tKey, currentLevel, canImprove, researchInProgress, noResources, maxed, resourceWaitSeconds, smithyLevelTooLow);
    }

    /// <summary>
    /// Finds the Smithy row matching a target. Matches on unit id first ("u21"), then on troop slot
    /// ("t1"), then by trailing number so "u21"/"t1" still line up when only one identity is present.
    /// </summary>
    public static SmithyTroopRow? FindRowForTarget(IReadOnlyList<SmithyTroopRow> rows, SmithyTroopTarget target)
    {
        if (rows is null || rows.Count == 0 || target is null || string.IsNullOrWhiteSpace(target.Key))
        {
            return null;
        }

        var key = target.Key.Trim();
        var exact = rows.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        if (key.StartsWith('t'))
        {
            return rows.FirstOrDefault(row => string.Equals(row.TKey, key, StringComparison.OrdinalIgnoreCase));
        }

        if (key.StartsWith('u') && int.TryParse(key.AsSpan(1), out var unit))
        {
            return rows.FirstOrDefault(row => row.UnitId == unit);
        }

        return null;
    }

    /// <summary>Decides what should happen to a target given its (possibly missing) Smithy row.</summary>
    public static SmithyTroopOutcome Classify(SmithyTroopRow? row, SmithyTroopTarget target)
    {
        if (row is null)
        {
            return SmithyTroopOutcome.NotResearched;
        }

        if (row.Maxed || row.CurrentLevel >= MaxLevel)
        {
            return SmithyTroopOutcome.Maxed;
        }

        if (row.CurrentLevel >= Math.Clamp(target.TargetLevel, 1, MaxLevel))
        {
            return SmithyTroopOutcome.AlreadyAtTarget;
        }

        if (row.CanImprove)
        {
            return SmithyTroopOutcome.Improve;
        }

        // Below target but the Smithy building is too low to research further. Terminal for this run: no
        // Improve button will appear until the Smithy is upgraded, so report it instead of deferring forever
        // (which spammed the task when the user's target was above the Smithy's level cap).
        if (row.SmithyLevelTooLow)
        {
            return SmithyTroopOutcome.SmithyLevelTooLow;
        }

        if (row.ResearchInProgress)
        {
            return SmithyTroopOutcome.InProgress;
        }

        if (row.NoResources)
        {
            return SmithyTroopOutcome.NoResources;
        }

        // No button, no error, not maxed: treat as busy/unknown so the task defers rather than declaring done.
        return SmithyTroopOutcome.InProgress;
    }

    private static int? ParseUnitId(string unitClass)
    {
        var match = UnitClassRegex.Match(unitClass ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    private static string? ParseTKey(string onClick)
    {
        var match = TKeyRegex.Match(onClick ?? string.Empty);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static int ParseLevel(string levelText)
    {
        var match = LevelRegex.Match(levelText ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var level) ? level : 0;
    }

    private static int? ParseDurationSeconds(string timerValue, string timerText)
    {
        foreach (var raw in new[] { timerValue, timerText })
        {
            var text = (raw ?? string.Empty).Trim();
            if (int.TryParse(text, out var seconds) && seconds > 0)
            {
                return seconds;
            }

            var parts = text.Split(':');
            if (parts.Length is 2 or 3
                && parts.All(part => int.TryParse(part, out _)))
            {
                var values = parts.Select(int.Parse).ToArray();
                seconds = parts.Length == 3
                    ? values[0] * 3600 + values[1] * 60 + values[2]
                    : values[0] * 60 + values[1];
                if (seconds > 0)
                {
                    return seconds;
                }
            }
        }

        return null;
    }

    private static int? ParseQueueTargetLevel(string rowText)
    {
        var match = Regex.Match(
            rowText ?? string.Empty,
            @"(?:to\s+)?level\s*(\d+)",
            RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var level)
            ? level
            : null;
    }

    private static string ParseQueueName(string rowText)
    {
        var text = Regex.Replace(rowText ?? string.Empty, @"\s+", " ").Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        text = Regex.Replace(text, @"(?:to\s+)?level\s*\d+.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\b\d{1,2}:\d{2}(?::\d{2})?\b.*$", string.Empty).Trim();
        return text;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int? GetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool GetBool(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();
    }
}
