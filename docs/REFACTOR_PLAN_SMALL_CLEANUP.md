# Refactor Plan: Small Cleanup and Reproducible Build

## Problem Statement

The solution is structurally sound and has a substantial focused test suite, but incremental changes are slowed by a handful of oversized UI and automation modules. In addition, the local .NET SDK installation cannot resolve the workload auto-import and workload-manifest SDK locators, so the Desktop project and full solution build fail without compiler diagnostics. The Worker project builds and its 925 tests pass, making this an environment/toolchain blocker rather than an established product-code regression.

## Solution

Make the build reproducible first, then perform only small, behavior-preserving cleanup passes in the highest-complexity areas. Keep the existing layer direction, contracts, task behavior, navigation, selectors, persisted formats, and UI behavior unchanged. Defer architectural migrations such as a broad MVVM rewrite or extracting collaborators from the Travian client facade.

## Commits

1. Reproduce the Desktop and solution build failure with the documented Release build command, record the installed SDK/workload resolver state, and identify the minimal repair required for the local .NET 8 toolchain. Do not alter application code in this commit.

2. Repair or document the required .NET SDK/workload installation so the workload resolver locators are available, then verify the Desktop project and the full solution build with zero warnings and errors. Keep the project target frameworks and package graph unchanged.

3. Add a lightweight build-environment preflight to the existing developer/release validation path only if the toolchain failure cannot otherwise be reported clearly. The preflight must fail with an actionable message before the application build and must not install workloads automatically.

4. Add focused characterization coverage around the continuous automation-loop selection, cancellation, pacing, and UI-state boundaries that are touched by future cleanup. Preserve existing scheduling order, queue deadlines, cancellation ownership, and busy-state behavior.

5. Simplify the continuous-loop UI orchestration in small internal moves: group related private state and methods by existing concern, remove only demonstrably redundant local control flow, and retain the existing shared UI-thread helpers and loop-controller ownership. Each move must preserve command state, dispatcher behavior, and shutdown semantics.

6. Add or extend tests for every behavior clarified in the loop cleanup, including cancellation while work is active, deferred work preservation, and UI responsiveness. Keep tests at public or observable boundaries rather than testing private implementation details.

7. Do a similarly narrow cleanup pass for the construction upgrade flow: isolate pure decision/parsing/calculation fragments only when they have an existing domain meaning or more than one consumer, while retaining the exact navigation and state-changing click order in the browser-facing facade.

8. Add fixture-backed parser or calculator tests for any extracted pure behavior, and run the construction parity checks. Do not change Official selectors, paths, retries, or fallback behavior during this refactor pass.

9. Review the touched areas for dead private code and duplicate one-off helpers using symbol, XAML, configuration, serialization, reflection, and event-binding searches. Delete only candidates proven unused; otherwise retain them and record the reason.

10. Run the documented Release build and full Desktop and Worker test suites after every completed pass. Finish with the relevant parity checks and a short maintainer note describing the stable build prerequisite and intentionally deferred architectural work.

## Decision Document

- The work is behavior-preserving maintenance, not a redesign.
- Build reproducibility is the first gate because a full Desktop/solution build currently cannot run in the inspected environment.
- The existing dependency direction remains Desktop to Worker to Core.
- Main-window loop orchestration and browser automation flows are the first cleanup candidates because they concentrate the largest modules and operational complexity.
- Existing UI-thread helpers, loop lifecycle ownership, public task contracts, persisted state formats, and Official-only browser behavior remain stable.
- New helpers are allowed only for shared logic, a clear domain operation, or meaningful encapsulation of complexity, errors, retries, or state.
- A broad MVVM migration, facade collaborator extraction, framework upgrade, package upgrade, selector replacement, and navigation changes are separate future migrations.

## Testing Decisions

- Good tests exercise observable behavior such as build success, task selection, cancellation, queue preservation, parser output, and visible command state; they do not assert private method layout.
- The continuous-loop orchestration, construction decision/parser boundaries, and build validation path receive coverage when touched.
- Reuse the existing focused queue/loop, construction, parser-fixture, WPF smoke, and release-bundle test patterns as prior art.
- Browser-facing changes require the existing parity matrix checks; selector or navigation changes would additionally require verified Official fixtures and a live check, but are out of scope here.

## Out of Scope

- Broad MVVM migration or a rewrite of MainWindow.
- Replacing the TravianClient facade with new collaborator objects.
- Functional changes to automation, queueing, UI, persistence, selectors, navigation, retries, or server support.
- Package/framework upgrades and changes to the public configuration or task contracts.
- Automatic installation or modification of developer machine workloads by application scripts.

## Further Notes

Current observation: the Worker project builds successfully and 925 Worker tests pass. The Desktop and full solution builds stop because the installed .NET SDK cannot find the workload auto-import and workload-manifest SDK locator directories; no application compiler errors were produced.
