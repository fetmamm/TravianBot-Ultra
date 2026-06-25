# 2026-06-25 — TravianClient domain seams and refactor status

## Context

`TravianClient` and `MainWindow` had grown into very large god-types
(`TravianClient` ~27k lines across partials, 96 public methods, 121 shared
fields; `MainWindow` ~25k lines, 232 fields). A staged, behavior-preserving
refactor was carried out to make files easier to navigate and change.

## Decisions

**Done (behavior-preserving):**

1. **File-map + conventions** — `docs/ARCHITECTURE.md` added; partial/folder
   naming conventions documented.
2. **Folder grouping** — `Services/Automation/` split into domain folders
   (`Core/`, `Farming/`, `Buildings/`, `Hero/`, `Resources/`, `Combat/`,
   `Training/`, `Features/`, `Travco/`). Namespaces are explicit and unchanged.
3. **God-file splits** — `BotTaskRunner`, `BrowserSession`, `VillageSettingsStore`
   and `TroopTrainingViewModel` made `partial` and split by concern. Pure moves
   of whole members; no logic changed.
4. **MVVM collection ownership** — the panel-bound `ObservableCollection`s moved
   off `MainWindow` code-behind onto view models (`ResourceTransferViewModel`,
   `ReinforcementViewModel`, `FarmListsViewModel`, `TravianQueueViewModel`,
   `AutomationLoopViewModel`, `AlarmsViewModel`, `TerminalViewModel`). Code-behind
   delegates and mutates in place; scan/persist logic stays in code-behind.
5. **Domain seams** — `TravianClient` now implements narrow domain interfaces
   `IFarmingClient`, `IBuildingClient`, `IHeroClient`, `ICombatClient`,
   `ISessionClient`. Contracts only; no method was moved out of the facade.

**Deferred — collaborator extraction (moving domain logic into separate
classes behind the seams) and the deeper testability work it enables:**

Not done autonomously, on purpose:

- Per [Engineering Notes §8], `TravianClient`'s working navigation/sequence
  logic must be left untouched during pure refactoring. Real extraction would
  move that sequence logic.
- The domain methods share pervasive mutable state (`_page`, `_config`,
  `_session`, ~121 fields) and shared private helpers (`EnsureLoggedInAsync`,
  `GotoAsync`, `Notify`, …). Extracting a domain cleanly means either touching
  the sequence logic or exposing most of the internal surface.
- A single operation spans **session + domain** (every flow calls `LoginAsync`
  first, then domain calls), so consumers cannot narrow to one domain interface
  either — the facade is genuinely used as a unit per operation.
- `TravianClient` drives Playwright against a live Travian server and is **not
  covered by unit tests**; correctness can only be confirmed by live runs.

**Not warranted:** moving DTOs to `Core`. There is no type-name duplication
between `Worker.Domain` and `Desktop.Models`, and Desktop already depends on
Worker, so the move would be churn without removing duplication.

## Recommended path for the deferred work

Extract one domain at a time, behind its existing seam, **with a live
smoke-test of that domain between each step** (start the Desktop app and
exercise farming → buildings → hero → combat). Treat shared state explicitly
(extend `TravianSessionCache`) rather than passing the whole facade around. Do
not do this as an unattended sweep.

[Engineering Notes §8]: ../ENGINEERING_NOTES.md
