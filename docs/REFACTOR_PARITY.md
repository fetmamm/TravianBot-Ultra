# Refactor parity matrix

Use this checklist for behavior-preserving refactors. `docs/ENGINEERING_NOTES.md`
remains the source of truth for behavior and Official-specific constraints.

## Baseline

- Build: 0 warnings, 0 errors (`scripts/Build-Check.ps1`, 2026-07-19).
- Desktop tests: 603 passed.
- Worker tests: 875 passed.
- Public task names, payload keys, config formats and storage paths stay stable.

### WPF smoke tests

`WpfSmokeFixture` owns the single STA thread and the single `Application` the whole
Desktop test assembly shares, with the App.xaml theme dictionaries merged, so controls
resolve `StaticResource` exactly as at runtime. Anything that constructs a `Window` or
`UserControl` **must** join `[Collection(WpfSmokeCollection.Name)]` — a WPF object built
on a second STA thread deadlocks against that Application's dispatcher, and the failure
shows up as an unrelated test timing out.

These cover what unit tests cannot reach: XAML parse errors, missing or renamed
`StaticResource` keys, and embedded assets referenced by pack URI. Verified to actually
fail by renaming a style key and confirming the dependent panels went red.

## Parity checks

| Area | Behavior to preserve | Automated check | Live check when browser flow changes |
|---|---|---|---|
| Login | Slow-network retry, language gate and authenticated shell detection | Login/parser and failure-classifier tests | Login on Official with normal and throttled network |
| Village switch | Canonical `newdid`, exact village identity and active-village verification | URL and village reconciliation tests | Switch between two villages with distinct coordinates |
| Construction | Queue snapshot is authoritative; upgrade/construct navigation and click order stay unchanged | Construction selector, timer, parser and policy tests | Read dorf2, upgrade one level, construct one building |
| Hero | Read global signals on the current Travian page before fallback navigation | Hero status and decision tests | Run from dorf1, dorf2 and a build page |
| Resources | Preserve modern scan, compatibility fallback, ranking and waits | Resource parser/calculator tests | Compare all 18 fields and levels on dorf1 |
| Queue selection | Preserve group order, per-village rotation, defer times and preview purity | Continuous-loop selector tests with explicit clock | Compare Next task with the item actually executed |
| Proxy change | Preserve controlled relogin now or at next sleep | Proxy/session state tests | Change active proxy using both user choices |
| Shutdown | Cancel scoped work, release session gate and close browser within existing limits | LoopController, runner and browser lifecycle tests | Stop during navigation and during a deferred task |

Fixture-backed parser tests are required before changing Official selectors. A live
check is required only when navigation order, clicks, React interaction, or other
state-changing browser behavior changes; mechanical moves and pure extractions use
the automated parity checks above.

## Compatibility classification

| Candidate | Classification | Decision |
|---|---|---|
| `IFarmingClient`, `IBuildingClient`, `IHeroClient`, `ICombatClient`, `ISessionClient` | Public dormant seams | Keep API-stable; collaborator extraction is a separate architecture migration |
| `AccountStoragePaths.Legacy*` and their consumers | Active compatibility migration | Keep until a separately approved storage migration retires the old formats |
| Legacy action-pacing config fallback | Active compatibility migration | Keep and cover with old-only and mixed-config tests |
| Resource-field compatibility scan | Active Official safety fallback | Keep until Official live evidence and fixtures prove it unnecessary |
| Direct navigation path literals | Resolved 2026-07-19 | All build-slot URL composition routes through `Paths` (`BuildBySlotWithGid`, `BuildBySlotWithCategory`); remaining raw `*.php` strings are DOM selectors, not navigation |
| `BreweryPayload` | Removed 2026-07-19 (production-orphaned) | Deleted; `run_brewery_celebration`, `TaskPayloadKind.Brewery`, and `brewery_auto_celebration_enabled` remain live — the handler reads `BotOptions` directly |

Private/internal code may be deleted only after symbol, XAML, reflection, config-key
and serialized-name searches all show no consumer. A name containing `legacy` is not
evidence that code is dead.
