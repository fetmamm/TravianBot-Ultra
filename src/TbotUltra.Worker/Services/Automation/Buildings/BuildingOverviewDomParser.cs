using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless interpretation of building-overview slot snapshots.
/// </summary>
internal static class BuildingOverviewDomParser
{
    private static readonly Dictionary<string, string> TravianBuildings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["g1"] = "Woodcutter",
        ["g2"] = "Clay Pit",
        ["g3"] = "Iron Mine",
        ["g4"] = "Cropland",
        ["g5"] = "Sawmill",
        ["g6"] = "Brickyard",
        ["g7"] = "Iron Foundry",
        ["g8"] = "Grain Mill",
        ["g9"] = "Bakery",
        ["g10"] = "Warehouse",
        ["g11"] = "Granary",
        ["g13"] = "Smithy",
        ["g14"] = "Tournament Square",
        ["g15"] = "Main Building",
        ["g16"] = "Rally Point",
        ["g17"] = "Marketplace",
        ["g18"] = "Embassy",
        ["g19"] = "Barracks",
        ["g20"] = "Stable",
        ["g21"] = "Workshop",
        ["g22"] = "Academy",
        ["g23"] = "Cranny",
        ["g24"] = "Town Hall",
        ["g25"] = "Residence",
        ["g26"] = "Palace",
        ["g27"] = "Treasury",
        ["g28"] = "Trade Office",
        ["g29"] = "Great Barracks",
        ["g30"] = "Great Stable",
        ["g31"] = "City Wall",
        ["g32"] = "Earth Wall",
        ["g33"] = "Palisade",
        ["g34"] = "Stonemason's Lodge",
        ["g35"] = "Brewery",
        ["g36"] = "Trapper",
        ["g37"] = "Hero's Mansion",
        ["g38"] = "Great Warehouse",
        ["g39"] = "Great Granary",
        ["g40"] = "Wonder of the World",
        ["g41"] = "Horse Drinking Trough",
        ["g42"] = "Stone Wall",
        ["g43"] = "Makeshift Wall",
        ["g44"] = "Command Center",
        ["g46"] = "Hospital",
    };

    private static readonly Lazy<IReadOnlyDictionary<string, string>> NormalizedBuildingCodesByName = new(() =>
    {
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in TravianBuildings)
        {
            var normalized = BuildingNames.Normalize(entry.Value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (mappings.TryGetValue(normalized, out var existingCode)
                && !string.Equals(existingCode, entry.Key, StringComparison.OrdinalIgnoreCase))
            {
                duplicates.Add(normalized);
                continue;
            }

            mappings[normalized] = entry.Key;
        }

        foreach (var duplicate in duplicates)
        {
            mappings.Remove(duplicate);
        }

        return mappings;
    });

    private static readonly Regex AidClassRegex = new(@"\baid(?<id>\d{1,2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FallbackSlotClassRegex = new(@"\ba(?<id>\d{1,2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BuildingSlotQueryRegex = new(@"[?&](?:id|a)=(?<id>\d{1,2})(?:\D|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BuildingSlotDataRegex = new(@"data-(?:aid|slot|slot-id|building-slot-id|id)\s*=\s*[""']?(?<id>\d{1,2})(?:[""'\s>]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BuildingCodeClassRegex = new(@"\bg(?<gid>\d{1,2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BuildingGidQueryRegex = new(@"[?&]gid=(?<gid>\d{1,2})(?:\D|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BuildingGidDataRegex = new(@"data-(?:gid|building-gid|type)\s*=\s*[""']?(?<gid>\d{1,2})(?:[""'\s>]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OverviewLevelRegex = new(@"\blevel\s*(?<level>\d{1,2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    internal static BuildingOverviewScanResult Parse(IReadOnlyList<BuildingOverviewSlotSnapshot> slotSnapshots)
    {
        var buildings = new Dictionary<int, BuildingInfo>();

        foreach (var slotSnapshot in slotSnapshots)
        {
            try
            {
                var info = CreateBuildingInfo(slotSnapshot);
                if (info is null)
                {
                    continue;
                }

                buildings[info.SlotId] = info;
            }
            catch
            {
                // Ignore malformed individual slots and keep parsing the rest of the overview.
            }
        }

        var occupiedSlots = buildings.Values
            .Where(item => item.HasOccupancyEvidence)
            .ToList();
        var missingBuildingCodeCount = occupiedSlots.Count(item => ParseGidFromBuildingCode(item.BuildingCode) is null);
        var unknownLevelCount = occupiedSlots.Count(item => !item.LevelKnown);
        var hasMainBuilding = ContainsBuilding(buildings.Values, 15, "Main Building");
        var hasRallyPoint = ContainsBuilding(buildings.Values, 16, "Rally Point");

        var metrics = BuildingOverviewScanPolicy.Evaluate(
            buildings.Count,
            missingBuildingCodeCount,
            unknownLevelCount,
            hasMainBuilding,
            hasRallyPoint);

        return new BuildingOverviewScanResult
        {
            Buildings = buildings,
            Metrics = metrics,
        };
    }

    private static BuildingInfo? CreateBuildingInfo(BuildingOverviewSlotSnapshot slotSnapshot)
    {
        var classes = (slotSnapshot.ClassName ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var slotId = TryExtractSlotId(classes)
            ?? TryExtractSlotIdFromText(slotSnapshot.OuterHtml);
        if (slotId is null || slotId < 19 || slotId > 40)
        {
            return null;
        }

        var buildingCode = TryExtractBuildingCode(classes)
            ?? TryExtractBuildingCodeFromText(slotSnapshot.OuterHtml);
        var level = TryParseOverviewLevel(slotSnapshot.LevelText, slotSnapshot.DataLevelText, slotSnapshot.Text);
        var levelKnown = level.HasValue;
        var nameCandidate = SelectBuildingNameCandidate(slotSnapshot.DataNameText, slotSnapshot.NameText, slotSnapshot.TitleText, slotSnapshot.AltText);
        var hasOccupancyEvidence = slotSnapshot.OccupiedEvidence
            || !string.IsNullOrWhiteSpace(buildingCode)
            || !string.IsNullOrWhiteSpace(nameCandidate);

        buildingCode ??= TryResolveBuildingCodeFromName(nameCandidate);

        var gid = ParseGidFromBuildingCode(buildingCode);
        var normalizedLevel = level ?? 0;

        if (slotId.Value == 40 && gid is 31 or 32 or 33 or 42 or 43 && normalizedLevel == 0)
        {
            normalizedLevel = 1;
            levelKnown = true;
        }

        if (string.IsNullOrEmpty(buildingCode) && normalizedLevel > 0 && slotId.Value == 39)
        {
            buildingCode = "g16";
        }

        var buildingName = ResolveBuildingDisplayName(
            buildingCode,
            nameCandidate,
            hasOccupancyEvidence);

        return new BuildingInfo
        {
            SlotId = slotId.Value,
            BuildingCode = buildingCode ?? string.Empty,
            BuildingName = buildingName,
            Level = normalizedLevel,
            LevelKnown = levelKnown,
            HasOccupancyEvidence = hasOccupancyEvidence,
        };
    }

    private static bool ContainsBuilding(IEnumerable<BuildingInfo> buildings, int gid, string name)
    {
        return buildings.Any(item =>
            ParseGidFromBuildingCode(item.BuildingCode) == gid
            || BuildingNames.Same(item.BuildingName, name));
    }

    private static int? TryParseOverviewLevel(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (TryParsePositiveInt(candidate, out var parsedLevel))
            {
                return parsedLevel;
            }

            var fallbackMatch = OverviewLevelRegex.Match(candidate ?? string.Empty);
            if (fallbackMatch.Success
                && int.TryParse(fallbackMatch.Groups["level"].Value, out parsedLevel))
            {
                return parsedLevel;
            }
        }

        return null;
    }

    private static bool TryParsePositiveInt(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return int.TryParse(text.Trim(), out value) && value >= 0;
    }

    private static int? TryExtractSlotIdFromText(string? text)
    {
        return TryExtractIntFromRegex(AidClassRegex, text, "id")
            ?? TryExtractIntFromRegex(FallbackSlotClassRegex, text, "id")
            ?? TryExtractIntFromRegex(BuildingSlotQueryRegex, text, "id")
            ?? TryExtractIntFromRegex(BuildingSlotDataRegex, text, "id");
    }

    private static string? TryExtractBuildingCodeFromText(string? text)
    {
        var gid = TryExtractIntFromRegex(BuildingCodeClassRegex, text, "gid")
            ?? TryExtractIntFromRegex(BuildingGidQueryRegex, text, "gid")
            ?? TryExtractIntFromRegex(BuildingGidDataRegex, text, "gid");
        return gid is > 0 ? $"g{gid.Value}" : null;
    }

    private static int? TryExtractIntFromRegex(Regex regex, string? text, string groupName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = regex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[groupName].Value, out var value)
            ? value
            : null;
    }

    internal static string? TryResolveBuildingCodeFromName(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalized = BuildingNames.Normalize(candidate ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (NormalizedBuildingCodesByName.Value.TryGetValue(normalized, out var buildingCode))
            {
                return buildingCode;
            }
        }

        return null;
    }

    internal static string ResolveBuildingDisplayName(string? buildingCode, string? nameCandidate, bool hasOccupancyEvidence)
    {
        if (!string.IsNullOrWhiteSpace(buildingCode)
            && !string.Equals(buildingCode, "g0", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveBuildingName(buildingCode);
        }

        if (!string.IsNullOrWhiteSpace(nameCandidate))
        {
            return nameCandidate!;
        }

        return hasOccupancyEvidence ? "Unknown" : "Empty";
    }

    internal static string? SelectBuildingNameCandidate(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var sanitized = SanitizeBuildingNameCandidate(candidate);
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                return sanitized;
            }
        }

        return null;
    }

    private static string? SanitizeBuildingNameCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var cleaned = string.Join(" ", candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        if (int.TryParse(cleaned, out _))
        {
            return null;
        }

        var lowered = cleaned.ToLowerInvariant();
        if (lowered.Contains("building site", StringComparison.Ordinal)
            || lowered.Contains("empty site", StringComparison.Ordinal)
            || lowered.Contains("construct", StringComparison.Ordinal)
            || lowered.Contains("free site", StringComparison.Ordinal)
            || lowered.Contains("click to build", StringComparison.Ordinal))
        {
            return null;
        }

        return cleaned;
    }

    private static int? TryExtractSlotId(IEnumerable<string> classes)
    {
        string? fallback = null;
        foreach (var className in classes)
        {
            if (className.StartsWith("aid", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(className[3..], out var aidSlotId))
            {
                return aidSlotId;
            }

            if (fallback is null
                && className.StartsWith("a", StringComparison.OrdinalIgnoreCase)
                && !className.StartsWith("aid", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(className[1..], out _))
            {
                fallback = className;
            }
        }

        return fallback is not null && int.TryParse(fallback[1..], out var slotId)
            ? slotId
            : null;
    }

    private static string? TryExtractBuildingCode(IEnumerable<string> classes)
    {
        foreach (var className in classes)
        {
            if (className.StartsWith("g", StringComparison.OrdinalIgnoreCase)
                && className.Length > 1
                && int.TryParse(className[1..], out _))
            {
                return className.ToLowerInvariant();
            }
        }

        return null;
    }

    private static string ResolveBuildingName(string? buildingCode)
    {
        if (string.IsNullOrWhiteSpace(buildingCode)
            || string.Equals(buildingCode, "g0", StringComparison.OrdinalIgnoreCase))
        {
            return "Empty";
        }

        return TravianBuildings.TryGetValue(buildingCode, out var buildingName)
            ? buildingName
            : buildingCode;
    }

    internal static int? ParseGidFromBuildingCode(string? buildingCode)
    {
        if (string.IsNullOrWhiteSpace(buildingCode)
            || string.Equals(buildingCode, "g0", StringComparison.OrdinalIgnoreCase)
            || buildingCode.Length < 2)
        {
            return null;
        }

        return int.TryParse(buildingCode[1..], out var gid)
            ? gid
            : null;
    }
}

internal sealed class BuildingOverviewSlotSnapshot
{
    public int Index { get; set; }
    public string ClassName { get; set; } = string.Empty;
    public string OuterHtml { get; set; } = string.Empty;
    public string LevelText { get; set; } = string.Empty;
    public string DataLevelText { get; set; } = string.Empty;
    public string DataNameText { get; set; } = string.Empty;
    public string NameText { get; set; } = string.Empty;
    public string TitleText { get; set; } = string.Empty;
    public string AltText { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool OccupiedEvidence { get; set; }
}

internal sealed class BuildingOverviewScanResult
{
    public Dictionary<int, BuildingInfo> Buildings { get; init; } = new();
    public BuildingOverviewScanMetrics Metrics { get; init; } = BuildingOverviewScanPolicy.Evaluate(0, 0, 0, false, false);
}

internal sealed class BuildingInfo
{
    public int SlotId { get; set; }
    public string BuildingCode { get; set; } = string.Empty;
    public string BuildingName { get; set; } = "Empty";
    public int Level { get; set; }
    public bool LevelKnown { get; set; }
    public bool HasOccupancyEvidence { get; set; }
}

