using System.Collections.Concurrent;
using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Infrastructure;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed class WatchtowerSnapshotStore
{
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _projectRoot;
    private readonly Action<string>? _log;

    public WatchtowerSnapshotStore(string projectRoot, Action<string>? log = null)
    {
        _projectRoot = projectRoot;
        _log = log;
    }

    public bool TryLoad(
        string accountName,
        string? serverUrl,
        out IReadOnlyDictionary<string, WatchtowerStatus> statuses)
    {
        statuses = new Dictionary<string, WatchtowerStatus>(StringComparer.OrdinalIgnoreCase);
        var filePath = AccountStoragePaths.WatchtowerSnapshotPath(_projectRoot, accountName, serverUrl);
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var cached = JsonSerializer.Deserialize<CachedWatchtowerSnapshot>(
                File.ReadAllText(filePath),
                JsonOptions);
            if (cached is null
                || !AccountKeyNormalizer.IsSameIdentity(
                    cached.AccountName,
                    cached.ServerUrl,
                    accountName,
                    serverUrl ?? string.Empty)
                || cached.Statuses is null)
            {
                return false;
            }

            statuses = new Dictionary<string, WatchtowerStatus>(
                cached.Statuses,
                StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log?.Invoke($"[watchtower-cache] could not read '{filePath}': {ex.Message}");
            return false;
        }
    }

    public void Save(
        string accountName,
        string? serverUrl,
        string villageKey,
        WatchtowerStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(villageKey);
        var filePath = AccountStoragePaths.WatchtowerSnapshotPath(_projectRoot, accountName, serverUrl);
        var fileLock = FileLocks.GetOrAdd(filePath, static _ => new object());
        lock (fileLock)
        {
            TryLoad(accountName, serverUrl, out var existing);
            var statuses = new Dictionary<string, WatchtowerStatus>(existing, StringComparer.OrdinalIgnoreCase)
            {
                [villageKey] = status,
            };
            var cached = new CachedWatchtowerSnapshot(
                accountName,
                serverUrl ?? string.Empty,
                DateTimeOffset.UtcNow,
                statuses);
            AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(cached, JsonOptions));
        }
    }

    private sealed record CachedWatchtowerSnapshot(
        string AccountName,
        string ServerUrl,
        DateTimeOffset UpdatedAtUtc,
        IReadOnlyDictionary<string, WatchtowerStatus> Statuses);
}
