# Engineering Notes

Last updated: 2026-07-15

Read this file before changing selectors, paths, browser behavior, account state, or server logic.
Keep it short and current. Move implementation history to `docs/history/` or an ADR.

## Project overview

Tbot Ultra is an Official Travian automation desktop application.

| Project | Responsibility |
|---|---|
| `TbotUltra.Core` | Domain models, parsers, calculators, configuration, queue logic |
| `TbotUltra.Worker` | Browser automation, Travian client, orchestration, diagnostics |
| `TbotUltra.Desktop` | WPF UI, ViewModels, dialogs, presentation services |

Dependency direction is Desktop -> Worker -> Core. Core must not depend on Worker or Desktop.

Build and test:

```powershell
dotnet build TbotUltra.sln -c Release
dotnet test TbotUltra.sln -c Release --no-build
```

Published artifacts belong under `artifacts/`, never beside source files.

## Active architecture rules

- Official Travian is the only supported server flavor.
- Do not add legacy, SS-Travi, or runtime flavor-switching code.
- Keep parsers and calculators pure where possible; cover them with focused unit tests.
- Keep `TravianClient` methods thin: navigation/clicking plus delegation to parsers or handlers.
- Preserve existing navigation and click semantics unless the task explicitly changes them.
- Use handler dictionaries for gid/type-specific behavior instead of expanding switch chains.
- Desktop code should call Worker services through explicit interfaces and ViewModels.
- `LoopController` owns loop lifecycle and cancellation. UI code must not create competing loop state.
- Long-running UI commands must use the shared busy/guard pattern and restore UI state in `finally`.

## Official-only browser conventions

### URLs and paths

- Build Travian URLs through the existing path helpers.
- Paths are server-root relative and must not inherit an account base-url subdirectory.
- Normalize the configured base URL before combining it with a path.
- Keep query strings intact; do not manually concatenate unescaped parameters.

Common endpoints:

| Purpose | Official path |
|---|---|
| Village resources | `/dorf1.php` |
| Village buildings | `/dorf2.php` |
| Building slot | `/build.php?id={slot}` |
| Map | `/karte.php` |
| Reports | `/berichte.php` |
| Messages | `/messages.php` |

### Selectors

- Scope selectors to the relevant Official page, widget, dialog, or row.
- Prefer stable attributes and semantic structure over generated class names.
- For React views, require visible/actionable elements and scope to the open dialog.
- Add selector fallbacks only when they are verified Official DOM variants.
- Never reintroduce broad legacy fallbacks merely because they make a test pass.
- When a selector changes, verify it against live Official HTML or a captured fixture.
- Keep destructive or state-changing clicks exact; a safe navigation retry is not permission to repeat an action.
- Prefer a real Playwright `ClickAsync` (trusted, isTrusted=true, moves the pointer) on classic visible
  buttons; keep synthetic `dispatchEvent` only as an actionability fallback, or for genuine React inputs
  (native value + input/change) and hidden controls. Farm-list start buttons use this real-click-with-JS-fallback
  pattern (`TryRealClickFarmButtonAsync`); do not revert them to pure JS dispatch.

### Navigation and browser lifecycle

- Treat `DOMContentLoaded` as sufficient when the required page marker is checked afterward.
- Session pacing sleep must save StorageState and close the browser without calling Travian logout; manual logout and account switching remain explicit logout flows.
- In-app browser shutdowns (sleep, proxy handover, reset, account/browser changes) may retain filtered auth state for resumption. A real desktop-process startup and user-triggered app exit must delete every account's saved Playwright auth state; startup cleanup also covers crashes where exit cleanup could not run.
- Full login always starts at the lobby (`lobby.legends.travian.com/account`); never probe or submit credentials on the configured game server first, and do not use direct game-server login as fallback. Select the owned world by cached lobby wuid when available, accept any authenticated path on the configured game origin (`/`, `dorf1`, `dorf2`, etc.), and store newly learned wuid in the per-account analysis snapshot.
- After submitting lobby credentials, treat execution-context destruction as an expected navigation transition and wait for the rendered owned-world card marker before continuing. Both the lobby login submit and `Play now` must use normal action-pacing click delays.
- After lobby SSO commits a navigation to the configured game origin, do not wait for the old context to render or prove the game shell. Suppress the known CMP overlay before `Play now`, save filtered auth state without slow live-origin cleanup, open a clean game context in the same Chromium process, then close the lobby context and verify login there. Creating the replacement first keeps a browser window present throughout; this early context boundary prevents the first consent-stack flash and removes lobby scripts/service workers because storage filtering alone does not clear live page/runtime state.
- Saved browser state may retain Travian lobby/auth hosts needed for SSO, but must still remove sibling game-server state and consent storage.
- Detect browser crash/closed-page errors and surface a specific diagnostic message.
- Popup handling must account for isolated browser contexts and popup blockers.
- Portable builds must resolve Playwright from the bundled `.playwright` directory.
- Numeric parsing must handle locale separators and bidirectional Unicode markers.
- Login automation requires the supported English UI; fail clearly if required markers are absent.
- Anti-detection browser setup is intentional and must be preserved: launch with
  `--disable-blink-features=AutomationControlled`, clear `navigator.webdriver` via init-script, launch headed
  Chrome maximized, and use `ViewportSize.NoViewport` so the viewport follows the user's real screen/work area.
  Do not reintroduce hard-coded viewport dimensions or drop the automation flag.

