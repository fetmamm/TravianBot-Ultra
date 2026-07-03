# Performance and Responsiveness Plan

Status: Re-audited 2026-07-03 against the current code. Several original areas are already done
(marked below). The top finding is NEW area 0 — per-second config file reads on the UI thread.
No fixes from the remaining areas have been implemented.

## Goal

Improve normal-use responsiveness without changing automation behavior, server behavior, task ordering, or account/village isolation.

Follow `AGENTS.md` and `docs/ENGINEERING_NOTES.md` before implementation. Changes should be small, measurable, and introduced one area at a time.

## Status Overview (2026-07-03 audit)

| Area | Status |
|---|---|
| 0. Per-second `LoadBotOptions` disk reads (NEW) | DONE (BotOptions cache + .env metadata cache) |
| 1. Session logging | DONE (append-only writes) |
| 2. Queue reads and UI refresh | Mostly done (store cache + 120ms UI snapshot); full-row rebuild remains |
| 3. Playwright storage-state saves | DONE (`BrowserStateSaveMode.Skip` on read-only ops) |
| 4. Background browser refresh | Partially done (20s tick + guards); adaptive schedule not done |
| 5. Queue and Travco collection updates | Open |
| 6. One-second UI timer work | Partially done (tab-gated updates); consolidation not done |
| 7. Config reads and writes | Open (read part done via area 0; write debounce remains) |
| 8. Reusable headless browser session | OBSOLETE — headless mode removed entirely (2026-07-03) |
| 9. Global layout scaling and DOM observer | Open |

## Recommended Order (updated)

| Priority | Area | Expected reward | Risk |
|---|---|---|---|
| 1 | 7. Debounce config writes (remaining part) | Medium | Medium |
| 2 | 5. Incremental queue and Travco UI updates | Medium-High | Medium |
| 3 | 4. Adaptive background refresh (remaining part) | Medium | Medium |
| 4 | 6. Timer consolidation (remaining part) | Medium | Low |
| 5 | 9. Global layout scaling and DOM observer | Uncertain-Medium | Medium-High |

## 0. Per-Second Config Reads on the UI Thread — DONE (2026-07-03)

Implemented: `MainWindow.LoadBotOptions` caches the parsed immutable `BotOptions` keyed on
(`BotConfigStore.Version`, active account); every write through `BotConfigStore` bumps `Version`.
`EnvAccountStore.ReadValues` caches the parsed .env keyed on file timestamp+length, so
`ActiveAccountName()` no longer reads the file content per call. Idle seconds now do zero config
disk reads. External hand-edits to bot.json while the app runs are no longer picked up within 1s
(accepted tradeoff; the app owns the file — .env hand-edits ARE still picked up via the metadata
check).

### Original evidence

- The 1s clock tick calls `UpdateNextTaskUi()` every second (`MainWindow.xaml.cs`).
- That calls `SelectNextQueueItemForContinuousLoop(preview: true)`, which calls `LoadBotOptions()`
  (`MainWindow.ContinuousLoop.cs:783`).
- `LoadBotOptions()` reads bot.json AND the account config from disk, JSON-parses both, builds a new
  `ConfigurationBuilder` and binds `BotOptions` via reflection — synchronously on the dispatcher thread,
  every second.
- The tick comment says "queue reads now come from the in-memory cache" — true for the queue, but NOT
  for the options read.
- The repo lives under OneDrive: transient file locks/sync stalls make these reads intermittently block
  the UI thread for tens to hundreds of milliseconds. This matches the observed intermittent lag.

### Recommended implementation

- Cache the parsed `BotOptions` (and/or the merged `JsonObject`) for the active account.
- Invalidate on: config save (`BotConfigStore.Save`/`SaveGlobal`/account-scoped saves), account switch,
  and settings-window save. All writes go through `BotConfigStore`, so invalidation can live there.
- Alternative minimal fix: let the Next-task preview reuse the most recently loaded options snapshot
  instead of loading fresh ones per tick.

### Risks

- Cache invalidation bugs could run the preview (or the loop) with stale settings. Keep the cache
  inside `BotConfigStore` so every write path invalidates it automatically.
- External edits to bot.json while the app runs would no longer be picked up between saves (today they
  are picked up within 1s by accident). This is acceptable; the app owns the file.

### Verification

- Settings save → next tick uses new values (add a log line on cache invalidation).
- Account switch → cache cleared (must not leak options across accounts).
- Measure: zero bot.json/account-config disk reads per idle second (today: ≥2 per second).

## 1. Session Logging — DONE

Implemented: `TryAppendSessionLogLines` appends batches with `File.AppendAllLines` (no complete-file
rewrite). Alarm/log sections are written as batched blocks. No further action planned.

## 2. Queue Reads and Queue UI Refresh — MOSTLY DONE

