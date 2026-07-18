using System;

namespace TbotUltra.Desktop;

// Runtime transition handling for the construction start delay setting shown in Settings > Construction.
// The continuous loop reloads BotOptions each iteration, so saved values apply without a restart.
public partial class MainWindow
{
    private void ApplyConstructionHumanizeToggleTransition(bool enabled)
    {
        var now = DateTimeOffset.UtcNow;
        var resetCount = 0;
        foreach (var item in _botService.GetQueueItemsForDisplay()
                     .Where(item => item.Status == TbotUltra.Worker.Domain.QueueStatus.Pending)
                     .Where(item => IsConstructionQueueTask(item.TaskName)))
        {
            var reset = Desktop.Services.ConstructionQueueState.ResolveHumanizeToggleReset(item, now);
            var keysToRemove = item.Payload.Keys
                .Where(key => !reset.Payload.ContainsKey(key))
                .ToArray();
            if (!reset.Changed || !_botService.PatchDeferredQueueItem(item.Id, null, keysToRemove, reset.Delay))
            {
                continue;
            }

            item.Payload = reset.Payload;
            resetCount++;
        }

        AppendLog($"[construction-humanize] {(enabled ? "enabled" : "disabled")}; cleared stale pacing state from {resetCount} pending row(s).");
        Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
        RefreshQueueUi();
    }
}
