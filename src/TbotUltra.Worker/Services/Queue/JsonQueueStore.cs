using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed class JsonQueueStore : IQueueStore
{
    // The queue path is resolved per operation so it can follow the active account at runtime (the
    // Desktop app points this at config/accounts/<account>/queue.json).
    //
    // Reads are served from an in-memory cache so the hot UI path (per-second GetAll calls for the
    // dashboard/Next-task) does not hit disk under the file lock and contend with the worker thread.
    // The cache is keyed by the resolved path: switching account changes the path, which invalidates
    // the cache automatically (a stale cache for a different account is never returned). Every write
    // still persists to disk AND refreshes the cache, so the file stays authoritative and closing the
    // app loses nothing. The cache holds defensive clones; callers get their own clones too.
    // Assumes a single process owns the file (true for the Desktop app); an external editor changing
    // queue.json while the app runs would not be observed until the next account switch.
    private readonly Func<string> _queuePathProvider;
    private readonly Action<string>? _log;
    private readonly object _sync = new();
    private List<QueueItem>? _cache;
    private string? _cachePath;

    private string _queuePath => _queuePathProvider();
    private string _lockPath => $"{_queuePath}.lock";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // Fixed-path constructor (Worker DI, tests). Delegates to the provider form.
    public JsonQueueStore(string queuePath, Action<string>? log = null)
        : this(() => queuePath, log)
    {
    }

    public JsonQueueStore(Func<string> queuePathProvider, Action<string>? log = null)
    {
        _queuePathProvider = queuePathProvider ?? throw new ArgumentNullException(nameof(queuePathProvider));
        _log = log;
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

    public bool UpdateDeferred(Guid id, Dictionary<string, string>? payload, TimeSpan? delay = null)
    {
        return Update(id, item =>
        {
            if (item.Status != QueueStatus.Pending)
            {
                return false;
            }

            if (payload is not null)
            {
                item.Payload = new Dictionary<string, string>(payload, StringComparer.OrdinalIgnoreCase);
            }

            if (delay.HasValue)
            {
                var effectiveDelay = delay.Value < TimeSpan.Zero ? TimeSpan.Zero : delay.Value;
                item.NextAttemptAt = DateTimeOffset.UtcNow.Add(effectiveDelay);
            }

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

    // Recovered items are deferred briefly instead of retried immediately: the crash may have hit
    // AFTER the browser action (troops queued, attack sent) but BEFORE the defer was persisted, so a
    // state-changing task could otherwise run twice back-to-back. The delay lets the post-login
    // status reads land first, and tasks that verify live state (build_troops queue scan,
    // construction queue read) then see the already-applied work and defer normally.
    private static readonly TimeSpan RecoveredRunningItemDefer = TimeSpan.FromSeconds(120);

    // Resets items stranded in Running (e.g. the process crashed mid-execution) back to Pending so
    // they are retried instead of stuck forever. Only safe to call at startup, before any execution
    // begins — a Running item found then necessarily belongs to a previous, dead session.
    public int ResetOrphanedRunningItems()
    {
        lock (_sync)
        {
            var items = LoadMutable();
            var now = DateTimeOffset.UtcNow;
            var resetCount = 0;
            foreach (var item in items.Where(item => item.Status == QueueStatus.Running))
            {
                item.Status = QueueStatus.Pending;
                item.NextAttemptAt = now.Add(RecoveredRunningItemDefer);
                item.UpdatedAt = now;
                resetCount += 1;
            }

            if (resetCount > 0)
            {
                SaveMutable(items);
            }

            return resetCount;
        }
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
        // Serve from cache when it belongs to the current account's path. Return clones so the caller
        // can mutate freely without touching the cached snapshot (cache is only updated on SaveMutable).
        if (_cache is not null && string.Equals(_cachePath, _queuePath, StringComparison.OrdinalIgnoreCase))
        {
            return _cache.Select(Clone).ToList();
        }

        EnsureFileExists();
        var loaded = WithFileLock(() =>
        {
            var raw = RetryFileIo(() => File.ReadAllText(_queuePath));
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
                // Corrupt queue file (crash mid-external-edit or an OneDrive sync conflict). Throwing here
                // used to block every queue operation forever; instead quarantine the broken file for
                // inspection and continue with an empty queue so automation can keep running.
                var quarantinePath = $"{_queuePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}";
                _log?.Invoke($"[queue] Queue file '{_queuePath}' is invalid JSON ({ex.Message}). Quarantined to '{quarantinePath}'; starting with an empty queue.");
                RetryFileIo(() =>
                {
                    File.Move(_queuePath, quarantinePath, overwrite: true);
                    File.WriteAllText(_queuePath, "[]");
                    return true;
                });
                return new List<QueueItem>();
            }
        });

        _cache = loaded.Select(Clone).ToList();
        _cachePath = _queuePath;
        return loaded;
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
            RetryFileIo(() =>
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, items, JsonOptions);
                }

                File.Move(tempPath, _queuePath, overwrite: true);
                return true;
            });
        });

        // Refresh the read cache from what we just persisted so subsequent GetAll calls stay off disk.
        _cache = items.Select(Clone).ToList();
        _cachePath = _queuePath;
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
            RetryFileIo(() =>
            {
                File.WriteAllText(_queuePath, "[]");
                return true;
            });
        }
        if (!File.Exists(_lockPath))
        {
            using var _ = RetryFileIo(() => new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite));
        }
    }

    private T WithFileLock<T>(Func<T> action)
    {
        using var lockStream = RetryFileIo(() => new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        return action();
    }

    private void WithFileLock(Action action)
    {
        using var lockStream = RetryFileIo(() => new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        action();
    }

    // Transient-lock retry for all queue file I/O. The project lives under OneDrive-synced Documents,
    // where File.Move/opens intermittently fail with UnauthorizedAccessException (ERROR_ACCESS_DENIED)
    // or a sharing-violation IOException while OneDrive/antivirus briefly holds the file. Mirrors
    // AtomicFile.RetryFileIo (Desktop) and BrowserSession.ReplaceStorageStateWithRetryAsync.
    private static T RetryFileIo<T>(Func<T> action)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < maxAttempts)
            {
                Thread.Sleep(40 * attempt);
            }
        }
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
