# Refaktoriseringsanalys – TbotUltra (WPF/C#)

## Context
Projektet har vuxit till en svårunderhållen kodbas. Mätningar på faktisk kod:
- `MainWindow.xaml.cs` = 148 KB + **28 partial-filer** (`MainWindow.*.cs`) ≈ **15 000 rader** kod-behind. Endast **6 ViewModels** finns → nästan all UI-logik ligger i code-behind.
- `TravianClient` är en god-class: `.cs` 211 KB, `.Buildings.cs` 179 KB, `.Resources.cs` 110 KB, `.Hero.cs` 105 KB (uppdelad i partials men varje del är enorm).
- `BotTaskRunner.cs` = 81 KB med 71 trådnings-/async-träffar.
- Threading: utbrett `_ = Task.Run(...)` fire-and-forget, `async void`-eventhandlers (6 enbart i `MainWindow.xaml.cs`), `Dispatcher.BeginInvoke`, manuella CTS-fält.
- `LoopController.cs` är en **påbörjad** extraktion (gate/CTS/closing-flagga med strukturerad loggning) — tydlig mall att fortsätta på.
- Beroenden: Desktop → Worker + Core, Worker → Core. Desktop-codebehind driver `TravianClient` direkt = hård UI/logik-koppling.

Målet: stegvisa, beteendebevarande refaktoriseringar med låg risk och hög nytta, som gör framtida AI-assisterad utveckling säkrare.

## Status / Framsteg

| Rank | Status | Senast uppdaterad |
|------|--------|-------------------|
| 1 | 🟢 Klar | 2026-06-03 |
| 2 | 🟢 Klar (lätt variant) | 2026-06-03 |
| 3 | 🟡 Pågår | 2026-06-03 |
| 4 | ⬜ Ej påbörjad | — |
| 5 | ⬜ Ej påbörjad | — |

### Rank 1 – delsteg
- [x] **Steg 1a:** Routa kvarvarande råa `_operationCts = new CancellationTokenSource()` genom `_loopController.CreateCts("operation")` för enhetlig strukturerad loggning. 16 call-sites i 6 filer (`Farming.Manual`, `Inbox` ×2, `Farming.FarmLists` ×3, `Farming.Natars`, `Resources.Actions` ×8, `SendTroops.Catapults`). Beteendebevarande (endast loggrad tillkommer). Bygger rent (0 fel/varningar). _Lämnad: lokal `using var operationCts` i `Resources.Actions.cs` (egen dispose-semantik)._
- [x] **Steg 1b:** Samlade det upprepade `_operationCts?.Dispose(); _operationCts = null;`-mönstret (**21 finally-block i 9 filer**) i privat hjälpare `DisposeOperationCts()` på `MainWindow` (nära `BeginOperation`). Lagd på MainWindow t.v. eftersom fältet ägs där; flyttas till `LoopController` i 1c. Bygger rent (0 fel/varningar). _Cancel-anropen (`_operationCts?.Cancel()`) lämnas — separat ansvar, hanteras i 1c._
- [x] **Steg 1c:** Flyttade `_operationCts` in i `LoopController` bakom ett litet API: `StartOperation(label)` (returnerar token), `CancelOperation()`, `HasActiveOperation`, `DisposeOperation()` (+ disposas i `LoopController.Dispose()`). Tog bort fältet ur `MainWindow`; create-block (13 ställen, 2 rader → 1), cancel (8 ställen) och null-check (1) omdirigerade. `DisposeOperationCts()` delegerar nu. Semantik oförändrad (plain field-access, ingen ny låsning). Bygger rent; Worker-tester 309/309 gröna. _Pre-existerande fel: `SessionPacerTests.SleepingStatusText_DoesNotShowResumeCountdown` (misslyckas även på baseline, ej relaterat)._
- [x] **Steg 1d:** Flyttade kvarvarande CTS-fält in i `LoopController`. `MainWindow` äger nu **inga** CTS-fält. Nya API:er: `StartLoop`/`CancelLoop`; `StartVillageSwitch`/`CancelVillageSwitch`/`DisposeVillageSwitch`; `QueueAutoRunRootToken` + `StartAutoQueueRun`/`CancelAutoQueueRun`/`DisposeAutoQueueRun` + `CancelQueueAutoRunRoot`. Alla CTS disposas nu i `LoopController.Dispose()`. Semantik oförändrad (länkad root→child bevarad; `_loopCts` disposas fortfarande aldrig under drift). Kompilerar rent (0 fel/varningar, verifierat via separat output-mapp då appen kördes live).

**Rank 1 är därmed klar.** `LoopController` äger nu hela CTS-/loop-state-livscykeln; `MainWindow` driver den via metoder.

### Rank 2 – async void-säkerhet (lätt variant)
- **Upptäckt:** Det globala skyddsnätet finns redan i `App.xaml.cs` (`DispatcherUnhandledException` → loggar + `e.Handled = true`, plus `AppDomain`/`TaskScheduler`-handlers). `async void`-undantag kraschar alltså **inte** appen redan idag. Dessutom har nästan alla ~35 handlers redan loggande try/catch (direkt eller via Core-metoder). Premissen "async void kan krascha UI" var alltså redan åtgärdad.
- [x] La till återanvändbar hjälpare `AsyncUi.GuardAsync(action, log)` (`src/TbotUltra.Desktop/AsyncUi.cs`) — loggar oväntade fel till **in-app-loggen** (`AppendLog`) med handler-namn istället för bara filen `logs/desktop-unhandled.log`; ignorerar `OperationCanceledException`.
- [x] Applicerade på den enda klart oskyddade handlern: `AccountsButton_Click` (tidiga `await` före all try; inre try saknade catch). Extraherade kroppen till `AccountsButtonClickAsync()` och wrappade. Bygger rent; tester 39/40 (samma pre-existerande SessionPacer-fel).
- _Full per-handler-wrapping bedömdes som låg marginalnytta och valdes bort medvetet — hjälparen finns nu för framtida handlers._

