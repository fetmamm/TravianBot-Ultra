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
    int? ResourceWaitSeconds = null)
{
    /// <summary>Stable identity, preferring the unit id ("u21") and falling back to the troop slot ("t1").</summary>
    public string Key => UnitId is int id ? $"u{id}" : (TKey ?? string.Empty);
}

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

        // Travian renders a hidden countdown inside the resource shortage message
        // (".errorMessage .timer[value=seconds]") with the exact seconds until enough resources exist.
        var resourceWaitSeconds = GetInt(element, "errorWaitSeconds");

        return new SmithyTroopRow(name, unitId, tKey, currentLevel, canImprove, researchInProgress, noResources, maxed, resourceWaitSeconds);
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