Implemented: `JsonQueueStore` keeps an in-memory cache of the queue (disk read only on first load /
path change; writes update the cache). The Desktop UI uses `GetQueueSnapshotForUi()` with a 120ms
snapshot so one refresh pass reuses one read.

Remaining (folded into area 5): `RefreshQueueUi` still rebuilds all rows, estimates, and item sources
on every call (~30 call sites).

## 3. Playwright Storage-State Persistence — DONE

Implemented: `FinalizeLeaseAsync` takes `BrowserStateSaveMode`; all read-only operations (hero reads,
village/status reads — including the background refresh) pass `Skip`. State is saved after operations
that can change the session (login, actions). No further action planned.

## 4. Background Browser Refresh — PARTIALLY DONE

Implemented: interval is 20s (was 16s); refresh is skipped while another snapshot refresh runs
(`_resourceSnapshotRefreshRunning`), during account switch (`_accountSwitchInProgress`), and uses the
session-scope token so it dies on stop/switch.

Remaining: adaptive stale-data scheduling, skipping when data is fresh or the relevant UI is hidden,
combining compatible DOM reads into one evaluation, and prioritizing user actions over background
reads on `_sessionGate`.

### Risks / verification (unchanged from original)

- Indicators may update later; incorrect gating could suppress refresh indefinitely.
- Test sleeping, paused, busy, login, village-switch states. Background work must never wake a
  sleeping session.

## 5. Incremental Queue and Travco UI Updates — OPEN

### Evidence

- Queue refresh recreates complete row lists and replaces DataGrid item sources.
- Travco `ApplyRows` (TravcoToolsWindow) clears and adds every row individually.
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

## 6. One-Second UI Timer Work — PARTIALLY DONE

Implemented: automation-loop countdowns, NPC forecasts, reinforcement status, and farming state are
already gated on the visible tab (`IsMainTabSelected`).

Remaining: consolidate the separate 1s construction-countdown timer under the main clock timer, derive
countdown text from stored finish timestamps instead of mutating every countdown each second, and skip
assigning unchanged text/brush values.

### Risks / verification (unchanged from original)

- Countdown display may be less visually precise; visibility-aware updates can show stale values
  briefly when opening a panel.
- Check timer transitions at zero, pause/resume, sleep/wake, and village changes.

## 7. Configuration Reads and Writes — OPEN (acute read part moved to area 0)

### Evidence

- `LoadBotOptions` synchronously reads/parses global and account JSON per call (see area 0 for the
  per-second call site).
- UI event handlers can save the full global and account configuration immediately.
- File retries use `Thread.Sleep`, sometimes while holding a shared I/O lock.

### Recommended implementation

- Area 0 covers the read cache. Additionally: debounce high-frequency UI setting writes and flush
  pending writes on window close and before account switching.

### Risks

- A sudden process termination may lose recently edited debounced values.
- Account-scoped and global settings must remain strictly separated.

### Verification

- Test every account switch and settings window save/reset path.
- Test dashboard toggles and village-scoped overlays.

## 8. Reusable Headless Browser Session — OBSOLETE (headless removed 2026-07-03)

Headless mode was removed entirely: `BotOptions.Headless`, the disabled Settings checkbox, the
`headless` config key, the per-operation headless `BrowserSession` branch in
`AcquireClientLeaseAsync`, and `BrowserSession`'s `headlessOverride` are all gone. The bot always
runs the shared visible browser session. (Playwright's internal warmup launch still uses a headless
Chromium — that is unrelated to the removed user-facing mode.)

## 9. Layout Scaling and Browser DOM Observer — OPEN

### Evidence

- `MainWindow.xaml` applies a `LayoutTransform` to the complete visual tree.
- `BrowserSession` injects a `MutationObserver` that can run a full-document `querySelectorAll` after
  frequent React DOM mutations.

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

## Quick Wins (updated)

1. ~~Append normal log lines instead of rewriting the complete session log.~~ DONE
2. ~~Reuse one queue snapshot during each UI refresh.~~ DONE
3. ~~Stop saving Playwright storage state after known read-only operations.~~ DONE
4. ~~Cache `LoadBotOptions` for the Next-task preview (area 0).~~ DONE
5. Skip expensive one-second updates for hidden panels (partially done; construction timer remains).
6. Debounce Travco checkbox persistence.

## Implementation Rules

- Implement one priority area per change set.
- Add timing or count diagnostics before optimization, then remove noisy temporary diagnostics.
- Preserve existing behavior unless the plan explicitly identifies an acceptable staleness tradeoff.
- Do not combine performance work with selector, path, or server changes.
- Update `docs/ENGINEERING_NOTES.md` only when a new enduring rule or pitfall is established.
- Update `README.md` only when user-visible behavior or configuration changes.