## Configuration and persisted state

### File ownership

| Data | Scope |
|---|---|
| `bot.json` | Application-wide settings |
| Account settings | Account-specific configuration |
| Village settings | Village-specific automation and queue state |
| Runtime snapshots | Worker-produced current state, not user configuration |

- Use the existing path provider; do not derive data paths from the executable working directory.
- Writes that can be interrupted must use the atomic file helper.
- Retry known transient sharing/lock failures with bounded delays.
- Corrupt queue/state files must be quarantined and logged, not silently overwritten.
- New settings require the full pipeline: model, defaults, load/save, ViewModel, UI, tests.
- Persist stable village identity using coordinates/key, not display name alone.
- Queue items must retain their target village identity.

### Timing and cancellation

- Current min/max interval keys are authoritative; obsolete interval keys stay ignored.
- Normalize invalid ranges so minimum never exceeds maximum.
- Pass the active cancellation token through every cancellable operation.
- Do not replace an available token with `CancellationToken.None`.
- Cancellation is expected control flow: stop cleanly and avoid error alarms.
- Sleeping/paused state must not lose persisted work or start a second loop.

### Proxy behavior

- Proxy configuration is account-scoped and secret values must be sanitized in diagnostics.
- Test the proxy using the same effective settings as the browser session.
- Do not log credentials or embed them in user-visible URLs.
- Keep browser and HTTP-client proxy behavior aligned.
- Bonus-video traffic must use the account's current route; never bypass the proxy or change IP only for video.
- Isolated bonus video has separate 60s setup and 240s action caps. Expected ad/provider failure must not
  block construction, hero dispatch, or other automation.
- Construct, resource, production, and both hero bonus videos share one post-play policy: the protected
  60-second interval starts only after a trusted play click succeeds, and the post-play verification timeout
  is 120 seconds. Missing iframe/dialog, missing reward, or visible provider help/error text cannot end the
  attempt during the protected minute. Afterward, provider failure needs two consecutive confirmations while
  the player is present, or one confirmation when the player is demonstrably absent. Cancel, shutdown, and a
  closed/crashed browser may still abort immediately.
- Hard and -25% hero-adventure videos share an account-scoped 0-100% chance setting (default 70%);
  evaluate an independent random roll whenever either enabled function is invoked.
- Classify video failures and apply account+proxy cooldown: network 10m, no-ad/cookies 20m, timeout 30m,
  stale isolated session 5m, missing codec 6h. Known failures do not get an immediate second attempt.
- Preserve the typed video failure and cooldown deadline across features. Production bonus must defer to
  that deadline without replacing its saved timers or treating an unattempted video as a four-hour failure.
- Construct-faster success requires both confirmed video completion and target-specific construction evidence:
  the exact slot/level must be newly queued versus the pre-video snapshot, or complete immediately.
- Production-bonus inspection is complete only when the Advantages tab contains one box for each of
  lumber, clay, iron, and crop. An empty/partial React render must be retried, never classified as
  "nothing to activate"; after two 30s render attempts it is an alarm/task failure.
- Video network diagnostics log only sanitized ad host, network error code, status, and aggregate counts; never path/query,
  credentials, cookies, or tokens.

## Feature implementation conventions

### Core and Worker

