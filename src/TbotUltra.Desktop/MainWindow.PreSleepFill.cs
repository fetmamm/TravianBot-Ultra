using System;
using System.Collections.Generic;
using System.Linq;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

// Pre-sleep construction fill: shortly before a session-pacing sleep, pull humanize-deferred
// construction starts forward to a random time inside the pre-sleep window so every build slot that
// CAN be filled is occupied when the sleep begins. Only items whose slot is free (humanize defer)
// are moved; resource/requirement/queue-full blocked items cannot start anyway. The rescheduled item
// carries the ConstructionPreSleepFill payload flag so the worker's humanize gate starts the build
// immediately instead of deferring again (the human pause was served by the random reschedule).
public partial class MainWindow
{
    private DateTimeOffset _lastPreSleepFillCheckUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan PreSleepFillCheckInterval = TimeSpan.FromSeconds(15);

    // Called every second from the clock tick; cheap guards first, real work at most every 15s.
    private void TickPreSleepConstructionFill()
    {
        if (_sessionPacer.Phase != SessionPacerPhase.Running
            || _sessionPacer.TimeUntilSleep is not { } untilSleep)
        {
            return;
        }

        // Automation must be running, otherwise a pulled-forward item would never execute.
        var loopRunning = _loopTask is not null && !_loopTask.IsCompleted;
        if (!loopRunning && !_autoQueueRunning)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastPreSleepFillCheckUtc < PreSleepFillCheckInterval)
        {
            return;
        }

        _lastPreSleepFillCheckUtc = now;

