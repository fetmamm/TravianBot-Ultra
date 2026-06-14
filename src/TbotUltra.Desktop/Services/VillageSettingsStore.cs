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
/// a renamed village keeps its enabled choice instead of reappearing as a new village. New villages
/// default to enabled only when they are the capital; every other newly discovered village defaults
/// to disabled. The user's explicit choice is always preserved across refreshes and restarts.
/// </summary>
public sealed class VillageSettingsStore
{
    // Minimal identity descriptor passed in by the UI. Key is precomputed by the caller (GetVillageKey)
    // so the newdid/coords/name key logic stays in one place.
    public sealed record VillageKeyInfo(string Key, string Name, int? CoordX, int? CoordY, bool IsCapital);

    private sealed class VillageSettingRecord
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int? CoordX { get; set; }
        public int? CoordY { get; set; }
        public bool IsCapital { get; set; }
        public bool IsEnabled { get; set; }
        // Per-village automation-loop group keys that are enabled. null = no override yet (use the global
        // default). An explicit empty list means "all groups disabled for this village".
        public List<string>? EnabledGroups { get; set; }
        // Whether NPC trade may run in this village. null = default enabled (true). The account-wide NPC
        // master toggle (Auto settings) still gates everything: NPC runs only when master AND this are on.
        public bool? NpcTrade { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class VillageSettingsFile
    {
        public List<VillageSettingRecord> Villages { get; set; } = new();

        // Last-read hero home village name (account-global). Remembered across restarts so the dashboard
        // hero icon can show on the right village before the first hero read of a new session.
        public string? HeroHomeVillageName { get; set; }
    }

    // Serializes villages.json I/O across the UI dispatcher and background refresh contexts, mirroring
    // BotConfigStore.FileIoLock.
    private static readonly object FileIoLock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _projectRoot;
    private readonly Func<string>? _activeAccountNameProvider;
    private readonly Action<string>? _log;

    // In-memory cache for the currently active account, keyed by village key. Avoids re-reading the
    // file on every village-list rebuild (which happens on each periodic refresh tick).
    private readonly Dictionary<string, VillageSettingRecord> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string? _cacheAccount;
    private string? _heroHomeVillageName;

    public VillageSettingsStore(string projectRoot, Func<string>? activeAccountNameProvider = null, Action<string>? log = null)
    {
        _projectRoot = projectRoot;
        _activeAccountNameProvider = activeAccountNameProvider;
        _log = log;
    }

    /// <summary>
    /// Merges a freshly read set of villages into the store: new villages are added (capital enabled,
    /// others disabled by default), known villages keep their enabled choice while their cached
    /// identity (name/coords/capital) is refreshed. Only persists when something actually changed.
    /// Never removes villages absent from <paramref name="villages"/> (a page read can be partial).
    /// </summary>
    public void Merge(IReadOnlyList<VillageKeyInfo> villages)
    {
        if (villages is null || villages.Count == 0)
        {
            return;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();

            var added = 0;
            var updated = 0;
            var dirty = false;

            foreach (var village in villages)
            {
                if (village is null || string.IsNullOrWhiteSpace(village.Key))
                {
                    continue;
                }

                var key = CanonicalKey(village);
                if (_cache.TryGetValue(key, out var existing))
                {
                    // Keep the user's enabled choice; refresh cached identity if it changed.
                    if (!string.Equals(existing.Name, village.Name, StringComparison.Ordinal)
                        || existing.CoordX != village.CoordX
                        || existing.CoordY != village.CoordY
                        || existing.IsCapital != village.IsCapital)
                    {
                        existing.Name = village.Name;
                        existing.CoordX = village.CoordX;
                        existing.CoordY = village.CoordY;
                        existing.IsCapital = village.IsCapital;
                        updated++;
                        dirty = true;
                    }

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
                        IsEnabled = village.IsCapital, // capital ON by default, every other new village OFF.
                        LastSeenUtc = DateTimeOffset.UtcNow,
                    };
                    added++;
                    dirty = true;
                }
            }

            if (dirty)
            {
                Save();
                _log?.Invoke($"Village settings merged: {added} new (capital enabled by default), {updated} updated.");
            }
        }
    }

