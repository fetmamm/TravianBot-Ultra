using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Account-scoped persistence of which villages are enabled for automation, plus a small cache of
/// each village's identity (name/coords/capital). Stored per account in
/// <c>config/accounts/&lt;account&gt;/villages.json</c>.
///
/// Villages are keyed by their stable village key (the same <c>newdid</c>-based key the UI uses), so
/// a renamed village keeps its enabled choice instead of reappearing as a new village. The only village
/// on a new account defaults to enabled; villages discovered after that default to disabled. Construction
/// is the only automation group enabled by default. Explicit choices survive refreshes and restarts.
/// </summary>
public sealed partial class VillageSettingsStore
{
    /// <summary>
    /// Returns whether a village is enabled for automation. Unknown villages default to disabled;
    /// Merge decides whether the first and only village should be enabled. Does not persist (read-only).
    /// </summary>
    public bool GetEnabled(VillageKeyInfo village)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return false;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _cache.TryGetValue(CanonicalKey(village), out var existing) && existing.IsEnabled;
        }
    }

    /// <summary>
    /// Returns whether a village key is enabled for automation, using only the persisted state. Unknown
    /// keys return <paramref name="defaultIfUnknown"/> (callers use true so a not-yet-discovered or
    /// village-less task is never blocked). Read-only.
    /// </summary>
    public bool IsEnabledByKey(string? key, bool defaultIfUnknown)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultIfUnknown;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _cache.TryGetValue(NormalizeKey(key), out var existing) ? existing.IsEnabled : defaultIfUnknown;
        }
    }

    /// <summary>
    /// Sets a village's enabled state and persists it. Upserts the record if it does not exist yet.
    /// No-ops (no write) when the stored value already matches, which keeps the toggle's
    /// Checked/Unchecked handler from writing on every list rebuild.
    /// </summary>
    public void SetEnabled(VillageKeyInfo village, bool enabled)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();

            var key = CanonicalKey(village);
            if (_cache.TryGetValue(key, out var existing))
            {
                if (existing.IsEnabled == enabled)
                {
                    return;
                }

                existing.IsEnabled = enabled;
                existing.Name = village.Name;
                existing.CoordX = village.CoordX;
                existing.CoordY = village.CoordY;
                existing.IsCapital = village.IsCapital;
                existing.LastSeenUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                _cache[key] = new VillageSettingRecord
                {
                    Key = key,
                    Name = village.Name,
                    CoordX = village.CoordX,
                    CoordY = village.CoordY,
                    IsCapital = village.IsCapital,
                    IsEnabled = enabled,
                    EnabledGroups = CreateDefaultEnabledGroups(),
                    NpcTrade = false,
                    ConstructFasterEnabled = false,
                    HeroResourcesEnabled = true,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
            }

            Save();
            _log?.Invoke($"Village '{village.Name}' automation set to {(enabled ? "enabled" : "disabled")}.");
        }
    }

    /// <summary>
    /// Returns the per-village enabled automation-loop group keys for a village descriptor, or null when
    /// the village has no override yet (caller falls back to the global default). Resolves the record by
    /// the canonical coordinate key first, then by village NAME as a fallback. The name fallback matters
    /// because the dashboard cards and the Village settings popup read from two different village-item
    /// generations (the picker vs the list, which update independently), so the same village can arrive
    /// here with a different key form (xy/newdid/name). Names are stable across those generations, so this
    /// keeps both read paths resolving to the same stored record — otherwise the popup missed the override
    /// and showed the global default (e.g. "Upgrade Troops" on) while the dashboard showed the real value.
    /// </summary>
    public IReadOnlyList<string>? GetEnabledGroups(VillageKeyInfo? village)
    {
        if (village is null)
        {
            return null;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return FindRecordByVillage(village)?.EnabledGroups;
        }
    }

    /// <summary>
    /// Returns the per-village enabled automation-loop group keys, or null when the village has no
    /// override yet (caller falls back to the global default).
    /// </summary>
    public IReadOnlyList<string>? GetEnabledGroups(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _cache.TryGetValue(NormalizeKey(key), out var existing) ? existing.EnabledGroups : null;
        }
    }

    /// <summary>
    /// Returns, for every enabled village, its per-village enabled group keys (null = no override yet,
    /// caller falls back to the global default). Used to compute the union of automation-loop groups
    /// that should be considered across all active villages, so a group enabled only in a non-selected
    /// village is still picked up. Read-only.
    /// </summary>
    public IReadOnlyList<(string Key, IReadOnlyList<string>? EnabledGroups)> GetEnabledVillagesGroups()
    {
        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _cache.Values
                .Where(v => v.IsEnabled)
                .Select(v => (v.Key, (IReadOnlyList<string>?)v.EnabledGroups))
                .ToList();
        }
    }

    /// <summary>
    /// Whether NPC trade is enabled for a village key (per-village). Unknown keys return
    /// <paramref name="defaultIfUnknown"/>; legacy villages without an explicit choice default to disabled.
    /// The account-wide NPC master toggle is applied separately by the caller.
    /// </summary>
    public bool IsNpcTradeEnabledByKey(string? key, bool defaultIfUnknown)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultIfUnknown;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _cache.TryGetValue(NormalizeKey(key), out var existing) ? existing.NpcTrade ?? false : defaultIfUnknown;
        }
    }

    /// <summary>Per-village NPC trade flag for the row (defaults to disabled when not yet stored).</summary>
    public bool GetNpcTrade(VillageKeyInfo village)
    {
        return village is not null && !string.IsNullOrWhiteSpace(village.Key)
            && IsNpcTradeEnabledByKey(CanonicalKey(village), defaultIfUnknown: false);
    }

    /// <summary>Sets a village's NPC trade flag and persists it (upserts the record). No-op when unchanged.</summary>
    public void SetNpcTrade(VillageKeyInfo village, bool enabled)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            var key = CanonicalKey(village);
            if (_cache.TryGetValue(key, out var existing))
            {
                if ((existing.NpcTrade ?? false) == enabled)
                {
                    return;
                }

                existing.NpcTrade = enabled;
                existing.LastSeenUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                _cache[key] = new VillageSettingRecord
                {
                    Key = key,
                    Name = village.Name,
                    CoordX = village.CoordX,
                    CoordY = village.CoordY,
                    IsCapital = village.IsCapital,
                    IsEnabled = false,
                    EnabledGroups = CreateDefaultEnabledGroups(),
                    NpcTrade = enabled,
                    ConstructFasterEnabled = false,
                    HeroResourcesEnabled = true,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
            }

            Save();
        }
    }

    public bool IsConstructFasterEnabledByKey(string? key, bool defaultIfUnknown)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultIfUnknown;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _cache.TryGetValue(NormalizeKey(key), out var existing)
                ? existing.ConstructFasterEnabled ?? false
                : defaultIfUnknown;
        }
    }

    public bool GetConstructFaster(VillageKeyInfo village)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return false;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return FindRecordByVillage(village)?.ConstructFasterEnabled ?? false;
        }
    }

    public void SetConstructFaster(VillageKeyInfo village, bool enabled)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            var record = FindRecordByVillage(village);
            if (record is not null)
            {
                if ((record.ConstructFasterEnabled ?? false) == enabled)
                {
                    return;
                }

                record.ConstructFasterEnabled = enabled;
                record.Name = village.Name;
                record.LastSeenUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                var key = CanonicalKey(village);
                _cache[key] = new VillageSettingRecord
                {
                    Key = key,
                    Name = village.Name,
                    CoordX = village.CoordX,
                    CoordY = village.CoordY,
                    IsCapital = village.IsCapital,
                    IsEnabled = false,
                    EnabledGroups = CreateDefaultEnabledGroups(),
                    NpcTrade = false,
                    ConstructFasterEnabled = enabled,
                    HeroResourcesEnabled = true,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
            }

            Save();
            _log?.Invoke($"Village '{village.Name}' construct-faster video set to {(enabled ? "enabled" : "disabled")}.");
        }
    }

    /// <summary>Sets a village's enabled automation-loop group keys and persists it (upserts the record).</summary>
    public void SetEnabledGroups(VillageKeyInfo village, IReadOnlyList<string> groups)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return;
        }

        var list = (groups ?? Array.Empty<string>()).ToList();

        lock (FileIoLock)
        {
            EnsureCacheLoaded();

            // Resolve the existing record by coordinate key OR village name, so a popup row built from a
            // stale item generation (no coords) updates the canonical record instead of splitting the
            // village's settings across a second key. Only create a new record when none exists.
            var existing = FindRecordByVillage(village);
            if (existing is not null)
            {
                if (existing.EnabledGroups is not null && existing.EnabledGroups.SequenceEqual(list, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                existing.EnabledGroups = list;
                existing.Name = village.Name;
                existing.LastSeenUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                var key = CanonicalKey(village);
                _cache[key] = new VillageSettingRecord
                {
                    Key = key,
                    Name = village.Name,
                    CoordX = village.CoordX,
                    CoordY = village.CoordY,
                    IsCapital = village.IsCapital,
                    IsEnabled = false,
                    EnabledGroups = list,
                    NpcTrade = false,
                    ConstructFasterEnabled = false,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
            }

            Save();
        }
    }

    private static List<string> CreateDefaultEnabledGroups() => new() { DefaultEnabledGroupKey };

}
