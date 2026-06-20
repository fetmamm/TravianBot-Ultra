# AutoTownHall — Town Hall celebrations

> **Status: PLAN ONLY — not implemented.** Scope when built: small celebration first; big deferred until a Town Hall 10 village exists.

## Context
Add an automated **Town Hall celebration** feature (small/big) for all tribes, modeled on the
existing Teutons **brewery celebration** (gid 35 → Town Hall gid 24). Requirements:
- Per-village **Town Hall** toggle column in the Village-settings popup (auto-generated from the catalog).
- Dashboard group with toggle + timer (auto-generated).
- **Small/big** choice: **global default** set by 2 radios in an NPC/Trade "Town Hall settings" box
  (default **Small**), with **per-village overrides** edited in a new all-villages overview popup.
- A new **overview popup** to quickly set, for every village, the toggle (on/off) + small/big.
  Opened from (a) a per-row **gear** in the TH column (like the troop-training gear) and (b) a button
  in the NPC TH box.
- **Hero-resources** checkbox on the Hero page (default **FALSE**) — if enabled, top up from hero
  inventory when resources are short. Mirrors `HeroResourceUseBrewery`.
- A running celebration must be **remembered across restarts** (celebrations take ~12 h): after start,
  read the remaining time from the page, update the UI, and **persist the end time per village** so the
  bot does not re-navigate/check while one is still running.
- Big needs Town Hall level ≥ 10. If big is chosen but level < 10 → run **small**.

> **Scope now: SMALL only.** No village currently has Town Hall level 10, and the big→small fallback
> already runs small whenever level < 10 — so big is never actually reached today. Therefore: build the
> full small/big **structure** (UI radios, per-village mode, options, fallback), implement and verify the
> **small** start path now, and **defer the big start-selector verification + implementation** until a
> Town Hall 10 village exists. Until then big silently falls back to small (logged).

