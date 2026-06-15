using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Tasks;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Account-scoped, per-village troop-training overrides (which troop / amount mode / run trigger per
/// building). Stored per account in <c>config/accounts/&lt;account&gt;/troop_training.json</c>, keyed by the
/// village's coordinate key — the same key the rest of the per-village settings use. Mirrors
/// <see cref="SmithyUpgradeTargetsStore"/>. Reads never throw; a missing/corrupt file yields <c>null</c>.
///
/// A village with no entry returns <c>null</c> and the loop falls back to the global troop-training config
/// in bot.json/settings.json (so existing single-config setups keep working). The popup writes an override
/// for a single village, or "Sync to all villages" copies one village's settings everywhere.
/// </summary>
public static class TroopTrainingSettingsStore
{
    private static readonly object FileIoLock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class VillageTroopTraining
    {
        public string Key { get; set; } = string.Empty;
        public TroopTrainingPayload? Settings { get; set; }
    }

    private sealed class TroopTrainingFile
    {
        // Per-village overrides, keyed by the village's coordinate key.
        public List<VillageTroopTraining> Villages { get; set; } = new();
    }

    /// <summary>Returns the override for a village, or <c>null</c> when it has no entry.</summary>
    public static TroopTrainingPayload? Load(string projectRoot, string? accountName, string? villageKey)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(villageKey))
        {
            return null;
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName);
            return file?.Villages?
                .FirstOrDefault(v => v is not null && string.Equals(v.Key, villageKey, StringComparison.OrdinalIgnoreCase))
                ?.Settings;
        }
    }

    /// <summary>Saves the override for a single village (upsert).</summary>
    public static void Save(string projectRoot, string? accountName, string? villageKey, TroopTrainingPayload? settings)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(villageKey))
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName) ?? new TroopTrainingFile();
            UpsertVillage(file, villageKey, settings);
            WriteFile(projectRoot, accountName, file);
        }
    }

    /// <summary>Writes the same override to every supplied village key — used by "Sync to all villages".</summary>
    public static void SaveForVillages(string projectRoot, string? accountName, IEnumerable<string> villageKeys, TroopTrainingPayload? settings)
    {
        if (string.IsNullOrWhiteSpace(accountName) || villageKeys is null)
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName) ?? new TroopTrainingFile();
            foreach (var key in villageKeys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    UpsertVillage(file, key, settings);
                }
            }

            WriteFile(projectRoot, accountName, file);
        }
    }

    private static void UpsertVillage(TroopTrainingFile file, string villageKey, TroopTrainingPayload? settings)
    {
        file.Villages ??= new List<VillageTroopTraining>();
        var existing = file.Villages
            .FirstOrDefault(v => v is not null && string.Equals(v.Key, villageKey, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            file.Villages.Add(new VillageTroopTraining { Key = villageKey, Settings = settings });
        }
        else
        {
            existing.Settings = settings;
        }
    }

    private static TroopTrainingFile? ReadFile(string projectRoot, string accountName)
    {
        var path = AccountStoragePaths.TroopTrainingPath(projectRoot, accountName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TroopTrainingFile>(File.ReadAllText(path), SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteFile(string projectRoot, string accountName, TroopTrainingFile file)
    {
        var path = AccountStoragePaths.TroopTrainingPath(projectRoot, accountName);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
    }
}
