using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public static class BuildingCatalogService
{
    private static readonly Dictionary<int, (string Name, string Category)> BaseBuildings = new()
    {
        [5] = ("Sawmill", "resource_buildings"),
        [6] = ("Brickyard", "resource_buildings"),
        [7] = ("Iron Foundry", "resource_buildings"),
        [8] = ("Grain Mill", "resource_buildings"),
        [9] = ("Bakery", "resource_buildings"),
        [10] = ("Warehouse", "infrastructure"),
        [11] = ("Granary", "infrastructure"),
        [13] = ("Smithy", "army_buildings"),
        [14] = ("Tournament Square", "army_buildings"),
        [15] = ("Main Building", "infrastructure"),
        [16] = ("Rally Point", "army_buildings"),
        [17] = ("Marketplace", "infrastructure"),
        [18] = ("Embassy", "infrastructure"),
        [19] = ("Barracks", "army_buildings"),
        [20] = ("Stable", "army_buildings"),
        [21] = ("Workshop", "army_buildings"),
        [22] = ("Academy", "army_buildings"),
        [23] = ("Cranny", "infrastructure"),
        [24] = ("Town Hall", "infrastructure"),
        [25] = ("Residence", "infrastructure"),
        [26] = ("Palace", "infrastructure"),
        [27] = ("Treasury", "infrastructure"),
        [28] = ("Trade Office", "infrastructure"),
        [29] = ("Great Barracks", "army_buildings"),
        [30] = ("Great Stable", "army_buildings"),
        [34] = ("Stonemason's Lodge", "infrastructure"),
        [37] = ("Hero's Mansion", "army_buildings"),
        [38] = ("Great Warehouse", "resource_buildings"),
        [39] = ("Great Granary", "resource_buildings"),
        [40] = ("Wonder of the World", "infrastructure"),
        [46] = ("Hospital", "army_buildings"),
    };

    private static readonly Dictionary<int, (string Name, string Category)> TribeSpecialBuildings = new()
    {
        [31] = ("City Wall", "infrastructure"),
        [32] = ("Earth Wall", "infrastructure"),
        [33] = ("Palisade", "infrastructure"),
        [35] = ("Brewery", "army_buildings"),
        [36] = ("Trapper", "army_buildings"),
        [41] = ("Horse Drinking Trough", "army_buildings"),
        [42] = ("Stone Wall", "infrastructure"),
        [43] = ("Makeshift Wall", "infrastructure"),
        [44] = ("Command Center", "infrastructure"),
    };

    private static readonly Dictionary<int, List<BuildingRequirementEntry>> BuildingRequirements = new()
    {
        [5] = [new("Clay Pit", 10), new("Main Building", 5)],
        [6] = [new("Woodcutter", 10), new("Main Building", 5)],
        [7] = [new("Iron Mine", 10), new("Main Building", 5)],
        [8] = [new("Cropland", 5), new("Main Building", 5)],
        [9] = [new("Cropland", 10), new("Grain Mill", 5), new("Main Building", 5)],
        [17] = [new("Main Building", 3), new("Warehouse", 1), new("Granary", 1)],
        [18] = [new("Main Building", 1)],
        [19] = [new("Main Building", 3), new("Rally Point", 1)],
        [20] = [new("Academy", 5), new("Smithy", 3)],
        [13] = [new("Main Building", 3), new("Academy", 1)],
        [14] = [new("Rally Point", 15), new("Academy", 10)],
        [21] = [new("Academy", 10), new("Main Building", 5)],
        [22] = [new("Barracks", 3), new("Main Building", 3)],
        [24] = [new("Academy", 10), new("Main Building", 10)],
        [25] = [new("Main Building", 5)],
        [26] = [new("Embassy", 1), new("Main Building", 5)],
        [27] = [new("Main Building", 10)],
        [28] = [new("Marketplace", 20), new("Stable", 10)],
        [29] = [new("Barracks", 20)],
        [30] = [new("Stable", 20)],
        [34] = [new("Main Building", 5)],
        [35] = [new("Main Building", 10)],
        [36] = [new("Rally Point", 1)],
        [37] = [new("Main Building", 3), new("Rally Point", 1)],
        [41] = [new("Rally Point", 10), new("Stable", 20)],
        [44] = [new("Main Building", 10), new("Academy", 15)],
        [46] = [new("Main Building", 10), new("Academy", 15)],
    };

    private static readonly HashSet<int> SingleInstanceGids =
    [
        13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 24, 25, 26, 27, 28, 29, 30, 34, 35, 36, 37, 40, 41, 44, 46,
        31, 32, 33, 42, 43,
    ];

    private static readonly Dictionary<string, int[]> TribeSpecialGids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Romans"] = [31, 41],
        ["Teutons"] = [32, 35],
        ["Gauls"] = [33, 36],
        ["Egyptians"] = [42],
        ["Huns"] = [43],
        ["Spartans"] = [44],
    };

    // Resource fields (gids 1-4) are not in BaseBuildings (which only lists constructible buildings),
    // but they appear in the catalog JSON and are needed for build-time/cost estimates of field upgrades.
    private static readonly Dictionary<int, string> ResourceFieldNames = new()
    {
        [1] = "Woodcutter",
        [2] = "Clay Pit",
        [3] = "Iron Mine",
        [4] = "Cropland",
    };

    private static readonly Lazy<IReadOnlyDictionary<string, int>> NameToGid = new(BuildNameToGid);

    private static readonly Lazy<IReadOnlyDictionary<int, BuildingCatalogEntry>> CatalogData = new(LoadCatalogData);

    // Reverse lookup name -> gid across base buildings, tribe specials and resource fields.
    // Used to resolve a queued/clicked building name back to its catalog gid for estimates.
    public static int? GidForName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return NameToGid.Value.TryGetValue(name.Trim(), out var gid) ? gid : null;
    }

    private static IReadOnlyDictionary<string, int> BuildNameToGid()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ResourceFieldNames)
        {
            map[pair.Value] = pair.Key;
        }

        foreach (var pair in BaseBuildings)
        {
            map[pair.Value.Name] = pair.Key;
        }

        foreach (var pair in TribeSpecialBuildings)
        {
            map[pair.Value.Name] = pair.Key;
        }

        return map;
    }

    public static IReadOnlyList<TribeBuildingCatalogEntry> GetCatalogForTribe(string tribe)
    {
        var normalized = NormalizeTribe(tribe);
        var entries = new List<TribeBuildingCatalogEntry>();

        foreach (var pair in BaseBuildings.OrderBy(item => item.Key))
        {
            entries.Add(new TribeBuildingCatalogEntry(
                Gid: pair.Key,
                Name: pair.Value.Name,
                Category: pair.Value.Category,
                IsSpecial: false,
                Requirements: RequirementsFor(pair.Key)));
        }

        foreach (var gid in TribeSpecialFor(normalized))
        {
            if (!TribeSpecialBuildings.TryGetValue(gid, out var value))
            {
                continue;
            }

            entries.Add(new TribeBuildingCatalogEntry(
                Gid: gid,
                Name: value.Name,
                Category: value.Category,
                IsSpecial: true,
                Requirements: RequirementsFor(gid)));
        }

        return entries
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<TribeBuildingCatalogFullEntry> GetFullCatalog(string playerTribe)
    {
        var normalizedPlayer = NormalizeTribe(playerTribe);
        var entries = new List<TribeBuildingCatalogFullEntry>();

        foreach (var pair in BaseBuildings.OrderBy(item => item.Key))
        {
            entries.Add(new TribeBuildingCatalogFullEntry(
                Gid: pair.Key,
                Name: pair.Value.Name,
                Category: pair.Value.Category,
                IsSpecial: false,
                RequiredTribe: null,
                MatchesPlayerTribe: true,
                Requirements: RequirementsFor(pair.Key)));
        }

        foreach (var pair in TribeSpecialBuildings.OrderBy(item => item.Key))
        {
            var owningTribe = TribeSpecialGids
                .FirstOrDefault(kvp => kvp.Value.Contains(pair.Key)).Key;
            entries.Add(new TribeBuildingCatalogFullEntry(
                Gid: pair.Key,
                Name: pair.Value.Name,
                Category: pair.Value.Category,
                IsSpecial: true,
                RequiredTribe: owningTribe,
                MatchesPlayerTribe: !string.IsNullOrEmpty(owningTribe)
                    && string.Equals(owningTribe, normalizedPlayer, StringComparison.OrdinalIgnoreCase),
                Requirements: RequirementsFor(pair.Key)));
        }

        return entries;
    }

    public static IReadOnlyList<BuildingRequirementEntry> RequirementsFor(int gid)
    {
        return BuildingRequirements.TryGetValue(gid, out var requirements)
            ? requirements
            : [];
    }

    public static int MaxLevelFor(int gid)
    {
        if (CatalogData.Value.TryGetValue(gid, out var entry))
        {
            return entry.MaxLevel;
        }

        return 20;
    }

    public static IReadOnlyList<BuildingLevelStats>? LevelsFor(int gid)
    {
        return CatalogData.Value.TryGetValue(gid, out var entry) ? entry.Levels : null;
    }

    public static BuildingLevelStats? CostFor(int gid, int level)
    {
        if (!CatalogData.Value.TryGetValue(gid, out var entry))
        {
            return null;
        }

        if (level < 1 || level > entry.Levels.Count)
        {
            return null;
        }

        return entry.Levels[level - 1];
    }

    // Build time scaled by server speed and reduced by the village Main Building level. Travian's Main
    // Building speeds up construction by 0.964^(level-1) (level 1 = no reduction, ~50% at level 20).
    public static double BuildSecondsFor(int gid, int level, double serverSpeed = 1.0, int mainBuildingLevel = 1)
    {
        var stats = CostFor(gid, level);
        if (stats is null || serverSpeed <= 0)
        {
            return 0;
        }

        var mainBuildingFactor = mainBuildingLevel > 1
            ? System.Math.Pow(0.964, mainBuildingLevel - 1)
            : 1.0;
        return stats.BuildSeconds1x / serverSpeed * mainBuildingFactor;
    }

    public static bool IsSingleInstance(int gid)
    {
        return SingleInstanceGids.Contains(gid);
    }

    /// <summary>
    /// Returns the in-game category index for a building gid, used by Travian's
    /// <c>/build.php?id={slot}&amp;category={N}</c> URL filter.
    /// 1 = Infrastructure, 2 = Army, 3 = Resource buildings.
    /// </summary>
    public static int? CategoryIndexFor(int gid)
    {
        string? category = null;
        if (BaseBuildings.TryGetValue(gid, out var baseB))
        {
            category = baseB.Category;
        }
        else if (TribeSpecialBuildings.TryGetValue(gid, out var spec))
        {
            category = spec.Category;
        }

        return category switch
        {
            "infrastructure" => 1,
            "army_buildings" => 2,
            "resource_buildings" => 3,
            _ => null,
        };
    }

    public static (int Gid, string Name)? WallForTribe(string tribe)
    {
        var normalized = NormalizeTribe(tribe);
        return normalized switch
        {
            "Romans" => (31, "City Wall"),
            "Teutons" => (32, "Earth Wall"),
            "Gauls" => (33, "Palisade"),
            "Egyptians" => (42, "Stone Wall"),
            "Huns" => (43, "Makeshift Wall"),
            _ => null,
        };
    }

    public static string NormalizeTribe(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var trimmed = value.Trim();
        return trimmed switch
        {
            var v when v.Contains("Roman", StringComparison.OrdinalIgnoreCase) => "Romans",
            var v when v.Contains("Teuton", StringComparison.OrdinalIgnoreCase) => "Teutons",
            var v when v.Contains("Gaul", StringComparison.OrdinalIgnoreCase) => "Gauls",
            var v when v.Contains("Egypt", StringComparison.OrdinalIgnoreCase) => "Egyptians",
            var v when v.Contains("Hun", StringComparison.OrdinalIgnoreCase) => "Huns",
            var v when v.Contains("Spartan", StringComparison.OrdinalIgnoreCase) => "Spartans",
            _ => trimmed,
        };
    }

    private static IReadOnlyList<int> TribeSpecialFor(string tribe)
    {
        if (TribeSpecialGids.TryGetValue(tribe, out var gids))
        {
            return gids;
        }

        return [];
    }

    private static IReadOnlyDictionary<int, BuildingCatalogEntry> LoadCatalogData()
    {
        var path = ResolveCatalogPath();
        if (path is null || !File.Exists(path))
        {
            return new Dictionary<int, BuildingCatalogEntry>();
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("buildings", out var buildings))
            {
                return new Dictionary<int, BuildingCatalogEntry>();
            }

            var result = new Dictionary<int, BuildingCatalogEntry>();
            foreach (var item in buildings.EnumerateObject())
            {
                if (!int.TryParse(item.Name, out var gid))
                {
                    continue;
                }

                var maxLevel = item.Value.TryGetProperty("max_level", out var ml) ? ml.GetInt32() : 20;
                var name = item.Value.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
                var tribe = item.Value.TryGetProperty("tribe", out var tr) && tr.ValueKind == JsonValueKind.String
                    ? tr.GetString()
                    : null;

                var levels = new List<BuildingLevelStats>();
                if (item.Value.TryGetProperty("levels", out var levelsArr) && levelsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var level in levelsArr.EnumerateArray())
                    {
                        levels.Add(new BuildingLevelStats(
                            Level: GetInt(level, "level"),
                            Wood: GetInt(level, "wood"),
                            Clay: GetInt(level, "clay"),
                            Iron: GetInt(level, "iron"),
                            Crop: GetInt(level, "crop"),
                            Upkeep: GetInt(level, "upkeep"),
                            CulturePoints: GetInt(level, "culture_points"),
                            BuildSeconds1x: GetLong(level, "build_seconds_1x")));
                    }
                }

                result[gid] = new BuildingCatalogEntry(gid, name, tribe, maxLevel, levels);
            }

            return result;
        }
        catch
        {
            return new Dictionary<int, BuildingCatalogEntry>();
        }
    }

    private static int GetInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;

    private static long GetLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;

    private static string? ResolveCatalogPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "config", "buildings_catalog.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private sealed record BuildingCatalogEntry(
        int Gid,
        string Name,
        string? Tribe,
        int MaxLevel,
        IReadOnlyList<BuildingLevelStats> Levels);
}

public sealed record BuildingLevelStats(
    int Level,
    int Wood,
    int Clay,
    int Iron,
    int Crop,
    int Upkeep,
    int CulturePoints,
    long BuildSeconds1x);
