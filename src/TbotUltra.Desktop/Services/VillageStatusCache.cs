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
/// display name use <see cref="TryGetByName"/>, which resolves the name through an index (last write
/// wins for duplicate names — the same behavior the previous name-keyed dictionary had). A status
/// whose coordinates cannot be resolved falls back to a name key so nothing is dropped.
/// </summary>
public sealed class VillageStatusCache
{
    private readonly Dictionary<string, VillageStatus> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _keyByName = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byKey.Count;

    public IEnumerable<VillageStatus> Values => _byKey.Values;

    /// <summary>Canonical-keyed view for persistence (VillageCacheStore.Save) and whole-cache scans.</summary>
    public IReadOnlyDictionary<string, VillageStatus> Snapshot => _byKey;

    public void Clear()
    {
        _byKey.Clear();
        _keyByName.Clear();
    }

    /// <summary>Replaces the cache content from the store's load (already canonical-keyed).</summary>
    public void LoadFrom(IReadOnlyDictionary<string, VillageStatus> entries)
    {
        Clear();
        foreach (var pair in entries)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
            {
                continue;
            }

            _byKey[pair.Key] = pair.Value;
            var name = NormalizeName(pair.Value.ActiveVillage);
            if (name is not null)
            {
                _keyByName[name] = pair.Key;
            }

            // Legacy name-keyed entry (coordinates were never resolvable): its key IS the display name.
            if (!IsCoordinateKey(pair.Key) && NormalizeName(pair.Key) is string legacyName)
            {
                _keyByName[legacyName] = pair.Key;
            }
        }
    }

    public bool TryGetByKey(string? key, [MaybeNullWhen(false)] out VillageStatus status)
    {
        status = null;
        return !string.IsNullOrWhiteSpace(key) && _byKey.TryGetValue(key, out status);
    }

    public bool TryGetByName(string? name, [MaybeNullWhen(false)] out VillageStatus status)
    {
        status = null;
        var normalized = NormalizeName(name);
        if (normalized is null)
        {
            return false;
        }

        if (_keyByName.TryGetValue(normalized, out var key) && _byKey.TryGetValue(key, out status))
        {
            return true;
        }

        // Entry stored directly under the name (coordinates unknown at write time).
        return _byKey.TryGetValue(normalized, out status);
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

        var key = TryResolveCoordinateKey(normalized, status)
            ?? (_keyByName.TryGetValue(normalized, out var mapped) ? mapped : normalized);

        // A canonical write supersedes a leftover legacy entry stored directly under the name.
        if (!string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _byKey.Remove(normalized);
        }

        _byKey[key] = status;
        _keyByName[normalized] = key;
    }

    /// <summary>
    /// Carries the cache across an in-game village rename. Canonical-keyed entries only move their
    /// name mapping (the key itself survives the rename); a legacy name-keyed entry is re-keyed unless
    /// a fresher entry already answers to the new name. Returns true when anything changed.
    /// </summary>
    public bool MigrateName(string? oldName, string? newName)
    {
        var oldNormalized = NormalizeName(oldName);
        var newNormalized = NormalizeName(newName);
        if (oldNormalized is null
            || newNormalized is null
            || string.Equals(oldNormalized, newNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Legacy entry stored directly under the old name.
        if (_byKey.TryGetValue(oldNormalized, out var legacyStatus) && !IsCoordinateKey(oldNormalized))
        {
            _byKey.Remove(oldNormalized);
            _keyByName.Remove(oldNormalized);
            if (!TryGetByName(newNormalized, out _))
            {
                _byKey[newNormalized] = legacyStatus;
                _keyByName[newNormalized] = newNormalized;
            }

            return true;
        }

        // Canonical entry: only the name lookup moves.
        if (_keyByName.TryGetValue(oldNormalized, out var key))
        {
            _keyByName.Remove(oldNormalized);
            _keyByName[newNormalized] = key;
            return true;
        }

        return false;
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
