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
/// keyed by village name.
///
/// Only the durable structure (buildings, resource fields, village list, tribe, capital flag) is saved.
/// Volatile values that change constantly (current resource amounts, storage capacities, build-queue
/// timers, gold/silver) are stripped before saving and simply refreshed live after launch.
/// </summary>
public sealed class VillageCacheStore
{
    private sealed class VillageCacheFile
    {
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
            if (file?.Villages is not null)
            {
                foreach (var pair in file.Villages)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                    {
                        result[pair.Key] = pair.Value;
                    }
                }
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, VillageStatus>(System.StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Persists the per-village statuses for the active account (volatile values stripped).</summary>
    public void Save(IReadOnlyDictionary<string, VillageStatus> villagesByName)
    {
        var account = GetActiveAccountName();
        if (string.IsNullOrWhiteSpace(account) || villagesByName is null || villagesByName.Count == 0)
        {
            return;
        }

        var file = new VillageCacheFile();
        foreach (var pair in villagesByName)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            {
                file.Villages[pair.Key] = StripVolatile(pair.Value);
            }
        }

        try
        {
            var path = AccountStoragePaths.VillageCachePath(_projectRoot, account);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            WriteAllTextShared(path, JsonSerializer.Serialize(file, SerializerOptions));
        }
        catch (System.Exception ex)
        {
            _log?.Invoke($"Could not save village cache: {ex.Message}");
        }
    }

    // Keep only the durable structure; reset values that change constantly so the file stays small and
    // never shows misleading stale amounts (they refresh live after launch).
    private static VillageStatus StripVolatile(VillageStatus status)
    {
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
            WarehouseCapacity = null,
            GranaryCapacity = null,
            ResourceStorageForecasts = null,
            TroopTrainingQueues = null,
            AdventureCount = null,
            ActiveConstructions = null,
        };
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
        lock (FileIoLock)
        {
            RetryFileIo(() =>
            {
                File.WriteAllText(path, content);
                return true;
            });
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
