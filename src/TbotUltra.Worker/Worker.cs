using Microsoft.Extensions.Options;
using TbotUltra.Core.Configuration;
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
                    catch (Exception ex)
                    {
                        _queueStore.MarkExecutionFailed(next.Id);
                        _logger.LogError(ex, "Queue item failed. Task={Task}", next.TaskName);
                    }
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
}