    /// <summary>
    /// Returns whether a village is enabled for automation. Unknown villages default to enabled only
    /// when they are the capital. Does not persist (read-only).
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
            return _cache.TryGetValue(CanonicalKey(village), out var existing) ? existing.IsEnabled : village.IsCapital;
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
    /// <paramref name="defaultIfUnknown"/>; villages with no explicit choice default to enabled (true).
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
            return _cache.TryGetValue(NormalizeKey(key), out var existing) ? existing.NpcTrade ?? true : defaultIfUnknown;
        }
    }

    /// <summary>Per-village NPC trade flag for the row (defaults to enabled when not yet stored).</summary>
    public bool GetNpcTrade(VillageKeyInfo village)
    {
        return village is not null && !string.IsNullOrWhiteSpace(village.Key)
            && IsNpcTradeEnabledByKey(CanonicalKey(village), defaultIfUnknown: true);
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
                if ((existing.NpcTrade ?? true) == enabled)
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
                    IsEnabled = village.IsCapital,
                    NpcTrade = enabled,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
            }

            Save();
        }
    }

    /// <summary>Returns the remembered hero home village name for the active account (null if none).</summary>
    public string? GetHeroHomeVillageName()
    {
        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _heroHomeVillageName;
        }
    }

    /// <summary>Persists the last-read hero home village name. No-op (no write) when unchanged.</summary>
    public void SetHeroHomeVillageName(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            if (string.Equals(_heroHomeVillageName, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _heroHomeVillageName = trimmed;
            Save();
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
                    IsEnabled = village.IsCapital,
                    EnabledGroups = list,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
            }

            Save();
        }
    }

    /// <summary>
    /// Drops the in-memory cache so the next access reloads from the active account's file. Call on
    /// account switch so one account's choices never leak into another.
    /// </summary>
    public void InvalidateCache()
    {
        lock (FileIoLock)
        {
            _cache.Clear();
            _cacheAccount = null;
            _heroHomeVillageName = null;
        }
    }

    private void EnsureCacheLoaded()
    {
        var account = GetActiveAccountName();
        if (string.Equals(_cacheAccount, account, StringComparison.Ordinal))
        {
            return;
        }

        _cache.Clear();
        _heroHomeVillageName = null;
        _cacheAccount = account;

        if (string.IsNullOrWhiteSpace(account))
        {
            return;
        }

        var path = AccountStoragePaths.VillageSettingsPath(_projectRoot, account);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var raw = ReadAllTextShared(path);
            var file = JsonSerializer.Deserialize<VillageSettingsFile>(raw, SerializerOptions);
            if (file?.Villages is null)
            {
                return;
            }

            _heroHomeVillageName = string.IsNullOrWhiteSpace(file.HeroHomeVillageName) ? null : file.HeroHomeVillageName.Trim();

            // Migrate legacy records to the canonical coordinate key and merge any duplicates: the same
            // village could previously be stored under more than one newdid (e.g. did:106838 and did:25471
            // for "SLAV") with divergent settings. Collapsing them by coordinate keeps a single source of
            // truth so the dashboard and the village settings window always agree.
            var migratedAnything = false;
            foreach (var record in file.Villages)
            {
                if (record is null || string.IsNullOrWhiteSpace(record.Key))
                {
                    continue;
                }

                var canonicalKey = record.CoordX.HasValue && record.CoordY.HasValue
                    ? VillageKey.FromCoords(record.CoordX.Value, record.CoordY.Value)
                    : record.Key;

                if (!string.Equals(canonicalKey, record.Key, StringComparison.Ordinal))
                {
                    record.Key = canonicalKey;
                    migratedAnything = true;
                }

                if (_cache.TryGetValue(canonicalKey, out var existing))
                {
                    _cache[canonicalKey] = MergeDuplicateRecords(existing, record);
                    migratedAnything = true;
                }
                else
                {
                    _cache[canonicalKey] = record;
                }
            }

            // Persist the cleaned-up file so the duplicates are gone on disk too (one-time, idempotent).
            if (migratedAnything)
            {
                Save();
            }
        }
        catch
        {
            // Corrupt or unreadable file: start from an empty cache rather than crashing the UI.
            _cache.Clear();
        }
    }

    // Picks the record to keep when two entries collapse onto the same coordinate key. Prefers the one
    // with an explicit group override, otherwise the most recently seen; then backfills any missing
    // group/NPC choice from the other and keeps the village enabled if either copy was enabled.
    private static VillageSettingRecord MergeDuplicateRecords(VillageSettingRecord a, VillageSettingRecord b)
    {
        var aHasGroups = a.EnabledGroups is not null;
        var bHasGroups = b.EnabledGroups is not null;

        VillageSettingRecord winner;
        VillageSettingRecord loser;
        if (aHasGroups != bHasGroups)
        {
            winner = aHasGroups ? a : b;
            loser = aHasGroups ? b : a;
        }
        else
        {
            winner = a.LastSeenUtc >= b.LastSeenUtc ? a : b;
            loser = ReferenceEquals(winner, a) ? b : a;
        }

        winner.EnabledGroups ??= loser.EnabledGroups;
        winner.NpcTrade ??= loser.NpcTrade;
        winner.IsEnabled = winner.IsEnabled || loser.IsEnabled;
        if (winner.LastSeenUtc < loser.LastSeenUtc)
        {
            winner.LastSeenUtc = loser.LastSeenUtc;
        }

        return winner;
    }

    /// <summary>
    /// Resolves an arbitrary village key (e.g. a name-based key from a queue item) to the canonical
    /// coordinate key stored for that village, so callers can group/compare villages consistently with the
    /// dashboard. Returns the input unchanged when it is already canonical or no match is found.
    /// </summary>
    public string? ResolveCanonicalKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return NormalizeKey(key);
        }
    }

    // The canonical storage key for a village: its coordinates when known (stable, unique, survives
    // renames and multiple newdids), otherwise the caller-provided key. Using this everywhere a record is
    // read or written keeps the in-memory key identical to the one produced on reload, so a village's
    // settings never split across newdids.
    private static string CanonicalKey(VillageKeyInfo village)
    {
        return village.CoordX.HasValue && village.CoordY.HasValue
            ? VillageKey.FromCoords(village.CoordX.Value, village.CoordY.Value)
            : village.Key;
    }

    // Resolves a non-canonical key to the stored coordinate key. Queue items only carry a village name (and
    // a newdid url), so their gating key is name-based; map it to the coordinate-keyed record by name so the
    // same per-village enabled/group choice applies. Coordinate ("xy:") and unknown keys pass through.
    // Caller already holds FileIoLock.
    // Resolves the stored record for a village descriptor: canonical coordinate key first, then a
    // case-insensitive village-NAME match. The name fallback covers callers whose key form differs from
    // how the record was stored (e.g. a newdid-only or name-only key when the record is coordinate-keyed),
    // so a known village always finds its override. Caller already holds FileIoLock.
    private VillageSettingRecord? FindRecordByVillage(VillageKeyInfo village)
    {
        if (_cache.TryGetValue(CanonicalKey(village), out var byKey))
        {
            return byKey;
        }

        if (!string.IsNullOrWhiteSpace(village.Name))
        {
            var name = village.Name.Trim();
            foreach (var record in _cache.Values)
            {
                if (string.Equals((record.Name ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return record;
                }
            }
        }

        return null;
    }

    private string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }

        var name = key.Substring("name:".Length);
        foreach (var record in _cache.Values)
        {
            if (string.Equals((record.Name ?? string.Empty).Trim().ToLowerInvariant(), name, StringComparison.Ordinal))
            {
                return record.Key;
            }
        }

        return key;
    }

    private void Save()
    {
        var account = _cacheAccount;
        if (string.IsNullOrWhiteSpace(account))
        {
            return;
        }

        var path = AccountStoragePaths.VillageSettingsPath(_projectRoot, account);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var file = new VillageSettingsFile
        {
            Villages = _cache.Values
                .OrderByDescending(v => v.IsCapital)
                .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            HeroHomeVillageName = _heroHomeVillageName,
        };

        WriteAllTextShared(path, JsonSerializer.Serialize(file, SerializerOptions));
    }

    private string GetActiveAccountName()
    {
        if (_activeAccountNameProvider is null)
        {
            return string.Empty;
        }

        try
        {
            return _activeAccountNameProvider() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadAllTextShared(string path)
    {
        return RetryFileIo(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }

    private static void WriteAllTextShared(string path, string content)
    {
        RetryFileIo(() =>
        {
            File.WriteAllText(path, content);
            return true;
        });
    }

    private static T RetryFileIo<T>(Func<T> action)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(40 * attempt);
            }
        }
    }
}
