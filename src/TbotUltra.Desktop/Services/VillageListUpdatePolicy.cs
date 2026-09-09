namespace TbotUltra.Desktop.Services;

internal static class VillageListUpdatePolicy
{
    internal static bool HasPotentialMembershipMismatch<T>(
        IReadOnlyList<T> liveVillages,
        IReadOnlyList<T> knownVillages,
        Func<T, string> keySelector)
    {
        if (liveVillages.Count == 0 || knownVillages.Count == 0)
        {
            return liveVillages.Count != knownVillages.Count;
        }

        var liveKeys = liveVillages
            .Select(keySelector)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownKeys = knownVillages
            .Select(keySelector)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return liveKeys.Count != knownKeys.Count || !liveKeys.SetEquals(knownKeys);
    }

    internal static IReadOnlyList<T> PreserveKnownVillages<T>(
        IReadOnlyList<T> incoming,
        IReadOnlyList<T> existing,
        Func<T, string> keySelector)
    {
        if (incoming.Count == 0 || existing.Count <= incoming.Count)
        {
            return incoming;
        }

        var incomingKeys = incoming
            .Select(keySelector)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (incomingKeys.Count == 0
            || !existing.Any(item => incomingKeys.Contains(keySelector(item))))
        {
            return incoming;
        }

        var merged = incoming.ToList();
        foreach (var item in existing)
        {
            if (incomingKeys.Add(keySelector(item)))
            {
                merged.Add(item);
            }
        }

        return merged;
    }
}