### Rank 3 – krympa MainWindow.xaml.cs (pågår)
Mönster: flytta sammanhängande, helst rena/statiska metodkluster till nya tematiska partials (`MainWindow.X.cs`). Eftersom alla partials delar samma klass är detta beteendebevarande; enda risken är saknade `using`. Verifieras med build + tester per skiva.
- [x] **Steg 3a:** Flyttade hela "deferred wait"-utvärderingsklustret (records + 14 rena `static`-metoder/dictionaries, ~357 rader) till ny partial `MainWindow.DeferredWaits.cs`. `MainWindow.xaml.cs`: **4042 → 3684 rader**. Ren relocation (inga signaturer/logik ändrade). Bygger rent; tester 39/40 (samma pre-existerande SessionPacer-fel). _Not: en ` `-literal hamnade som faktiskt NBSP-tecken i källan (terminalbegränsning) — kompilerar identiskt._
- [x] **Steg 3b:** Flyttade 8 server/konto-hjälpare (`SyncServerFromActiveAccount`, `FetchDefaultServerOptionsAsync`, `FetchEffectiveServerOptions`, `RefreshAccountPicker`, `UpdateAccountInfoLabel`, `AbbreviateServerSpeed`, `GetActiveAccountServerUrl`, `ExtractServerSpeedLabel`, ~226 rader) till ny partial `MainWindow.ServerAccount.cs`. `MainWindow.xaml.cs`: **3684 → 3458 rader**. Ren relocation. Bygger rent (0 varningar); tester 39/40.
- [x] **Steg 3c:** Flyttade 8 loop-/exekverings-indikatormetoder (`SetLoopIndicator`, `ApplyStartLoopButtonVisual`, `SetLoopStateBadge`, `UpdateExecutionStateIndicator`, `UpdateExecutionStateIndicatorOnUiThread`, `UpdateBuildQueueStatusText`, `TickBuildQueueCountdown`, `FormatCountdown`, ~274 rader) till ny partial `MainWindow.LoopIndicators.cs`. `MainWindow.xaml.cs`: **3458 → 3184 rader**. Ren relocation. Bygger rent (0 varningar); tester 39/40.

**Rank 3 sammanfattning hittills:** `MainWindow.xaml.cs` **4042 → 3184 rader** (−858, ~21%) fördelat på tre nya tematiska partials (`DeferredWaits`, `ServerAccount`, `LoopIndicators`). Mönstret är etablerat; fler skivor kan göras vid behov (t.ex. login/session-flöden, build-queue-deferred-refresh).

## Rekommenderade refaktoriseringar

| Rank | Område | Rader (faktisk) | Problem | Föreslagen åtgärd | Risk | Reward |
|------|--------|-----------------|---------|-------------------|------|--------|
| 1 | Threading/loop-state | `LoopController.cs` 193 · `AutomationLoop.Ui.cs` 795 · `ContinuousLoop.cs` 634 · `AutomationLoop.cs` 370 | CTS-fält + `_ = Task.Run` spridda i code-behind; race-/freeze-risk svår att felsöka | Fortsätt påbörjad `LoopController`-extraktion: flytta in `_loopCts`/`_operationCts`/`_autoQueueRunCts`-livscykel och fire-and-forget-körningar bakom dess gate+loggning | Låg | Hög |
| 2 | `async void`-handlers | `MainWindow.xaml.cs` 3 477 (6 st `async void`) + flera partials | Obevakade undantag i `async void` kan krascha UI | Inför en `SafeInvokeAsync`-hjälpare och låt handlers delegera dit (try/catch→log) | Låg | Hög |
| 3 | Monolitisk code-behind | `MainWindow.xaml.cs` 3 477 · 28 partials = 15 018 rader totalt · bara 6 VMs | Kvarvarande logik i monolitisk code-behind utöver befintliga partials | Flytta residual-logik till tematiska partials/services enligt befintligt split-mönster | Låg | Medel |
| 4 | `TravianClient` god-class | `.cs` 4 512 · `.Buildings.cs` 3 528 · `.Resources.cs` 2 090 · `.Hero.cs` 2 058 | Tusentals rader, stateless parsing blandat med I/O → otestbart | Extrahera rena, stateless parsers/helpers till egna klasser med enhetstester | Medel | Hög |
| 5 | UI/logik-koppling | `TroopTrainingViewModel.cs` 969 (mall) · `MainWindow.TroopTraining.cs` 619 | Code-behind anropar `TravianClient` direkt; ingen VM-gräns | Använd `TroopTrainingViewModel` som mall: flytta en panels logik till VM/service | Medel | Medel |

**Rekommenderad start:** Fortsätt `LoopController`-extraktionen (Rank 1) — lägst risk, störst nytta för stabilitet och felsökning, och mönstret är redan etablerat.

**Lämna orört just nu:** `TravianClient`-partialernas faktiska Travian-DOM-/spel-logik — fungerande och bräcklig; rör bara stateless parsing, inte navigations-/sekvenslogiken.