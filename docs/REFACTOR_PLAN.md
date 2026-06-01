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