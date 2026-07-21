# MVVM and Architecture Migration Plan

## Problem Statement

The Desktop layer still concentrates orchestration, state mutation, persistence coordination, and UI event handling in `MainWindow` partials. The Worker layer is organized by partial files but its browser facade and task runner retain shared state and broad responsibilities. This makes cross-cutting changes expensive and increases the chance of UI-thread, cancellation, and browser-flow regressions.

## Solution

Migrate incrementally, with Desktop first. Move one observable UI area at a time from code-behind to an existing or new ViewModel backed by an explicit Desktop service. Then introduce narrow Worker collaborators behind the existing `TravianClient` and `BotTaskRunner` contracts. Each commit must keep public behavior, persisted formats, task names, browser navigation, and selector behavior unchanged.

## Commits

1. Establish characterization tests for each Desktop area before moving it: command enablement, visible state, collection updates, cancellation, and persistence side effects.
2. Document the current ownership of MainWindow state, ViewModels, services, commands, and XAML bindings for the first selected panel.
3. Move read-only projection state for that panel to its ViewModel while keeping existing event handlers as thin forwarding adapters.
4. Move panel commands to the ViewModel through injected service interfaces; preserve command names and busy/guard behavior.
5. Move persistence and worker orchestration from code-behind to the corresponding Desktop service, retaining atomic-store and cancellation rules.
6. Remove migrated MainWindow fields and forwarding code only after XAML smoke and behavioral tests prove the panel remains identical.
7. Repeat commits 1-6 for Dashboard/Village Overview, Queue, Buildings, Resources, Farming, Hero, Troop Training, and Settings in that order of dependency and risk.
8. Consolidate shared ViewModel command/busy-state conventions only after at least two migrated panels demonstrate identical requirements.
9. Add narrow Worker collaborators one domain at a time, starting with pure parsers/calculators and then stateful browser operations with stable boundaries.
10. Introduce a building-automation collaborator behind the existing building client contract; retain exact navigation, click, retry, and confirmation sequences in tests.
11. Repeat the collaborator migration for resources, hero, farming, training, combat, and session domains.
12. Split BotTaskRunner handler orchestration only where a domain collaborator owns a cohesive task family; retain the existing handler registry and public task contracts.
13. Retire partial-class-only organization only after a collaborator is fully adopted and its direct facade dependencies are removed.
14. Run Release build, Desktop and Worker test suites, WPF smoke tests, and the relevant parity matrix checks after every phase.

## Decision Document

- Desktop/MVVM migration comes before Worker architecture migration.
- Existing public Worker client interfaces remain stable during the migration.
- MainWindow remains the composition root and XAML event bridge until each panel is independently migrated.
- ViewModels own presentation state and commands; Desktop services own persistence and orchestration; Worker owns browser interaction.
- Core remains free of Worker and Desktop dependencies.
- Browser selectors, paths, action order, retries, persisted formats, and task payload contracts are not changed as part of this migration.

## Testing Decisions

- Tests assert observable UI state, command availability, persisted results, queue selection, cancellation, and browser-operation results rather than private method placement.
- Reuse existing ViewModel, queue/loop, WPF smoke, parser-fixture, and parity-matrix tests as prior art.
- Every browser-facing collaborator extraction retains existing fixture and live-check requirements.

## Out of Scope

- A rewrite of the WPF application or a framework migration.
- Functional changes to automation behavior, server support, selectors, paths, or persistence schemas.
- Replacing every MainWindow partial or TravianClient member in a single release.

## Further Notes

The local GitHub integration has no repository write permission, so this plan is intentionally stored in the repository documentation rather than filed as an issue.
