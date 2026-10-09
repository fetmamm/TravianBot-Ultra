using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Account-scoped persistence of each village's last-read buildings + resource fields, so a village
/// scan is remembered across program restarts (the user gets something to work on without re-scanning
/// everything every launch). Stored per account in <c>config/accounts/&lt;account&gt;/village_cache.json</c>,
/// keyed by the canonical coordinate key (<c>xy:X|Y</c> — the same identity queue.json and the settings
/// store use, so an in-game rename cannot orphan an entry and duplicate names cannot collide). Legacy
/// name-keyed entries are re-keyed on load via the entry's own village list; an entry whose coordinates
/// cannot be resolved stays under its name so nothing is lost.
///
/// Only the durable structure (buildings, resource fields, village list, tribe, capital flag) is saved.
/// Volatile values that change constantly (current resource amounts, storage capacities, build-queue
/// timers, gold/silver) are stripped before saving and simply refreshed live after launch.
/// </summary>
public sealed class VillageCacheStore
{
    private sealed class VillageCacheFile
    {
        public DateTimeOffset? UpdatedAtUtc { get; set; }
        public Dictionary<string, VillageStatus> Villages { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object FileIoLock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _projectRoot;
    private readonly System.Func<string>? _activeAccountNameProvider;
    private readonly System.Action<string>? _log;

    public VillageCacheStore(string projectRoot, System.Func<string>? activeAccountNameProvider = null, System.Action<string>? log = null)
    {
        _projectRoot = projectRoot;
        _activeAccountNameProvider = activeAccountNameProvider;
        _log = log;
    }

    /// <summary>Loads the persisted per-village statuses for the active account (empty on miss/corrupt).</summary>
    public Dictionary<string, VillageStatus> Load()
    {
        var account = GetActiveAccountName();
        if (string.IsNullOrWhiteSpace(account))
        {
            return new Dictionary<string, VillageStatus>(System.StringComparer.OrdinalIgnoreCase);
        }

        var path = AccountStoragePaths.VillageCachePath(_projectRoot, account);
        if (!File.Exists(path))
        {
            return new Dictionary<string, VillageStatus>(System.StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var raw = ReadAllTextShared(path);
            var file = JsonSerializer.Deserialize<VillageCacheFile>(raw, SerializerOptions);
            var result = new Dictionary<string, VillageStatus>(System.StringComparer.OrdinalIgnoreCase);
            var migratedCount = 0;
            if (file?.Villages is not null)
            {
                foreach (var pair in file.Villages)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                    {
                        continue;
                    }

                    // One-time migration of legacy name-keyed entries onto the canonical coordinate key,
                    // resolved from the entry's own village list. Unresolvable (or colliding) entries stay
                    // under their name key so nothing is dropped.
                    var key = pair.Key;
                    if (!VillageStatusCache.IsCoordinateKey(key))
                    {
                        var coordinateKey = VillageStatusCache.TryResolveCoordinateKey(key, pair.Value);
                        if (coordinateKey is not null
                            && !file.Villages.ContainsKey(coordinateKey)
                            && !result.ContainsKey(coordinateKey))
                        {
                            key = coordinateKey;
                            migratedCount++;
                        }
                    }

                    var label = string.IsNullOrWhiteSpace(pair.Value.ActiveVillage) ? key : pair.Value.ActiveVillage;
                    result[key] = ReconcileTimers(label, pair.Value, DateTimeOffset.UtcNow);
                }
            }

            if (migratedCount > 0)
            {
                _log?.Invoke($"[village-cache] migrated {migratedCount} legacy name-keyed entr(y/ies) to coordinate keys.");
            }

            return result;
        }
        catch (System.Exception ex)
        {
            _log?.Invoke($"[village-cache] could not load '{path}': {ex.Message}");
            return new Dictionary<string, VillageStatus>(System.StringComparer.OrdinalIgnoreCase);
        }
    }

    public DateTimeOffset? LoadUpdatedAtUtc()
    {
        var account = GetActiveAccountName();
        if (string.IsNullOrWhiteSpace(account)) return null;
        var path = AccountStoragePaths.VillageCachePath(_projectRoot, account);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<VillageCacheFile>(ReadAllTextShared(path), SerializerOptions)?.UpdatedAtUtc;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"[village-cache] could not read snapshot timestamp: {ex.Message}");
            return null;
        }
    }

    /// <summary>Persists per-village statuses without letting an early partial snapshot discard
    /// other villages or a previously known layout. Volatile values are stripped.</summary>
    public void Save(IReadOnlyDictionary<string, VillageStatus> villagesByKey)
    {
        var account = GetActiveAccountName();
        Save(account, villagesByKey);
    }

