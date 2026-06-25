# Architecture & File Map

Navigation map for the solution: **where each feature lives**, plus the file/folder
conventions. Goal: find the right file fast (human or AI) without reading whole classes.

Read `docs/ENGINEERING_NOTES.md` first for rules, Official/SS-Travi behavior, and pitfalls.
This file is a map only — no behavior rules. Keep it current when structure changes.

---

## 1. Projects

| Project | Responsibility | Depends on |
|---|---|---|
| `TbotUltra.Core` | Config (`BotOptions`, `ServerFlavor`), task payloads, catalogs. No browser/UI. | — |
| `TbotUltra.Worker` | Game automation via Playwright. `TravianClient` owns server interaction, `BotTaskRunner` runs tasks. | Core |
| `TbotUltra.Desktop` | WPF UI: `MainWindow` partials + ViewModels. | Worker, Core |

Dependency direction: `Desktop → Worker → Core`. Tests: `*.Tests` per project.

---

## 2. Worker — `TravianClient` (server interaction)

One `partial class TravianClient` split by domain into `Services/Automation/<Domain>/`.
All parts share the same class state (fields/ctor live in `Core/TravianClient.cs`).
The folder is organizational only; the namespace stays as declared in each file.

The public surface is grouped behind narrow domain seams that `TravianClient`
implements directly: `IFarmingClient`, `IBuildingClient`, `IHeroClient`,
`ICombatClient`, `ISessionClient` (each `I*Client.cs` sits in its domain folder).
These are contracts only — no logic was extracted out of the facade.

| Folder | What | Key files |
|---|---|---|
| `Automation/Core/` | Client plumbing: login, navigation, session, captcha/manual-gate, account snapshot, villages, selectors, retry, page-tasks, capital cache | `TravianClient.cs` (state+ctor), `.Login`, `.Navigation`, `.UiSync`, `.ManualVerification`, `.CaptchaAutoSolve`, `.AccountSnapshot`, `.Villages`, `.Selectors`, `.RetryPolicy`, `.Tasks`, `.CapitalCache`; helpers `TravianSessionCache`, `TravianUrls`, `TravianParsing`, `CapitalCacheKey` |
| `Automation/Farming/` | Farm lists, list creation, Natar farming | `TravianClient.FarmLists`, `.FarmListCreation`, `.NatarFarming`; `FarmListLossStateClassifier` |
| `Automation/Buildings/` | Construction & upgrades | `TravianClient.Buildings`, `.Upgrade`; `BuildingDomParser`, `BuildingNames`, `ConstructionSlots`, `BuildQueueFingerprints`, `UpgradeMath` |
| `Automation/Hero/` | Hero, adventures, hero resources | `TravianClient.Hero`, `.HeroResourceTransfer`, `.AdventureDanger`; `HeroCalc` |
| `Automation/Resources/` | Resource read/transfer, NPC trade | `TravianClient.Resources`, `.ResourceTransfer`, `.NpcTrade`; `ResourceCapacitySnapshot` |
| `Automation/Combat/` | Catapult waves, reinforcements, manual attack | `TravianClient.Catapults`, `.Reinforcements`, `.ManualAttack`; `CatapultWavePlanner` |
| `Automation/Training/` | Troop training | `TravianClient.TroopTraining`; `TroopTrainingCalculator` |
| `Automation/Features/` | Daily quests, inbox, brewery & town-hall celebration, map oasis | `TravianClient.DailyQuests`, `.Inbox`, `.BreweryCelebration`, `.TownHallCelebration`, `.MapOasis`; `DailyQuestDomParser`, `MapOasisApiParser` |
| `Automation/Travco/` | Travco inactive search (standalone, not a `TravianClient` partial) | `TravcoInactiveSearch`, `TravcoInactiveSearchParser` |

---

## 3. Worker — `BotTaskRunner` (task orchestration)

One `partial class BotTaskRunner` in `Services/`, split by concern:

| File | What |
|---|---|
| `BotTaskRunner.cs` | Core: handler registry (`TaskHandlers`), fields, ctor, `ExecuteOnceAsync`, client-lease lifecycle, shutdown, map-oasis scan, shared records |
| `BotTaskRunner.Session.cs` | Login/logout, post-login snapshot, stable account signals |
| `BotTaskRunner.Farming.cs` | Farm-list / Natar public API |
| `BotTaskRunner.Combat.cs` | Catapult-wave reads & start |
| `BotTaskRunner.VillageReads.cs` | Village / resource / buildings / page reads |
| `BotTaskRunner.Hero.cs` | Hero adventure, revive, attributes, inventory |
| `BotTaskRunner.Features.cs` | Troop-training read, brewery/townhall, NPC, adventure tests, inbox, smithy reads |
| `BotTaskRunner.Travco.cs` | Travco tab open/scrape/close |
| `BotTaskRunner.TaskHandlers.cs` | The static `Execute*` task handlers + snapshot writers + result classification |

