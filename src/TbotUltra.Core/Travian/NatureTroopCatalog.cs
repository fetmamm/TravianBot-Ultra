namespace TbotUltra.Core.Travian;

public sealed record NatureTroopStats(
    int UnitId,
    string Name,
    int DefenceAgainstInfantry,
    int DefenceAgainstCavalry,
    int CropUse)
{
    public int BountyPerResource => CropUse * 40;
}

public sealed record NatureTroopGroup(NatureTroopStats Troop, int Count);

/// <summary>
/// Official Travian: Legends nature troop values. Unknown or malformed garrisons must never
/// be treated as an empty oasis or used for a hero-raid safety decision.
/// Source: https://blog.travian.com/id/2024/01/game-secrets-maximizing-hero-experience-and-growth/
/// </summary>
public static class NatureTroopCatalog
{
    private static readonly IReadOnlyList<NatureTroopStats> Troops =
    [
        new(31, "Rat", 25, 20, 1),
        new(32, "Spider", 35, 40, 1),
        new(33, "Snake", 40, 60, 1),
        new(34, "Bat", 66, 50, 1),
        new(35, "Wild Boar", 70, 33, 2),
        new(36, "Wolf", 80, 70, 2),
        new(37, "Bear", 140, 200, 3),
        new(38, "Crocodile", 380, 240, 3),
        new(39, "Tiger", 170, 250, 3),
        new(40, "Elephant", 440, 520, 5),
    ];

    public static IReadOnlyList<NatureTroopStats> All => Troops;

    public static bool TryParseGarrison(string? animals, out IReadOnlyList<NatureTroopGroup> garrison)
    {
        garrison = [];
        if (string.IsNullOrWhiteSpace(animals))
        {
            return false;
        }

        var groups = new List<NatureTroopGroup>();
        var seen = new HashSet<int>();
        foreach (var part in animals.Split(',', StringSplitOptions.TrimEntries))
        {
            var separator = part.LastIndexOf(' ');
            if (separator < 1 || !int.TryParse(part[(separator + 1)..], out var count) || count <= 0)
            {
                return false;
            }

            var name = part[..separator];
            var troop = Troops.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (troop is null || !seen.Add(troop.UnitId))
            {
                return false;
            }

            groups.Add(new NatureTroopGroup(troop, count));
        }

        if (groups.Count == 0)
        {
            return false;
        }

        garrison = groups;
        return true;
    }

    public static bool TryCalculateBountyPerResource(
        IReadOnlyList<NatureTroopGroup> garrison,
        out int bounty)
    {
        bounty = 0;
        if (garrison.Count == 0)
        {
            return false;
        }

        try
        {
            bounty = checked(garrison.Sum(group => checked(group.Count * group.Troop.BountyPerResource)));
            return true;
        }
        catch (OverflowException)
        {
            bounty = 0;
            return false;
        }
    }
}
