using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Desktop.Services;

public sealed class ManualFarmingPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _projectRoot;

    public ManualFarmingPreferenceStore(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    public ManualFarmingPreference Load(string accountName)
    {
        var filePath = AccountStoragePaths.ManualFarmingPreferencePath(_projectRoot, accountName);
        if (TryLoadFromFile(filePath, out var preference))
        {
            return Normalize(preference);
        }

        var all = LoadLegacyAll();
        var key = AccountStoragePaths.NormalizeAccountKey(accountName);
        if (all.TryGetValue(key, out preference) && preference is not null)
        {
            Save(accountName, preference);
            return Normalize(preference);
        }

        return new ManualFarmingPreference();
    }

    public void Save(string accountName, ManualFarmingPreference preference)
    {
        var filePath = AccountStoragePaths.ManualFarmingPreferencePath(_projectRoot, accountName);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, JsonSerializer.Serialize(Normalize(preference), JsonOptions));
        RemoveFromLegacy(accountName);
    }

    private static bool TryLoadFromFile(string filePath, out ManualFarmingPreference preference)
    {
        preference = new ManualFarmingPreference();
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var raw = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            preference = JsonSerializer.Deserialize<ManualFarmingPreference>(raw, JsonOptions) ?? new ManualFarmingPreference();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private Dictionary<string, ManualFarmingPreference> LoadLegacyAll()
    {
        var legacyPath = AccountStoragePaths.LegacyManualFarmingPreferencePath(_projectRoot);
        if (!File.Exists(legacyPath))
        {
            return new Dictionary<string, ManualFarmingPreference>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var raw = File.ReadAllText(legacyPath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new Dictionary<string, ManualFarmingPreference>(StringComparer.OrdinalIgnoreCase);
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, ManualFarmingPreference>>(raw, JsonOptions);
            return loaded is null
                ? new Dictionary<string, ManualFarmingPreference>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ManualFarmingPreference>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, ManualFarmingPreference>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void RemoveFromLegacy(string accountName)
    {
        var legacyPath = AccountStoragePaths.LegacyManualFarmingPreferencePath(_projectRoot);
        var all = LoadLegacyAll();
        if (!all.Remove(AccountStoragePaths.NormalizeAccountKey(accountName)))
        {
            return;
        }

        if (all.Count == 0)
        {
            if (File.Exists(legacyPath))
            {
                File.Delete(legacyPath);
            }

            return;
        }

        File.WriteAllText(legacyPath, JsonSerializer.Serialize(all, JsonOptions));
    }

    private static ManualFarmingPreference Normalize(ManualFarmingPreference preference)
    {
        return preference with
        {
            TroopCount = Math.Max(1, preference.TroopCount),
            VariancePercent = NormalizeVariancePercent(preference.VariancePercent),
        };
    }

    private static int NormalizeVariancePercent(int value)
    {
        return value switch
        {
            0 or 5 or 10 or 20 or 50 => value,
            _ => 10,
        };
    }

}

public sealed record ManualFarmingPreference(
    [property: JsonPropertyName("troopCount")] int TroopCount = 1,
    [property: JsonPropertyName("variancePercent")] int VariancePercent = 10);
