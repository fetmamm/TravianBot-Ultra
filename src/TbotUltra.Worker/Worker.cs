using Microsoft.Extensions.Options;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Worker;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly BotOptions _botOptions;
    private readonly BotTaskRunner _taskRunner;
    private readonly IQueueStore _queueStore;
    private readonly IQueueScheduler _queueScheduler;
    private readonly QueueExecutor _queueExecutor;

    public Worker(
        ILogger<Worker> logger,
        IOptions<BotOptions> botOptions,
        BotTaskRunner taskRunner,
        IQueueStore queueStore,
        IQueueScheduler queueScheduler,
        QueueExecutor queueExecutor)
    {
        _logger = logger;
        _botOptions = botOptions.Value;
        _taskRunner = taskRunner;
        _queueStore = queueStore;
        _queueScheduler = queueScheduler;
        _queueExecutor = queueExecutor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TbotUltra.Worker started for server {ServerName}.", _botOptions.ServerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var items = _queueStore.GetAll();
                var next = _queueScheduler.SelectNext(items);
                if (next is not null)
                {
                    _logger.LogInformation(
                        "Worker queue tick. Task={Task}, Priority={Priority}, Retries={Retries}/{MaxRetries}.",
                        next.TaskName,
                        next.Priority,
                        next.Retries,
                        next.MaxRetries);

                    _queueStore.MarkRunning(next.Id);
                    try
                    {
                        await _queueExecutor.ExecuteAsync(
                            _botOptions,
                            next,
                            message => _logger.LogInformation("{Message}", message),
                            stoppingToken);
                        _queueStore.MarkSucceeded(next.Id);
                    }
                    catch (TaskWaitException wait)
                    {
                        // Transient block (e.g. waiting for resources, build queue full). Defer
                        // without bumping Retries — the task should NOT eventually be marked Failed
                        // just because a slow resource accumulation took many ticks.
                        var delay = ClampDeferDelay(wait.DelaySeconds);
                        _queueStore.MarkDeferred(next.Id, delay);
                        _logger.LogInformation(
                            "Queue item deferred. Task={Task}, Delay={DelaySeconds}s, Reason={Reason}",
                            next.TaskName,
                            (int)delay.TotalSeconds,
                            wait.Message);
                    }
                    catch (TaskBlockedPermanentlyException permanent)
                    {
                        _queueStore.MarkExecutionFailed(next.Id);
                        _logger.LogWarning(
                            "Queue item permanently blocked, marking failed. Task={Task}, Reason={Reason}",
                            next.TaskName,
                            permanent.Message);
                    }
                    catch (Exception ex)
                    {
                        _queueStore.MarkExecutionFailed(next.Id);
                        _logger.LogError(ex, "Queue item failed. Task={Task}", next.TaskName);
                    }
                }
                else if (HasPendingQueueWork(items))
                {
                    // Queue has work, just not ready yet (deferred). Don't run LoopTasks in the
                    // gap — the user's intent for a queued upgrade with insufficient resources is
                    // "wait for that", not "go off and do hero adventures while we wait".
                    _logger.LogDebug(
                        "Worker idle tick: queue has deferred work, skipping fallback LoopTasks.");
                }
                else
                {
                    var tasks = _botOptions.LoopTasks is { Count: > 0 } configuredTasks
                        ? configuredTasks
                        : ["status"];

                    _logger.LogInformation(
                        "Worker fallback tick. Interval={IntervalSeconds}s, tasks={Tasks}.",
                        _botOptions.LoopIntervalSeconds,
                        string.Join(",", tasks));

                    await _taskRunner.ExecuteOnceAsync(
                        _botOptions,
                        message => _logger.LogInformation("{Message}", message),
                        tasksOverride: tasks,
                        accountName: null,
                        cancellationToken: stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker tick failed while reading village status.");
            }

            await Task.Delay(TimeSpan.FromSeconds(_botOptions.LoopIntervalSeconds), stoppingToken);
        }
    }

    // Cap defer delays: too short and we burn ticks on the same blocked task; too long and a
    // misparse strands the task for hours. 60s lower bound matches the typical loop interval.
    private static readonly TimeSpan MinDeferDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxDeferDelay = TimeSpan.FromMinutes(30);

    private static TimeSpan ClampDeferDelay(int seconds)
    {
        var requested = TimeSpan.FromSeconds(Math.Max(1, seconds));
        if (requested < MinDeferDelay) return MinDeferDelay;
        if (requested > MaxDeferDelay) return MaxDeferDelay;
        return requested;
    }

    private static bool HasPendingQueueWork(IReadOnlyList<QueueItem> items)
    {
        foreach (var item in items)
        {
            if (!item.IsRuntimeOnly && item.Status == QueueStatus.Pending)
            {
                return true;
            }
        }

        return false;
    }
}
