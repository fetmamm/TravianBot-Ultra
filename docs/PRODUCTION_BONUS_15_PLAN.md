# Plan: Auto-activate +15% resource production (bonus video)

## Context
Official Travian's payment wizard has an "Advantages" tab where each resource
(lumber/clay/iron/crop) can get a production bonus: +25% for 72h (costs gold) or
+15% for 8h (free, watch a video). New feature: automatically activate the free
15% bonus for all resources where possible, re-attempting ~every 24h, reusing the
existing isolated bonus-video browser flow (same as hero adventure videos and
construct-25%-faster). UI: dashboard toggle "Activate 15% more production" under
"Construct 25% faster" with a gear icon opening a popup that shows per-resource
timers (25% = yellow, 15% = purple, "Not active" in gray).

The bonus is account-wide (not per-village), so the toggle/state are account-scoped.

## DOM reference (from temp_build_out/DOM/advantages_gold.txt and dorf1_15_25%_activated.txt)

### Opening the Advantages tab
- dorf1 has `button.productionBoostButton` with `onclick="Travian.React.openPaymentWizard({activeTab:'advantages'}); return false;"` — opens the wizard directly on the Advantages tab (preferred route).
- Fallback: `a.shop` (`Travian.React.openPaymentWizard({})`) then click `a.tabItem` with text `Advantages`.
- Wait for content root `#paymentWizardContent` + `.advantagesBonusBox` before reading.

### Per-resource boxes: `div.advantagesBonusBox.{lumber|clay|iron|crop}ProductionBonus` (+ `active` when a bonus runs)
| State | Markers |
|---|---|
| Nothing active | No `active` class. `.bonusVideo` div present: text `+15% for 8 hours` + PURPLE button `button.textButtonV2.purple` (contains `i.videoIcon`, span "Activate") → the free 15% button. Gold "Activate" button also present — never click. |
| 15% active | `active`; `.bonusDuration` has `+15% active for:` + `span.timerReact` (`07:59:53`). Gold button "Upgrade" — never click. No purple button. |
| 25% active | `active`; `.bonusDuration` has `+25% active for:` + `span.timerReact`. Gold button "Extend" — never click. |

**Safety rule: only ever click `.advantagesBonusBox .bonusVideo button.textButtonV2.purple`. All `button.prosButton`/gold buttons (Activate/Extend/Upgrade) cost gold and must be excluded.**

### dorf1 quick indicators (production table `#production`)
Per row `i.r1..r4` + sibling `svg.advantageBonusArrow.productionBoost` first class:
`quest`/`premiumFeature` = 25% active, `videoFeature` = 15% active, empty = none.
(Used only as cheap cross-check; the advantages boxes are the SOT for timers.)

## Design

### 1. Config chain (account-scoped bool, per ENGINEERING_NOTES §3)
New key `production_bonus_video_enabled` (default false) through the whole chain:
- `Core/Configuration/BotOptionPayloadKeys.cs` → `ProductionBonusVideoEnabled = "production_bonus_video_enabled"`
- `BotOptions.cs` property, `BotOptionsFactory.cs` (both read paths), `BotOptionsPayloadApplier.cs`
- `Desktop/Services/BotConfigStore.cs` → add to `AccountScopedKeys`
- Test coverage in `BotOptionsPayloadApplierTests.cs` (same rows as `AutoCollectDailyQuestsEnabled`).

### 2. Worker — new TravianClient partial + stateless parser
**New file** `Worker/Services/Automation/Features/TravianClient.ProductionBonus.cs`
(navigation/clicks only, models `TravianClient.ConstructFaster.cs` + `TravianClient.AdventureDanger.cs`):

- `public async Task<string> ActivateProductionBonusVideosAsync(CancellationToken ct)` — main-browser entry:
  1. `EnsureLoggedInAsync()`; open Advantages tab in MAIN browser (read-only), read all 4 box states (early exit if no purple button anywhere → return state-only result). Navigate back to dorf1.
  2. If any resource is activatable → `_runInIsolatedBonusVideoBrowserAsync` (one isolated session for all resources; guard null like construct-faster). Inside: `CreateIsolatedBonusVideoClient(videoPage)` → `RunProductionBonusVideosInCurrentBrowserAsync`.
  3. `finally`: return main browser to dorf1 (`ReturnMainPageAfterIsolatedBonusVideoAsync` pattern).
  4. Verify + collect timers in MAIN browser: reload dorf1, re-open Advantages tab, read all boxes (`timerReact` per resource), navigate back to dorf1. Encode per-resource state in the result string (see §4).
  5. Never throw into the caller except `OperationCanceledException` (adventure-video pattern: catch → log verbose → return skip message).

