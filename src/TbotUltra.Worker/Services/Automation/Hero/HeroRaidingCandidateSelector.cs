using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services.Automation;

internal sealed record HeroRaidingCandidate(
    MapOasisEntry Oasis,
    IReadOnlyList<NatureTroopGroup> Garrison,
    int EstimatedBountyPerResource,
    double DistanceFields);

internal static class HeroRaidingCandidateSelector
{
    internal static IReadOnlyList<HeroRaidingCandidate> Select(
        IEnumerable<MapOasisEntry> oases,
        int heroVillageX,
        int heroVillageY,
        int maximumDistance,
        int observationMinutes,
        IEnumerable<string> selectedTypes,
        IReadOnlySet<(int X, int Y)> excluded,
        DateTimeOffset now,
        int? wrappedWorldWidth = null)
    {
        var types = new HashSet<string>(selectedTypes, StringComparer.OrdinalIgnoreCase);
        if (types.Count == 0) return [];

        var candidates = new List<HeroRaidingCandidate>();
        foreach (var oasis in oases)
        {
            if (oasis.IsOccupied || excluded.Contains((oasis.X, oasis.Y))
                || !MatchesSelectedType(oasis, types)
                || oasis.ObservedAtUtc is not DateTimeOffset observedAt
                || observedAt > now
                || now - observedAt > TimeSpan.FromMinutes(observationMinutes)
                || !NatureTroopCatalog.TryParseGarrison(oasis.Animals, out var garrison)
                || !NatureTroopCatalog.TryCalculateBountyPerResource(garrison, out var bounty))
            {
                continue;
            }

            var xDistance = Math.Abs(oasis.X - heroVillageX);
            var yDistance = Math.Abs(oasis.Y - heroVillageY);
            if (wrappedWorldWidth is > 0)
            {
                xDistance = Math.Min(xDistance, wrappedWorldWidth.Value - xDistance);
                yDistance = Math.Min(yDistance, wrappedWorldWidth.Value - yDistance);
            }

            var distance = Math.Sqrt((double)xDistance * xDistance + (double)yDistance * yDistance);
            if (distance <= 0 || distance > maximumDistance) continue;

            candidates.Add(new HeroRaidingCandidate(oasis, garrison, bounty, distance));
        }

        // All candidates share the same hero and speed. Bounty per distance is the local
        // ordering proxy; Travian's live send preview is the final travel-time authority.
        return candidates
            .OrderByDescending(candidate => candidate.EstimatedBountyPerResource / candidate.DistanceFields)
            .ThenBy(candidate => candidate.DistanceFields)
            .ThenBy(candidate => candidate.Oasis.Y)
            .ThenBy(candidate => candidate.Oasis.X)
            .ToList();
    }

    internal static bool MatchesSelectedType(MapOasisEntry oasis, IReadOnlySet<string> selectedTypes)
    {
        if (!string.Equals(oasis.FilterType, "Crop", StringComparison.OrdinalIgnoreCase))
            return selectedTypes.Contains(oasis.FilterType);

        // The shared map scan still groups both pure-crop bonuses as "Crop".
        // Hero raiding applies its narrower choice using the observed oasis bonus.
        return oasis.OasisType switch
        {
            "Crop 25%" => selectedTypes.Contains("Crop25") || selectedTypes.Contains("Crop"),
            "Crop 50%" => selectedTypes.Contains("Crop50") || selectedTypes.Contains("Crop"),
            _ => false,
        };
    }
}
