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

    public async Task<BotTaskExecutionResult> ExecuteAsync(
        BotOptions baseOptions,
        QueueItem item,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        if (!TaskCatalog.IsAllowed(item.TaskName))
        {
            log($"[queue] REJECTED item id={item.Id} task='{item.TaskName}' — task is not in the allow-list");
            throw new InvalidOperationException($"Task '{item.TaskName}' is not allowed.");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        log($"[queue] EXEC id={item.Id} group={item.Group} task='{item.TaskName}' priority={item.Priority} retries={item.Retries}/{item.MaxRetries}");
        try
        {
            var options = BotOptionsPayloadApplier.Apply(baseOptions, item.Payload);
            var result = await _taskRunner.ExecuteOnceAsync(
                options,
                log,
                tasksOverride: [item.TaskName],
                accountName: null,
                cancellationToken: cancellationToken);
            log($"[queue] DONE id={item.Id} task='{item.TaskName}' in {sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }
        catch (OperationCanceledException)
        {
            log($"[queue] CANCELED id={item.Id} task='{item.TaskName}' after {sw.Elapsed.TotalSeconds:F1}s");
            throw;
        }
        catch (TaskWaitException waitEx)
        {
            // Normal "retry later" control flow — the scheduler re-defers this item. Not a failure.
            log($"[queue] DEFER id={item.Id} task='{item.TaskName}' after {sw.Elapsed.TotalSeconds:F1}s — wait {waitEx.DelaySeconds}s");
            throw;
        }
        catch (TaskBlockedPermanentlyException blockedEx)
        {
            log($"[queue] BLOCKED id={item.Id} task='{item.TaskName}' after {sw.Elapsed.TotalSeconds:F1}s: {blockedEx.Message}");
            throw;
        }
        catch (Exception ex)
        {
            if (BrowserFailureClassifier.IsTargetCrash(ex))
            {
                log($"[queue] DEFER id={item.Id} task='{item.TaskName}' after {sw.Elapsed.TotalSeconds:F1}s — Chromium target crashed");
            }
            else
            {
                log($"[queue] FAIL id={item.Id} task='{item.TaskName}' after {sw.Elapsed.TotalSeconds:F1}s: {ex.GetType().Name}: {ex.Message}");
            }

            throw;
        }
    }
}
