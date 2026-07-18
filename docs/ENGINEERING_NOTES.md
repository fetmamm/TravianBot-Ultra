# Engineering Notes

Last updated: 2026-07-18

Read this file before changing architecture, selectors, paths, browser behavior, persisted state, queueing,
or server logic. Keep it short and current: durable rules belong here; detailed decisions belong in ADRs;
implementation history belongs in `docs/history/`.

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

- Official Travian is the only supported server flavor. Do not add SS-Travi, legacy selector fallbacks, or
  runtime flavor switching. Historical flavor-aware branches are archived, not active conventions.
- Keep parsers and calculators pure where possible and cover them with focused tests.
- Keep `TravianClient` methods thin: navigation/clicking plus delegation to parsers or handlers.
- Preserve working navigation and click sequences unless the task explicitly changes them.
- Prefer handler dictionaries for gid/type behavior instead of growing switch chains.
- Desktop calls Worker through explicit interfaces and ViewModels; calculations do not belong in code-behind.
- `LoopController` owns loop lifecycle and cancellation. UI code must not create competing loop state.
- Long-running UI commands use the shared busy/guard pattern, expose Cancel when supported, and restore UI
  state in `finally`.

## Official-only paths and selectors

- Build URLs through existing path helpers. Paths are server-root relative, never relative to an account
  base-URL subdirectory. Normalize the base URL and preserve escaped query strings.
- Current-page matching requires the same path and every query parameter supplied by the target helper;
  server-added parameters may be extra. Never identify all `/build.php` URLs as the same slot.
- Common paths are `/dorf1.php`, `/dorf2.php`, `/build.php?id={slot}`, `/karte.php`, `/berichte.php`, and
  `/messages.php`.
- Scope selectors to the relevant Official page, widget, dialog, row, or building contract.
- Prefer stable attributes and semantic structure over generated class names.
- Selector changes are additive only for verified Official DOM variants. Do not add broad legacy fallbacks or
  replace a verified selector without evidence.
- Verify selector changes against live Official HTML or a captured fixture. React elements must be visible and
  actionable, and dialog actions must be scoped to the open dialog.
- State-changing clicks must be exact. Navigation retry does not permit repeating an action.
- Prefer trusted Playwright clicks for visible classic buttons. Synthetic dispatch is an actionability fallback
  or a tool for genuine React/hidden controls. Preserve the farm-list real-click-with-JS-fallback pattern.
- React inputs may require native value assignment plus `input`/`change` events.
- Numeric parsing must handle locale separators, Unicode minus, and bidirectional markers.
- The account server picker loads active and upcoming non-standard Official worlds from Travian Lobby's public
  `/api/metadata` and `/api/calendar`. Treat any published `.travian.com` URL whose type is not `normal`, or
  whose host does not match the regular regional `ts{N}.x{speed}.{region}` scheme, as `Special`; ignore entries
  without a URL.
- `Special` is the first picker group. Hide a matching user-added duplicate while discovery is available, but
  keep the persisted custom entry so it remains usable if the calendar cannot be reached later.

## Configuration and persisted state

- `bot.json` is application-wide; account settings are account-scoped; village settings and queue state are
  village-scoped; runtime snapshots are Worker-owned observations, not user configuration.
- Use the existing path provider. Never derive data paths from the executable working directory.
- Interruptible writes use the atomic file helper. Retry bounded transient lock/sharing failures.
- Quarantine and log corrupt queue/state files instead of silently overwriting them.
- New settings require the complete pipeline: model, defaults, load/save, ViewModel, UI, and tests.
- Persist village identity by coordinates/key, not display name. Names may collide or change; queue items retain
  their target village identity.
- The village status cache and queue use canonical coordinate keys. Legacy name-keyed entries are migrated only
  when coordinates can be resolved; active coordinates come from `#villageName[data-x][data-y]`.
- Queue status transitions are gated. `MarkDeferred` accepts only RUNNING items; Pending items use
  `UpdateDeferred`/`UpdatePending`. Check the returned boolean.
- New villages default to Auto enabled. The version-1 migration enables existing villages once; later manual
  Auto-off choices persist.

## Timing, cancellation, proxy and logging

- Normalize invalid timing ranges so minimum never exceeds maximum.
- Pass the active cancellation token through every cancellable operation. Never replace it with
  `CancellationToken.None`; cancellation is expected control flow, not an alarm.
- Sleeping/paused state must preserve work and must not start a competing loop.
- Known queue deadlines are authoritative and may not be shortened by pacing.
- Proxy settings are account-scoped. Browser, HTTP client, tests, and bonus video use the same effective route.
  Never log credentials or place them in user-visible URLs.
