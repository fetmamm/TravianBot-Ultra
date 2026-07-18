using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class NewVillageStartupAnalyzer
{
    public static IReadOnlyList<Village> FindVillagesWithoutKnownStatus(
        IReadOnlyList<Village> villages,
        IReadOnlyDictionary<string, VillageStatus> cachedStatuses)
    {
        if (villages is null || villages.Count == 0)
        {
            return [];
        }

        // Cache entries are canonically keyed by coordinates (xy:X|Y); legacy entries by name. Collect
        // both the keys and each entry's own village name so either identity marks a village as known.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in cachedStatuses ?? new Dictionary<string, VillageStatus>())
        {
            if (!HasKnownDorf1AndDorf2Status(pair.Value) || NormalizeName(pair.Key) is not string key)
            {
                continue;
            }

            known.Add(key);
            if (NormalizeName(pair.Value.ActiveVillage) is string name)
            {
                known.Add(name);
            }
        }

        // Deduplicate by coordinate key when coordinates are known, so two villages sharing a name are
        // each scanned; name grouping remains only for villages without coordinates.
        return villages
            .Where(village => village is not null && NormalizeName(village.Name) is not null)
            .GroupBy(
                village => village.CoordX.HasValue && village.CoordY.HasValue
                    ? VillageKey.FromCoords(village.CoordX.Value, village.CoordY.Value)
                    : NormalizeName(village.Name)!,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(village => !IsKnown(village, known))
            .ToList();
    }

    private static bool IsKnown(Village village, HashSet<string> known)
    {
        if (village.CoordX.HasValue
            && village.CoordY.HasValue
            && known.Contains(VillageKey.FromCoords(village.CoordX.Value, village.CoordY.Value)))
        {
            return true;
        }

        return NormalizeName(village.Name) is string name && known.Contains(name);
    }

    private static bool HasKnownDorf1AndDorf2Status(VillageStatus? status)
    {
        return status is not null
            && status.ResourceFields is { Count: > 0 }
            && status.Buildings is { Count: > 0 };
    }

    private static string? NormalizeName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }
}
