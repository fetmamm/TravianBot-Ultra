# Construct 25% faster (bonus-video build) — implementation plan


## Original prompt from User
– implementera en funktion som kan köra build 25% faster på alla byggnader och resursfält. knappen för detta syns på sidan där upgrade to level x finns. den syns bara om tillräckligt med resurser finns. se båda DOM
om funktionen är vald av användaren ska den köras. då ska gå till byggsidan som vanligt och klicka på upgrade 25% faster. sedan kommer popup eller så startar video direkt. detta ska ske på samma sätt som hero make adventure hard funktionen gör, när den öpnar ny browser med mer för att slippa conscent stacken. viktigt.
när videon har spelats klart så bygga byggnaden automatiskt och användaren returneras till dorf1 eller 2. då ska funktionen checka så att vald byggnad faktiskt byggdes, sedan är den färdig.

det ska finnas settings i UI på dashboard i auto settings. det är fullt i den kolumnen så gör en ny till höger. ska heta “Construct 25% faster” med tooltip icon på höger sida. ska ha toggle.
om togglad ska funktionen köras. den ska vara disabled default. ska finnas en settings icon brevid som öppnar ny popup. denna ska vara by för by liknande village settings. så användaren ska kunna välja vlka byar detta ska köras för i construction. ska finnas en toggle knapp för att toggla alla på en gång och untoggla.
i popupen ska finnas en inställning för lägsta byggtiden det ska köras för. ex om en byggnad tar 50min att konstruera och settings står på 60min så ska den inte köras. bara om mer än 60min.
ska vara input fält där användaren kan skriva in min ex 60.
default = 30min
denna ska justera för alla byar.
ska också finnas en random setting med toggle. ska också finnas dropdown med 0-100% med 10% intervall ex. 0,10,20 etc. om användaren togglar denna ska funktionen köras “random” för de byar där den är togglad. detta för att simulera mer mänskligt beteende och inte alltid titta på videos. default = 50%.
så om 50% är vald och denna setting är toggled ON och det är toggle ON för en by och funktionen ska köras så ska det vara 50% chans att den körs den gången. vid nästa build 50% chans igen.

## Context

Official Travian shows a purple "Upgrade/Construct 25% faster" button on build pages when resources
suffice; clicking it plays a bonus video and then starts the build with 25% shorter duration. The bot
should optionally use this, reusing the existing isolated-bonus-video browser machinery (same as
"increase adventures to hard") to avoid the consent stack in the main session. User decisions:
- Fallback: if the video flow fails at any point (button missing/disabled, video won't play, build not
  started after video) → **build normally immediately + write an ALARM log line** for troubleshooting.
- Scope: **both** new constructions ("Construct 25% faster") and upgrades ("Upgrade 25% faster",
  buildings + resource fields).
- Default OFF. Per-village enable. Global min-build-time (default 30 min) and random-chance
  (toggle + 0–100% in 10% steps, default 50%) settings shared by all villages.

## DOM facts (from temp_build_out/DOM/25%_*.txt)

- Button: `.upgradeButtonsContainer .section2 button.videoFeatureButton`
  - enabled: classes `textButtonV1 purple build|new videoFeatureButton`, onclick
    `Travian.React.VideoFeature.openVideo({type:'buildingUpgrade',data:{villageId,slotId,buildingId},bonus:25})`,
    attr `time="<fasterSeconds>"`.
  - not enough resources: extra class **`disabled`**, onclick is a no-op → treat as "don't run".
  - Normal duration to compare against min-build-time: `.section1 .duration .value` (e.g. `0:25:20`) —
    the upgrade flow already reads/knows `durationSeconds`.
- After click: React dialog `#dialogOverlay .dialog.videoFeature.videoFeatureInfoDialog` containing
  `#videoFeature.infoScreen.buildingUpgrade` with a "Watch video" button — same dialog family the
  adventure flow already handles (`#videoFeature`, then `#videoArea` iframe, trusted center click).
- After the video completes, Travian starts the build automatically and redirects to dorf1/dorf2.

## 1. Config chain (follow existing convention)