- `RunProductionBonusVideosInCurrentBrowserAsync` (isolated browser):
  1. Goto dorf1; `IsLoggedInAsync()` check — skip (never log in) if stale cookies, same comment/reason as `RunAdventureVideoBonusInCurrentBrowserAsync`.
  2. `AcceptConsentManagerIfPresentAsync` (reuse, it takes a `logPrefix`).
  3. Open Advantages tab (productionBoostButton, fallback a.shop + tab click; paced clicks via `DelayBeforeClickAsync`).
  4. For each resource with a purple button (max 4, sequential):
     click purple button → `ConfirmProductionBonusVideoDialogAsync` (reuse the `#videoFeature` dialog logic incl. "Don't show it again" checkbox — extract/copy from `ConfirmConstructFasterVideoDialogAsync` + `TickConstructFasterDontShowAgainAsync`) → start video (same trusted center-click on `#videoArea, #videoFeature iframe`, consent re-accept loop, `IsH264PlaybackSupportedAsync` diagnostics) → wait for completion (poll: box shows 15% `timerReact`, or dialog closed + back on page; timeout ~75s like construct-faster) → re-open Advantages tab for the next resource.
  5. Per-resource failures are logged and skipped; continue with remaining resources.

**New file** `Worker/Services/Automation/Features/ProductionBonusDomParser.cs` — stateless, unit-tested (ENGINEERING_NOTES §4):
- Parse box-state JSON extracted by one `EvaluateAsync` (per box: resource key, has `active`, bonus percent from `.bonusDuration .bonusText` (15/25), `timerReact` text, purple-button present/enabled).
- `ParseTimerToSeconds("07:59:53")` — `H+:MM:SS`, strip bidi markers per ENGINEERING_NOTES pitfalls.
- Build/parse the machine-token result string (see §4).

### 3. Task registration + scheduling
- `Core/Tasks/TaskCatalog.cs`: `new("activate_production_bonus", TaskGroup.Construction, "Activate 15% production", true, TaskPayloadKind.None)`.
- `Worker/Services/BotTaskRunner.cs` handler registry: `["activate_production_bonus"] = ExecuteActivateProductionBonusAsync`.
- `BotTaskRunner.TaskHandlers.cs`: handler calls `context.Client.ActivateProductionBonusVideosAsync(...)`, logs result (same shape as `ExecuteCollectDailyQuestsAsync`).
- Desktop enqueues it as a runtime task (`_botService.EnqueueRuntime("activate_production_bonus", "Activate 15% production", payload, priority: -40, maxRetries: 1)`) from the continuous loop, following the `TryQueueAutoCollectDailyQuestsAsync` pattern in `MainWindow.Resources.Snapshot.cs`:
  - Gate: toggle enabled + no active/pending item of the same name + store says `min(nextAttemptUtc)` over the 4 resources has passed (pure time check, no page probe).
  - On toggle enable: `TriggerImmediateIfLoopRunning(...)` like daily quests in `MainWindow.Dashboard.Settings.cs`; on disable: remove pending items (same removal pattern as `RemovePendingCollectDailyQuests`).

### 4. Result tokens + Desktop state store
Worker result string carries per-resource machine tokens (protocol comment like `troops_blocked=`), e.g.:
`production_bonus_state=lumber:25:13935;clay:15:28793;iron:none:0;crop:25:9942 production_bonus_next_attempt=lumber:86400;...`
- Per resource: `bonus` (`15`/`25`/`none`) + remaining seconds (from `timerReact`), plus next-attempt seconds computed by the client:
  - 15% activated now (or already running) → next attempt = 24h from activation.
  - 25% active (no purple) → next attempt = 25% remaining + 5 min.
  - Nothing active but purple missing/disabled (cooldown) → retry in 4h.
- **New file** `Desktop/Services/ProductionBonusStateStore.cs`, modeled 1:1 on `TownHallCelebrationStateStore.cs`: per-account file (new path in `Core/Accounts/AccountStoragePaths.cs`), stores per resource `{ Bonus, BonusEndsAtUtc, NextAttemptAtUtc }` as **absolute UTC times** (ENGINEERING_NOTES rule); expired entries are cleared on load.
- **New file** `Desktop/MainWindow.ProductionBonus.cs`: parses the task result (success handler, like `ApplyTownHallCelebrationDeferSignal` is wired for town hall), saves the store, opens the popup, enqueue-gating helpers.

### 5. Desktop UI
**Toggle** (`MainWindow.xaml`, auto-settings box, right column ~line 945): make the Grid.Column=1 StackPanel vertical; row 1 = existing Construct-faster row (unchanged, as its own horizontal StackPanel); row 2 (`Margin="0,8,0,0"`) = new horizontal StackPanel:
- `CheckBox x:Name="ProductionBonusVideoCheckBox" Content="Activate 15% more production"` with `ToggleSwitchStyle`, Checked/Unchecked → `ProductionBonusVideoSetting_Changed`.
- `SettingInfoIconStyle` info icon (tooltip: watches a free bonus video to activate +15% production for 8h per resource, retries every 24h; never spends gold).
- Gear button (Segoe MDL2 `&#xE713;`, same sizing as `ConstructFasterSettingsButton`) → `ProductionBonusSettingsButton_Click`.

