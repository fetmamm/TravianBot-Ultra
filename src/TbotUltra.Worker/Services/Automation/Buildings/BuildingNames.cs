using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless building-identity helpers extracted from <see cref="TravianClient"/>:
/// name normalization/aliasing, name-based comparison, level lookup by name, and
/// catalog max-level resolution. Pure functions so they can be unit-tested in isolation.
/// </summary>
internal static class BuildingNames
{
    internal static int MaxLevelFor(Building building)
    {
        if (building.Gid is int gid)
        {
            return BuildingCatalogService.MaxLevelFor(gid);
        }

        return 40;
    }

    internal static int LevelByName(VillageStatus status, string name)
    {
        var matches = status.Buildings
            .Where(building => Same(building.Name, name))
            .Select(building => building.Level ?? 0)
            .ToList();

        return matches.Count > 0 ? matches.Max() : 0;
    }

    internal static bool Same(string left, string right)
    {
        return Normalize(left) == Normalize(right);
    }

    internal static string Normalize(string name)
    {
        var cleaned = string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().ToLowerInvariant();
        return cleaned switch
        {
            "granary / silo" => "granary",
            "silo" => "granary",
            "blacksmith" => "smithy",
            // Server displays the full names; older catalog/config strings used the short forms.
            "stonemason's lodge" => "stonemason",
            "stonemasons lodge" => "stonemason",
            "hero's mansion" => "hero mansion",
            "heros mansion" => "hero mansion",
            "city wall" => "wall",
            "earth wall" => "wall",
            "palisade" => "wall",
            "stone wall" => "wall",
            "makeshift wall" => "wall",
            _ => cleaned,
        };
    }
}
