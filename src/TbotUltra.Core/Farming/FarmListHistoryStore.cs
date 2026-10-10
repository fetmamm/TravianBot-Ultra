using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Infrastructure;

namespace TbotUltra.Core.Farming;

public sealed record FarmListHistoryEntry(
    DateTimeOffset SentAtUtc,
    string ListName,
    string? ListId,
    string? VillageName,
    string Origin,
    string Response,
    TimeSpan? SincePrevious)
{
    [JsonIgnore]
    public string SentAtLocalText => SentAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    [JsonIgnore]
    public string SincePreviousText => SincePrevious is { } elapsed
        ? elapsed.ToString(@"d\.hh\:mm\:ss")
        : "—";
}

public static class FarmListHistoryStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private const int MaxEntries = 50_000;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public static IReadOnlyList<FarmListHistoryEntry> Load(
        string projectRoot, string accountName, string? serverUrl, Action<string>? log = null)
    {
        lock (Gate)
        {
            return LoadCore(projectRoot, accountName, serverUrl, DateTimeOffset.UtcNow, log);
        }
    }

    public static void Append(
        string projectRoot,
        string accountName,
        string? serverUrl,
        IEnumerable<FarmListHistoryEntry> newEntries,
        Action<string>? log = null)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            var entries = LoadCore(projectRoot, accountName, serverUrl, now, log).ToList();
            foreach (var entry in newEntries.OrderBy(item => item.SentAtUtc))
            {
                var key = FarmListDispatchStateStore.CreateKey(entry.ListId, entry.ListName);
                var previous = entries.LastOrDefault(item =>
                    string.Equals(FarmListDispatchStateStore.CreateKey(item.ListId, item.ListName), key, StringComparison.OrdinalIgnoreCase));
                entries.Add(entry with
                {
                    SincePrevious = previous is null ? null : entry.SentAtUtc - previous.SentAtUtc,
                });
            }

            entries = entries
                .Where(item => item.SentAtUtc >= now - Retention)
                .TakeLast(MaxEntries)
                .ToList();
            var path = AccountStoragePaths.FarmListHistoryPath(projectRoot, accountName, serverUrl);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(entries, SerializerOptions));
        }
    }

    public static void Clear(string projectRoot, string accountName, string? serverUrl)
    {
        lock (Gate)
        {
            var path = AccountStoragePaths.FarmListHistoryPath(projectRoot, accountName, serverUrl);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static IReadOnlyList<FarmListHistoryEntry> LoadCore(
        string projectRoot, string accountName, string? serverUrl, DateTimeOffset now, Action<string>? log)
    {
        var path = AccountStoragePaths.FarmListHistoryPath(projectRoot, accountName, serverUrl);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<FarmListHistoryEntry>>(File.ReadAllText(path)) ?? [])
                .Where(item => item.SentAtUtc >= now - Retention)
                .TakeLast(MaxEntries)
                .ToList();
        }
        catch (JsonException ex)
        {
            var quarantinePath = $"{path}.corrupt-{now:yyyyMMddHHmmssfff}";
            File.Move(path, quarantinePath);
            log?.Invoke($"[farm-list] corrupt send history moved to '{Path.GetFileName(quarantinePath)}': {ex.Message}");
            return [];
        }
    }
}