- Parse HTML/JSON into domain models before making scheduling decisions.
- Put resource/time/capacity calculations in Core, not in WPF code-behind.
- Worker services own browser interaction, retries, and operational logging.
- Log enough context to identify account, village, operation, and failure stage without exposing secrets.
- Prefer explicit result types for expected unavailable/blocked states.

### Desktop UI

- UI text is English.
- Reuse theme resources and existing controls; do not hard-code a near-match color.
- Secondary explanations belong in the shared `i` info-icon tooltip when permanent text wastes space.
- Busy overlays must expose the red Cancel button when the operation supports cancellation.
- Disable duplicate action buttons while a command runs and restore them in `finally`.
- Marshal observable UI collections through the dispatcher.
- Use immutable/snapshot enumeration when sanitizing or exporting mutable collections.

### Diagnostics export

- Diagnostics generation shows the shared busy overlay and a red Cancel button.
- Cancellation must reach collection, manifest creation, and ZIP creation.
- The Diagnostics description lives in the shared info-icon tooltip.
- Sanitize settings, logs, paths, URLs, tokens, cookies, and proxy credentials.
- Enumerate mutable JSON arrays from a snapshot (`ToList`) before replacing elements.
- Warn that screenshots can contain visible game data and should be reviewed before sharing.
- Partial output must not be presented as a successful diagnostics archive.

## Construction and queue invariants

- `ActiveConstructions` is the source of truth for occupied construction slots.
- Track construction state per village and per slot/category.
- A full queue is a normal blocked state, not an exception.
- Check storage, prerequisites, available slot, and resources before clicking Build/Upgrade.
- Existing buildings and level-zero construction sites are distinct cases.
- Building-type selectors must be exact enough to avoid upgrading the wrong slot.
- Building-template choices are evaluated at their row position: available is green/selectable, missing prerequisites is yellow and opens
  a confirmation that can insert the complete ordered prerequisite chain, and tribe-incompatible or otherwise impossible is red/disabled.
- Auto-assigned template buildings must not consume ordinary slots explicitly reserved by later rows. Template constructs may fall back
  to a currently free, non-reserved ordinary slot if their planned slot becomes occupied before execution.
- Persist template resource scopes exactly, insert a template plan into the queue atomically, and correlate construct/upgrade rows so a
  runtime slot fallback rebinds the dependent upgrade before it can execute. Invalid template JSON must be quarantined before replacement.
- “Construct faster” controls are not build/upgrade actions.
- Construct-faster applies to both building slots and resource fields; verify results on `dorf2` and `dorf1` respectively before normal-click fallback.
- Town Hall celebration rows must calculate resource shortfall before clicking; generic research/hero-transfer links are not start actions.
- Mutually exclusive building rules must be evaluated before queue execution.
- GID 13 and other special buildings must use the catalog and verified Official behavior.
- Building catalog data must cover all supported tribes: Romans, Teutons, Gauls, Egyptians, and Huns.
- Vikings are not supported and require no catalog coverage.

## Village, hero, and React pitfalls

- Village switching uses stable village identifiers and coordinates; visible names may collide.
- Verify the active village after switching before performing state-changing actions.
- Missing villages in a refresh are quarantined until confirmed, not immediately deleted.
- Snapshot objects must be complete enough that consumers do not infer false zero/empty state.
- Hero transfers must scope controls to the active dialog and verify the selected target village.
- Troop/hero ownership and current location are separate facts.
- React inputs may require native value assignment plus input/change events.
- Never select the first matching button globally when multiple dialogs/widgets can exist.
- On an empty Official building slot, hero-resource cost reads and transfer clicks must be scoped to
  `#contract_building{gid}`; the page contains one transfer control per available building type.

## Caching, pacing, and logging

- Cache only data with a clear owner, invalidation rule, and safe stale behavior.
- Do not let an incomplete refresh erase the last valid snapshot without an explicit reason.
- Apply configured pacing to browser actions; do not add unbounded sleeps.
- Retry only transient failures and cap attempts.
- Detailed browser logging is a global development-only setting and must remain OFF by default. Its
  `[browser-trace:verbose]` records belong in the normal session log and must pass through the central
  sanitizer; never log credentials, cookies, tokens, headers, storage state, or complete HTML/JSON.
- Travian navigation and reload mutations must use the traced navigation adapter. Existing DOM mutations
  are observed centrally through the browser-session action observer; new browser mutations must use a
  traced action path and may not increase the source-guard baseline.
