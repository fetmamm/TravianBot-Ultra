# Performance and Responsiveness Plan

Status: Analysis and future implementation plan only. No fixes from this plan have been implemented.

## Goal

Improve normal-use responsiveness without changing automation behavior, server-flavor behavior, task ordering, or account/village isolation.

Follow `AGENTS.md` and `docs/ENGINEERING_NOTES.md` before implementation. Changes should be small, measurable, and introduced one area at a time.

## Recommended Order

| Priority | Area | Expected reward | Risk |
|---|---|---|---|
| 1 | Session logging | Very high | Low |
| 2 | Queue reads and UI refresh | High | Medium |
| 3 | Playwright storage-state saves | High | Low-Medium |
| 4 | Background browser refresh | High | Medium |
| 5 | Queue and Travco collection updates | Medium-High | Medium |
| 6 | One-second UI timer work | Medium-High | Low |
| 7 | Config reads and writes | Medium | Medium |
| 8 | Reusable headless browser session | Very high in headless mode | High |
| 9 | Global layout scaling and DOM observer | Uncertain-Medium | Medium-High |

## 1. Session Logging

### Evidence

- `MainWindow.Logging.Stream.cs` processes log batches on the WPF dispatcher.
- `TryAppendSessionLogLines` retains all session lines in memory, rebuilds the complete output, and calls `File.WriteAllLines` after each flush.
- Work and allocations increase throughout a long-running session.

### Recommended implementation

- Replace complete-file rewrites with buffered append-only writes.
- Keep alarm presentation separate from physical log-file layout where possible.
- Flush immediately for alarms and shutdown; batch normal diagnostic messages.
- Cap retained in-memory history independently from the file log.

### Risks

- Buffered messages can be lost during an abrupt process termination.
- Existing support tooling may depend on the current alarm/log section layout.
- Concurrent append and shutdown flushing require explicit synchronization.

### Verification

- Preserve visible terminal behavior and Clean-mode filtering.
- Verify alarms remain available in diagnostics.
- Stress test sustained verbose logging and compare UI dispatcher latency and file-write volume.

## 2. Queue Reads and Queue UI Refresh

### Evidence

- `JsonQueueStore` reads and deserializes the complete queue file for every query.
- Every mutation rereads and rewrites the complete queue.
- `UpdateNextTaskUi` runs from the dispatcher every third one-second tick and may read the queue multiple times.
- `RefreshQueueUi` rebuilds all rows, estimates, and grid item sources, then repopulates building UI.

### Recommended implementation

- Introduce an account-scoped in-memory queue snapshot owned by the queue store.
- Persist after mutations while keeping existing file locking and crash recovery.
- Publish one queue-changed notification so UI updates become event-driven.
- Compute one immutable queue snapshot per UI refresh and reuse it throughout that refresh.
- Keep account switching as a full queue-cache invalidation boundary.

### Risks

- Stale cache state or lost updates if multiple writers are not coordinated.
- Incorrect cache invalidation could leak one account's queue into another.
- A crash between memory mutation and persistence could lose a recent change.

### Verification

- Extend queue store/scheduler tests for concurrent reads, account switching, recovery, ordering, and persistence failures.
- Confirm queue operations remain atomic and per-account.
- Measure queue file reads during idle UI operation; the target is zero polling reads.

## 3. Playwright Storage-State Persistence

### Evidence

- `BotTaskRunner.FinalizeLeaseAsync` calls `BrowserSession.SaveStateAsync` after every serialized browser operation.
- Saving requests the complete Playwright storage state, parses/filters JSON, writes a temporary file, replaces the target, and removes the temporary file.
- The 16-second background refresh therefore also causes repeated storage-state persistence.

### Recommended implementation

- Track whether an operation can change authentication or storage state.
- Save immediately after login, logout, account/server changes, captcha/manual verification, and other confirmed session changes.
- Debounce or skip saves after read-only DOM operations.
- Force a final save during orderly shutdown when the state is dirty.

### Risks

- A newly changed session may not survive an abrupt crash if incorrectly classified as read-only.
- Some sites may update storage during apparently read-only navigation.

### Verification

- Restart after login and verify the session is restored.
- Verify Official. Check legacy branches only when intentionally touched.
- Record save frequency before and after the change.

## 4. Background Browser Refresh

### Evidence

- A dispatcher timer starts resource/browser refresh work every 16 seconds.
- Official refreshes may also inspect tasks and daily quests.
- Other servers may run hero-revive and inbox checks.
- All browser operations share one `_sessionGate`, so background reads queue behind or ahead of user actions and automation.

### Recommended implementation

- Use an adaptive stale-data schedule instead of a fixed 16-second interval.
- Skip refreshes when browser work is queued/running, the relevant UI is hidden, or data is still fresh.
- Combine compatible current-page DOM reads into one Playwright evaluation.
- Prioritize explicit user actions and active automation over background status refresh.

### Risks

- Resource, inbox, hero, task, or quest indicators may update later.
- Incorrect gating could suppress an important refresh indefinitely.
- Combining DOM reads increases parser/test surface.

### Verification

- Define maximum acceptable staleness per indicator.
- Test sleeping, paused, busy, login, village-switch, and Official states.
- Confirm background work never wakes a sleeping session.

