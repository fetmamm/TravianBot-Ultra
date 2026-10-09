using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// In-memory cache of each village's last-read status, keyed by the canonical coordinate key
/// (<c>xy:X|Y</c>, same identity as queue.json and the settings store) so a renamed village keeps its
/// entry and two villages with the same name never overwrite each other. Callers that only have a
/// display name use <see cref="TryGetByName"/>, which succeeds only when that name identifies exactly
/// one cached village. A status whose coordinates cannot be resolved falls back to a name key only when
/// no canonical same-name entries exist; duplicate names are never resolved by last-write-wins.
/// </summary>
public sealed class VillageStatusCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, VillageStatus> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get { lock (_gate) return _byKey.Count; }
    }

    public IEnumerable<VillageStatus> Values
    {
        get { lock (_gate) return _byKey.Values.ToList(); }
    }

    /// <summary>Canonical-keyed view for persistence (VillageCacheStore.Save) and whole-cache scans.</summary>
    public IReadOnlyDictionary<string, VillageStatus> Snapshot
    {
        get { lock (_gate) return new Dictionary<string, VillageStatus>(_byKey, StringComparer.OrdinalIgnoreCase); }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byKey.Clear();
        }
    }

    /// <summary>Replaces the cache content from the store's load (already canonical-keyed).</summary>
    public void LoadFrom(IReadOnlyDictionary<string, VillageStatus> entries)
    {
        lock (_gate)
        {
            _byKey.Clear();
            foreach (var pair in entries)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                {
                    _byKey[pair.Key] = pair.Value;
                }
            }
        }
    }

    public bool TryGetByKey(string? key, [MaybeNullWhen(false)] out VillageStatus status)
    {
        lock (_gate)
        {
            status = null;
            return !string.IsNullOrWhiteSpace(key) && _byKey.TryGetValue(key, out status);
        }
    }

    public bool TryGetByName(string? name, [MaybeNullWhen(false)] out VillageStatus status)
    {
        status = null;
        var normalized = NormalizeName(name);
        if (normalized is null)
        {
            return false;
        }

        lock (_gate)
        {
            var matches = _byKey.Values
                .Where(candidate => string.Equals(
                    NormalizeName(candidate.ActiveVillage),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (matches.Count == 1)
            {
                status = matches[0];
                return true;
            }

            // A single unresolved legacy entry can still be read by its direct name key. Never use it
            // when canonical same-name entries exist, because that would guess between duplicate villages.
            return matches.Count == 0 && _byKey.TryGetValue(normalized, out status);
        }
    }

    public bool TryGetUniqueKeyByName(string? name, [NotNullWhen(true)] out string? key)
    {
        key = null;
        var normalized = NormalizeName(name);
        if (normalized is null)
        {
            return false;
        }

        lock (_gate)
        {
            var matches = _byKey
                .Where(pair => string.Equals(
                    NormalizeName(pair.Value.ActiveVillage),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();
            if (matches.Count != 1)
            {
                return false;
            }

            key = matches[0];
            return true;
        }
    }

    /// <summary>
    /// Stores a status under the village's canonical coordinate key, resolved from the status's own
    /// village list by the given display name. When coordinates cannot be resolved (name missing or
    /// duplicated in the list), an existing name-to-key mapping is reused so partial reads still land
    /// on the right entry; only as a last resort is the status stored under the name itself.
    /// </summary>
    public void Set(string? name, VillageStatus status)
    {
        var normalized = NormalizeName(name);
        if (normalized is null || status is null)
        {
            return;
        }

        lock (_gate)
        {
            var key = TryResolveCoordinateKey(normalized, status);
            if (key is null)
            {
                var existingKeys = _byKey
                    .Where(pair => string.Equals(
                        NormalizeName(pair.Value.ActiveVillage),
                        normalized,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key)
                    .Take(2)
                    .ToList();
                if (existingKeys.Count > 1)
                {
                    return;
                }

                key = existingKeys.Count == 1 ? existingKeys[0] : normalized;
            }

            if (!string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _byKey.Remove(normalized);
            }

            if (_byKey.TryGetValue(key, out var previous))
            {
                var cityCapability = status.CityCapability == CityCapability.Unknown
                    ? previous.CityCapability
                    : status.CityCapability;
                var cityStatus = status.CityCapability == CityCapability.Disabled
                    ? CityStatus.Unknown
                    : status.CityStatus == CityStatus.Unknown
                        ? previous.CityStatus
                        : status.CityStatus;
                var watchtowerStatus = status.CityCapability == CityCapability.Disabled
                    || status.CityStatus == CityStatus.Village
                        ? null
                        : status.WatchtowerStatus is { } incomingWatchtower
                            && previous.WatchtowerStatus is { } previousWatchtower
                            && incomingWatchtower.ObservedAtUtc < previousWatchtower.ObservedAtUtc
                                ? previousWatchtower
                                : status.WatchtowerStatus ?? previous.WatchtowerStatus;
                if (cityCapability != status.CityCapability
                    || cityStatus != status.CityStatus
                    || !ReferenceEquals(watchtowerStatus, status.WatchtowerStatus))
                {
                    status = status with
                    {
                        CityCapability = cityCapability,
                        CityStatus = cityStatus,
                        WatchtowerStatus = watchtowerStatus,
                    };
                }

                status = PreserveKnownResourceProduction(status, previous);
            }

            _byKey[key] = status;
        }
    }

    private static VillageStatus PreserveKnownResourceProduction(VillageStatus current, VillageStatus previous)
    {
        var previousForecasts = previous.ResourceStorageForecasts?
            .Where(item => !string.IsNullOrWhiteSpace(item.ResourceKey))
            .GroupBy(item => item.ResourceKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        if (previousForecasts is null
            || previousForecasts.Count == 0
            || !previousForecasts.Values.Any(item => item.ProductionPerHour is not null))
        {
            return current;
        }

        var currentForecasts = current.ResourceStorageForecasts?.ToList() ?? [];
        var merged = new List<ResourceStorageForecast>(Math.Max(currentForecasts.Count, previousForecasts.Count));
        var changed = false;
        foreach (var forecast in currentForecasts)
        {
            if (forecast.ProductionPerHour is not null
                || !previousForecasts.TryGetValue(forecast.ResourceKey, out var previousForecast)
                || previousForecast.ProductionPerHour is null)
            {
                merged.Add(forecast);
                previousForecasts.Remove(forecast.ResourceKey);
                continue;
            }

            merged.Add(WithProduction(forecast, previousForecast.ProductionPerHour.Value));
            previousForecasts.Remove(forecast.ResourceKey);
            changed = true;
        }

        foreach (var previousForecast in previousForecasts.Values)
        {
            merged.Add(previousForecast);
            changed = true;
        }

        return changed ? current with { ResourceStorageForecasts = merged } : current;
    }

    private static ResourceStorageForecast WithProduction(
        ResourceStorageForecast forecast,
        double productionPerHour)
    {
        int? secondsToFull = null;
        int? secondsToEmpty = null;
        if (forecast.Current is long current && forecast.Capacity is long capacity)
        {
            if (productionPerHour > 0)
            {
                secondsToFull = ToBoundedSeconds((Math.Max(0L, capacity - current) / productionPerHour) * 3600d);
            }
            else if (productionPerHour < 0)
            {
                secondsToEmpty = ToBoundedSeconds((Math.Max(0L, current) / -productionPerHour) * 3600d);
            }
        }

        return forecast with
        {
            ProductionPerHour = productionPerHour,
            SecondsToFull = secondsToFull,
            SecondsToEmpty = secondsToEmpty,
        };
    }

    private static int ToBoundedSeconds(double seconds)
        => seconds >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(Math.Max(0d, seconds));

    /// <summary>
    /// Carries the cache across an in-game village rename. Canonical-keyed entries only move their
    /// name mapping (the key itself survives the rename); a legacy name-keyed entry is re-keyed unless
    /// a fresher entry already answers to the new name. Returns true when anything changed.
    /// </summary>
    public bool MigrateName(string? oldName, string? newName, string? villageKey = null)
    {
        var oldNormalized = NormalizeName(oldName);
        var newNormalized = NormalizeName(newName);
        if (oldNormalized is null
            || newNormalized is null
            || string.Equals(oldNormalized, newNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (_gate)
        {
            var targetKey = !string.IsNullOrWhiteSpace(villageKey) && _byKey.ContainsKey(villageKey)
                ? villageKey
                : _byKey
                    .Where(pair => string.Equals(
                        NormalizeName(pair.Value.ActiveVillage),
                        oldNormalized,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key)
                    .Take(2)
                    .ToList() is { Count: 1 } uniqueMatch
                        ? uniqueMatch[0]
                        : null;
            if (targetKey is null || !_byKey.TryGetValue(targetKey, out var status))
            {
                return false;
            }

            var ownerX = status.ActiveVillageCoordX;
            var ownerY = status.ActiveVillageCoordY;
            var renamedVillages = status.Villages
                .Select(village =>
                {
                    var isOwner = ownerX.HasValue && ownerY.HasValue
                        ? village.CoordX == ownerX && village.CoordY == ownerY
                        : string.Equals(
                            NormalizeName(village.Name),
                            oldNormalized,
                            StringComparison.OrdinalIgnoreCase)
                          && status.Villages.Count(candidate => string.Equals(
                              NormalizeName(candidate.Name),
                              oldNormalized,
                              StringComparison.OrdinalIgnoreCase)) == 1;
                    return isOwner ? village with { Name = newNormalized } : village;
                })
                .ToList();
            var renamedStatus = status with
            {
                ActiveVillage = newNormalized,
                Villages = renamedVillages,
            };

            if (!IsCoordinateKey(targetKey))
            {
                _byKey.Remove(targetKey);
                targetKey = newNormalized;
            }

            _byKey[targetKey] = renamedStatus;
            return true;
        }
    }

    public static bool IsCoordinateKey(string? key)
        => key is not null && key.StartsWith("xy:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the coordinate key for the village a status belongs to. The status's own active-village
    /// coordinates (sidebar data-x/data-y) are exact and used first — they disambiguate even duplicate
    /// names. The fallback matches the name in the status's village list; that returns null when the
    /// name matches no entry with coordinates, or matches more than one (owner ambiguous — the caller
    /// falls back to a name key rather than guessing).
    /// </summary>
    public static string? TryResolveCoordinateKey(string? name, VillageStatus status)
    {
        if (status is null)
        {
            return null;
        }

        if (status.ActiveVillageCoordX.HasValue && status.ActiveVillageCoordY.HasValue)
        {
            return VillageKey.FromCoords(status.ActiveVillageCoordX.Value, status.ActiveVillageCoordY.Value);
        }

        var normalized = NormalizeName(name);
        if (normalized is null || status.Villages is null)
        {
            return null;
        }

        var matches = status.Villages
            .Where(village => village is not null
                && village.CoordX.HasValue
                && village.CoordY.HasValue
                && string.Equals(NormalizeName(village.Name), normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1
            ? VillageKey.FromCoords(matches[0].CoordX!.Value, matches[0].CoordY!.Value)
            : null;
    }

    // Same normalization rule as MainWindow.NormalizeVillageName ("Unknown village"/"-" are placeholders).
    private static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        return string.Equals(trimmed, "Unknown village", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "-", StringComparison.Ordinal)
            ? null
            : trimmed;
    }
}
