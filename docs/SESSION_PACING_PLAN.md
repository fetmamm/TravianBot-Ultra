# Session pacing & action pacing

## Context
Boten kan idag köra automation oavbrutet 24/7 (continuous loop + queue), vilket är
aggressivt mot servern och mindre stabilt. Vi vill att programmet kör i kontrollerade
pass: en max-körtid → kontrollerad stopp + logout → sömn → auto-login + fortsatt körning,
samt rimliga slumpade pauser mellan task-steg och loopvarv. Allt utan att låsa UI:t och
utan stora omskrivningar. Funktionerna ska kunna slås av/på i Settings (ON default),
med synliga countdown-timers och en override-knapp under sömn.

Bekräftade beslut:
- **Click-pacing:** endast centrala punkter (låg risk), inte varje enskilt klick.
- **Override under sömn:** avbryter sömnen, loggar in, startar continuous run och
  nollställer run-timern (ny full max-run-period).
- Pacing-inställningar är **globala** (bot.json), som `headless`/`silver_limit` — program-
  beteende, inte per-konto. (Kontobyte = full reset påverkar dem inte.)

---

## Funktion 1: Session pacing

### Arkitektur — ny service, tunn MainWindow
Ny klass `SessionPacer` i `src/TbotUltra.Desktop/Services/Orchestration/SessionPacer.cs`
(bredvid [LoopController.cs](src/TbotUltra.Desktop/Services/Orchestration/LoopController.cs)).
Ren timing/state-maskin, ingen browser, ingen UI.

- Äger en `DispatcherTimer` (1s tick) → driver countdown + state-övergångar på UI-tråden
  (låser inte UI; tunga steg är async och hanteras av MainWindow via events).
- State: `Disabled | Running | Sleeping`.
- Konfig: `Enabled`, `MaxRunMinutes`, `SleepMinutes`, `VariationPercent`.
- Vid `Running`-start beräknas `_runDeadline = now + MaxRunMinutes ± random(VariationPercent)`.
  Vid sömn beräknas `_wakeAt = now + SleepMinutes ± random(VariationPercent)` (egen rand-roll).
- API: `Configure(settings)`, `NotifyAutomationStarted()`, `NotifyAutomationStopped()`,
  `WakeNow()` (override).
- Events (MainWindow lyssnar): `SleepStarting`, `WakeRequested`, `Tick` (UI-uppdatering).
- Exponerar för UI: `Phase`, `TimeUntilSleep`, `TimeUntilWake`, `StatusText`.
- Loggar tydligt: "session sleep starting (ran Xm)", "sleeping for Ym", "waking — resuming".

### Wiring — ny partial `MainWindow.SessionPacing.cs`
Håll [MainWindow.xaml.cs](src/TbotUltra.Desktop/MainWindow.xaml.cs) tunn. Den nya partial:
- Skapar/äger `_sessionPacer`, sätter `Logger = AppendLog`.
- `SleepStarting` → kör kontrollerad stopp av automation (samma väg som paus/stop:
  `_loopController.RequestLoopStop()` + `RequestQueueStop()`, vänta ut pågående) och
  **befintlig logout** via `_botService.ExecuteLogoutAsync(...)` (återanvänd logiken i
  `LogoutButton_Click`, [MainWindow.xaml.cs:1210](src/TbotUltra.Desktop/MainWindow.xaml.cs)).
  Extrahera den delade logout-koden till en `LogoutCoreAsync(...)` som både knappen och
  pacern anropar (undvik dubblering).
- `WakeRequested` (timer eller override) → `ExecuteLoginFlowAsync()` (befintlig) och, om
  continuous run är på, `StartContinuousLoopRunner()`. Återanvänd guards `_loginInProgress`
  / `_accountSwitchInProgress` för att undvika race conditions.
- `NotifyAutomationStarted()` anropas där continuous run faktiskt börjar köra
  (`StartContinuousLoopRunner`, [MainWindow.AutomationLoop.Ui-flödet](src/TbotUltra.Desktop/MainWindow.AutomationLoop.cs)).
  `NotifyAutomationStopped()` vid paus/stopp.
- Alla async-event-handlers wrappas i en `SafeInvokeAsync`-liknande try/catch→logg (inte rå `async void`),
  enligt ENGINEERING_NOTES §8.

### UI — countdown + override (täcker inte skärmen)
Lägg ett litet pacing-panel i vänster kontrollpanel, i `Border Grid.Row="5"` under
Start-bot-raden i [MainWindow.xaml](src/TbotUltra.Desktop/MainWindow.xaml) (~rad 214), i samma
stil som `LoopStateBadge`:
- En rad "Next sleep in: HH:MM:SS" (när `Running`).
- När `Sleeping`: "Sleeping — resumes in HH:MM:SS" + en **override-knapp** ("Run now").
- Uppdateras från `SessionPacer.Tick` via code-behind (samma mönster som `LoopStateTextBlock`).
- UI förblir fullt navigerbart (ingen scrim/overlay som [BusyOverlayControl](src/TbotUltra.Desktop/Views/BusyOverlayControl.xaml)).

---

## Funktion 2: Action pacing

Ny stateless helper `ActionPacer` (i `src/TbotUltra.Core/Configuration/` eller `Worker`) byggd
ur fyra fält + `Enabled`. Metod: `Task DelayAsync(min, max, CancellationToken)` som väntar en
slumpad tid i intervallet (hoppar om disabled). Återanvänds på fyra **centrala** punkter:

1. **Före ny task** — i `RunContinuousLoopAsync` precis före `ExecuteSingleQueueItemAsync`
   ([MainWindow.ContinuousLoop.cs:633](src/TbotUltra.Desktop/MainWindow.ContinuousLoop.cs)).