- Trace semantic reads and decisions, not every request or internal DOM poll. Every trace flow/operation
  must emit exactly one end event, including cancellation and exception paths.
- Construction follows the visible queue order per village for both manual and template rows. A deferred
  head row holds every later construction row in that village; another village may still run. Automatic
  requirement repair may insert or promote prerequisites ahead of the blocked row only after a live read
  confirms that the relevant Travian build slot is available; a full live build queue must not mutate repairs.
- Alarms are actionable failures. Expected waiting/blocked states belong in normal logs/status.
- Avoid alarm loops: deduplicate or rate-limit repeated failures with identical cause.
- Release builds uploaded to Discord must come from the documented clean publish workflow.
- Verify bundled runtime files and startup dependencies from the publish directory, not the developer tree.
- Authenticode signing is intentionally not required.

## Account access, queue deadlines, and alarms

- A known queue deadline is authoritative and must never be shortened by action pacing. Pacing only controls loop passes without a known deadline.
- An unreadable disabled farmlist timer is an estimated 60-second wait. Exact farmlist timers receive a random 5-15 second rendering margin.
- Account access is classified as `LoggedIn`, `LoggedOut`, `Unavailable`, `Restricted`, `Challenge`, or `Unknown`. Verify `Unknown` once on canonical `/dorf1.php`; network failures are `Unavailable` and do not count toward restriction.
- `Restricted`, `Challenge`, or three consecutive verified `Unknown` states create a persistent account-specific automation hold. Stop only that account's loop/session, retain its queue/settings, and require manual re-enable after review.
- Write an alarm to the session log only when its 30-minute deduplication window starts. Identical repeats update the visible occurrence count instead of writing another alarm line.
- Expected bonus-video fallback is a warning. A production-bonus task may surface at most its final task failure as an alarm during one run.

## Support status

| Area | Status | Notes |
|---|---|---|
| Official Travian | Supported | English UI and verified Official DOM |
| SS-Travi/legacy | Unsupported | Do not add fallbacks |
| Romans | Supported | Building catalog required |
| Teutons | Supported | Building catalog required |
| Gauls | Supported | Building catalog required |
| Egyptians | Supported | Building catalog required |
| Huns | Supported | Building catalog required |
| Vikings | Unsupported | Excluded by product decision |

## Official support recipe

When adding or repairing automation:

1. Capture the relevant Official page/dialog state.
2. Identify a stable scoped marker and actionable selector.
3. Parse the state into a domain model.
4. Put decisions/calculations in Core.
5. Keep browser steps in Worker and UI orchestration in Desktop.
6. Add focused parser/calculator tests and a regression test for the reported failure.
7. Verify cancellation, retries, logging, and persisted-state behavior.
8. Test from the publish output when packaging/runtime behavior is involved.
9. Record only durable rules here; archive detailed investigation/history.

## Target architecture

- Smaller domain services around construction, farming, hero, map, messages, and account state.
- Pure parsers/calculators with fixtures independent of Playwright.
- Thin browser adapters with explicit timeouts, cancellation, and result states.
- ViewModels that expose commands/state without browser or filesystem details.
- Central path, persistence, diagnostics, and release-packaging services.
- Tests split into fast domain tests, fixture-based browser parsing tests, and limited live smoke checks.

## Documentation history

Detailed historical notes are intentionally outside this active guide:

- [Pre-compression snapshot, 2026-07-14](history/engineering-notes-2026-07-14-pre-compression.md)
- [Engineering notes archive](history/engineering-notes-archive.md)
- [UI theme](adr/2026-06-03-ui-theme.md)
- [Multi-village state](adr/2026-06-05-multi-village.md)
- [Dashboard overview](adr/2026-06-06-dashboard-overview.md)
- [Shutdown cleanup](adr/2026-06-08-shutdown-cleanup.md)
- [Farmlists and Travco](adr/2026-06-09-farmlists-and-travco.md)
- [Construction queue](adr/2026-06-20-construction-queue.md)
- [Map oasis scan](adr/2026-06-20-map-oasis-scan.md)
- [Smithy and troop training](adr/2026-06-20-smithy-troop-training.md)
- [Town Hall celebration](adr/2026-06-20-town-hall-celebration.md)
- [TravianClient seams](adr/2026-06-25-travianclient-seams.md)

Before deleting or shortening a rule, confirm that its detail exists in the snapshot, archive, or an ADR.