- Retry only transient failures with bounded attempts. Apply configured pacing; do not add unbounded sleeps.
- Alarms represent actionable failures. Expected waiting/blocking is normal status. Deduplicate identical alarms
  for 30 minutes; repeated occurrences update visible count without another alarm line.
- Detailed browser logging is development-only and off by default. Trace semantic operations, emit exactly one
  end event per flow, and sanitize all secrets. Navigation/mutations use the traced adapters.

## Browser, login and account access

- `DOMContentLoaded` is sufficient only when followed by a required page-marker check.
- Full login starts in the Travian lobby and enters the owned world through SSO; never submit credentials to the
  configured game server or add direct-server fallback.
- Preserve filtered SSO state only in in-app session transitions. Real process startup and user exit clear every
  account's saved Playwright auth state.
- Preserve the intentional headed/maximized anti-detection setup and `ViewportSize.NoViewport`.
- Login automation requires English UI and fails clearly when required markers are missing.
- Synchronize `BotOptions.BaseUrl` from the active account before login and fail fast when their normalized origins
  differ. An account switch invalidates the browser-session generation so a late `OpenPageAsync` cannot resurrect
  the previous account after shutdown.
- Lobby world matching treats speed labels (`x3`, etc.) as optional display metadata but rejects an explicit
  conflicting speed. If neither cached wuid nor automatic name/host matching reaches the configured origin,
  interactive login shows every owned lobby world with card details and UID. A failed selection reopens the picker
  with remaining worlds. Persist the selected wuid only after that world lands on the configured game origin.
- A recent-login cache hit is valid only on the configured game origin, never on lobby/login URLs, and still probes
  explicit restriction/challenge signals before skipping the full login check.
- Account `.env` mutations hold one shared per-file read-modify-write lock and use atomic replacement. New values are
  JSON-quoted so passwords round-trip spaces, quotes, backslashes, equals signs, hashes, and newlines; legacy values
  remain readable. New account keys add a stable identity hash and stores reject cross-identity overwrites.
- Account-analysis field updates are atomic per account/world; World UID, village, tribe, Gold Club, and settings
  writers must merge inside `AccountAnalysisStore.Update`, never load then save independently.
- Official special-server discovery routes through the active account proxy, never falls back to direct traffic when
  `NeverUseOwnIp` is enabled, isolates malformed source payloads, and uses a seven-day atomically written last-known-good
  cache when live sources are unavailable.
- Account holds are account-specific: restriction, challenge, or repeated verified unknown state stops only that
  account and preserves its queue/settings until manual re-enable.
- Detailed lifecycle, SSO, cleanup, and access rules: [browser/session ADR](adr/2026-07-18-browser-session-and-login.md).

## Feature implementation conventions

### Core and Worker

- Parse HTML/JSON into domain models before scheduling decisions.
- Put resource, time, capacity, prerequisite, and queue calculations in Core.
- Worker owns browser interaction, timeouts, retries, cancellation, and operational logging.
- Prefer explicit result types for expected unavailable, deferred, and blocked states.
- Log account, village, operation, and failure stage without exposing secrets.

### Desktop

- UI text is English. Reuse theme resources and controls; do not hard-code near-match colors.
- The Settings window is category-tabbed: General (including post-login automation), Pacing, Construction, Hero,
  Farming, Troops, and NPC / Trade. Place new settings in their owning category instead of growing one long page.
- Account-wide construction behavior, including storage look-ahead and construction start delay, belongs in the
  Construction settings category rather than the Buildings workspace.
- Secondary explanations use the shared `i` tooltip when permanent text wastes space.
- Disable duplicate commands while running; marshal observable collections through the dispatcher.
- Keep `DataGrid.RowHeight` unset or `Double.NaN`; the string `Auto` is not a WPF `Double`.
- Enumerate mutable collections through immutable snapshots when sanitizing/exporting.
- Village Overview is read-only and uses cache/queue snapshots; opening it never navigates or scans.
- Overview projections show only real deadlines and never mutate queue or scheduler state.

### New features

1. Capture the relevant Official page/dialog state and identify stable scoped markers.
2. Add only verified Official selectors and use existing root-relative path helpers.
3. Parse into domain models; keep decisions/calculations outside browser and WPF code.
4. Reuse queue, cancellation, pacing, persistence, logging, and busy-state patterns.
5. Add focused parser/calculator tests and a regression test for the reported failure.
6. Verify retries, cancellation, secrets, persisted state, and publish output when applicable.
7. Record durable cross-cutting rules here; put feature decisions in an ADR and history in the archive.

## Construction and queue invariants

