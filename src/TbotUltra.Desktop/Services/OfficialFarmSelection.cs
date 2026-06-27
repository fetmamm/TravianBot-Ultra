using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class OfficialFarmSelection
{
    // Villages at or below this population are treated as low-value (often abandoned/never-played
    // accounts). When skipLowPopulationVillages is on, they are dropped from the selection.
    public const int LowPopulationThreshold = 8;

    // referenceVillage: when supplied, distance is computed live (straight-line) from that village to
    //   each row's coordinates, replacing the stored row.Distance for ordering and the distance filter.
    //   This makes "nearest first" correct for oasis lists (no stored distance) and lets the user change
    //   the reference village without rescraping. When null, the stored row.Distance is used (legacy).
    // oasisTypes: when supplied, only rows whose OasisType is in the set are kept (oasis source lists).
    // includeOccupied: when false, rows flagged IsOccupied are dropped (oasis source lists).
    public static IReadOnlyList<FarmCoordinate> Filter(
        IEnumerable<TravcoListStore.TravcoSavedRow> sourceRows,
        IReadOnlySet<string> existingCoordinates,
        int amount,
        string order,
        string populationMode,
        long populationLimit,
        double? maximumDistance,
        bool skipDuplicates,
        (int X, int Y)? referenceVillage = null,
        IReadOnlySet<string>? oasisTypes = null,
        bool includeOccupied = true,
        bool skipLowPopulationVillages = false)
    {
        if (amount <= 0)
        {
            return [];
        }

        // Project to coordinate-parseable candidates with an effective distance, applying the
        // oasis-specific filters up front so they affect both ordering and the slot count.
        var candidates = new List<(int X, int Y, long? Pop, double? Distance)>();
        foreach (var row in sourceRows)
        {
            if (!row.Selected || !TravianMapDistance.TryParseCoordinates(row.Coordinates, out var x, out var y))
            {
                continue;
            }

            if (oasisTypes is not null && !oasisTypes.Contains(row.OasisType ?? string.Empty))
            {
                continue;
            }

            if (!includeOccupied && row.IsOccupied == true)
            {
                continue;
            }

            var distance = referenceVillage is { } village
                ? TravianMapDistance.Calculate(village.X, village.Y, x, y)
                : row.Distance;
            candidates.Add((x, y, row.Pop, distance));
        }

        IEnumerable<(int X, int Y, long? Pop, double? Distance)> filtered = populationMode switch
        {
            "under" => candidates.Where(row => row.Pop.HasValue && row.Pop.Value <= populationLimit),
            "over" => candidates.Where(row => row.Pop.HasValue && row.Pop.Value >= populationLimit),
            _ => candidates,
        };

        if (skipLowPopulationVillages)
        {
            // Only drop villages with a KNOWN population at or below the threshold; rows with unknown
            // population are kept so we never silently discard targets we could not read.
            filtered = filtered.Where(row => !(row.Pop.HasValue && row.Pop.Value <= LowPopulationThreshold));
        }

        if (maximumDistance.HasValue)
        {
            filtered = filtered.Where(row => row.Distance.HasValue && row.Distance.Value <= maximumDistance.Value);
        }

        filtered = order switch
        {
            "distance_desc" => filtered.OrderByDescending(row => row.Distance ?? double.MinValue),
            "pop_desc" => filtered.OrderByDescending(row => row.Pop ?? long.MinValue),
            "pop_asc" => filtered.OrderBy(row => row.Pop ?? long.MaxValue),
            _ => filtered.OrderBy(row => row.Distance ?? double.MaxValue),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FarmCoordinate>();
        foreach (var row in filtered)
        {
            var key = $"{row.X}|{row.Y}";
            if (!seen.Add(key) || (skipDuplicates && existingCoordinates.Contains(key)))
            {
                continue;
            }

            result.Add(new FarmCoordinate(row.X, row.Y));
            if (result.Count >= amount)
            {
                break;
            }
        }

        return result;
    }

}
