# Fix: Auto Collect Tasks blocked behind Construction queue

## Context

When the continuous auto-loop runs with **Auto Collect Tasks** (and/or **Auto Collect Daily
Rewards**) enabled, the collect actions are *sometimes* not performed for hours. The user suspected
Collect Tasks ends up in the queue under the **Construction** group — and that this is a design flaw.

This is confirmed by code and the supplied log.

### Root cause (confirmed)

- `collect_tasks` and `collect_daily_quests` are declared with `TaskGroup.Construction` in
  `src/TbotUltra.Core/Tasks/TaskCatalog.cs:25-26`, so `QueueGroupCatalog.ResolveGroup` maps them to
  `QueueGroup.Construction`.
- In the continuous loop, `SelectNextQueueItemForContinuousLoop`
  (`src/TbotUltra.Desktop/MainWindow.ContinuousLoop.cs:359`) iterates only over **enabled** automation
  groups, and for the Construction group it calls `IsConstructionGroupReady()` first. While a building
  timer is running (`_buildQueueRemainingSeconds > 0`, non-Travian-Plus), that returns `false` and the
  **entire Construction group is skipped** — including the lightweight, runtime-only collect task.
- The collect task therefore waits until the construction queue is ready, which can be many hours.

### Log proof (`logs/TbotUltra_Log_20260602_215947.txt`)

- `22:02:44` line 395 — `Tasks: claimable rewards detected — queued collect_tasks.`
- For ~5 hours: repeated `group=Construction skipped (construction group not ready)`
  (lines 158, 252, 270, 331, 388, 403, 422, 435, …).
- `03:06:23` line 14772 — `[LOOP 453] PICK group=Construction, task=collect_tasks` → ran in 3s once
  construction became ready.
- `03:19:21/22` lines 15480-15487 — another collect ran in ~1s because construction *happened* to be
  ready. This is why it works "sometimes."

### Secondary latent bug

Because they live in `QueueGroup.Construction`, the collect tasks are only ever picked when the
**Construction** automation-loop group toggle is enabled — even though they are actually gated by their
own settings (`AutoCollectTasksEnabled` / `AutoCollectDailyQuestsEnabled`). Turning the Construction
group off silently disables auto-collect.

### Why this approach

Selected approach: **always-on utility pick** (keep the tasks in their current group; change only the
continuous-loop selector). This is the minimal, behavior-preserving change in line with
`docs/ENGINEERING_NOTES.md` §9 ("ingen omskrivning … stegvis, beteendebevarande"). The enqueue side is
already correctly gated by the per-feature settings and de-duplicated
(`MainWindow.Resources.Snapshot.cs` `TryQueueAutoCollect…Async`), so once an item is queued it is always
safe and desirable to run it promptly. The AutoQueue path is unaffected (it uses the store's
`SelectNextQueueItem`, which is not group-gated).

## Status

- **Implemented 2026-06-03** in `src/TbotUltra.Desktop/MainWindow.ContinuousLoop.cs`.
- Added `IsAlwaysOnUtilityTask(...)` for `collect_tasks` and `collect_daily_quests`.
- `SelectNextQueueItemForContinuousLoop()` now picks ready collect utility tasks before checking
  enabled automation-loop groups or `IsConstructionGroupReady()`.
- Existing group assignment remains unchanged: both tasks still display/store as `Construction`.
- Focused unit test not added: the selector is private and depends on `MainWindow` UI state; avoided
  widening visibility for this narrow scheduling fix.

## Change

All edits are in **one method file**: `src/TbotUltra.Desktop/MainWindow.ContinuousLoop.cs`.

1. Add a small helper to identify the always-on utility tasks:

   ```csharp
   private static bool IsAlwaysOnUtilityTask(string? taskName) =>
       string.Equals(taskName, "collect_tasks", StringComparison.OrdinalIgnoreCase)
       || string.Equals(taskName, "collect_daily_quests", StringComparison.OrdinalIgnoreCase);
   ```

2. At the **top of `SelectNextQueueItemForContinuousLoop`** (before the
   `GetContinuousLoopEnabledGroupsInOrder()` early-return and before the group loop), scan the queue for
   a ready utility item and return it first:
   - Filter: `IsAlwaysOnUtilityTask(item.TaskName)` AND `item.Status == QueueStatus.Pending` AND
     `item.NextAttemptAt <= now` (respect retry backoff).
   - Order with the existing `OrderContinuousLoopGroupItems(...)` helper for stable FIFO/priority
     behavior, take the first.
   - This makes collect tasks eligible regardless of `IsConstructionGroupReady()` and regardless of
     which automation-loop groups are enabled, while still respecting the loop's serial execution
     (one item per tick) and the per-feature enqueue gating.

   This sits naturally with the existing pattern of returning the first ready item; reuse
   `_botService.GetQueueItemsForDisplay()` (already fetched as `queueItems`) and the existing `now`.

3. Keep the existing group loop unchanged — collect items, being utility-picked first, will simply never
   reach the Construction branch. (They remain in `QueueGroup.Construction` for storage/display; the
   misleading label is cosmetic and out of scope for this minimal fix.)

### Notes / edge cases

- **Wake-up:** the loop's `WaitForNextContinuousLoopPassAsync` already polls
  `SelectNextQueueItemForContinuousLoop() is not null` each slice, so a utility item queued by the 16s
  background refresh (`HandleResourceSnapshotRefreshTickAsync`) ends the wait early — no extra wiring
  needed.
- **No starvation of construction:** utility items are rare (only when claimable rewards are detected,
  de-duplicated, `maxRetries: 1`) and complete in ~1-3s.
- **Logging:** the existing `[LOOP n] PICK group=…, task=…` line will now show
  `group=Construction, task=collect_tasks` even while construction is busy — acceptable and accurate.
  Optionally adjust the verbose skip messaging if desired (not required).

## Verification

Implementation note 2026-06-03: source changes are in place and verified with:

- `dotnet test src\TbotUltra.Worker.Tests\TbotUltra.Worker.Tests.csproj --filter "QueueStoreAndSchedulerTests|TaskCatalog"`
- `dotnet build TbotUltra.sln /p:UseAppHost=false`

1. `dotnet build TbotUltra.sln` and `dotnet test` (queue/scheduler + task-catalog tests still pass; no
   group reassignment, so existing `InlineData` for `collect_*` → `Construction` remain valid).
2. Add/adjust a focused unit test if feasible: with an active construction build timer (Construction
   group "not ready") plus a pending `collect_tasks` runtime item, assert the continuous-loop selector
   returns the `collect_tasks` item. (If `SelectNextQueueItemForContinuousLoop` is not directly
   reachable from tests, rely on live verification below.)
3. Live verification on the official server:
   - Enable **Auto Collect Tasks** and start a building upgrade so the Construction queue is busy.
   - Trigger a claimable task reward, run the continuous loop, and confirm the log shows
     `PICK … task=collect_tasks` and `OK … queue:collect_tasks` **without** waiting for the build timer.
   - Repeat with **Auto Collect Daily Rewards** and with the **Construction** automation-loop group
     toggled **off** — confirm collect still runs.
