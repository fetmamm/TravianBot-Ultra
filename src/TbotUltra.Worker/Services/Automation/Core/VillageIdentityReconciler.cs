using System.Text.RegularExpressions;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

internal static class VillageIdentityReconciler
{
    internal static bool IsAcceptedSwitchName(
        string? activeVillageName,
        string? requestedVillageName,
        string? resolvedVillageName)
    {
        return IsSameName(activeVillageName, requestedVillageName)
            || (!string.IsNullOrWhiteSpace(resolvedVillageName)
                && IsSameName(activeVillageName, resolvedVillageName));
    }

    internal static bool IsSameName(string? left, string? right)
    {
        var normalizedLeft = NormalizeName(left);
        var normalizedRight = NormalizeName(right);
        return normalizedLeft.Length > 0
            && normalizedRight.Length > 0
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    internal static Village? FindByNameOrCoordinates(
        IReadOnlyList<Village> villages,
        string villageName,
        (int? X, int? Y) coordinates)
    {
        var byName = villages.FirstOrDefault(village =>
            IsSameName(village.Name, villageName)
            && !string.IsNullOrWhiteSpace(village.Url));
        if (byName is not null)
        {
            return byName;
        }

        if (!HasCoordinates(coordinates))
        {
            return null;
        }

        return villages.FirstOrDefault(village =>
            SameCoordinates((village.CoordX, village.CoordY), coordinates)
            && !string.IsNullOrWhiteSpace(village.Url));
    }

    internal static bool HasCoordinates((int? X, int? Y) coordinates) =>
        coordinates.X.HasValue && coordinates.Y.HasValue;

    internal static bool SameCoordinates((int? X, int? Y) left, (int? X, int? Y) right) =>
        left.X.HasValue
        && left.Y.HasValue
        && right.X.HasValue
        && right.Y.HasValue
        && left.X.Value == right.X.Value
        && left.Y.Value == right.Y.Value;

    internal static IReadOnlyList<Village>? ReconcileRenamedByCoordinates(
        IReadOnlyList<Village> cached,
        string activeVillageName,
        (int? X, int? Y) activeCoordinates)
    {
        if (string.IsNullOrWhiteSpace(activeVillageName)
            || cached is not { Count: > 0 }
            || !HasCoordinates(activeCoordinates)
            || cached.Any(village => IsSameName(village.Name, activeVillageName)))
        {
            return null;
        }

        var changed = false;
        var updated = cached
            .Select(village =>
            {
                if (!SameCoordinates((village.CoordX, village.CoordY), activeCoordinates)
                    || IsSameName(village.Name, activeVillageName))
                {
                    return village;
                }

                changed = true;
                return village with { Name = activeVillageName.Trim() };
            })
            .ToList();

        return changed ? updated : null;
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value
            .Replace("\u202A", string.Empty)
            .Replace("\u202B", string.Empty)
            .Replace("\u202C", string.Empty)
            .Replace("\u202D", string.Empty)
            .Replace("\u202E", string.Empty)
            .Replace("\u200E", string.Empty)
            .Replace("\u200F", string.Empty)
            .Replace('−', '-');
        cleaned = Regex.Replace(cleaned, @"\s*\(\s*-?\d+\s*\|\s*-?\d+\s*\)\s*$", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }
}
