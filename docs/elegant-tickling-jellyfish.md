# Construction: stop defer-churn (UI blink) + no navigation at timer end

## Context

Log `logs/TbotUltra_Log_20260712_170508.txt` shows two construction problems:

**Problem 1 — UI blink / defer-churn (17:10:35–17:11:09):** `upgrade_all_resources_to_level` (srw) deferred "build queue full" with retryAt 17:31:46, but was re-picked and re-executed **11 times in ~35 s** (LOOP 2–12). Each cycle flips the queue icons (pending→running→deferred), which is the yellow/green/gray blink.

**Problem 2 — navigates at exact timer end (17:12:45):** pha's queue-full retryAt = the moment the first construction finished. The task woke exactly then, navigated dorf1→dorf2, and only **then** computed the humanize delay (51 s) and deferred. So the bot visibly reacts at the exact second the timer ends — defeating the humanize delay's purpose. 51 s later it navigates again and builds.

## Root causes

### Problem 1
Trace: `SelectNextConstructionQueueItem` ([MainWindow.ContinuousLoop.cs:1215](src/TbotUltra.Desktop/MainWindow.ContinuousLoop.cs:1215)) → `ResolveConstructionQueueAvailability` (line 1346) reads **`_villageStatusCacheByName["srw"]`** → `ConstructionQueueState.ResolveAvailability`. The cached srw status was **stale from the previous session** (old construction timers, expiring ~17:11:09), so it reported a free slot → `Available`.

`ConstructionQueueSelector.SelectNext` ([ConstructionQueueSelector.cs:45](src/TbotUltra.Desktop/Services/ConstructionQueueSelector.cs:45)): a queue-full-deferred item + availability `Available` ⇒ `shouldValidateNow` ⇒ returns the item with `ForcedLiveValidation` (by design: worker re-checks live once). Worker confirms full, defers again — **but nothing corrects the stale cache**, so the very next pick force-validates again. Loop.

Why the cache never updated: the post-defer refresh at [MainWindow.QueueExecution.cs:856](src/TbotUltra.Desktop/MainWindow.QueueExecution.cs:856) is gated on `IsBuildingMutationTask` (upgrade_building_to_level/max, construct, demolish) — **resource-upgrade tasks are excluded**, so `RefreshConstructionStatusAfterDeferAsync` (which writes fresh `ActiveConstructions` into the village cache) never ran. Churn only stopped when the stale cached timers expired (~17:11:09), flipping availability to `Unknown` (item has current classification version ⇒ no legacy validation ⇒ blocker holds).

### Problem 2
Queue-full defer wait ([TravianClient.Buildings.cs:3968](src/TbotUltra.Worker/Services/Automation/Buildings/TravianClient.Buildings.cs:3968)) = shortest remaining construction + 1 s. The humanize gate `MaybeGetConstructionHumanizeDeferAsync` (line 3985) only runs **inside the task, after village-switch + dorf2 navigation**, because it re-reads slot status from the page. But at queue-full defer time the worker has **already read all active constructions' remaining timers** (`status.Active[*].TimeLeftSeconds`) — everything needed to compute the humanize delay up front, no navigation required.

## Fix 1 — feed the live queue-full result back into the cache

**File:** `src/TbotUltra.Desktop/MainWindow.QueueExecution.cs` (~line 856)

Change the refresh gate from `IsBuildingMutationTask(item.TaskName)` to the existing `NeedsConstructionStatusRefresh(item.TaskName)` ([MainWindow.DeferredRefresh.cs:491](src/TbotUltra.Desktop/MainWindow.DeferredRefresh.cs:491) = resource upgrades + building mutations). The browser is already on the deferring village's dorf1, so `RefreshConstructionStatusAfterDeferAsync` is a cheap current-page read (no navigation) that writes the real `ActiveConstructions` into `_villageStatusCacheByName` → `ResolveAvailability` returns `Full` → selector holds the blocker. Result: **one** live validation (by design), then quiet until retryAt.

Update the comment there to note it also fixes stale-cache availability churn for resource upgrades.

## Fix 2 — bake the humanize delay into the queue-full wait (no timer-edge navigation)

**Worker — `src/TbotUltra.Worker/Services/Automation/Features/../Buildings/TravianClient.Buildings.cs`**, in the queue-full defer path (~lines 3944–3970), when `_config.ConstructionHumanizeDelayEnabled`:

