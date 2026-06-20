using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Account-scoped, per-village Town Hall celebration mode overrides. A village without an entry inherits
/// the account-wide default from settings.json; saved entries are normalized to "small" or "big".
/// </summary>
public static class TownHallSettingsStore
{
    private static readonly object FileIoLock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class VillageTownHallSettings
    {
        public string Key { get; set; } = string.Empty;
        public string? Mode { get; set; }
    }

    private sealed class TownHallSettingsFile
    {
        public List<VillageTownHallSettings> Villages { get; set; } = new();
    }

    public static string? LoadMode(string projectRoot, string? accountName, string? villageKey)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(villageKey))
        {
            return null;
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName);
            var raw = file?.Villages?
                .FirstOrDefault(v => v is not null && string.Equals(v.Key, villageKey, StringComparison.OrdinalIgnoreCase))
                ?.Mode;
            return string.IsNullOrWhiteSpace(raw) ? null : TownHallCelebrationDefaults.NormalizeMode(raw);
        }
    }

    public static void SaveMode(string projectRoot, string? accountName, string? villageKey, string? mode)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(villageKey))
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName) ?? new TownHallSettingsFile();
            UpsertVillage(file, villageKey, mode);
            WriteFile(projectRoot, accountName, file);
        }
    }

    private static void UpsertVillage(TownHallSettingsFile file, string villageKey, string? mode)
    {
        file.Villages ??= new List<VillageTownHallSettings>();
        var normalized = TownHallCelebrationDefaults.NormalizeMode(mode);
        var existing = file.Villages
            .FirstOrDefault(v => v is not null && string.Equals(v.Key, villageKey, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            file.Villages.Add(new VillageTownHallSettings { Key = villageKey, Mode = normalized });
        }
        else
        {
            existing.Mode = normalized;
        }
    }

    private static TownHallSettingsFile? ReadFile(string projectRoot, string accountName)
    {
        var path = AccountStoragePaths.TownHallSettingsPath(projectRoot, accountName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TownHallSettingsFile>(File.ReadAllText(path), SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteFile(string projectRoot, string accountName, TownHallSettingsFile file)
    {
        var path = AccountStoragePaths.TownHallSettingsPath(projectRoot, accountName);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
    }
}
