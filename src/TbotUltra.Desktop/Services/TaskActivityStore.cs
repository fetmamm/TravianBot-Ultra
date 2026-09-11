using System.IO;
using System.Text.Json;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Desktop.Services;

internal sealed record TaskActivityRecord(
    string TaskName,
    DateTimeOffset OccurredAtUtc);

internal sealed record TaskActivitySummary(
    string TaskName,
    int Runs,
    DateTimeOffset LastRunUtc,
    int PeakLocalHour);

/// <summary>
/// Persists explicit task activity events independently from queue state. A queue item may perform
/// several verified construction steps, remain pending, or be removed without changing this journal.
/// </summary>
internal static class TaskActivityStore
{
    private const int HistoryDaysToKeep = 180;
    private const int MaximumEntries = 50_000;
    private static readonly object FileIoLock = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class ActivityFile
    {
        public List<TaskActivityRecord> Entries { get; set; } = new();
    }

    public static IReadOnlyList<TaskActivityRecord> Load(string projectRoot, string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return [];
        }

        lock (FileIoLock)
        {
            return ReadFileOrNull(projectRoot, accountName)?.Entries?
                .Where(IsValid)
                .OrderBy(entry => entry.OccurredAtUtc)
                .ToList()
                ?? [];
        }
    }

    public static void Record(
        string projectRoot,
        string? accountName,
        string? taskName,
        DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(taskName))
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFileOrNull(projectRoot, accountName) ?? new ActivityFile();
            file.Entries ??= new List<TaskActivityRecord>();
            file.Entries.Add(new TaskActivityRecord(taskName.Trim(), occurredAtUtc.ToUniversalTime()));

            var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-HistoryDaysToKeep);
            file.Entries = file.Entries
                .Where(entry => IsValid(entry) && entry.OccurredAtUtc >= cutoffUtc)
                .OrderBy(entry => entry.OccurredAtUtc)
                .TakeLast(MaximumEntries)
                .ToList();

            var path = AccountStoragePaths.TaskActivityHistoryPath(projectRoot, accountName);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
        }
    }

    private static bool IsValid(TaskActivityRecord entry) =>
        !string.IsNullOrWhiteSpace(entry.TaskName) && entry.OccurredAtUtc != default;

    private static ActivityFile? ReadFileOrNull(string projectRoot, string accountName)
    {
        var path = AccountStoragePaths.TaskActivityHistoryPath(projectRoot, accountName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<ActivityFile>(stream, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }
}

internal static class TaskActivityStatistics
{
    public static IReadOnlyList<TaskActivitySummary> Build(
        IReadOnlyList<TaskActivityRecord> records,
        DateTimeOffset cutoffUtc)
    {
        return records
            .Where(record => record.OccurredAtUtc >= cutoffUtc)
            .GroupBy(record => record.TaskName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(record => record.OccurredAtUtc).ToList();
                var peakHour = ordered
                    .GroupBy(record => record.OccurredAtUtc.ToLocalTime().Hour)
                    .OrderByDescending(hourGroup => hourGroup.Count())
                    .ThenBy(hourGroup => hourGroup.Key)
                    .First();
                return new TaskActivitySummary(
                    group.Key,
                    ordered.Count,
                    ordered[0].OccurredAtUtc,
                    peakHour.Key);
            })
            .OrderByDescending(summary => summary.Runs)
            .ThenBy(summary => summary.TaskName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