    internal void Save(string? account, IReadOnlyDictionary<string, VillageStatus> villagesByKey)
    {
        if (string.IsNullOrWhiteSpace(account) || villagesByKey is null || villagesByKey.Count == 0)
        {
            return;
        }

        try
        {
            var path = AccountStoragePaths.VillageCachePath(_projectRoot, account);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // The UI may receive a Watchtower observation before post-login cache loading. Keep the
            // read and write under one lock so that an older one-village snapshot cannot replace the
            // complete on-disk account cache or race another queued snapshot write.
            lock (FileIoLock)
            {
                var file = File.Exists(path)
                    ? JsonSerializer.Deserialize<VillageCacheFile>(ReadAllTextShared(path), SerializerOptions)
                        ?? throw new InvalidDataException($"Village cache at '{path}' was empty or invalid.")
                    : new VillageCacheFile();
                var merged = new Dictionary<string, VillageStatus>(
                    file.Villages ?? new Dictionary<string, VillageStatus>(),
                    System.StringComparer.OrdinalIgnoreCase);
                foreach (var pair in villagesByKey)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                    {
                        continue;
                    }

                    var status = pair.Value;
                    if (merged.TryGetValue(pair.Key, out var previous))
                    {
                        status = PreserveKnownLayout(status, previous);
                    }

                    merged[pair.Key] = StripVolatile(status);
                }

                file.Villages = merged;
                file.UpdatedAtUtc = DateTimeOffset.UtcNow;
                WriteAllTextShared(path, JsonSerializer.Serialize(file, SerializerOptions));
                if (merged.Count > villagesByKey.Count)
                {
                    _log?.Invoke($"[village-cache] preserved {merged.Count - villagesByKey.Count} saved village(s) missing from a partial snapshot for account '{account}'.");
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.Invoke($"Could not save village cache: {ex.Message}");
        }
    }

    private static VillageStatus PreserveKnownLayout(VillageStatus incoming, VillageStatus previous) =>
        incoming with
        {
            Villages = incoming.Villages.Count > 0 ? incoming.Villages : previous.Villages,
            ResourceFields = incoming.ResourceFields.Count > 0 ? incoming.ResourceFields : previous.ResourceFields,
            Buildings = incoming.Buildings.Count > 0 ? incoming.Buildings : previous.Buildings,
            Tribe = string.Equals(incoming.Tribe, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? previous.Tribe
                : incoming.Tribe,
            VillageCount = incoming.VillageCount > 0 ? incoming.VillageCount : previous.VillageCount,
            WarehouseCapacity = incoming.WarehouseCapacity ?? previous.WarehouseCapacity,
            GranaryCapacity = incoming.GranaryCapacity ?? previous.GranaryCapacity,
            IsCapital = incoming.IsCapital ?? previous.IsCapital,
            CityCapability = incoming.CityCapability == CityCapability.Unknown
                ? previous.CityCapability
                : incoming.CityCapability,
            CityStatus = incoming.CityStatus == CityStatus.Unknown ? previous.CityStatus : incoming.CityStatus,
            WatchtowerStatus = incoming.WatchtowerStatus ?? previous.WatchtowerStatus,
        };

    // Keep durable structure and absolute timer finishes. Tick-down values are rebuilt from FinishUtc
    // on load so a restart or machine sleep cannot freeze an old countdown.
    private static VillageStatus StripVolatile(VillageStatus status)
    {
        var activeConstructions = status.ActiveConstructions?
            .Where(item => item.Finish is not null)
            .Select(item => item with { TimeLeftSeconds = null })
            .ToList();
        var troopTrainingQueues = status.TroopTrainingQueues?
            .Where(item => item.Finish is not null)
            .Select(item => item with
            {
                QueueItems = Array.Empty<BuildQueueItem>(),
                RemainingSeconds = null,
                RemainingText = string.Empty,
            })
            .ToList();
        var smithyStatus = status.SmithyUpgradeStatus is null
            ? null
            : status.SmithyUpgradeStatus with
            {
                RemainingSeconds = null,
                ActiveUpgradeRemainingSeconds = Array.Empty<int>(),
                RemainingText = string.Empty,
                ActiveUpgrades = status.SmithyUpgradeStatus.ActiveUpgrades?
                    .Where(entry => entry.Finish is not null)
                    .Select(entry => entry with { TimeLeftSeconds = null })
                    .ToList(),
            };
        var breweryStatus = status.BreweryCelebrationStatus is null
            ? null
            : status.BreweryCelebrationStatus with
            {
                RemainingSeconds = null,
                RemainingText = string.Empty,
            };
        var farmLists = status.FarmLists?
            .Select(item => item with { RemainingSeconds = null })
            .ToList();
        var heroStatus = status.HeroStatus is null
            ? null
            : status.HeroStatus with
            {
                SecondsUntilAdventureReady = null,
                SecondsUntilReturn = null,
                ReviveRemainingSeconds = null,
            };

        // Keep warehouse/granary capacity + the storage forecasts so storage is remembered per village
        // across restarts (current amounts are stale until the first live refresh, which is fine).
        return status with
        {
            Resources = new Dictionary<string, string>(),
            BuildQueue = System.Array.Empty<BuildQueueItem>(),
            Gold = null,
            Silver = null,
            IsBuildingInProgress = false,
            ActiveBuildCount = 0,
            BuildQueueRemainingSeconds = null,
            BuildQueueRemainingText = string.Empty,
            ServerTimeUtc = null,
            UnreadMessages = 0,
            UnreadReports = 0,
            TroopTrainingQueues = troopTrainingQueues,
            AdventureCount = null,
            ActiveConstructions = activeConstructions,
            SmithyUpgradeStatus = smithyStatus,
            BreweryCelebrationStatus = breweryStatus,
            FarmLists = farmLists,
            HeroStatus = heroStatus,
            // Incoming-attack persistence has its own account+world store. Keeping Dorf1 signals here
            // would turn a stale browser marker into a fresh observation after restart.
            IncomingAttackSignals = null,
            IncomingAttackActiveVillageReadWasAuthoritative = false,
            IncomingAttackPlusOverviewWasRead = false,
            HasTroopsAtHome = null,
            TroopPresenceObservedAtUtc = null,
        };
    }

    private VillageStatus ReconcileTimers(string villageName, VillageStatus status, DateTimeOffset now)
    {
        var staleCount = 0;

        // ActiveConstructions is a browser snapshot, not a local timer prediction. Keep entries until
        // a confirmed dorf1/dorf2 read says the queue is empty; an elapsed FinishUtc alone cannot clear it.
        var activeConstructions = (status.ActiveConstructions ?? [])
            .Select(item => item with
            {
                TimeLeftSeconds = item.Finish?.RemainingSecondsAt(now) ?? item.TimeLeftSeconds,
            })
            .ToList();
        var currentActiveConstructions = ConstructionQueueState.ResolveCurrentActiveConstructions(
            status with { ActiveConstructions = activeConstructions },
            now);
        staleCount += Math.Max(0, activeConstructions.Count - currentActiveConstructions.Count);

        var troopTrainingQueues = (status.TroopTrainingQueues ?? [])
            .Where(item =>
            {
                var active = item.Finish is not null && !item.Finish.IsFinishedAt(now);
                if (!active && item.Finish is not null)
                {
                    staleCount++;
                }

                return active;
            })
            .Select(item =>
            {
                var remaining = item.Finish!.RemainingSecondsAt(now);
                return item with
                {
                    RemainingSeconds = remaining,
                    RemainingText = FormatDuration(remaining),
                };
            })
            .ToList();

        var previousSmithyCount = status.SmithyUpgradeStatus?.ActiveUpgrades?.Count
            ?? status.SmithyUpgradeStatus?.ActiveUpgradeFinishes?.Count
            ?? 0;
        var smithyUpgrades = SmithyQueueState.ResolveActiveUpgrades(status.SmithyUpgradeStatus, now);
        staleCount += Math.Max(0, previousSmithyCount - smithyUpgrades.Count);
        var smithyFinishes = smithyUpgrades
            .Where(entry => entry.Finish is not null)
            .Select(entry => entry.Finish!)
            .ToList();
        var smithyRemaining = smithyUpgrades
            .Where(entry => entry.TimeLeftSeconds is > 0)
            .Select(entry => entry.TimeLeftSeconds!.Value)
            .OrderBy(value => value)
            .ToList();
        var smithyStatus = status.SmithyUpgradeStatus is null
            ? null
            : status.SmithyUpgradeStatus with
            {
                ActiveUpgradeCount = smithyRemaining.Count,
                RemainingSeconds = smithyRemaining.FirstOrDefault() is var first && first > 0 ? first : null,
                ActiveUpgradeRemainingSeconds = smithyRemaining,
                RemainingText = smithyRemaining.Count > 0 ? FormatDuration(smithyRemaining[0]) : "Ready",
                ActiveUpgradeFinishes = smithyFinishes,
                ActiveUpgrades = smithyUpgrades,
            };

        var breweryStatus = ReconcileBrewery(status.BreweryCelebrationStatus, now, ref staleCount);
        var farmLists = status.FarmLists?
            .Select(item =>
            {
                if (item.Finish is null)
                {
                    return item with { RemainingSeconds = null };
                }

                if (item.Finish.IsFinishedAt(now))
                {
                    staleCount++;
                    return item with { RemainingSeconds = null, Finish = null };
                }

                return item with { RemainingSeconds = item.Finish.RemainingSecondsAt(now) };
            })
            .ToList();
        var heroStatus = ReconcileHero(status.HeroStatus, now, ref staleCount);

        var buildRemaining = currentActiveConstructions
            .Where(item => item.TimeLeftSeconds is > 0)
            .Select(item => item.TimeLeftSeconds!.Value)
            .DefaultIfEmpty(0)
            .Min();
        if (currentActiveConstructions.Count == 0 && status.BuildQueueFinish is not null)
        {
            if (status.BuildQueueFinish.IsFinishedAt(now))
            {
                staleCount++;
            }
            else
            {
                buildRemaining = status.BuildQueueFinish.RemainingSecondsAt(now);
            }
        }

        if (staleCount > 0)
        {
            _log?.Invoke($"[village-cache] cleared {staleCount} stale timer(s) for '{villageName}'; live scan will confirm current state.");
        }

        return status with
        {
            IsBuildingInProgress = currentActiveConstructions.Count > 0,
            ActiveBuildCount = currentActiveConstructions.Count,
            BuildQueueRemainingSeconds = buildRemaining > 0 ? buildRemaining : null,
            BuildQueueRemainingText = buildRemaining > 0 ? FormatDuration(buildRemaining) : string.Empty,
            BuildQueueFinish = buildRemaining > 0
                ? status.BuildQueueFinish ?? currentActiveConstructions.OrderBy(item => item.TimeLeftSeconds).FirstOrDefault()?.Finish
                : null,
            ActiveConstructions = activeConstructions,
            TroopTrainingQueues = troopTrainingQueues,
            SmithyUpgradeStatus = smithyStatus,
            BreweryCelebrationStatus = breweryStatus,
            FarmLists = farmLists,
            HeroStatus = heroStatus,
        };
    }

    private static BreweryCelebrationStatus? ReconcileBrewery(
        BreweryCelebrationStatus? status,
        DateTimeOffset now,
        ref int staleCount)
    {
        if (status?.Finish is null)
        {
            return status;
        }

        if (status.Finish.IsFinishedAt(now))
        {
            staleCount++;
            return status with
            {
                CelebrationRunning = false,
                RemainingSeconds = null,
                RemainingText = "Ready",
                Finish = null,
            };
        }

        var remaining = status.Finish.RemainingSecondsAt(now);
        return status with { RemainingSeconds = remaining, RemainingText = FormatDuration(remaining) };
    }

    private static HeroStatus? ReconcileHero(HeroStatus? status, DateTimeOffset now, ref int staleCount)
    {
        if (status is null)
        {
            return null;
        }

        var adventure = ReconcileFinish(status.AdventureReadyFinish, now, ref staleCount);
        var heroReturn = ReconcileFinish(status.ReturnFinish, now, ref staleCount);
        var revive = ReconcileFinish(status.ReviveFinish, now, ref staleCount);
        return status with
        {
            SecondsUntilAdventureReady = adventure?.RemainingSecondsAt(now),
            SecondsUntilReturn = heroReturn?.RemainingSecondsAt(now),
            ReviveRemainingSeconds = revive?.RemainingSecondsAt(now),
            AdventureReadyFinish = adventure,
            ReturnFinish = heroReturn,
            ReviveFinish = revive,
        };
    }

    private static TimerSnapshot? ReconcileFinish(TimerSnapshot? finish, DateTimeOffset now, ref int staleCount)
    {
        if (finish is null || !finish.IsFinishedAt(now))
        {
            return finish;
        }

        staleCount++;
        return null;
    }

    private static string FormatDuration(int seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}:{duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
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
        lock (FileIoLock)
        {
            return RetryFileIo(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            });
        }
    }

    private static void WriteAllTextShared(string path, string content)
    {
        // Lock serializes in-process writers; AtomicFile makes the write itself crash-safe so a
        // concurrent reader never sees a half-written village cache.
        lock (FileIoLock)
        {
            AtomicFile.WriteAllText(path, content);
        }
    }

    private static T RetryFileIo<T>(System.Func<T> action)
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
