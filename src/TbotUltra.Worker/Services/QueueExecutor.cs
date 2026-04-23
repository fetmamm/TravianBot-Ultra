using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed class QueueExecutor
{
    private readonly BotTaskRunner _taskRunner;

    public QueueExecutor(BotTaskRunner taskRunner)
    {
        _taskRunner = taskRunner;
    }

    public async Task ExecuteAsync(
        BotOptions baseOptions,
        QueueItem item,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        if (!TaskCatalog.IsAllowed(item.TaskName))
        {
            throw new InvalidOperationException($"Task '{item.TaskName}' is not allowed.");
        }

        var options = BotOptionsPayloadApplier.Apply(baseOptions, item.Payload);
        await _taskRunner.ExecuteOnceAsync(
            options,
            log,
            tasksOverride: [item.TaskName],
            accountName: null,
            cancellationToken: cancellationToken);
    }
}