`BotOptionPayloadKeys.cs` — new keys (all account-scoped, add to `BotConfigStore.AccountScopedKeyValues`):
- `construct_faster_enabled` (master toggle, default false)
- `construct_faster_min_build_minutes` (default 30)
- `construct_faster_random_enabled` (default false)
- `construct_faster_random_chance_percent` (default 50)

`BotOptions.cs` + `BotOptionsFactory.FromConfiguration` + `BotOptionsPayloadApplier` (read the 4 keys
from payload → options, copy in the clone blocks) — mirror `IncreaseAdventuresToHard` /
`NpcTradeThresholdPercent` style.

## 2. Per-village storage

`VillageSettingsStore` (+ `VillageSettingRecord`): new field `ConstructFasterEnabled` (bool?, default
false), getter/setter following the `NpcTrade` pattern (`GetNpcTrade`/`SetNpcTrade` in
`VillageSettingsStore.Toggles.cs`). Min-minutes/random stay account-scoped (identical for all
villages) — NOT per-village.

## 3. Dashboard UI (Auto settings card)

`MainWindow.xaml` lines ~831–937: the card is one vertical `StackPanel` (full). Restructure card body
into a 2-column `Grid`: column 0 = existing StackPanel unchanged, column 1 = new StackPanel (top
aligned) with one row:
- `CheckBox` "Construct 25% faster" (`ToggleSwitchStyle`), default unchecked
- info icon (`SettingInfoIconStyle`) on the RIGHT of the label, tooltip explaining the feature
- gear `Button` (`&#xE713;`, Segoe MDL2 — copy `HeroInventorySettingsButton` markup) opening the popup

Code-behind: `MainWindow.Dashboard.Settings.cs` — load/save with the existing suppress-flag pattern
(`_suppressXxxConfigWrite`, `ApplyXxxConfigToUi(BotOptions)`, `Xxx_Changed` → `_botConfigStore.Load()`,
set key, `Save()`).

## 4. New popup: `ConstructFasterSettingsWindow` (.xaml + .xaml.cs)

Modeled on `HeroResourceOverviewWindow` (per-village rows) + `VillageSettingsWindow` styling:
- Row per village: ToggleSwitch + village name (capital gold like Village settings).
- "Toggle all" button (flips all rows on/off).
- "Minimum build time (min)" numeric TextBox, default 30 — tooltip: builds shorter than this never
  use the video.
- "Random" ToggleSwitch + ComboBox 0,10,…,100 (`Tag`-based, default 50) — tooltip: when ON, each
  build rolls this chance to use the video (simulates human behavior).
- Save & close / Cancel. Save writes per-village flags via `VillageSettingsStore` and the two global
  settings via `BotConfigStore`.

## 5. Desktop → worker payload

Where construction payloads are built (same place hero-resource settings are merged per village —
`ApplyHeroResourceSettingsForVillage` in `MainWindow.TroopTraining.cs` / construction payload
builders): resolve effective enable = master toggle AND village's `ConstructFasterEnabled`, and put
the 4 keys into the construction task payload. Worker sees plain `BotOptions` values; no per-village
logic in the worker.

## 6. Worker: video flow (new partial `TravianClient.ConstructFaster.cs`)

Decision helper, called at the moment the flow is about to click the normal build button:
`ShouldUseConstructFaster(durationSeconds)`:
- `_config.ConstructFasterEnabled` false → no
- `durationSeconds <= MinBuildMinutes * 60` → no
- button `.upgradeButtonsContainer .section2 button.videoFeatureButton` missing or has class
  `disabled` → no
- random: if `ConstructFasterRandomEnabled` and `Random.Shared.Next(0, 100) >= ChancePercent` → no
  (fresh roll per build attempt)

`RunConstructFasterVideoAsync(slotId, gid, buildingName, ct)` — mirrors
`RunAdventureVideoBonusAsync`/`...CoreAsync` in `TravianClient.AdventureDanger.cs`:
1. `_runInIsolatedBonusVideoBrowserAsync(...)` (existing `BrowserSession.BonusVideo.cs` bootstrap:
   seeded auth cookies, consent/ad hosts flow, cleanup in finally).
2. In isolated page: goto the same build page (`build.php?id={slot}&gid={gid}`), accept consent
   manager (reuse the existing accept helper), locate the videoFeatureButton, re-check not-disabled,
   trusted click.