- `ActiveConstructions` is the source of truth for occupied construction slots. A full queue is a normal blocked
  state, not an exception.
- Construction follows visible per-village queue order. A deferred head blocks later construction in that village;
  verified automatic prerequisite repair may be promoted only when a live slot is available.
- Check storage, prerequisites, available slots, and resources before a Build/Upgrade click.
- Storage-capacity blocks create the required Warehouse/Granary dependency at highest queue priority and keep the
  parent deferred. Queue-time storage preflight covers constructs, selected/max building upgrades, single/bulk
  resource upgrades, upgrade-all, and templates. It projects earlier same-village work, splits targets at each
  capacity boundary, and atomically inserts only the next required storage level immediately before the blocked
  stage. If Warehouse or Granary does not exist, offer to construct it in a verified free slot before upgrading it.
  The confirmation groups actions by the resource/construction stage and visually distinguishes construction from
  upgrades; the displayed order must match the queue insertion order. The account-scoped Construction setting can
  request 1-10 storage levels ahead (default 1); a triggered storage action targets the greater of the minimum level
  required by the cost and the current storage-building level plus that configured value.
- Resource `Upgrade to max` uses the level-10 staged plan only in non-capital villages. Capitals show that max-mode
  storage planning is unsupported and direct the user to choose an explicit `Upgrade all to level` target.
- Official storage blocks use `.upgradeBlocked > .errorMessage`; disabled actions can remain in the DOM with a
  CSS `disabled` class. Construction and upgrades share the same `storage_capacity` flow.
- Correlate Official queue rows by slot when present, otherwise normalized name plus level/count. Do not treat
  `.underConstruction`, `.buildDuration`, or `#building_contract` as queue rows.
- Existing buildings and level-zero sites are distinct. Select exact building types and verify active village,
  target slot, and result before considering an action successful.
- Templates preserve resource scope, reservations, ordered prerequisites, atomic insertion, and runtime slot
  rebinding. Tribe-incompatible choices remain disabled.
- Catalog coverage is required for Romans, Teutons, Gauls, Egyptians, and Huns. Vikings are unsupported.
- Detailed queue, storage, click, and estimate rules: [construction ADR](adr/2026-06-20-construction-queue.md).

## Current pitfalls

- Account tribe and active-village tribe are different on special servers. Cache village tribe by stable identity;
  unknown tribe is deferred, never borrowed from another village/account.
- Verify active village after switching and before state-changing actions. Missing villages are quarantined until
  confirmed, not deleted after one incomplete refresh.
- Hero ownership and current location are separate. Scope transfers to the active dialog and verify the target.
- Empty building slots contain one contract per available type; scope cost reads and transfer clicks to the exact
  `#contract_building{gid}`.
- Cache only data with an owner, invalidation rule, and safe stale behavior. Incomplete refreshes must not erase
  the last valid snapshot or fabricate zero/empty state.
- Construction mutations use the short fresh-read cache; read-only observations may use the longer cache but
  never past a known completion deadline. Navigation and state-changing clicks invalidate both.
- Persisted account analysis may seed the stable village list. Cold start without a snapshot reads the profile;
  later full logins merge the live sidebar so new/renamed villages are found without another profile visit.
- Browser activity statistics are account-scoped: lifetime counters persist; session counters do not.
- Farm-list exact timers get a 5-15s render margin; unreadable disabled timers use an estimated 60s wait.
- Bonus-video failures use shared protected timing, typed cooldowns, account proxy routing, and sanitized logs.
  See [bonus-video ADR](adr/2026-07-18-bonus-video.md).
- Diagnostics use shared busy/cancel behavior, sanitize settings/logs/paths/URLs/auth/proxy data, and never present
  partial output as a successful archive. Screenshots may contain visible game data.

## Target architecture

- Smaller domain services for construction, farming, hero, map, messages, and account state.
- Pure fixture-tested parsers/calculators independent of Playwright.
- Thin browser adapters with explicit timeouts, cancellation, and result states.
- ViewModels exposing commands/state without browser or filesystem details.
- Central path, persistence, diagnostics, and release-packaging services.
- Fast domain tests, fixture-based parsing tests, and limited live smoke checks.

## Architecture decisions

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
- [Browser session and login](adr/2026-07-18-browser-session-and-login.md)
- [Bonus video](adr/2026-07-18-bonus-video.md)

## Arkiverad historik

Äldre beslut och detaljerad historik finns i:

- [Pre-compression snapshot, 2026-07-14](history/engineering-notes-2026-07-14-pre-compression.md)
- [Engineering notes archive](history/engineering-notes-archive.md)

Before deleting or shortening a rule, confirm that its detail exists in the snapshot, archive, or an ADR.