2. **Efter loopvarv klart** — golv i inter-pass-väntan
   `WaitForNextContinuousLoopPassAsync` ([MainWindow.ContinuousLoop.cs:701](src/TbotUltra.Desktop/MainWindow.ContinuousLoop.cs)).
3. **Efter sidladdning innan handling** — i centrala `GotoAsync`
   ([TravianClient.cs:1177](src/TbotUltra.Worker/Services/Automation/TravianClient.cs)) efter
   navigeringen är klar, före return (en punkt → täcker all navigation, låg risk).
4. **Mellan klick i samma funktion** — endast i befintliga fler-klick-loopar (t.ex.
   farm-sends där det redan finns en delay), inte varje enskilt klick.

Action-pacing-konfig måste nå Worker → den färdas via **BotOptions** (befintlig kanal
Desktop→Worker). I `GotoAsync`/Worker byggs `ActionPacer` ur `_config`.

---

## Settings-plumbing (end-to-end)

Pacing-fält (globala, ej i `AccountScopedKeys`):
- Session: `session_pacing_enabled` (default true), `session_pacing_max_run_minutes` (120),
  `session_pacing_sleep_minutes` (60), `session_pacing_variation_percent` (15).
- Action: `action_pacing_enabled` (default true), `action_pacing_task_*`, `action_pacing_pageload_*`,
  `action_pacing_click_*`, `action_pacing_loop_*` (min/max sekunder per typ, rimliga defaults t.ex.
  task 1–3s, pageload 0.5–1.5s, click 0.3–0.8s, loop 2–5s).

Spegla **end-to-end** enligt ENGINEERING_NOTES §8 ("Dashboard-settings-mönster"):
- [BotOptionPayloadKeys.cs](src/TbotUltra.Core/Configuration/BotOptionPayloadKeys.cs) — nya nyckel-konstanter.
- [BotOptions.cs](src/TbotUltra.Core/Configuration/BotOptions.cs) — nya properties (sätt defaults).
- [BotOptionsFactory.cs](src/TbotUltra.Core/Configuration/BotOptionsFactory.cs) — `FromConfiguration` + `CloneWithOverrides`.
- [BotOptionsPayloadApplier.cs](src/TbotUltra.Core/Configuration/BotOptionsPayloadApplier.cs) — endast för action-pacing-fält som Worker läser.
- Session-pacing-fält behöver bara Desktop → läs via `BotConfigStore.Load()` JsonObject i SessionPacing-partial.

## Settings-UI (Settings-popupen)
I [SettingsWindow.xaml](src/TbotUltra.Desktop/SettingsWindow.xaml) / [.xaml.cs](src/TbotUltra.Desktop/SettingsWindow.xaml.cs):
- **Ta bort "Act more human"** (`HumanLikeCheckBox`) — dead code (`human_like_enabled` läses
  ingenstans i Worker). Ta bort checkbox + load/save-rader + (valfritt) fältet i BotOptions.
- Ny sektion **"Runtime & pacing"** (samma Border/header-stil som "Automation"):
  - Session pacing: enable-checkbox + tre numeriska fält (max run, sleep, variation %).
  - Action pacing: enable-checkbox + min/max-fält per delay-typ (kompakt grid).
  - **Reset-knapp som ikon** (ingen text), spegla `StorageRefreshButton`-stilen
    ([MainWindow.xaml:308](src/TbotUltra.Desktop/MainWindow.xaml): `FontFamily="Segoe MDL2 Assets"`,
    `Content="&#xE72C;"`, 26x26). Återställer **endast** pacing-fälten till defaults i UI:t
    (ej global `ResetSettingsToDefaults`). Använd en central `PacingDefaults`-konstantkälla.
- Load/save: följ befintligt code-behind-mönster (`LoadConfig`/`SaveButton_Click`).

---

## Kritiska filer
- Ny: `src/TbotUltra.Desktop/Services/Orchestration/SessionPacer.cs`
- Ny: `src/TbotUltra.Desktop/MainWindow.SessionPacing.cs`
- Ny: `ActionPacer.cs` (+ `PacingDefaults`) i Core/Configuration
- `MainWindow.xaml` (pacing-panel + override-knapp), `MainWindow.ContinuousLoop.cs` (punkt 1,2),
  `MainWindow.xaml.cs` (extrahera `LogoutCoreAsync`, koppla pacer i start/stop)
- `TravianClient.cs` (punkt 3 i `GotoAsync`)
- `SettingsWindow.xaml(.cs)` (ny sektion, ta bort Act more human, reset-ikon)
- `BotOptionPayloadKeys/BotOptions/BotOptionsFactory/BotOptionsPayloadApplier.cs`
- Uppdatera `docs/ENGINEERING_NOTES.md` (Beslutslogg) + README.

## Verifiering
1. `dotnet build TbotUltra.sln` + `dotnet test`.
2. Manuellt: sätt max run = 1 min, sleep = 1 min, variation 0 % → logga in, starta bot.
   Verifiera: countdown räknar ner → vid 0 stoppas automation kontrollerat + logout → "Sleeping"
   med countdown → vid 0 auto-login + continuous run startar igen. Logg visar varje övergång.
3. Override: tryck "Run now" under sömn → loggar in direkt och run-timern nollställs.
4. Action pacing: slå på, kör continuous loop → verifiera slumpade pauser i loggen mellan
   task-start, efter sidladdning och mellan loopvarv; UI svarar hela tiden.
5. Reset-ikonen återställer pacing-fälten till default. Snabb SS- + official-körning (ingen regression).
