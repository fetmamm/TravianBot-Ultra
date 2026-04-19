using Microsoft.Extensions.Options;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Services;

namespace TbotUltra.Worker;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly BotOptions _botOptions;
    private readonly BotTaskRunner _taskRunner;

    public Worker(
        ILogger<Worker> logger,
        IOptions<BotOptions> botOptions,
        BotTaskRunner taskRunner)
    {
        _logger = logger;
        _botOptions = botOptions.Value;
        _taskRunner = taskRunner;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TbotUltra.Worker started for server {ServerName}.", _botOptions.ServerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var tasks = _botOptions.LoopTasks is { Count: > 0 } configuredTasks
                ? configuredTasks
                : ["status"];

            _logger.LogInformation(
                "Worker tick. Interval={IntervalSeconds}s, tasks={Tasks}.",
                _botOptions.LoopIntervalSeconds,
                string.Join(",", tasks));

            try
            {
                await _taskRunner.ExecuteOnceAsync(
                    _botOptions,
                    message => _logger.LogInformation("{Message}", message),
                    tasksOverride: tasks,
                    accountName: null,
                    cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker tick failed while reading village status.");
            }

            await Task.Delay(TimeSpan.FromSeconds(_botOptions.LoopIntervalSeconds), stoppingToken);
        }
    }
}
