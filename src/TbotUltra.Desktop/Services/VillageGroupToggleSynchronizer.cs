using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

internal static class VillageGroupToggleSynchronizer
{
    internal static bool Apply(
        IReadOnlyList<VillageSettingsRow>? rows,
        VillageSettingsStore.VillageKeyInfo village,
        IReadOnlyCollection<string> enabledGroups)
    {
        var row = rows?.FirstOrDefault(candidate =>
            candidate.KeyInfo is not null
            && (string.Equals(candidate.KeyInfo.Key, village.Key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.KeyInfo.Name, village.Name, StringComparison.OrdinalIgnoreCase)));
        if (row is null)
        {
            return false;
        }

        foreach (var toggle in row.GroupToggles)
        {
            toggle.IsEnabled = toggle.CanToggle
                && enabledGroups.Contains(toggle.GroupKey, StringComparer.OrdinalIgnoreCase);
        }

        return true;
    }
}