## 5. Incremental Queue and Travco UI Updates

### Evidence

- Queue refresh recreates complete row lists and replaces DataGrid item sources.
- Travco `ApplyRows` clears and adds every row individually.
- Changing one saved Travco row rebuilds, saves, reloads, and reapplies the complete list.
- Large saved or scanned lists can produce many collection and binding notifications.

### Recommended implementation

- Keep stable observable collections and update changed rows by ID/key.
- Batch large collection replacement into one notification where incremental matching is not worthwhile.
- Debounce Travco selection persistence and avoid reloading the list after each checkbox change.
- Confirm DataGrid virtualization remains enabled.

### Risks

- Incorrect identity matching can leave stale, missing, or duplicate rows.
- Selection and scroll position can regress.
- Batched collections require careful WPF thread ownership.

### Verification

- Test large queue histories and large Travco/oasis lists.
- Verify selected rows, sort order, scroll position, editing, save, and account isolation.

## 6. One-Second UI Timer Work

### Evidence

- The main dispatcher timer updates clock, pacing, farm-list timers, automation timers, queue selection preview, troop timers, Smithy timers, resource forecasts, reinforcement status, and execution state.
- A separate one-second dispatcher timer updates the construction queue countdown.
- Many updates raise multiple binding notifications even when their panel is hidden.

### Recommended implementation

- Consolidate one-second countdown work under one timer.
- Update hidden panels less frequently or only when they become visible.
- Store finish timestamps and derive display text rather than mutating every countdown every second.
- Avoid assigning unchanged text, brushes, or enabled states.

### Risks

- Countdown display may be less visually precise.
- Visibility-aware updates can show stale values briefly when opening a panel.

### Verification

- Check all timer transitions at zero, pause/resume, sleep/wake, and village changes.
- Measure dispatcher work while idle on each main tab.

## 7. Configuration Reads and Writes

### Evidence

- `LoadBotOptions` synchronously reads/parses global and account JSON, creates a memory stream, builds a new configuration, and constructs options.
- UI event handlers can save the full global and account configuration immediately.
- File retries use `Thread.Sleep`, sometimes while holding a shared I/O lock.

### Recommended implementation

- Maintain an immutable active-account `BotOptions` snapshot.
- Invalidate it only when relevant config, account, server, or village settings change.
- Debounce high-frequency UI setting writes.
- Flush pending writes on window close and before account switching.

### Risks

- Cache invalidation bugs can run automation with stale settings.
- A sudden process termination may lose recently edited debounced values.
- Account-scoped and global settings must remain strictly separated.

### Verification

- Test every account switch and settings window save/reset path.
- Test dashboard toggles and village-scoped overlays.
- Preserve the rule that server behavior targets official Travian only; do not reintroduce persisted server variants.

## 8. Reusable Headless Browser Session

### Evidence

- In headless mode, `BotTaskRunner.AcquireClientLeaseAsync` creates a new `BrowserSession`.
- Finalization disposes that session after the operation.
- Repeated tasks therefore repeatedly start Playwright and Chromium.

### Recommended implementation

- Reuse one headless browser context per account/server, similar to the visible shared session.
- Add explicit health checks, bounded recovery, and cleanup.
- Reset the session on account/server change, logout, unrecoverable page failure, and shutdown.

### Risks

- Highest lifecycle risk in this plan.
- Stale pages, memory growth, stuck browser state, and cross-account leakage are possible.
- Recovery logic becomes more complex.

### Verification

- Implement only after the lower-risk I/O and UI improvements.
- Run long-duration tests with account switching, browser crashes, cancellation, and repeated tasks.
- Monitor browser memory and page count.

## 9. Layout Scaling and Browser DOM Observer

### Evidence

- `MainWindow.xaml` applies a `LayoutTransform` to the complete visual tree.
- `BrowserSession` injects a `MutationObserver` that can run a full-document `querySelectorAll` after frequent React DOM mutations.

### Recommended implementation

- Evaluate replacing global layout scaling with native sizing, DPI-aware dimensions, or a render-only approach.
- Change the DOM observer to inspect only added/changed nodes and relevant target attributes.

### Risks

- UI changes can regress layout at different DPI values and resolutions.
- A narrower observer may miss unusual popup-producing links or forms.

### Verification

- Handle these changes last and independently.
- Visually verify supported resolutions and Windows scaling levels.
- Regression-test popup blocking on Official.

## Quick Wins

1. Append normal log lines instead of rewriting the complete session log.
2. Reuse one queue snapshot during each UI refresh.
3. Stop saving Playwright storage state after known read-only operations.
4. Skip expensive one-second updates for hidden panels.
5. Increase background refresh intervals while another browser operation is active.
6. Debounce Travco checkbox persistence.

## Implementation Rules

- Implement one priority area per change set.
- Add timing or count diagnostics before optimization, then remove noisy temporary diagnostics.
- Preserve existing behavior unless the plan explicitly identifies an acceptable staleness tradeoff.
- Do not combine performance work with selector, path, or server-flavor changes.
- Update `docs/ENGINEERING_NOTES.md` only when a new enduring rule or pitfall is established.
- Update `README.md` only when user-visible behavior or configuration changes.
