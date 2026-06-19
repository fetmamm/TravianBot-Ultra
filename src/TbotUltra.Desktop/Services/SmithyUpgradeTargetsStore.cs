using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Desktop.Services;

/// <summary>One troop the user selected for Smithy improvement, with its target level.</summary>
public sealed record SmithyUpgradeSelection(string Key, string Name, int TargetLevel);

/// <summary>
/// Account-scoped, per-village persistence of the Smithy troop-upgrade selection (which troops, and to
/// which level). Stored per account in <c>config/accounts/&lt;account&gt;/smithy_upgrade.json</c>, keyed by
/// the village's coordinate key (the same key the rest of the per-village settings use). The popup
/// reads/writes the selected village; the loop snapshots each village's selection into its task payload.
/// Reads never throw — a missing/corrupt file yields an empty selection.
///
/// A village with no entry has NO troops selected (every checkbox defaults to off): the user opts each
/// village in explicitly, or uses "Sync to all villages" to copy one village's choice everywhere.
/// </summary>
public static class SmithyUpgradeTargetsStore
{
    private static readonly object FileIoLock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class VillageSmithySelection
    {
        public string Key { get; set; } = string.Empty;
        public List<SmithyUpgradeSelection> Troops { get; set; } = new();
    }

    private sealed class SmithyUpgradeFile
    {
        // Per-village selections, keyed by the village's coordinate key.
        public List<VillageSmithySelection> Villages { get; set; } = new();
    }

    /// <summary>Returns the selection for a village, or an empty list when it has no entry.</summary>
    public static IReadOnlyList<SmithyUpgradeSelection> Load(string projectRoot, string? accountName, string? villageKey)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(villageKey))
        {
            return [];
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName);
            var match = file?.Villages?
                .FirstOrDefault(v => v is not null && string.Equals(v.Key, villageKey, StringComparison.OrdinalIgnoreCase));
            return match is not null ? Clean(match.Troops) : [];
        }
    }

    /// <summary>Saves the selection for a single village (upsert).</summary>
    public static void Save(string projectRoot, string? accountName, string? villageKey, IReadOnlyList<SmithyUpgradeSelection> selections)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(villageKey))
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName) ?? new SmithyUpgradeFile();
            UpsertVillage(file, villageKey, selections);
            WriteFile(projectRoot, accountName, file);
        }
    }

    /// <summary>Writes the same selection to every supplied village key — used by "Sync to all villages".</summary>
    public static void SaveForVillages(string projectRoot, string? accountName, IEnumerable<string> villageKeys, IReadOnlyList<SmithyUpgradeSelection> selections)
    {
        if (string.IsNullOrWhiteSpace(accountName) || villageKeys is null)
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName) ?? new SmithyUpgradeFile();
            foreach (var key in villageKeys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    UpsertVillage(file, key, selections);
                }
            }

            WriteFile(projectRoot, accountName, file);
        }
    }

    private static void UpsertVillage(SmithyUpgradeFile file, string villageKey, IReadOnlyList<SmithyUpgradeSelection> selections)
    {
        file.Villages ??= new List<VillageSmithySelection>();
        var clean = Clean(selections);
        var existing = file.Villages
            .FirstOrDefault(v => v is not null && string.Equals(v.Key, villageKey, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            file.Villages.Add(new VillageSmithySelection { Key = villageKey, Troops = clean });
        }
        else
        {
            existing.Troops = clean;
        }
    }

    private static List<SmithyUpgradeSelection> Clean(IReadOnlyList<SmithyUpgradeSelection>? selections)
    {
        return (selections ?? [])
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Key))
            .ToList();
    }

    private static SmithyUpgradeFile? ReadFile(string projectRoot, string accountName)
    {
        var path = AccountStoragePaths.SmithyUpgradePath(projectRoot, accountName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SmithyUpgradeFile>(File.ReadAllText(path), SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteFile(string projectRoot, string accountName, SmithyUpgradeFile file)
    {
        var path = AccountStoragePaths.SmithyUpgradePath(projectRoot, accountName);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Atomic temp-file + move so a crash mid-write cannot corrupt smithy_upgrade.json.
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
    }
}
