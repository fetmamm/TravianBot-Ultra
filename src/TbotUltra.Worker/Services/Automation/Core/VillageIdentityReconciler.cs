using System.Text.RegularExpressions;
using TbotUltra.Core.Travian;
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
        // Coordinates are the authoritative identity. A player may have several villages with the
        // same display name, so checking the name first can select the wrong village even though the
        // caller supplied an exact coordinate pair.
        if (HasCoordinates(coordinates))
        {
            var byCoordinates = villages.FirstOrDefault(village =>
                SameCoordinates((village.CoordX, village.CoordY), coordinates)
                && !string.IsNullOrWhiteSpace(village.Url));
            if (byCoordinates is not null)
            {
                return byCoordinates;
            }
        }

        var byName = villages.FirstOrDefault(village =>
            IsSameName(village.Name, villageName)
            && !string.IsNullOrWhiteSpace(village.Url));
        return byName;
    }

    internal static Village MergeFreshWithCached(Village fresh, IReadOnlyList<Village> cached)
    {
        var freshDid = TravianUrls.TryParseNewdid(fresh.Url);
        var match = freshDid.HasValue
            ? cached.FirstOrDefault(village => TravianUrls.TryParseNewdid(village.Url) == freshDid)
            : null;
        match ??= fresh.CoordX.HasValue && fresh.CoordY.HasValue
            ? cached.FirstOrDefault(village => SameCoordinates(
                (village.CoordX, village.CoordY),
                (fresh.CoordX, fresh.CoordY)))
            : null;

        if (match is null)
        {
            var nameMatches = cached
                .Where(village => IsSameName(village.Name, fresh.Name))
                .Take(2)
                .ToList();
            match = nameMatches.Count == 1 ? nameMatches[0] : null;
        }

        return match is null
            ? fresh
            : fresh with
            {
                IsCapital = fresh.IsCapital ?? match.IsCapital,
                CoordX = fresh.CoordX ?? match.CoordX,
                CoordY = fresh.CoordY ?? match.CoordY,
                Population = fresh.Population ?? match.Population,
                CropFields = fresh.CropFields ?? match.CropFields,
                Tribe = IsKnownTribe(fresh.Tribe) ? fresh.Tribe : match.Tribe,
            };
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
            || !HasCoordinates(activeCoordinates))
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

    internal static IReadOnlyList<Village> EnrichActiveVillageCoordinates(
        IReadOnlyList<Village> villages,
        int? activeVillageDid,
        (int? X, int? Y) activeCoordinates)
    {
        if (!activeVillageDid.HasValue && !HasCoordinates(activeCoordinates))
        {
            return villages;
        }

        return villages
            .Select(village =>
            {
                var sameByDid = activeVillageDid.HasValue
                    && TravianUrls.TryParseNewdid(village.Url) == activeVillageDid;
                var sameByCoordinates = SameCoordinates(
                    (village.CoordX, village.CoordY),
                    activeCoordinates);
                return sameByDid || sameByCoordinates
                    ? village with
                    {
                        CoordX = village.CoordX ?? activeCoordinates.X,
                        CoordY = village.CoordY ?? activeCoordinates.Y,
                    }
                    : village;
            })
            .ToList();
    }

    internal static string BuildStableVillageToken(
        int? villageDid,
        (int? X, int? Y) coordinates,
        string? fallbackName)
    {
        if (villageDid.HasValue)
        {
            return villageDid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (HasCoordinates(coordinates))
        {
            return $"xy:{coordinates.X}|{coordinates.Y}";
        }

        return string.IsNullOrWhiteSpace(fallbackName) ? "current" : fallbackName.Trim();
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

    private static bool IsKnownTribe(string? tribe)
        => !string.IsNullOrWhiteSpace(tribe)
           && !string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase);
}
