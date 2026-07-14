namespace TbotUltra.Desktop.Services.Orchestration;

public sealed class BackgroundTaskTracker : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _stopping;
    private bool _disposed;

    public bool Run(Func<CancellationToken, Task> operation, Action<Exception>? logError = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_gate)
        {
            if (_stopping || _disposed)
            {
                return false;
            }

            var task = Task.Run(async () =>
            {
                try
                {
                    await operation(_shutdownCts.Token);
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logError?.Invoke(ex);
                }
            });

            TrackUnsafe(task);
            return true;
        }
    }

    public bool Track(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_gate)
        {
            if (_stopping || _disposed)
            {
                return false;
            }

            TrackUnsafe(task);
            return true;
        }
    }

    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        Task[] tasks;
        lock (_gate)
        {
            if (!_stopping)
            {
                _stopping = true;
                _shutdownCts.Cancel();
            }

            tasks = _tasks.ToArray();
        }

        if (tasks.Length == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            // Tracked tasks are observed here. Individual jobs own their error logging.
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _stopping = true;
            _disposed = true;
            _shutdownCts.Cancel();
            _shutdownCts.Dispose();
            _tasks.Clear();
        }
    }

    private void TrackUnsafe(Task task)
    {
        _tasks.Add(task);
        _ = task.ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception;
                lock (_gate)
                {
                    _tasks.Remove(completedTask);
                }
            },
            // Cleanup must run even when the tracked operation's own token was canceled.
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
