# Tbot Ultra

`Preview` of the program

![Version](https://img.shields.io/badge/Version-v0.3.7-blue?style=for-the-badge)

<p align="center">
  <img src="assets/images/tbot_v037.png" alt="Tbot Ultra Dashboard">
</p>

---

Join our `Discord!`

Here you can ask questions, report bugs and come with suggestions for new features.

[![Join the Discord](https://img.shields.io/badge/Discord-Join%20the%20community-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.gg/7bgzKy9sHK)

![Platform](https://img.shields.io/badge/Platform-Windows-blue?style=for-the-badge)
![Status](https://img.shields.io/badge/Status-Active%20Development-brightgreen?style=for-the-badge)

---

## TBot Ultra - Advanced Travian Bot
Advanced open-source `Travian Bot` for Travian Legends with automation, farming, building management, resource optimization and `multi-village support`.

Also works for SS-Travian private server but the main goal forward is the official Travian servers.

## What can this program do?

Tbot Ultra is an automation tool for Travian. It helps players automate repetitive tasks such as village management, resource handling, farming, troops, construction and so much more.
It is made to be realistic and human, and to reduce the boring tasks of travian.

Compatible with:

- `Official Travian` — Travian Legends 4.6+ — Official servers

- `SS-Travi` — T4.4 — Private server (Latest robust version = 0.2.6)

`NOTE:` It is important that the language is set to `English` in the travian browser settings page.

`Way forward:` The goal of the project forward is to focus on the official servers. SS-Travi is not being developement anymore.

## Features:

- Automatic building, resources and buildings
- Hero send adventures
- Revive hero
- Spend hero attribute points
- Use hero inventory resources for buildings, smithy, brewery, town hall celebrations
- Collect daily quests
- Collect tasks
- Read messages and reports
- Upgrade troops in smithy
- Send resources between own villages
- Send reinforcements between own villages
- Auto Farming, send lists and manua farming
- Create multiple farmlists with selected village and default troops
- Send catapult waves
- Session pacing
- Auto sleep (logout and wait)
- Captcha solver (SS-Travi servers)
- Oasis scan for whole map (and add to farmlists)
- Travco inactive search (and add to farmlists)
- Reduce adventure duration 25% button / video watch
- Increase adventure to hard button / video watch
- Auto town hall celebrations


- Read ingame status
- Read village data
- Clean dark mode UI
- Multi-village support
- Calculate building time and resource cost for buildings + resourcefields
- Easy update to new version via the app (checks at startup and hourly while open)
- Support to use Proxy per account

## Future functions:

- +15% resource production for 8h automatic watch video
- construct 25% faster button / video watch
- Detect incomming attacks and send notification
- Auto scout players and farms
- Auto bid on auctions
- Auction hustle to gain silver (buy cheap, sell expensive)

All features is configurable to all the players villages. So different villages can use different settings (ex. only build troops in one village and only buildings in another etc.)

## Known bugs:

Please visit the [issues page](https://github.com/fetmamm/Tbot_ultra_new/issues) for known bugs or the [Discord channel](https://discord.gg/7bgzKy9sHK).

---

## Download

Goto the latest [Releases](../../releases) page and download:

- **Portable** (`tbot-ultra-win-x64-...-portable.zip`)

Extract the zip file

Run `Tbot Ultra.exe` inside the extracted folder.

Windows may show a SmartScreen or certificate warning because the application is not digitally signed. This is normal and does not indicate a virus or security issue.

If prompted, click More info → Run anyway.

Set up your account and server inside the app (**Account → Manage**)

**Multiple accounts at once:** with the portable build, extract a separate copy of the
folder per account (e.g. `Tbot-account1\`, `Tbot-account2\`) and run them at the same
time — each copy keeps its own config, account, browser session and logs. Don't run two
instances from the same folder.

---

## Solution layout

```
TbotUltra.sln
├── TbotUltra.Core          shared domain (no UI, no browser, no I/O)
├── TbotUltra.Worker        engine: browser automation + queue executor
├── TbotUltra.Worker.Tests  xUnit tests for the worker
├── TbotUltra.Desktop       WPF UI (the app the user runs)
└── TbotUltra.Desktop.Tests xUnit tests for desktop view models / services
```

Dependency direction: `Desktop → Worker → Core`. Core has no dependency on
the others.

---

## Top-level folders

| Folder | Purpose |
|---|---|
| `src/` | All C# projects (see "Source tree" below). |
| `config/` | Runtime configuration and persisted state (queue, accounts, caches). |
| `Captcha_solver/` | Standalone Python + tiny C# launcher for captcha solving. |
| `assets/` | App icons used by the WPF project and installer. |
| `installer/` | Inno Setup script (`TbotUltraSetup.iss`) for building the Windows installer. |
| `playwright/` | Local Playwright browser cache (downloaded on first run). |
| `ms-playwright/` | Same — Playwright's default cache location. |
| `.release-template/` | Files copied into release builds (`README_RELEASE.txt`, env template, default config). |
| `.github/workflows/` | CI: `build-exe-on-version.yml`, `discord-push.yml`. |
| `artifacts/` | Local verification artifacts (gitignored). |
| `logs/` | Runtime logs from the desktop app (gitignored). |
| `temp_build_out/` | Scratch space for ad-hoc local builds (gitignored). |

Top-level files worth knowing:

- `AGENTS.md` — coding rules for AI assistants working in this repo.
- `VERSION` — current version string, consumed by CI.
- `Start_Tbot.bat` / `Start_Tbot_UI.vbs` — launchers.
- `Smoke_Check.bat` — build + run worker tests.

---

## Source tree

### `src/TbotUltra.Core/` — shared domain

Pure C#, no WPF, no Playwright. Safe to reference from both Worker and tests.

```
Accounts/         AccountKeyNormalizer, AccountStoragePaths, constants
Configuration/    BotOptions + factory, .env parser, payload key mapping
Tasks/            TaskCatalog + all task payload records (Building, Hero,
                  Farming, Reinforcements, ResourceTransfer, TroopTraining, …)
Travian/          Game-data catalogs (TroopCatalog, TroopTrainingBuildingType)
```

### `src/TbotUltra.Worker/` — engine

Runs the browser, executes queue items, talks to Travian.

```
Program.cs                  worker host entrypoint
Worker.cs                   top-level background service loop
ProjectContext.cs           resolves project root + paths at runtime
ProjectRootLocator.cs

Configuration/              AccountOptions binding
Domain/                     QueueItem, QueueGroup, QueueStatus,
                            TravianModels, CatapultWaveLimits, exceptions
Infrastructure/             BrowserSession (Playwright wrapper)

Services/
  Accounts/                 EnvAccountProvider, AccountAnalysisStore,
                            NatarFarmCacheStore
  Automation/               TravianClient.* — partial classes per concern
                            (Buildings, Hero, Inbox, Resources, Catapults,
                             NpcTrade, ResourceTransfer, Reinforcements,
                             TroopTraining, BreweryCelebration, CapitalCache,
                             CaptchaAutoSolve, RetryPolicy, Selectors)
                            CatapultWavePlanner
  Catalogs/                 BuildingCatalogService, TaskCatalog
  Queue/                    JsonQueueStore, PriorityFifoQueueScheduler,
                            QueueExecutor, QueueGroupCatalog, interfaces
  BotTaskRunner.cs          dispatches a TaskDescriptor onto TravianClient
  CaptchaAutoSolver.cs      bridge to the Python solver
```

### `src/TbotUltra.Desktop/` — WPF UI

The main app. MainWindow is split into many partial files, one per feature
tab/area, so the codebase scales without one massive file.

```
App.xaml / App.xaml.cs            WPF entry
MainWindow.xaml                   root window
MainWindow.<Feature>.cs           partial classes, grouped by feature:
                                    AutomationLoop, Buildings, ContinuousLoop,
                                    Dashboard.*, Farming.*, Hero, Inbox,
                                    Logging.*, QueueExecution, QueueUi.*,
                                    Reinforcements, Resources.*,
                                    ResourceTransfer, SendTroops.Catapults,
                                    TroopTraining
<Name>Window.xaml(.cs)            dialogs (Accounts, AddQueueItem, Settings,
                                  ServerList, Support, FunctionTest, …)

Assets/                           app icon
Common/                           BaseViewModel, RelayCommand
Models/                           row/option types bound to the UI lists
Services/
  Logging/  LogClassifier
  Orchestration/  LoopController
  AccountDeletionService, BotConfigStore, DesktopBotService,
  EnvAccountStore, ManualFarmingPreferenceStore,
  ServerCatalogStore, ServerDiscoveryService
Themes/                           Badges/Buttons/Toggles/Tooltips resources
ViewModels/                       Hero, Inbox, Main, Resources, TroopTraining
Views/                            BuildingsPanel, HeroPanel, InboxPanel,
                                  TroopsPanel (user controls hosted by MainWindow)
```

### `src/TbotUltra.Worker.Tests/` & `src/TbotUltra.Desktop.Tests/`

xUnit. Each test file targets one class (e.g.
`QueueStoreAndSchedulerTests`, `BuildingCatalogServiceTests`,
`HeroViewModelTests`, `ServerDiscoveryServiceTests`).

---

## `config/` — runtime state

```
bot.json                   global program/server options only
buildings_catalog.json     static building data
servers.user.json          user's saved server list
accounts/<account>/        per-account state:
                             settings.json       account automation/UI settings
                             queue.json          persisted task queue (one per account)
                             queue.json.lock     file lock to serialize queue writes
                             villages.json       per-village enable / NPC / group choices + hero home
                             village_cache.json  remembered buildings/fields/storage per village
                             smithy_upgrade.json per-village Smithy targets
                             troop_training.json per-village troop-training rules
account-analysis/          cached account snapshots
cache/                     capital-state, manual-farming prefs, natar-farms
```

`.env` lives at repo root and holds credentials / per-account secrets.

---

## `Captcha_solver/`

Optional component. C# launcher (`Program.cs`, `Program_test.csproj`) plus a
Python ML project under `math_ai/` (Keras model, training and inference
scripts, dataset folders). Started by the worker when an arithmetic captcha
needs solving.

---

## Conventions

- Code and UI are English (see `AGENTS.md`).
- `TravianClient` is intentionally split into many partial files — keep the
  same pattern when adding a new browser-driven feature.
- MainWindow likewise uses one partial per feature.
- Core has no references to Playwright or WPF — keep it that way.
- Don't commit anything under `artifacts/`, `logs/`, `temp_build_out/`,
  `bin/`, `obj/`, `playwright/`, `ms-playwright/`.

---

## Status

- Runtime + UI fully C# (`TbotUltra.Desktop` + `TbotUltra.Worker`).
- Queue persists per account in `config/accounts/<account>/queue.json`, managed from the Queue tab.
- Captcha solving handled by the Python module in `Captcha_solver/`.

---

## Disclaimer

This project is provided for educational purposes. Automating gameplay may
violate the terms of service of Travian and/or private servers, and can lead
to account bans. Use it at your own risk — the authors take no responsibility
for any consequences of using this software.

Credentials and per-account data are stored locally only (in `.env` and
`config/`, all gitignored) and are never committed to this repository.

---

## License

Released under the [MIT License](LICENSE).

---

## Keywords
Travian Bot, Travian Automation, Travian Legends, Travian Scripts, Travian Game Bot, Travian Farming Bot, Travian Automation Bot, Browser Game Automation, Browser Game Bot, Python Bot, C# Bot, Open Source Travian Bot, Travian Assistant, Travian Resource Management, Travian Village Management, Travian Auto Farm, Travian Auto Build, Travian Multi Village Bot, Travian Helper, Travian Tool, Travian Utility, Travian Gaming Automation, Python Automation, C# Automation, Browser Game Scripts.

