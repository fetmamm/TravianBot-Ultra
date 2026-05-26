using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Worker.Services;

public sealed class NatarFarmCacheStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _rootPath;

    public NatarFarmCacheStore(string rootPath)
    {
        _rootPath = rootPath;
    }

    public string GetFilePath(string accountName, string? serverUrl = null, string? selectionMode = null)
    {
        return AccountStoragePaths.NatarFarmCachePath(_rootPath, accountName, serverUrl, selectionMode);
    }

    public bool IsAnalyzed(string accountName, string? serverUrl = null, string? selectionMode = null)
    {
        return TryLoad(accountName, out var snapshot, serverUrl, selectionMode)
            && snapshot is not null
            && snapshot.SchemaVersion == CurrentSchemaVersion
            && snapshot.Coordinates.Count > 0;
    }

    public bool TryLoad(string accountName, out NatarFarmCacheSnapshot? snapshot, string? serverUrl = null, string? selectionMode = null)
    {
        snapshot = null;
        var filePath = GetFilePath(accountName, serverUrl, selectionMode);
        if (TryLoadFromPath(filePath, accountName, serverUrl, selectionMode, out snapshot))
        {
            return true;
        }

        var legacyPath = AccountStoragePaths.LegacyNatarFarmCachePath(_rootPath, accountName, serverUrl, selectionMode);
        if (TryLoadFromPath(legacyPath, accountName, serverUrl, selectionMode, out snapshot))
        {
            WriteSnapshot(snapshot!);
            DeleteFileIfExists(legacyPath);
            return true;
        }

        return false;
    }

    private static bool TryLoadFromPath(string filePath, string accountName, string? serverUrl, string? selectionMode, out NatarFarmCacheSnapshot? snapshot)
    {
        snapshot = null;
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

            snapshot = JsonSerializer.Deserialize<NatarFarmCacheSnapshot>(raw, JsonOptions);
            if (snapshot is null)
            {
                return false;
            }

            if (!string.Equals(AccountStoragePaths.NormalizeAccountKey(accountName), AccountStoragePaths.NormalizeAccountKey(snapshot.AccountName), StringComparison.Ordinal))
            {
                snapshot = null;
                return false;
            }

            if (!string.Equals(AccountStoragePaths.NormalizeServerKey(serverUrl), AccountStoragePaths.NormalizeServerKey(snapshot.ServerUrl), StringComparison.Ordinal))
            {
                snapshot = null;
                return false;
            }

            if (!string.Equals(AccountStoragePaths.NormalizeSelectionMode(selectionMode), AccountStoragePaths.NormalizeSelectionMode(snapshot.SelectionMode), StringComparison.Ordinal))
            {
                snapshot = null;
                return false;
            }

            snapshot = NormalizeSnapshot(snapshot);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Save(NatarFarmCacheSnapshot snapshot)
    {
        var normalized = NormalizeSnapshot(snapshot);
        if (TryLoad(normalized.AccountName, out var existing, normalized.ServerUrl, normalized.SelectionMode)
            && existing is not null
            && AreSameCoordinates(existing.Coordinates, normalized.Coordinates))
        {
            return false;
        }

        WriteSnapshot(normalized);
        DeleteFileIfExists(AccountStoragePaths.LegacyNatarFarmCachePath(_rootPath, normalized.AccountName, normalized.ServerUrl, normalized.SelectionMode));
        return true;
    }

    private void WriteSnapshot(NatarFarmCacheSnapshot snapshot)
    {
        var normalized = NormalizeSnapshot(snapshot);
        var filePath = GetFilePath(normalized.AccountName, normalized.ServerUrl, normalized.SelectionMode);
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Natar farm cache path is invalid.");
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, JsonSerializer.Serialize(normalized, JsonOptions));
    }

    public void SaveSelection(string accountName, string? serverUrl, string? selectionMode, IReadOnlySet<string> enabledCoordinateKeys)
    {
        if (!TryLoad(accountName, out var snapshot, serverUrl, selectionMode) || snapshot is null)
        {
            return;
        }

        var updated = snapshot with
        {
            Coordinates = snapshot.Coordinates
                .Select(item => item with { Enabled = enabledCoordinateKeys.Contains(BuildCoordinateKey(item.X, item.Y)) })
                .ToList(),
        };
        Save(updated);
    }

    private static NatarFarmCacheSnapshot NormalizeSnapshot(NatarFarmCacheSnapshot snapshot)
    {
        var normalizedCoordinates = snapshot.Coordinates
            .Where(item => item is not null)
            .GroupBy(item => BuildCoordinateKey(item.X, item.Y), StringComparer.Ordinal)
            .Select(group =>
            {
                var firstWithName = group.FirstOrDefault(coord => !string.IsNullOrWhiteSpace(coord.VillageName));
                var selected = firstWithName ?? group.First();
                return selected with { Enabled = group.Any(coord => coord.Enabled) };
            })
            .OrderBy(item => item.X)
            .ThenBy(item => item.Y)
            .ToList();

        return snapshot with
        {
            AccountName = snapshot.AccountName?.Trim() ?? string.Empty,
            ServerUrl = snapshot.ServerUrl?.Trim().TrimEnd('/') ?? string.Empty,
            SelectionMode = AccountStoragePaths.NormalizeSelectionMode(snapshot.SelectionMode),
            Coordinates = normalizedCoordinates,
        };
    }

    private static bool AreSameCoordinates(IReadOnlyList<NatarFarmCoordinate> left, IReadOnlyList<NatarFarmCoordinate> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].X != right[index].X || left[index].Y != right[index].Y || left[index].Enabled != right[index].Enabled)
            {
                return false;
            }
        }

        return true;
    }

    public static string BuildCoordinateKey(int x, int y) => $"{x}|{y}";

    private static void DeleteFileIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}

public sealed record NatarFarmCacheSnapshot(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("analyzedAtUtc")] DateTimeOffset AnalyzedAtUtc,
    [property: JsonPropertyName("accountName")] string AccountName,
    [property: JsonPropertyName("serverUrl")] string ServerUrl,
    [property: JsonPropertyName("selectionMode")] string SelectionMode,
    [property: JsonPropertyName("coordinates")] List<NatarFarmCoordinate> Coordinates);

public sealed record NatarFarmCoordinate(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("villageName")] string? VillageName = null,
    [property: JsonPropertyName("enabled")] bool Enabled = true);
