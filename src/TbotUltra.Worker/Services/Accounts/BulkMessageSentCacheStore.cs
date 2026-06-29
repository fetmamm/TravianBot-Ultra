using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Worker.Services;

public sealed class BulkMessageSentCacheStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _rootPath;

    public BulkMessageSentCacheStore(string rootPath)
    {
        _rootPath = rootPath;
    }

    public string GetFilePath(string accountName, string? serverUrl = null)
    {
        return AccountStoragePaths.BulkMessageSentCachePath(_rootPath, accountName, serverUrl);
    }

    public IReadOnlyList<string> LoadSentPlayerNames(string accountName, string? serverUrl = null)
    {
        return TryLoad(accountName, out var snapshot, serverUrl) && snapshot is not null
            ? snapshot.Players.Select(player => player.Name).ToList()
            : [];
    }

    public int Count(string accountName, string? serverUrl = null)
    {
        return TryLoad(accountName, out var snapshot, serverUrl) && snapshot is not null
            ? snapshot.Players.Count
            : 0;
    }

    public bool TryLoad(string accountName, out BulkMessageSentCacheSnapshot? snapshot, string? serverUrl = null)
    {
        snapshot = null;
        var filePath = GetFilePath(accountName, serverUrl);
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

            snapshot = JsonSerializer.Deserialize<BulkMessageSentCacheSnapshot>(raw, JsonOptions);
            if (snapshot is null)
            {
                return false;
            }

            if (!string.Equals(AccountStoragePaths.NormalizeAccountKey(accountName), AccountStoragePaths.NormalizeAccountKey(snapshot.AccountName), StringComparison.Ordinal)
                || !string.Equals(AccountStoragePaths.NormalizeServerKey(serverUrl), AccountStoragePaths.NormalizeServerKey(snapshot.ServerUrl), StringComparison.Ordinal))
            {
                snapshot = null;
                return false;
            }

            snapshot = NormalizeSnapshot(snapshot);
            return snapshot.SchemaVersion == CurrentSchemaVersion;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    public void AddSentPlayers(string accountName, string? serverUrl, IReadOnlyList<string> playerNames, DateTimeOffset sentAtUtc)
    {
        if (playerNames.Count == 0)
        {
            return;
        }

        TryLoad(accountName, out var existing, serverUrl);
        var byKey = (existing?.Players ?? [])
            .ToDictionary(player => NormalizePlayerKey(player.Name), player => player, StringComparer.Ordinal);

        foreach (var playerName in playerNames)
        {
            var cleanName = (playerName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleanName))
            {
                continue;
            }

            byKey[NormalizePlayerKey(cleanName)] = new BulkMessageSentPlayer(cleanName, sentAtUtc);
        }

        var snapshot = new BulkMessageSentCacheSnapshot(
            SchemaVersion: CurrentSchemaVersion,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            AccountName: accountName.Trim(),
            ServerUrl: (serverUrl ?? string.Empty).Trim().TrimEnd('/'),
            Players: byKey.Values
                .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());

        WriteSnapshot(snapshot);
    }

    public void Clear(string accountName, string? serverUrl = null)
    {
        var filePath = GetFilePath(accountName, serverUrl);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private void WriteSnapshot(BulkMessageSentCacheSnapshot snapshot)
    {
        var normalized = NormalizeSnapshot(snapshot);
        var filePath = GetFilePath(normalized.AccountName, normalized.ServerUrl);
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Bulk message cache path is invalid.");
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static BulkMessageSentCacheSnapshot NormalizeSnapshot(BulkMessageSentCacheSnapshot snapshot)
    {
        var byKey = snapshot.Players
            .Where(player => player is not null && !string.IsNullOrWhiteSpace(player.Name))
            .GroupBy(player => NormalizePlayerKey(player.Name), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(player => player.SentAtUtc).First())
            .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return snapshot with
        {
            SchemaVersion = CurrentSchemaVersion,
            AccountName = snapshot.AccountName.Trim(),
            ServerUrl = snapshot.ServerUrl.Trim().TrimEnd('/'),
            Players = byKey,
        };
    }

    private static string NormalizePlayerKey(string? value)
    {
        return MapSqlPlayerParser.NormalizeNameKey(value);
    }
}

public sealed record BulkMessageSentCacheSnapshot(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("accountName")] string AccountName,
    [property: JsonPropertyName("serverUrl")] string ServerUrl,
    [property: JsonPropertyName("players")] List<BulkMessageSentPlayer> Players);

public sealed record BulkMessageSentPlayer(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sentAtUtc")] DateTimeOffset SentAtUtc);
