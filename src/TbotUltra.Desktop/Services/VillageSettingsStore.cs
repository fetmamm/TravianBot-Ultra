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
/// a renamed village keeps its enabled choice instead of reappearing as a new village. New villages default
/// to enabled, while a user's explicit disabled choice is preserved. Construction
/// is the only automation group enabled by default. Explicit choices survive refreshes and restarts.
/// </summary>
public sealed partial class VillageSettingsStore
{
    private const int CurrentFileVersion = 1;
    public const bool DefaultAutomationEnabled = true;
    public const string DefaultEnabledGroupKey = "construction";

    public static IReadOnlyList<string> DefaultEnabledGroups { get; } = new[] { DefaultEnabledGroupKey };

    // Minimal identity descriptor passed in by the UI. Key is precomputed by the caller (GetVillageKey)
    // so the newdid/coords/name key logic stays in one place.
    public sealed record VillageKeyInfo(string Key, string Name, int? CoordX, int? CoordY, bool IsCapital);

    public sealed record HeroResourceSettings(
        bool IsEnabled,
        bool UseConstruction,
        bool UseSmithy,
        bool UseBrewery,
        bool UseTownHall,
        bool MaxUseEnabled,
        int MaxUsePerResource);

    private sealed class VillageSettingRecord
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int? CoordX { get; set; }
        public int? CoordY { get; set; }
        public bool IsCapital { get; set; }
        public bool IsEnabled { get; set; }
        // Per-village automation-loop group keys that are enabled. Legacy null values are migrated to the
        // village default (Construction only). An explicit empty list means all groups are disabled.
        public List<string>? EnabledGroups { get; set; }
        // Whether NPC trade may run in this village. Legacy null values are migrated to disabled. The account-wide NPC
        // master toggle (Auto settings) still gates everything: NPC runs only when master AND this are on.
        public bool? NpcTrade { get; set; }
        // Whether construct-faster bonus videos may run in this village. Account-wide master toggle still gates it.
        public bool? ConstructFasterEnabled { get; set; }
        public bool? HeroResourcesEnabled { get; set; }
        public bool? HeroResourceUseConstruction { get; set; }
        public bool? HeroResourceUseSmithy { get; set; }
        public bool? HeroResourceUseBrewery { get; set; }
        public bool? HeroResourceUseTownHall { get; set; }
        public bool? HeroResourceMaxUseEnabled { get; set; }
        public int? HeroResourceMaxUsePerResource { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
        // When this village first went missing from a CONFIRMED login/scan village list (coordinate
        // identity), or null while it is live. Set/cleared only by DisableVillagesMissingFromConfirmedList.
        // Drives retention-based pruning of lost/destroyed villages so their queue items don't linger.
        public DateTimeOffset? ConfirmedMissingSinceUtc { get; set; }
    }

    private sealed class VillageSettingsFile
    {
        public int Version { get; set; }
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
            var enableVillagesForNewDefault = file.Version < CurrentFileVersion;
            var migratedAnything = enableVillagesForNewDefault;
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

                if (record.EnabledGroups is null)
                {
                    record.EnabledGroups = CreateDefaultEnabledGroups();
                    migratedAnything = true;
                }

                if (record.NpcTrade is null)
                {
                    record.NpcTrade = false;
                    migratedAnything = true;
                }

                if (record.ConstructFasterEnabled is null)
                {
                    record.ConstructFasterEnabled = false;
                    migratedAnything = true;
                }

                if (record.HeroResourcesEnabled is null)
                {
                    record.HeroResourcesEnabled = true;
                    migratedAnything = true;
                }

                if (enableVillagesForNewDefault)
                {
                    record.IsEnabled = DefaultAutomationEnabled;
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
        winner.ConstructFasterEnabled ??= loser.ConstructFasterEnabled;
        winner.HeroResourcesEnabled ??= loser.HeroResourcesEnabled;
        winner.HeroResourceUseConstruction ??= loser.HeroResourceUseConstruction;
        winner.HeroResourceUseSmithy ??= loser.HeroResourceUseSmithy;
        winner.HeroResourceUseBrewery ??= loser.HeroResourceUseBrewery;
        winner.HeroResourceUseTownHall ??= loser.HeroResourceUseTownHall;
        winner.HeroResourceMaxUseEnabled ??= loser.HeroResourceMaxUseEnabled;
        winner.HeroResourceMaxUsePerResource ??= loser.HeroResourceMaxUsePerResource;
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

    /// <summary>
    /// The village NAME currently stored for a village's canonical (coordinate) key, or null when the
    /// village is unknown. Read this BEFORE <see cref="Merge"/> (which overwrites the cached identity) to
    /// detect an in-game rename by coordinates, so callers can migrate other name-keyed state (e.g. the
    /// village status cache) from the old name to the new one.
    /// </summary>
    public string? GetStoredName(VillageKeyInfo village)
    {
        if (village is null)
        {
            return null;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _cache.TryGetValue(CanonicalKey(village), out var record) ? record.Name : null;
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
            Version = CurrentFileVersion,
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
        // Atomic temp-file + move so a crash mid-write cannot corrupt villages.json.
        AtomicFile.WriteAllText(path, content);
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