### Other Worker services
- `Services/Accounts/` — account provider, analysis store, hero/Natar caches.
- `Services/Queue/` — queue store, scheduler, executor, group catalog.
- `Services/Catalogs/` — building & task catalogs.
- `Services/` (root) — `CaptchaAutoSolver`, `BrowserFailureClassifier`.
- `Infrastructure/BrowserSession.cs` — Playwright browser lifecycle (partial; bonus-video,
  warmup/install and storage-state filtering live in `BrowserSession.<Area>.cs`).
- `Domain/` — Worker DTOs (`TravianModels`, `MapOasisModels`, `TravcoModels`, queue types, exceptions).

---

## 4. Core

- `Configuration/` — `BotOptions`, `BotOptionsFactory`, `BotOptionsPayloadApplier`, `ServerFlavor`, defaults, `ActionPacer`.
- `Tasks/` — task payloads (`*Payload.cs`), `TaskCatalog`, `TaskDescriptor`, `TaskGroup`.
- `Travian/` — `SmithyPageParser`, `TroopCatalog`, troop types.
- `Accounts/` — key normalizer, storage paths, analysis constants.

---

## 5. Desktop (WPF)

- `MainWindow.*.cs` — code-behind partials grouped by UI area (e.g. `MainWindow.Farming.FarmLists.cs`,
  `MainWindow.Buildings.Queueing.cs`, `MainWindow.Resources.Snapshot.cs`, `MainWindow.QueueUi.*.cs`).
  `MainWindow.xaml.cs` is the root partial.
- `ViewModels/` — MVVM view models (`BuildingsViewModel`, `HeroViewModel`, `ResourcesViewModel`,
  `TroopTrainingViewModel`, `InboxViewModel`, `TravcoToolsViewModel`, plus the panel-collection owners
  `ResourceTransferViewModel`, `ReinforcementViewModel`, `FarmListsViewModel`, `TravianQueueViewModel`,
  `AutomationLoopViewModel`, `AlarmsViewModel`, `TerminalViewModel`). New logic should land here, not in
  code-behind. Bound `ObservableCollection`s live on the view model; code-behind delegates and mutates in
  place (the scan/persist logic migrates later).
- `Services/` — desktop-side stores & orchestration: `Orchestration/` (`LoopController`, `SessionPacer`,
  `BackgroundTaskTracker`), `*Store.cs` (per-feature persistence), queue helpers, `DesktopBotService`.
- `Models/` — UI row/item models bound to the views.
- `Views/`, `Themes/`, `Assets/` — XAML windows, theme, resources.

---

## 6. File & structure conventions

Applies to the whole solution. The codebase already follows this — keep it consistent.

**File naming**
- One primary type per file; file name = type name (`LoopController.cs`).
  Small tightly-related helper/DTO types may share a file.
- Partial-class parts: `<ClassName>.<Area>.cs`, sub-grouped as `<ClassName>.<Area>.<SubArea>.cs`
  (e.g. `MainWindow.QueueUi.Display.cs`). The root partial holding state/ctor is `<ClassName>.cs`
  (or `<ClassName>.xaml.cs` for WPF code-behind).
- `<Area>`/`<SubArea>` are PascalCase, no `-`, `_`, or spaces.

**Partial-class splitting** (how the big classes were tamed)
- A god class becomes `partial`; each part is a cohesive feature/concern in its own file.
- Shared state (fields, ctor, shared records/DTOs) stays in the root file.
- Moving a method between parts never changes behavior — same class, same access.

**Folders**
- Folder = domain grouping for navigation (`Automation/Hero/`, `Services/Orchestration/`).
- Namespaces are declared explicitly per file and are independent of folder; moving a file
  between folders does not change its namespace or break references.

**Where to add new code**
- New server interaction → a `TravianClient.<Domain>` part under the matching `Automation/<Domain>/` folder.
- New task type → a handler in `BotTaskRunner.TaskHandlers.cs` + payload in `Core/Tasks/`.
- New UI logic → a `ViewModel`, not `MainWindow` code-behind.
- New shared model/contract used by both Worker and Desktop → put it in `Core`.

---

## 7. Quick "where is X?" index

| Looking for… | Go to |
|---|---|
| Login / session / captcha gate | `Automation/Core/TravianClient.Login.cs`, `.ManualVerification.cs` |
| Farm list send / create | `Automation/Farming/`, `BotTaskRunner.Farming.cs` |
| Building upgrade/construct logic | `Automation/Buildings/`, handlers in `BotTaskRunner.TaskHandlers.cs` |
| Hero adventures / attributes | `Automation/Hero/`, `BotTaskRunner.Hero.cs`, `ViewModels/HeroViewModel.cs` |
| Catapult waves / reinforcements | `Automation/Combat/` |
| Task dispatch / blocking rules | `BotTaskRunner.cs`, `BotTaskRunner.TaskHandlers.cs` |
| Queue scheduling/persistence | `Worker/Services/Queue/` |
| Config / payloads / flavor | `Core/Configuration/`, `Core/Tasks/` |
| Browser lifecycle | `Worker/Infrastructure/BrowserSession.cs` |
| UI loop/threading | `Desktop/Services/Orchestration/LoopController.cs` |
| Per-feature UI panel | `Desktop/MainWindow.<Area>.cs` + matching `ViewModels/<Area>ViewModel.cs` |
