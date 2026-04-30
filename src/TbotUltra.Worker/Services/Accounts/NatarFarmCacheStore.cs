using System.Text.Json;
using System.Text.Json.Serialization;

namespace TbotUltra.Worker.Services;

public sealed class NatarFarmCacheStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _rootPath;

    public NatarFarmCacheStore(string rootPath)
    {
        _rootPath = rootPath;
    }

    public string GetFilePath(string accountName, string? serverUrl = null, string? selectionMode = null)
    {
        var normalizedAccount = NormalizeAccountName(accountName);
        var normalizedServer = NormalizeServerKey(serverUrl);
        var normalizedSelection = NormalizeSelectionMode(selectionMode);
        return Path.Combine(_rootPath, "config", "cache", "natar-farms", $"{normalizedAccount}__{normalizedServer}__{normalizedSelection}.json");
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

            if (!string.Equals(NormalizeAccountName(accountName), NormalizeAccountName(snapshot.AccountName), StringComparison.Ordinal))
            {
                snapshot = null;
                return false;
            }

            if (!string.Equals(NormalizeServerKey(serverUrl), NormalizeServerKey(snapshot.ServerUrl), StringComparison.Ordinal))
            {
                snapshot = null;
                return false;
            }

            if (!string.Equals(NormalizeSelectionMode(selectionMode), NormalizeSelectionMode(snapshot.SelectionMode), StringComparison.Ordinal))
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

        var filePath = GetFilePath(normalized.AccountName, normalized.ServerUrl, normalized.SelectionMode);
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Natar farm cache path is invalid.");
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, JsonSerializer.Serialize(normalized, JsonOptions));
        return true;
    }

    private static NatarFarmCacheSnapshot NormalizeSnapshot(NatarFarmCacheSnapshot snapshot)
    {
        var normalizedCoordinates = snapshot.Coordinates
            .Where(item => item is not null)
            .GroupBy(item => $"{item.X}|{item.Y}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.X)
            .ThenBy(item => item.Y)
            .ToList();

        return snapshot with
        {
            AccountName = snapshot.AccountName?.Trim() ?? string.Empty,
            ServerUrl = snapshot.ServerUrl?.Trim().TrimEnd('/') ?? string.Empty,
            SelectionMode = NormalizeSelectionMode(snapshot.SelectionMode),
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
            if (left[index].X != right[index].X || left[index].Y != right[index].Y)
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeAccountName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "main";
        }

        var chars = value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var joined = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return joined.Length == 0 ? "main" : joined;
    }

    private static string NormalizeServerKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "default_server";
        }

        var trimmed = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return "default_server";
        }

        var hostPart = string.IsNullOrWhiteSpace(uri.Host) ? "default_server" : uri.Host.ToLowerInvariant();
        var portPart = uri.IsDefaultPort ? string.Empty : $"_{uri.Port}";
        var chars = $"{hostPart}{portPart}"
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var joined = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return joined.Length == 0 ? "default_server" : joined;
    }

    private static string NormalizeSelectionMode(string? value)
    {
        return string.Equals(value?.Trim(), "all_villages", StringComparison.OrdinalIgnoreCase)
            ? "all_villages"
            : "farm_villages";
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
    [property: JsonPropertyName("y")] int Y);
