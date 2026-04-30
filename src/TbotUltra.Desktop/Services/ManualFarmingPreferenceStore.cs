using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TbotUltra.Desktop.Services;

public sealed class ManualFarmingPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;

    public ManualFarmingPreferenceStore(string projectRoot)
    {
        _filePath = Path.Combine(projectRoot, "config", "cache", "manual-farming-preferences.json");
    }

    public ManualFarmingPreference Load(string accountName)
    {
        var all = LoadAll();
        var key = NormalizeAccountName(accountName);
        return all.TryGetValue(key, out var preference) && preference is not null
            ? Normalize(preference)
            : new ManualFarmingPreference();
    }

    public void Save(string accountName, ManualFarmingPreference preference)
    {
        var all = LoadAll();
        all[NormalizeAccountName(accountName)] = Normalize(preference);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_filePath, JsonSerializer.Serialize(all, JsonOptions));
    }

    private Dictionary<string, ManualFarmingPreference> LoadAll()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, ManualFarmingPreference>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var raw = File.ReadAllText(_filePath);
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

    private static string NormalizeAccountName(string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return "main";
        }

        var chars = accountName.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var normalized = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? "main" : normalized;
    }
}

public sealed record ManualFarmingPreference(
    [property: JsonPropertyName("troopCount")] int TroopCount = 1,
    [property: JsonPropertyName("variancePercent")] int VariancePercent = 10);