3. Wait for `#videoFeature` info dialog → click its "Watch video" button (reuse dialog helper logic).
4. Wait for `#videoArea` iframe → trusted mouse click at bounding-box center (2 attempts) — exactly
   like the adventure flow.
5. Wait for completion: poll for redirect to dorf1/dorf2 OR the dialog closing, timeout ~60–90s.
6. Existing cleanup runs (main page quarantined to about:blank, consent/ad storage flushed,
   `ReturnMainPageAfterIsolatedBonusVideoAsync`-style return).

Hook points in `TravianClient.Buildings.cs`:
- Upgrade path `UpgradeBuildingToLevelAsync` (both `ClickUpgradeToLevelButtonAsync` call sites,
  ~line 219 and ~272): before the normal click, if `ShouldUseConstructFaster(durationSeconds)` → run
  video flow; on success verify (below) and treat as the click having happened; on ANY failure →
  `Notify("ALARM: construct-faster video failed (<reason>) — building normally.")` and fall through
  to the normal click.
- New-construct path `ConstructBuildingAsync` (~line 1442; click helper
  `ClickConstructBuildingButtonAsync` ~1724): same gate; duration for the threshold read from the
  construct card's duration value.

Verification after video (main browser): navigate to dorf2/build page and reuse the existing checks
(`WaitForBuildingLevelAdvanceAsync` triple check: slot level, `BuildQueueFingerprints.ContainsBuilding`,
`ReadActiveConstructionsAsync` match). Started → return success message noting "25% faster (video)";
not started → ALARM + normal build click fallback. Post-build bookkeeping (queue wait read, session
cache/timer updates) runs exactly as for a normal build so dashboard timers stay correct.

Logging: `[construct-faster]` prefix on all Notify lines (decision, roll result, each step, outcome).

## 7. Docs

Update `docs/ENGINEERING_NOTES.md` (bonus-video conventions section: new boxless build-page variant,
selectors, ALARM fallback rule) and `docs/ARCHITECTURE.md` (new files).

## Files touched (new °)

- °`src/TbotUltra.Worker/Services/Automation/Buildings/TravianClient.ConstructFaster.cs`
- `src/TbotUltra.Worker/Services/Automation/Buildings/TravianClient.Buildings.cs` (hooks)
- `src/TbotUltra.Core/Configuration/BotOptionPayloadKeys.cs`, `BotOptions.cs`,
  `BotOptionsFactory.cs`, `BotOptionsPayloadApplier.cs`
- `src/TbotUltra.Desktop/Services/BotConfigStore.cs` (AccountScopedKeys)
- `src/TbotUltra.Desktop/Services/VillageSettingsStore*.cs` (per-village flag)
- `src/TbotUltra.Desktop/MainWindow.xaml` (Auto settings card column 2)
- `src/TbotUltra.Desktop/MainWindow.Dashboard.Settings.cs` (toggle load/save + popup open)
- °`src/TbotUltra.Desktop/ConstructFasterSettingsWindow.xaml` + `.xaml.cs`
- Construction payload builder site(s) in MainWindow (merge per-village flag + globals)
- Tests: `Worker.Tests` for the decision helper (threshold/roll/disabled-button gating via
  parser-level unit if feasible), `Desktop.Tests` for store round-trip of the new keys/flags.

## Verification

1. `dotnet build src\TbotUltra.Desktop\TbotUltra.Desktop.csproj -p:OutDir=%TEMP%\claude\buildcheck\`
2. `dotnet test` Worker.Tests + Desktop.Tests (OutDir temp per DLL-lock convention).
3. Live: toggle ON for one village, set min build time low (e.g. 1 min), random OFF; queue an upgrade
   costing enough resources → expect isolated browser opens, video plays, build starts, dashboard
   timer shows the ~25% shorter duration; log shows `[construct-faster]` steps.
4. Live negative: not enough resources (button disabled) → normal build + no video, no ALARM (that is
   a "don't run" gate, not a failure). Kill the video mid-flow → ALARM line + normal build.
5. Random 50%: run several builds, log shows roll results both ways.
