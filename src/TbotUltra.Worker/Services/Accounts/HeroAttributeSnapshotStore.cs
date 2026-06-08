using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed class HeroAttributeSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _projectRoot;

    public HeroAttributeSnapshotStore(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    public bool TryLoad(
        string accountName,
        string? serverUrl,
        out HeroAttributeSnapshot? snapshot)
    {
        snapshot = null;
        var filePath = AccountStoragePaths.HeroAttributeSnapshotPath(_projectRoot, accountName, serverUrl);
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var cached = JsonSerializer.Deserialize<CachedHeroAttributeSnapshot>(
                File.ReadAllText(filePath),
                JsonOptions);
            if (cached is null
                || !string.Equals(
                    AccountStoragePaths.NormalizeAccountKey(accountName),
                    AccountStoragePaths.NormalizeAccountKey(cached.AccountName),
                    StringComparison.Ordinal)
                || !string.Equals(
                    AccountStoragePaths.NormalizeServerKey(serverUrl),
                    AccountStoragePaths.NormalizeServerKey(cached.ServerUrl),
                    StringComparison.Ordinal))
            {
                return false;
            }

            snapshot = cached.Snapshot;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Save(string accountName, string? serverUrl, HeroAttributeSnapshot snapshot)
    {
        var filePath = AccountStoragePaths.HeroAttributeSnapshotPath(_projectRoot, accountName, serverUrl);
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Hero attribute cache path is invalid.");
        }

        Directory.CreateDirectory(directory);
        var cached = new CachedHeroAttributeSnapshot(
            accountName,
            serverUrl ?? string.Empty,
            snapshot,
            DateTimeOffset.UtcNow);
        File.WriteAllText(filePath, JsonSerializer.Serialize(cached, JsonOptions));
    }

    private sealed record CachedHeroAttributeSnapshot(
        string AccountName,
        string ServerUrl,
        HeroAttributeSnapshot Snapshot,
        DateTimeOffset UpdatedAtUtc);
}
