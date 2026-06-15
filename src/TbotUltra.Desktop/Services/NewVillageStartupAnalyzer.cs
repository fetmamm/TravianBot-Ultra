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

        var knownNames = new HashSet<string>(
            (cachedStatuses ?? new Dictionary<string, VillageStatus>())
                .Where(pair => HasKnownDorf1AndDorf2Status(pair.Value))
                .Select(pair => NormalizeName(pair.Key))
                .OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        return villages
            .Where(village => village is not null && NormalizeName(village.Name) is not null)
            .GroupBy(village => NormalizeName(village.Name)!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(village => !knownNames.Contains(NormalizeName(village.Name)!))
            .ToList();
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
