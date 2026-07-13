using System.IO;
using System.Text.Json;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Desktop.Services;

internal sealed record ProxyUsageIdentity(
    string Key,
    string ConnectionType,
    string ProxyEndpoint,
    string ExitIp,
    string Country);

internal sealed record ProxyUsageRecord(
    string Key,
    string ConnectionType,
    string ProxyEndpoint,
    string ExitIp,
    string Country,
    double TotalSeconds,
    int SessionCount,
    DateTimeOffset FirstUsedUtc,
    DateTimeOffset LastUsedUtc);

/// <summary>
/// Persists accumulated connection usage per account. The caller checkpoints the active interval;
/// this store only merges elapsed time and metadata, so the file stays small regardless of runtime.
/// </summary>
internal static class ProxyUsageStore
{
    private static readonly object FileIoLock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class UsageEntry
    {
        public string Key { get; set; } = string.Empty;
        public string ConnectionType { get; set; } = string.Empty;
        public string ProxyEndpoint { get; set; } = string.Empty;
        public string ExitIp { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public double TotalSeconds { get; set; }
        public int SessionCount { get; set; }
        public DateTimeOffset FirstUsedUtc { get; set; }
        public DateTimeOffset LastUsedUtc { get; set; }
    }

    private sealed class UsageFile
    {
        public List<UsageEntry> Entries { get; set; } = new();
    }

    public static IReadOnlyList<ProxyUsageRecord> Load(string projectRoot, string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return Array.Empty<ProxyUsageRecord>();
        }

        lock (FileIoLock)
        {
            var file = ReadFileOrNull(projectRoot, accountName);
            return (file?.Entries ?? new List<UsageEntry>())
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.TotalSeconds > 0)
                .Select(entry => new ProxyUsageRecord(
                    entry.Key,
                    NormalizeConnectionType(entry.ConnectionType),
                    entry.ProxyEndpoint?.Trim() ?? string.Empty,
                    entry.ExitIp?.Trim() ?? string.Empty,
                    entry.Country?.Trim() ?? string.Empty,
                    Math.Max(0, entry.TotalSeconds),
                    Math.Max(0, entry.SessionCount),
                    entry.FirstUsedUtc.ToUniversalTime(),
                    entry.LastUsedUtc.ToUniversalTime()))
                .OrderByDescending(entry => entry.LastUsedUtc)
                .ToList();
        }
    }

    public static void RecordUsage(
        string projectRoot,
        string? accountName,
        ProxyUsageIdentity identity,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        bool startsNewSession)
    {
        if (string.IsNullOrWhiteSpace(accountName)
            || string.IsNullOrWhiteSpace(identity.Key)
            || endUtc <= startUtc)
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFileOrNull(projectRoot, accountName) ?? new UsageFile();
            file.Entries ??= new List<UsageEntry>();
            var entry = file.Entries.FirstOrDefault(item =>
                string.Equals(item.Key, identity.Key, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new UsageEntry
                {
                    Key = identity.Key.Trim(),
                    FirstUsedUtc = startUtc.ToUniversalTime(),
                };
                file.Entries.Add(entry);
            }

            entry.ConnectionType = NormalizeConnectionType(identity.ConnectionType);
            UpdateIfPresent(identity.ProxyEndpoint, value => entry.ProxyEndpoint = value);
            UpdateIfPresent(identity.ExitIp, value => entry.ExitIp = value);
            UpdateIfPresent(identity.Country, value => entry.Country = value);
            entry.TotalSeconds = Math.Max(0, entry.TotalSeconds) + (endUtc - startUtc).TotalSeconds;
            entry.SessionCount = Math.Max(0, entry.SessionCount) + (startsNewSession ? 1 : 0);
            entry.FirstUsedUtc = entry.FirstUsedUtc == default || startUtc < entry.FirstUsedUtc
                ? startUtc.ToUniversalTime()
                : entry.FirstUsedUtc.ToUniversalTime();
            entry.LastUsedUtc = endUtc.ToUniversalTime();

            WriteFile(projectRoot, accountName, file);
        }
    }

    private static void UpdateIfPresent(string? value, Action<string> update)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            update(value.Trim());
        }
    }

    private static string NormalizeConnectionType(string? value) =>
        string.Equals(value?.Trim(), "proxy", StringComparison.OrdinalIgnoreCase) ? "Proxy" : "Direct";

    private static UsageFile? ReadFileOrNull(string projectRoot, string accountName)
    {
        var path = AccountStoragePaths.ProxyUsagePath(projectRoot, accountName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UsageFile>(ReadAllTextWithRetry(path), SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteFile(string projectRoot, string accountName, UsageFile file)
    {
        var path = AccountStoragePaths.ProxyUsagePath(projectRoot, accountName);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
    }

    private static string ReadAllTextWithRetry(string path)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                Thread.Sleep(40 * attempt);
            }
        }
    }
}
