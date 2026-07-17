using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services;

public sealed record TownHallCelebrationState(string Mode, DateTimeOffset EndsAtUtc);

/// <summary>
/// Per-account remembered Town Hall celebration timers. The worker reports a queue wait after reading the
/// live page; the desktop persists the resulting end time so restart can restore the countdown without
/// navigating back to the Town Hall while the celebration is still running.
/// </summary>
public static class TownHallCelebrationStateStore
{
    private static readonly object FileIoLock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class VillageTownHallState
    {
        public string Key { get; set; } = string.Empty;
        public string Mode { get; set; } = TownHallCelebrationDefaults.Small;
        public DateTimeOffset EndsAtUtc { get; set; }
    }

    private sealed class TownHallStateFile
    {
        public List<VillageTownHallState> Villages { get; set; } = new();
    }

    public static TownHallCelebrationState? LoadActive(
        string projectRoot,
        string? accountName,
        string? villageKey,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(villageKey))
        {
            return null;
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName);
            var match = file?.Villages?
                .FirstOrDefault(v => v is not null && string.Equals(v.Key, villageKey, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return null;
            }

            if (match.EndsAtUtc <= nowUtc)
            {
                ClearLocked(projectRoot, accountName, villageKey, file!);
                return null;
            }

            return new TownHallCelebrationState(
                TownHallCelebrationDefaults.NormalizeMode(match.Mode),
                match.EndsAtUtc.ToUniversalTime());
        }
    }

    public static IReadOnlyDictionary<string, TownHallCelebrationState> ReadAllActive(
        string projectRoot,
        string? accountName,
        DateTimeOffset nowUtc)
    {
        var result = new Dictionary<string, TownHallCelebrationState>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return result;
        }

        lock (FileIoLock)
        {
            foreach (var entry in ReadFile(projectRoot, accountName)?.Villages ?? [])
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Key) || entry.EndsAtUtc <= nowUtc)
                {
                    continue;
                }

                result[entry.Key] = new TownHallCelebrationState(
                    TownHallCelebrationDefaults.NormalizeMode(entry.Mode),
                    entry.EndsAtUtc.ToUniversalTime());
            }
        }

        return result;
    }

    public static void Save(
        string projectRoot,
        string? accountName,
        string? villageKey,
        string? mode,
        DateTimeOffset endsAtUtc)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(villageKey))
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName) ?? new TownHallStateFile();
            file.Villages ??= new List<VillageTownHallState>();
            var existing = file.Villages
                .FirstOrDefault(v => v is not null && string.Equals(v.Key, villageKey, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                file.Villages.Add(new VillageTownHallState
                {
                    Key = villageKey,
                    Mode = TownHallCelebrationDefaults.NormalizeMode(mode),
                    EndsAtUtc = endsAtUtc.ToUniversalTime(),
                });
            }
            else
            {
                existing.Mode = TownHallCelebrationDefaults.NormalizeMode(mode);
                existing.EndsAtUtc = endsAtUtc.ToUniversalTime();
            }

            WriteFile(projectRoot, accountName, file);
        }
    }

    public static void Clear(string projectRoot, string? accountName, string? villageKey)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(villageKey))
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName);
            if (file is null)
            {
                return;
            }

            ClearLocked(projectRoot, accountName, villageKey, file);
        }
    }

    private static void ClearLocked(string projectRoot, string accountName, string villageKey, TownHallStateFile file)
    {
        var removed = file.Villages?.RemoveAll(v =>
            v is not null && string.Equals(v.Key, villageKey, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            WriteFile(projectRoot, accountName, file);
        }
    }

    private static TownHallStateFile? ReadFile(string projectRoot, string accountName)
    {
        var path = AccountStoragePaths.TownHallCelebrationStatePath(projectRoot, accountName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TownHallStateFile>(File.ReadAllText(path), SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteFile(string projectRoot, string accountName, TownHallStateFile file)
    {
        var path = AccountStoragePaths.TownHallCelebrationStatePath(projectRoot, accountName);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
    }
}
