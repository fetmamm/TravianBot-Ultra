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

                if (_cache.TryGetValue(village.Key, out var existing))
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
                    _cache[village.Key] = new VillageSettingRecord
                    {
                        Key = village.Key,
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
            return _cache.TryGetValue(village.Key, out var existing) ? existing.IsEnabled : village.IsCapital;
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
            return _cache.TryGetValue(key, out var existing) ? existing.IsEnabled : defaultIfUnknown;
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

            if (_cache.TryGetValue(village.Key, out var existing))
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
                _cache[village.Key] = new VillageSettingRecord
                {
                    Key = village.Key,
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
            return _cache.TryGetValue(key, out var existing) ? existing.EnabledGroups : null;
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
            return _cache.TryGetValue(key, out var existing) ? existing.NpcTrade ?? true : defaultIfUnknown;
        }
    }

    /// <summary>Per-village NPC trade flag for the row (defaults to enabled when not yet stored).</summary>
    public bool GetNpcTrade(VillageKeyInfo village)
    {
        return village is not null && !string.IsNullOrWhiteSpace(village.Key)
            && IsNpcTradeEnabledByKey(village.Key, defaultIfUnknown: true);
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
            if (_cache.TryGetValue(village.Key, out var existing))
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
                _cache[village.Key] = new VillageSettingRecord
                {
                    Key = village.Key,
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

            if (_cache.TryGetValue(village.Key, out var existing))
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
                _cache[village.Key] = new VillageSettingRecord
                {
                    Key = village.Key,
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

            foreach (var record in file.Villages)
            {
                if (record is not null && !string.IsNullOrWhiteSpace(record.Key))
                {
                    _cache[record.Key] = record;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable file: start from an empty cache rather than crashing the UI.
            _cache.Clear();
        }
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