## Build step 0 — verify live DOM (Chrome)
Verify only the **small** celebration DOM now (the big action isn't visible without TH 10). Per the user:
open Chrome via the chrome-devtools MCP, user logs in and says "ready", then navigate to the Town Hall and
inspect the small-celebration start link/button + the running timer, on **both** server variants
(Official + SS-Travi). Record confirmed selectors in `docs/ENGINEERING_NOTES.md` before writing the worker
selectors. Follow the ENGINEERING_NOTES selector rules (additive: SS/legacy first, Official fallback; scope
to `.build_details`). The big-celebration selector is a documented TODO to verify later on a TH 10 village.

## Backend registration (new queue group + task)
Add `TownHallCelebration = 9` to both enums (same value):
[TaskGroup.cs](src/TbotUltra.Core/Tasks/TaskGroup.cs), [QueueGroup.cs](src/TbotUltra.Worker/Domain/QueueGroup.cs).

Register where brewery is registered:
- [QueueGroupCatalog.cs](src/TbotUltra.Worker/Services/Queue/QueueGroupCatalog.cs): `Metadata` entry
  `("town_hall_celebration", "Town Hall celebration", "Small/big Town Hall celebrations.")` + `ToQueueGroup` arm.
- [TaskCatalog.cs](src/TbotUltra.Core/Tasks/TaskCatalog.cs): `new("run_town_hall_celebration", TaskGroup.TownHallCelebration, "Run Town Hall celebration", true, TaskPayloadKind.None)`.
- [BotTaskRunner.cs](src/TbotUltra.Worker/Services/BotTaskRunner.cs): `["run_town_hall_celebration"] = ExecuteRunTownHallCelebrationAsync`.

## Settings model (small/big + hero resources)
**Global default mode** (NPC radios) — mirror `ContinuousFarmSendMode`:
- [BotOptionPayloadKeys.cs](src/TbotUltra.Core/Configuration/BotOptionPayloadKeys.cs): `TownHallCelebrationMode = "town_hall_celebration_mode"`.
- [BotOptions.cs](src/TbotUltra.Core/Configuration/BotOptions.cs): `TownHallCelebrationMode` (default `"small"`).
- Wire through [BotOptionsPayloadApplier.cs](src/TbotUltra.Core/Configuration/BotOptionsPayloadApplier.cs) + [BotOptionsFactory.cs](src/TbotUltra.Core/Configuration/BotOptionsFactory.cs).

**Hero resources** — mirror `HeroResourceUseBrewery` across the same files
([BotOptionPayloadKeys.cs](src/TbotUltra.Core/Configuration/BotOptionPayloadKeys.cs):87, BotOptions, Applier, Factory):
- `HeroResourceUseTownHall = "hero_resource_use_town_hall"`, BotOptions default `false`.

**Per-village override store** — new `TownHallSettingsStore` modeled on
[TroopTrainingSettingsStore.cs](src/TbotUltra.Desktop/Services/TroopTrainingSettingsStore.cs)
(per account + village JSON), storing the per-village mode (`small`/`big`, nullable = inherit global default).
The per-village enable is the existing `QueueGroup.TownHallCelebration` flag in
[VillageSettingsStore.cs](src/TbotUltra.Desktop/Services/VillageSettingsStore.cs) (no new field).

## Worker: new partial `TravianClient.TownHallCelebration.cs`
Copy [TravianClient.BreweryCelebration.cs](src/TbotUltra.Worker/Services/Automation/TravianClient.BreweryCelebration.cs); adapt:
- `ResolveTownHallBuilding` → gid **24** / name "Town Hall". Drop the Teutons + capital-only gates.
- `TryProbeTownHallSlotAsync` → probe `g24` on dorf2 (swap `g35`→`g24`, alt "Brewery"→"Town Hall").
- Town Hall **level** from `ResolveTownHallBuilding(buildings).Level` (no extra nav).
- `RunTownHallCelebrationAsync(mode, ct)`:
  1. resolve slot → if none: `"Town Hall not found. queue_wait_seconds=600"`.
  2. goto slot (`Paths.BuildBySlot`) + `PauseForManualStepIfVisibleAsync` + `EnsureLoggedInAsync`.
  3. if a celebration is running → `"running. queue_wait_seconds={remaining}"`.
  4. effective mode = `big` only if level ≥ 10, else fall back to `small` (log). **Now: the small path is
     implemented + verified; the big start selector is a TODO** — until a TH 10 village exists every run
     falls back to small, so big is never exercised yet.
  5. start the chosen celebration; if no start button **and** `context.Options.HeroResourceUseTownHall`
     → top up via the hero transfer (reuse `TryHeroResourceTransferForBreweryAsync` shape in
     [TravianClient.HeroResourceTransfer.cs](src/TbotUltra.Worker/Services/Automation/TravianClient.HeroResourceTransfer.cs)) and retry.
  6. on reload **read the remaining time from the page** and return `"started. queue_wait_seconds={remaining}"`.

  The `queue_wait_seconds=` return is the happy path: `ThrowIfTaskBlocked` (BotTaskRunner.cs:2620) →
  `TaskWaitException` → desktop defer (`TryExtractQueueWaitDelay` → `MarkQueueItemDeferred`) sets the queue
  item's `NextAttemptAt`, which drives the dashboard timer generically.
- Add `ExecuteRunTownHallCelebrationAsync` (copy BotTaskRunner.cs:2275) + public `RunTownHallCelebrationAsync` wrapper (copy BotTaskRunner.cs:1317).

## Persisting a running celebration (survive restart)
New `TownHallCelebrationStateStore` (per account JSON: villageKey → `{ mode, endsAtUtc }`).
- **Persist** when the run task defers: in [MainWindow.QueueExecution.cs](src/TbotUltra.Desktop/MainWindow.QueueExecution.cs)
  reuse the brewery mirror hook (`ApplyBreweryCelebrationDeferSignal`, ~line 214–217) — add a sibling branch for
  `run_town_hall_celebration` that writes `endsAtUtc = now + queueWaitDelay` for that village and updates the UI.
- **Restore / skip** in [MainWindow.ContinuousLoop.cs](src/TbotUltra.Desktop/MainWindow.ContinuousLoop.cs)
  `EnsureContinuousLoopRuntimeItemsAsync`: for each TH-enabled village, if stored `endsAtUtc` is still in the
  future, **do not enqueue** a run task; instead seed a deferred runtime item with `NextAttemptAt = endsAtUtc`
  (or surface the timer from the store) so the dashboard shows the countdown without navigating. When `endsAtUtc`
  has passed, clear it and resume normal generation.

## Continuous-loop generation (per village)
In `EnsureContinuousLoopRuntimeItemsAsync`, add a per-village block modeled on the **smithy** loop
(ContinuousLoop.cs:275-305):
```
foreach (var village in automationVillages)
{
    var key = GetVillageKey(village);
    if (!IsGroupEnabledForVillage(key, QueueGroup.TownHallCelebration)
        || HasActiveTaskForVillage("run_town_hall_celebration", village.Name)) continue;
    if (TownHallCelebrationStateStore says still running) continue; // restore/skip (above)
    var payload = BuildVillageRuntimePayload(village);
    payload[BotOptionPayloadKeys.TownHallCelebrationMode] =
        perVillageMode(key) ?? options.TownHallCelebrationMode; // override else global default
    _botService.EnqueueRuntime("run_town_hall_celebration", "Town Hall celebration", payload, priority: -50, maxRetries: 0);
}
```
Worker reads the resolved mode from `context.Options.TownHallCelebrationMode` (payload applier set it).

## UI
- **Dashboard card** — auto. Add `QueueGroup.TownHallCelebration` to the explicit array in
  `GetDefaultContinuousLoopGroupOrder` ([MainWindow.AutomationLoop.Ui.cs](src/TbotUltra.Desktop/MainWindow.AutomationLoop.Ui.cs):793).
  Backfill it into the stored **visible** set so existing accounts see the card (mirror how the order list backfills missing groups).
- **Village-settings TH column "Town Hall" + per-row gear** — the column auto-generates from the visible
  dashboard cards ([MainWindow.Dashboard.Villages.cs](src/TbotUltra.Desktop/MainWindow.Dashboard.Villages.cs):516).
  Reuse the brewery short-label override (Dashboard.Villages.cs:520-525) so the column shows **"Town Hall"** while
  the card stays "Town Hall celebration". In [VillageSettingsWindow.xaml.cs](src/TbotUltra.Desktop/VillageSettingsWindow.xaml.cs)
  add a TH branch to `BuildGroupColumns` that uses a **gear cell template** like `BuildBuildTroopsCellTemplate`
  (toggle + gear button); the gear opens the new overview popup (callback wired from MainWindow like
  `OpenTroopSettingsFromVillageSettings`).
- **Town Hall overview popup (new window)** — lists all villages with: enable toggle (→ VillageSettingsStore
  group set) + small/big radio per village (→ TownHallSettingsStore, default Small). Model the window on the
  existing per-village dialog pattern. Save writes both stores and refreshes the dashboard.
- **NPC/Trade "Town Hall settings" box (new XAML)** — sibling `<Border Margin="0,16,0,0">` after Resource Trading
  inside `<StackPanel Grid.Column="2">` ([MainWindow.xaml](src/TbotUltra.Desktop/MainWindow.xaml):2486-2488).
  Contains the 2 global-default radios (Small/Big, default Small) + a button that opens the overview popup.
  Radios bind via code-behind handlers modeled on `FarmSendListPerListRadioButton_Checked` (write
  `town_hall_celebration_mode` to bot.json) + an `ApplyTownHallCelebrationModeToUi(options)` with a `_suppress…` guard.
- **Hero page checkbox** — add a checkbox under the existing hero-resource ones in
  [HeroPanel.xaml](src/TbotUltra.Desktop/Views/HeroPanel.xaml):587, bound to a new `HeroViewModel.HeroResourceUseTownHall`
  (default false), persisted to bot.json — mirror `HeroResourceUseBrewery` in
  [HeroViewModel.cs](src/TbotUltra.Desktop/ViewModels/HeroViewModel.cs) + [MainWindow.Hero.cs](src/TbotUltra.Desktop/MainWindow.Hero.cs).

## ADR
Add `docs/adr/2026-06-20-town-hall-celebration.md` documenting: group, global-default + per-village-override mode,
hero-resources opt-in, restart persistence of end time, big→small fallback, and confirmed selectors.

## Verification
1. Build the **Worker** project (compile-verify via `-p:OutDir` temp if the Desktop app is open — see `project_build_dll_lock_running_app`).
2. Run desktop, log in. Confirm: dashboard **Town Hall celebration** card (toggle+timer); **Town Hall** column with per-row gear; **Town Hall settings** box (radios + open-overview button) on NPC/Trade; **overview popup** lists all villages (toggle + small/big, default Small); Hero-page **hero-resources** checkbox (default off).
3. Enable TH for a village with a Town Hall, start the loop → it navigates, starts the **small** celebration, reads remaining time, dashboard counts down; per-village override beats the global default.
4. Selecting **big** on a village with TH < 10 (all villages today) → runs **small**, logs fallback. (Big start path verified later on a TH 10 village.)
5. Restart the app mid-celebration → the timer is restored from disk and the bot does **not** re-navigate until it ends.
6. Re-verify the **small** start selector on both server variants (Official + SS-Travi).
