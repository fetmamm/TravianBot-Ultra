using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public static class BuildingCatalogService
{
    private static readonly Dictionary<int, (string Name, string Category)> BaseBuildings = new()
    {
        [10] = ("Warehouse", "resource_buildings"),
        [11] = ("Granary", "resource_buildings"),
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
        [34] = ("Stonemason", "infrastructure"),
        [37] = ("Hero Mansion", "army_buildings"),
        [38] = ("Great Warehouse", "resource_buildings"),
        [39] = ("Great Granary", "resource_buildings"),
        [40] = ("Wonder of the World", "infrastructure"),
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
        [44] = ("Command Center", "army_buildings"),
    };

    private static readonly Dictionary<int, List<BuildingRequirementEntry>> BuildingRequirements = new()
    {
        [17] = [new("Main Building", 3), new("Warehouse", 1), new("Granary", 1)],
        [18] = [new("Main Building", 1)],
        [19] = [new("Main Building", 3), new("Rally Point", 1)],
        [20] = [new("Academy", 5), new("Blacksmith", 3)],
        [21] = [new("Academy", 10), new("Main Building", 5)],
        [22] = [new("Barracks", 3), new("Main Building", 3)],
        [24] = [new("Academy", 10), new("Main Building", 10)],
        [25] = [new("Main Building", 5)],
        [26] = [new("Embassy", 1), new("Main Building", 5)],
        [27] = [new("Main Building", 10)],
        [28] = [new("Marketplace", 20), new("Stable", 10)],
        [31] = [new("Rally Point", 1)],
        [32] = [new("Rally Point", 1)],
        [33] = [new("Rally Point", 1)],
        [34] = [new("Main Building", 5)],
        [35] = [new("Main Building", 10)],
        [36] = [new("Rally Point", 1)],
        [37] = [new("Main Building", 3), new("Rally Point", 1)],
        [41] = [new("Stable", 20)],
        [42] = [new("Rally Point", 1)],
        [43] = [new("Rally Point", 1)],
        [44] = [new("Main Building", 10), new("Academy", 15)],
    };

    private static readonly Dictionary<int, int> BuildingMaxLevelsByGid = new()
    {
        [10] = 20, [11] = 20, [15] = 20, [16] = 20, [17] = 20, [18] = 20, [19] = 20, [20] = 20, [21] = 20,
        [22] = 20, [23] = 10, [24] = 20, [25] = 20, [26] = 20, [27] = 20, [28] = 20, [29] = 20, [30] = 20,
        [31] = 20, [32] = 20, [33] = 20, [34] = 20, [35] = 20, [36] = 20, [37] = 20, [38] = 20, [39] = 20,
        [40] = 100, [41] = 20, [42] = 20, [43] = 20, [44] = 20,
    };

    private static readonly HashSet<int> SingleInstanceGids =
    [
        15, 16, 17, 18, 19, 20, 21, 22, 24, 25, 26, 27, 28, 29, 30, 34, 35, 36, 37, 40, 41, 44,
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

    public static IReadOnlyList<BuildingRequirementEntry> RequirementsFor(int gid)
    {
        return BuildingRequirements.TryGetValue(gid, out var requirements)
            ? requirements
            : [];
    }

    public static int MaxLevelFor(int gid)
    {
        return BuildingMaxLevelsByGid.TryGetValue(gid, out var level)
            ? level
            : 20;
    }

    public static bool IsSingleInstance(int gid)
    {
        return SingleInstanceGids.Contains(gid);
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
}