1. Compute the post-free humanize delay from the timers already read (same category filter as line 3956):
   - ≥2 running in category: after the shortest (`relevantWait`) frees, reference = `secondShortestRemaining - relevantWait`; delay = `min(ref * pct, cap)` (same `ConstructionHumanizeQueuePercentMin/Max` + cap as the existing percent case).
   - exactly 1 running: no-plus case — random `ConstructionHumanizeNoPlusMinMinutes..MaxMinutes`.
2. Store `_session.ConstructionHumanizeUntilBySlot[slotKey] = now + relevantWait + delay` (same `villageToken:kind:slotId` key the gate uses). The existing gate branch at line 4004 then returns null at retry (delay already elapsed) → build proceeds immediately after the single navigation.
3. Return `queue_wait_seconds = relevantWait + 1 + delay` and append `queue_humanize_extra_seconds=<delay>` to the message (emit `=0` when humanize is disabled so stale payload values get overwritten). Log the pre-applied delay (`[construction-humanize] ... pre-applied to queue-full wait`).

**Desktop — keep the resync from stripping the delay:**
- Add `queue_humanize_extra_seconds` as a payload key (`BotOptionPayloadKeys`) and to `DeferredUpgradePayloadKeys` so `TryExtractDeferredUpgradePayload` ([MainWindow.DeferredRefresh.cs:38](src/TbotUltra.Desktop/MainWindow.DeferredRefresh.cs:38)) persists it on the item.
- In `RefreshDeferredConstructionWaitsAsync`'s queue-full sync ([MainWindow.DeferredRefresh.cs:114-164](src/TbotUltra.Desktop/MainWindow.DeferredRefresh.cs:114)): add the item's stored extra seconds to `queueFullDelay` before the compare/update, so the resync targets `constructionEnd + humanizeDelay` instead of rewriting retryAt back to the bare construction end.
- In the release branch (`queueFullDelay == TimeSpan.Zero`, line 118): skip the immediate release for an item whose payload extra > 0 and whose own `NextAttemptAt` is still in the future — its retryAt already contains the humanize window; releasing to Zero would recreate the timer-edge wake.

Net behavior: one defer covers "construction ends + random human pause"; at wake the worker navigates once and builds immediately. If the app restarts mid-wait, `ConstructionHumanizeUntilBySlot` (in-memory) is lost but the item's retryAt still includes the delay — degrades gracefully to today's behavior at worst.

Note: the separate "humanized construction start delay" defer phase (and its UI countdown via `ApplyConstructionInlineWait`) mostly disappears for the queue-full path — the queue timer just shows the combined wait. The gate stays as a fallback for paths that never went through a queue-full defer (e.g. Plus-queue placement behind one running build), which is unchanged.

## Files to modify

| File | Change |
|---|---|
| `src/TbotUltra.Desktop/MainWindow.QueueExecution.cs` | Fix 1: refresh gate → `NeedsConstructionStatusRefresh` |
| `src/TbotUltra.Worker/Services/Automation/Buildings/TravianClient.Buildings.cs` | Fix 2: pre-compute humanize delay in queue-full defer; set `ConstructionHumanizeUntilBySlot`; emit `queue_humanize_extra_seconds` |
| `src/TbotUltra.Core/Configuration/BotOptionPayloadKeys.cs` | new payload key const |
| `src/TbotUltra.Desktop/MainWindow.DeferredRefresh.cs` | add key to `DeferredUpgradePayloadKeys`; queue-full resync + release branch honor the extra seconds |

No new settings; reuses existing `ConstructionHumanize*` config.

## Verification

1. Build: `dotnet build src/TbotUltra.Worker/TbotUltra.Worker.csproj -p:OutDir=... -clp:ErrorsOnly` + Desktop (temp OutDir — app may be running, DLL lock).
2. Tests: `ConstructionQueueSelectorTests` (Desktop.Tests) must still pass; add a case if selector behavior is touched (it isn't — fix 1 is upstream).
3. Live check (user run): start with a full build queue in a non-selected village →
   - queue-full defer logs **once**, then `village queue blocked` — no repeated EXEC/DEFER within seconds, no icon blink;
   - defer log shows `queue_wait_seconds = remaining + 1 + humanize` and the pre-applied humanize log line;
   - at wake: one village-switch + dorf2 navigation, then immediate build — no `[construction-humanize] ... waiting Ns before start` second defer at the timer edge.
