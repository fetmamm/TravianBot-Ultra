using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed class JsonQueueStore : IQueueStore
{
    private readonly string _queuePath;
    private readonly string _lockPath;
    private readonly object _sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonQueueStore(string queuePath)
    {
        _queuePath = queuePath;
        _lockPath = $"{queuePath}.lock";
    }

    public IReadOnlyList<QueueItem> GetAll()
    {
        lock (_sync)
        {
            return LoadMutable()
                .Select(Clone)
                .ToList();
        }
    }

    public QueueItem Add(string taskName, Dictionary<string, string>? payload, int priority, int maxRetries)
    {
        if (!TaskCatalog.IsAllowed(taskName))
        {
            throw new InvalidOperationException($"Task '{taskName}' is not allowed.");
        }

        if (maxRetries < 0)
        {
            throw new InvalidOperationException("Max retries must be >= 0.");
        }

        lock (_sync)
        {
            var items = LoadMutable();
            var item = CreateQueueItem(taskName, null, payload, priority, maxRetries, isRuntimeOnly: false);
            items.Add(item);
            SaveMutable(items);
            return Clone(item);
        }
    }

    public QueueItem AddRuntime(string taskName, string displayName, Dictionary<string, string>? payload, int priority, int maxRetries)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            throw new InvalidOperationException("Runtime task name is required.");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("Runtime display name is required.");
        }

        if (maxRetries < 0)
        {
            throw new InvalidOperationException("Max retries must be >= 0.");
        }

        lock (_sync)
        {
            var items = LoadMutable();
            var item = CreateQueueItem(taskName, displayName.Trim(), payload, priority, maxRetries, isRuntimeOnly: true);
            items.Add(item);
            SaveMutable(items);
            return Clone(item);
        }
    }

    public bool Remove(Guid id)
    {
        lock (_sync)
        {
            var items = LoadMutable();
            var removed = items.RemoveAll(item => item.Id == id) > 0;
            if (removed)
            {
                SaveMutable(items);
            }

            return removed;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            SaveMutable(new List<QueueItem>());
        }
    }

    public bool MoveUp(Guid id)
    {
        lock (_sync)
        {
            var items = LoadMutable();
            var group = items
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.CreatedAt)
                .Where(item => item.Group == items.FirstOrDefault(entry => entry.Id == id)?.Group)
                .ToList();

            var index = group.FindIndex(item => item.Id == id);
            if (index <= 0)
            {
                return false;
            }

            var current = group[index];
            var previous = group[index - 1];
            if (current.Priority != previous.Priority)
            {
                return false;
            }

            var createdAt = current.CreatedAt;
            current.CreatedAt = previous.CreatedAt;
            previous.CreatedAt = createdAt;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            previous.UpdatedAt = DateTimeOffset.UtcNow;
            SaveMutable(items);
            return true;
        }
    }

    public bool MoveDown(Guid id)
    {
        lock (_sync)
        {
            var items = LoadMutable();
            var group = items
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.CreatedAt)
                .Where(item => item.Group == items.FirstOrDefault(entry => entry.Id == id)?.Group)
                .ToList();

            var index = group.FindIndex(item => item.Id == id);
            if (index < 0 || index >= group.Count - 1)
            {
                return false;
            }

            var current = group[index];
            var next = group[index + 1];
            if (current.Priority != next.Priority)
            {
                return false;
            }

            var createdAt = current.CreatedAt;
            current.CreatedAt = next.CreatedAt;
            next.CreatedAt = createdAt;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            next.UpdatedAt = DateTimeOffset.UtcNow;
            SaveMutable(items);
            return true;
        }
    }

    public bool Pause(Guid id)
    {
        return Update(id, item =>
        {
            if (item.Status != QueueStatus.Pending)
            {
                return false;
            }

            item.Status = QueueStatus.Paused;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        });
    }

    public bool Resume(Guid id)
    {
        return Update(id, item =>
        {
            if (item.Status != QueueStatus.Paused)
            {
                return false;
            }

            item.Status = QueueStatus.Pending;
            item.NextAttemptAt = DateTimeOffset.UtcNow;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        });
    }

    public bool Retry(Guid id)
    {
        return Update(id, item =>
        {
            if (item.Status is not (QueueStatus.Failed or QueueStatus.Paused))
            {
                return false;
            }

            item.Status = QueueStatus.Pending;
            item.Retries = 0;
            item.NextAttemptAt = DateTimeOffset.UtcNow;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        });
    }

    public bool MarkRunning(Guid id)
    {
        return Update(id, item =>
        {
            if (item.Status != QueueStatus.Pending)
            {
                return false;
            }

            item.Status = QueueStatus.Running;
            item.NextAttemptAt = DateTimeOffset.UtcNow;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        });
    }

    public bool MarkSucceeded(Guid id)
    {
        return Update(id, item =>
        {
            if (item.Status != QueueStatus.Running)
            {
                return false;
            }

            item.Status = QueueStatus.Succeeded;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        });
    }

    public bool MarkCanceled(Guid id)
    {
        return Update(id, item =>
        {
            if (item.Status != QueueStatus.Running)
            {
                return false;
            }

            item.Status = QueueStatus.Canceled;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        });
    }

    public bool MarkDeferred(Guid id, TimeSpan delay)
    {
        return Update(id, item =>
        {
            if (item.Status != QueueStatus.Running)
            {
                return false;
            }

            var effectiveDelay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
            item.Status = QueueStatus.Pending;
            item.NextAttemptAt = DateTimeOffset.UtcNow.Add(effectiveDelay);
            item.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        });
    }

    public bool MarkExecutionFailed(Guid id)
    {
        return Update(id, item =>
        {
            if (item.Status != QueueStatus.Running)
            {
                return false;
            }

            if (item.IsRuntimeOnly)
            {
                item.Retries += 1;
                item.Status = QueueStatus.Failed;
                item.NextAttemptAt = DateTimeOffset.UtcNow;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                return true;
            }

            item.Retries += 1;
            var failedPermanently = item.Retries > item.MaxRetries;
            item.Status = failedPermanently
                ? QueueStatus.Failed
                : QueueStatus.Pending;
            item.NextAttemptAt = failedPermanently
                ? DateTimeOffset.UtcNow
                : DateTimeOffset.UtcNow.Add(ComputeRetryBackoff(item.Retries));
            item.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        });
    }

    private bool Update(Guid id, Func<QueueItem, bool> update)
    {
        lock (_sync)
        {
            var items = LoadMutable();
            var item = items.FirstOrDefault(entry => entry.Id == id);
            if (item is null)
            {
                return false;
            }

            var changed = update(item);
            if (changed)
            {
                SaveMutable(items);
            }

            return changed;
        }
    }

    private List<QueueItem> LoadMutable()
    {
        EnsureFileExists();
        return WithFileLock(() =>
        {
            var raw = File.ReadAllText(_queuePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<QueueItem>();
            }

            try
            {
                var items = JsonSerializer.Deserialize<List<QueueItem>>(raw, JsonOptions) ?? [];
                foreach (var item in items.Where(item => item is not null))
                {
                    item!.Group = QueueGroupCatalog.ResolveGroup(item.TaskName);
                }

                return items.Where(item => item is not null).ToList()!;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Queue file '{_queuePath}' is invalid JSON. Fix or reset the file.",
                    ex);
            }
        });
    }

    private void SaveMutable(List<QueueItem> items)
    {
        EnsureFileExists();
        var directory = Path.GetDirectoryName(_queuePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Queue path is invalid.");
        }

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(_queuePath)}.tmp");
        WithFileLock(() =>
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Could not remove stale queue temp file '{tempPath}'.", ex);
                }
            }

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, items, JsonOptions);
            }

            File.Move(tempPath, _queuePath, overwrite: true);
        });
    }

    private void EnsureFileExists()
    {
        var directory = Path.GetDirectoryName(_queuePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Queue path is invalid.");
        }

        Directory.CreateDirectory(directory);
        if (!File.Exists(_queuePath))
        {
            File.WriteAllText(_queuePath, "[]");
        }
        if (!File.Exists(_lockPath))
        {
            using var _ = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
    }

    private T WithFileLock<T>(Func<T> action)
    {
        using var lockStream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return action();
    }

    private void WithFileLock(Action action)
    {
        using var lockStream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        action();
    }

    private static QueueItem Clone(QueueItem source)
    {
        return new QueueItem
        {
            Id = source.Id,
            TaskName = source.TaskName,
            DisplayName = source.DisplayName,
            Group = source.Group,
            Payload = new Dictionary<string, string>(source.Payload, StringComparer.OrdinalIgnoreCase),
            Priority = source.Priority,
            Status = source.Status,
            Retries = source.Retries,
            MaxRetries = source.MaxRetries,
            IsRuntimeOnly = source.IsRuntimeOnly,
            NextAttemptAt = source.NextAttemptAt,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
        };
    }

    private static QueueItem CreateQueueItem(
        string taskName,
        string? displayName,
        Dictionary<string, string>? payload,
        int priority,
        int maxRetries,
        bool isRuntimeOnly)
    {
        var now = DateTimeOffset.UtcNow;
        return new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = taskName.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            Group = QueueGroupCatalog.ResolveGroup(taskName),
            Payload = payload is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(payload, StringComparer.OrdinalIgnoreCase),
            Priority = priority,
            Status = QueueStatus.Pending,
            Retries = 0,
            MaxRetries = maxRetries,
            IsRuntimeOnly = isRuntimeOnly,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static TimeSpan ComputeRetryBackoff(int retries)
    {
        var boundedRetries = Math.Max(1, Math.Min(retries, 6));
        var baseSeconds = 5 * (int)Math.Pow(2, boundedRetries);
        var jitterMs = Random.Shared.Next(200, 1800);
        return TimeSpan.FromSeconds(baseSeconds) + TimeSpan.FromMilliseconds(jitterMs);
    }
}
