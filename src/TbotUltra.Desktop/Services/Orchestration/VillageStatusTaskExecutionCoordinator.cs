using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IVillageStatusTaskExecutionPort
{
    string? ActiveVillageKey { get; }
    string? ActiveVillageName { get; }
    string? ResolveCanonicalVillageKey(string? villageKey);
    string? GetVillageKey(QueueItem item);
    string? GetVillageName(QueueItem item);
    ValueTask DelayBeforeTaskAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask ApplyPostTaskCooldownAsync(
        QueueItem item,
        BotOptions options,
        CancellationToken cancellationToken);
    void LogRomanLoginFillOutcome(string villageKey, string villageName);
    void Log(string message);
}

internal sealed class VillageStatusTaskExecutionCoordinator(
    IVillageStatusTaskExecutionPort port,
    AutomationPassRuntime passRuntime,
    AutomationSessionRuntime sessionRuntime,
    AutomationQueueSelectionCoordinator queueSelection,
    ContinuousVillageStatusRound villageStatusRound,
    AutomationQueueItemLifecycle queueItemLifecycle)
{
    internal async ValueTask<bool> ExecuteAsync(
        BotOptions options,
        AutomationRuntimeVillage village,
        CancellationToken cancellationToken)
    {
        var villageKey = port.ResolveCanonicalVillageKey(village.Key);
        if (string.IsNullOrWhiteSpace(villageKey))
        {
            return true;
        }

        var attemptedItemIds = new HashSet<Guid>();
        var postLoginRound = villageStatusRound.LoginRoundPending;
        var shortVillageHoldApplied = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var urgent = queueSelection.SelectUrgent(options, attemptedItemIds, postLoginRound);
            var next = urgent ?? queueSelection.SelectVillageStatusItem(villageKey, attemptedItemIds);
            if (next is null)
            {
                if (postLoginRound && !shortVillageHoldApplied)
                {
                    var hold = queueSelection.PreviewVillageHold(options, attemptedItemIds, villageKey);
                    if (hold.Reason == AutomationQueueSelectionReason.ShortVillageHold
                        && hold.HoldUntil is { } holdUntil
                        && holdUntil > DateTimeOffset.UtcNow)
                    {
                        shortVillageHoldApplied = true;
                        port.Log(
                            $"[village-round] waiting up to {Math.Ceiling((holdUntil - DateTimeOffset.UtcNow).TotalSeconds)}s "
                                + $"for a soon-ready task in '{village.Name}'.");
                        var holdDelay = holdUntil - DateTimeOffset.UtcNow;
                        if (holdDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(holdDelay, cancellationToken);
                        }
                        continue;
                    }
                }

                if (postLoginRound)
                {
                    port.LogRomanLoginFillOutcome(villageKey, village.Name);
                }
                if (passRuntime.SnapshotVillageBatch(port.ActiveVillageKey).HasUrgentPreemption)
                {
                    passRuntime.CompleteUrgentPreemption(port.ActiveVillageKey);
                    port.Log($"[village-scan] urgent work complete; '{village.Name}' has no more ready work.");
                }
                return true;
            }

            attemptedItemIds.Add(next.Id);
            if (urgent is not null
                && !string.Equals(
                    port.GetVillageKey(urgent),
                    port.ActiveVillageKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                passRuntime.RecordUrgentPreemption(port.ActiveVillageKey, port.GetVillageKey(urgent));
                port.Log(
                    $"[village-scan] urgent preemption task='{urgent.TaskName}' "
                        + $"village='{port.GetVillageName(urgent) ?? "-"}'; "
                        + $"'{village.Name}' will resume afterward.");
            }
            else
            {
                port.Log(
                    $"[village-scan] reacting in '{village.Name}': "
                        + $"group={next.Group}, task={next.TaskName}.");
            }

            await port.DelayBeforeTaskAsync(options, cancellationToken);
            RecordVillageBatchAttempt(next);
            var shouldContinue = await queueItemLifecycle.ExecuteAsync(
                next,
                options,
                "[village-scan]",
                AutomationRunMode.ContinuousLoop,
                cancellationToken);
            sessionRuntime.RecordBrowserActivity(
                options.ContinuousKeepAliveEnabled,
                options.ContinuousKeepAliveMinMinutes,
                options.ContinuousKeepAliveMaxMinutes);
            if (!shouldContinue)
            {
                return false;
            }

            await port.ApplyPostTaskCooldownAsync(next, options, cancellationToken);
        }
    }

    private void RecordVillageBatchAttempt(QueueItem item)
    {
        var before = passRuntime.SnapshotVillageBatch(port.ActiveVillageKey);
        var after = passRuntime.RecordVillageAttempt(port.GetVillageKey(item), port.ActiveVillageKey);
        if (string.IsNullOrWhiteSpace(after.VillageKey))
        {
            return;
        }

        if (!string.Equals(before.VillageKey, after.VillageKey, StringComparison.OrdinalIgnoreCase)
            || before.AttemptCount == 0)
        {
            var targetName = port.GetVillageName(item) ?? port.ActiveVillageName ?? "-";
            var targetKey = port.GetVillageKey(item);
            if (!string.IsNullOrWhiteSpace(targetKey)
                && !string.Equals(targetKey, after.VillageKey, StringComparison.OrdinalIgnoreCase))
            {
                port.Log(
                    $"[village-batch] switch requested fromKey='{after.VillageKey}' "
                        + $"toVillage='{targetName}' targetKey='{targetKey}' source='village-scan'.");
            }
            else
            {
                port.Log(
                    $"[village-batch] start village='{targetName}' "
                        + $"key='{after.VillageKey}' source='village-scan'.");
            }
        }
    }
}