Toggle handlers in `MainWindow.Dashboard.Settings.cs`, copying the `AutoCollectDailyQuests` trio: suppress flag + `ApplyProductionBonusVideoConfigToUi(BotOptions)` (call it from `MainWindow.xaml.cs` next to `ApplyAutoCollectDailyQuestsConfigToUi`, ~line 704) + `_Changed` handler that writes `_botConfigStore` and triggers immediate queueing when enabled.

**Popup** — **new files** `ProductionBonusSettingsWindow.xaml(.cs)` (pattern: `ConstructFasterSettingsWindow`, opened with `Owner = this` + `ShowDialog()`; read-only, so just a Close button):
- Grid: header row with 4 columns (Wood, Clay, Iron, Crop), then two data rows:
  - Row "25%": per resource a badge showing the remaining 25% time, styled like the automation-loop status badge (`Border CornerRadius="8" Padding="8,3" MinWidth="64" BorderThickness="1"`, cf. MainWindow.xaml ~line 765) with `WarningBgBrush`/`WarningBorderBrush`/`WarningTextBrush` (yellow).
  - Row "15%": same badge in purple — remaining 15% time when active, otherwise time until `NextAttemptAtUtc` (next auto-activation try; only meaningful while the toggle is on — when off and no active 15% timer, show "Not active").
  - No timer → gray text "Not active" (`TextMutedBrush`, `ControlBackgroundBrush`/`BorderMutedBrush` badge).
- New purple brushes in `Themes/Palette.xaml` next to the Warning group: `PurpleBrush #8957E5`, `PurpleBorderBrush #8957E5`, `PurpleBgBrush #211632`, `PurpleTextBrush #A371F7` (matches the dark GitHub-style palette).
- `DispatcherTimer` (1s) in the window code-behind recomputes countdown texts from the store's absolute UTC times; stops on close.

### 6. Files summary
Modified: `BotOptionPayloadKeys.cs`, `BotOptions.cs`, `BotOptionsFactory.cs`, `BotOptionsPayloadApplier.cs`, `BotConfigStore.cs`, `TaskCatalog.cs`, `BotTaskRunner.cs`, `BotTaskRunner.TaskHandlers.cs`, `AccountStoragePaths.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `MainWindow.Dashboard.Settings.cs`, `MainWindow.Resources.Snapshot.cs` (queue trigger), `Themes/Palette.xaml`, `BotOptionsPayloadApplierTests.cs`.
New: `Worker/Services/Automation/Features/TravianClient.ProductionBonus.cs`, `Worker/Services/Automation/Features/ProductionBonusDomParser.cs`, `Desktop/Services/ProductionBonusStateStore.cs`, `Desktop/MainWindow.ProductionBonus.cs`, `Desktop/ProductionBonusSettingsWindow.xaml(.cs)`, parser tests in `Worker.Tests`.

### 7. Reused existing utilities (no new duplicates)
- `_runInIsolatedBonusVideoBrowserAsync` + `CreateIsolatedBonusVideoClient` (`TravianClient.AdventureDanger.cs:155`) — isolated video browser with 120s hard timebox (`BrowserSession.BonusVideo.cs`).
- `AcceptConsentManagerIfPresentAsync`, `IsH264PlaybackSupportedAsync`, `DelayBeforeClickAsync`, `PauseForManualStepIfVisibleAsync`, `GotoAsync`/`Paths`, `IsLoggedInAsync`.
- `#videoFeature` dialog confirm + don't-show-again + trusted play click + completion polling (construct-faster/adventure implementations as templates).
- `AtomicFile.WriteAllText` for the store; `EnqueueRuntime` for scheduling; `TriggerImmediateIfLoopRunning`.

## Verification
1. `.\scripts\Build-Check.ps1` and `.\scripts\Run-Tests.ps1` (isolated output — safe with app open). Unit tests: `ProductionBonusDomParser` (box classification from captured DOM snippets incl. bidi timer text, token round-trip), payload applier test for the new key.
2. Live smoke (Official, per ENGINEERING_NOTES §7): enable toggle → verify a `activate_production_bonus` item queues and runs; watch the log: wizard opens on Advantages, purple button clicked ONLY for resources without an active bonus, video plays in the isolated browser, browser closes, main page refreshes, `+15% active for:` timer confirmed and store file written.
3. Open the popup: yellow 25% timers, purple 15% timers counting down, "Not active" in gray for resources without bonus; timers survive app restart (absolute UTC in store).
4. Negative check: with 25% active on a resource, confirm the log shows it skipped (no gold button ever clicked) and next attempt scheduled at 25% expiry.

## Status
- [ ] Config chain (`production_bonus_video_enabled`)
- [ ] Worker: `TravianClient.ProductionBonus.cs` + `ProductionBonusDomParser` + tests
- [ ] Task registration + continuous-loop trigger
- [ ] `ProductionBonusStateStore` + result-token parsing
- [ ] Dashboard toggle + gear icon
- [ ] `ProductionBonusSettingsWindow` popup + purple palette brushes
- [ ] Build + tests green
- [ ] Live smoke verified on Official