        try
        {
            RunPreSleepConstructionFillSweep(now, untilSleep);
        }
        catch (Exception ex)
        {
            AppendLog($"[pre-sleep-fill] sweep failed: {ex.Message}");
        }
    }

    private void RunPreSleepConstructionFillSweep(DateTimeOffset now, TimeSpan untilSleep)
    {
        // Covered by the existing "Construction start delay" toggle: the sweep only moves
        // humanize-deferred starts, which exist only while that setting is on. With the delay off,
        // builds start the moment a slot frees, so the slots fill themselves before sleep.
        var options = LoadBotOptions();
        if (!options.ConstructionHumanizeDelayEnabled)
        {
            return;
        }

        var pendingConstruction = _botService.GetQueueItemsForDisplay()
            .Where(item => item.Group == QueueGroup.Construction && item.Status == QueueStatus.Pending)
            .Where(IsQueueItemAllowedByAutomationSettings)
            .ToList();
        if (pendingConstruction.Count == 0)
        {
            return;
        }

        // Window scales with how many villages have queued construction work: each final start costs
        // roughly a village switch + navigation + retries, so more villages need a longer runway.
        var (villageCount, windowMinutes) = ResolvePreSleepFillWindow(pendingConstruction);
        if (untilSleep > TimeSpan.FromMinutes(windowMinutes))
        {
            return;
        }

        var sleepAt = now + untilSleep;
        var latestStart = sleepAt - TimeSpan.FromMinutes(PacingDefaults.PreSleepFillMarginMinutes);

        // Only humanize-deferred items: the slot is free and the item is purely waiting out the human
        // pause. Items already scheduled early enough run on their own; flagged items were moved already.
        var candidates = pendingConstruction
            .Where(ConstructionQueueState.IsConstructionHumanizeDeferred)
            .Where(item => item.NextAttemptAt > latestStart)
            .Where(item => !item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionPreSleepFill))
            .ToList();

        foreach (var item in candidates)
        {
            var runwaySeconds = Math.Max(0, (latestStart - now).TotalSeconds);
            var delay = TimeSpan.FromSeconds(Random.Shared.NextDouble() * runwaySeconds);
            var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.ConstructionPreSleepFill] = "true",
            };

            if (!_botService.PatchDeferredQueueItem(
                    item.Id,
                    new Dictionary<string, string>
                    {
                        [BotOptionPayloadKeys.ConstructionPreSleepFill] = "true",
                    },
                    null,
                    delay))
            {
                AppendLog($"[pre-sleep-fill] could not reschedule id={item.Id} task='{item.TaskName}'.");
                continue;
            }

            item.Payload = payload;
            var villageName = NormalizeVillageName(GetQueueItemVillageName(item)) ?? "-";
            AppendLog(
                $"[pre-sleep-fill] pulled construction start forward: task='{item.TaskName}' village='{villageName}' " +
                $"new start {FormatQueueServerTime(now + delay)} (sleep at {FormatQueueServerTime(sleepAt)}, " +
                $"window {windowMinutes}m for {villageCount} village(s)).");
            RequestQueueUiRefresh(item.Id);
        }
    }

    // A construction item that becomes naturally due inside the pre-sleep runway must carry the
    // override on its first execution. Otherwise it opens dorf2, creates a humanize defer beyond
    // sleep, and the periodic sweep runs the same item again seconds later.
    private void MarkDueConstructionForPreSleepFill(QueueItem item)
    {
        if (_sessionPacer.Phase != SessionPacerPhase.Running
            || _sessionPacer.TimeUntilSleep is not { } untilSleep
            || item.Group != QueueGroup.Construction
            || item.Status != QueueStatus.Pending
            || !ConstructionQueueState.UsesConstructionHumanizeStartGate(item.TaskName)
            || item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionPreSleepFill))
        {
            return;
        }

        var options = LoadBotOptions();
        if (!options.ConstructionHumanizeDelayEnabled)
        {
            return;
        }

        var pendingConstruction = _botService.GetQueueItemsForDisplay()
            .Where(candidate => candidate.Group == QueueGroup.Construction && candidate.Status == QueueStatus.Pending)
            .Where(IsQueueItemAllowedByAutomationSettings)
            .ToList();
        var (villageCount, windowMinutes) = ResolvePreSleepFillWindow(pendingConstruction);
        if (untilSleep > TimeSpan.FromMinutes(windowMinutes))
        {
            return;
        }

        var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ConstructionPreSleepFill] = "true",
        };
        if (!_botService.PatchDeferredQueueItem(
                item.Id,
                new Dictionary<string, string>
                {
                    [BotOptionPayloadKeys.ConstructionPreSleepFill] = "true",
                },
                null,
                TimeSpan.Zero))
        {
            AppendLog($"[pre-sleep-fill] could not mark due item id={item.Id} task='{item.TaskName}'.");
            return;
        }

        item.Payload = payload;
        var villageName = NormalizeVillageName(GetQueueItemVillageName(item)) ?? "-";
        AppendLog(
            $"[pre-sleep-fill] due construction marked before execution: task='{item.TaskName}' village='{villageName}' " +
            $"sleep in {untilSleep.TotalSeconds:F0}s (window {windowMinutes}m for {villageCount} village(s)).");
    }

    private (int VillageCount, int WindowMinutes) ResolvePreSleepFillWindow(IReadOnlyCollection<QueueItem> pendingConstruction)
    {
        var villageCount = Math.Max(1, pendingConstruction
            .Select(item => GetQueueItemVillageKey(item) ?? NormalizeVillageName(GetQueueItemVillageName(item)) ?? "-")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());
        var windowMinutes = Math.Clamp(
            villageCount * PacingDefaults.PreSleepFillPerVillageMinutes,
            PacingDefaults.PreSleepFillWindowMinMinutes,
            PacingDefaults.PreSleepFillWindowMaxMinutes);
        return (villageCount, windowMinutes);
    }

    // Bounded hold before an automatic sleep: if a pre-sleep fill item is due or already running, give
    // it a short chance to finish so the final build is actually clicked home before the browser closes.
    private async Task WaitBrieflyForPreSleepFillItemsAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(PacingDefaults.PreSleepFillSleepHoldMaxMinutes);
        var announced = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var now = DateTimeOffset.UtcNow;
            var hasActiveFillItem = _botService.GetQueueItemsForDisplay()
                .Where(item => item.Group == QueueGroup.Construction)
                .Where(item => item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionPreSleepFill))
                .Any(item => item.Status == QueueStatus.Running
                    || (item.Status == QueueStatus.Pending && item.NextAttemptAt <= now + TimeSpan.FromSeconds(30)));
            if (!hasActiveFillItem)
            {
                return;
            }

            if (!announced)
            {
                announced = true;
                AppendLog(
                    $"[pre-sleep-fill] delaying sleep briefly (max {PacingDefaults.PreSleepFillSleepHoldMaxMinutes}m): " +
                    "a final construction start is due or running.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        AppendLog("[pre-sleep-fill] sleep hold reached its limit; continuing with sleep.");
    }
}
