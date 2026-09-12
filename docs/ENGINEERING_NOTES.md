# Engineering Notes

Last updated: 2026-07-31

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
dotnet build TbotUltra.sln -c Release --disable-build-servers -m:1 -p:MSBuildEnableWorkloadResolver=false -p:UseSharedCompilation=false -p:BuildInParallel=false
dotnet test TbotUltra.sln -c Release --no-build --disable-build-servers -m:1 -p:MSBuildEnableWorkloadResolver=false -p:UseSharedCompilation=false -p:BuildInParallel=false
```

The solution uses no optional .NET workloads. Keep `MSBuildEnableWorkloadResolver=false` in build
and test commands so a partial machine-wide workload installation cannot block the Desktop project.
The local SDK also requires disabled build servers and a serial solution build; use the commands above
or the repository verification scripts rather than a plain parallel `dotnet build`.

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
- A dispatcher exception raised before any WPF window has loaded is a fatal startup failure: log it and explicitly
  shut down the application so a failed `StartupUri` construction cannot leave an invisible process running.

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
- On Official Travian, a sidebar `li.infoType_22` containing a countdown timer marks an avatar pending
  deletion. Shop-backed flows must detect it before opening Shop; +15% production videos are then disabled
  in that account's settings instead of being classified as transient video failures.
- The account server picker loads active and upcoming non-standard Official worlds from Travian Lobby's public
  `/api/metadata` and `/api/calendar`. Treat any published `.travian.com` URL whose type is not `normal`, or
  whose host does not match the regular regional `ts{N}.x{speed}.{region}` scheme, as `Special`; ignore entries
  without a URL.
- `Special` is the first picker group. Hide a matching user-added duplicate while discovery is available, but
  keep the persisted custom entry so it remains usable if the calendar cannot be reached later.

## Configuration and persisted state

- `bot.json` is application-wide; account settings are account-scoped; village settings and queue state are
  village-scoped; runtime snapshots are Worker-owned observations, not user configuration.
- `Reset program` is an in-process restart boundary: cancel all automation/session work, close Chromium and
  auxiliary popups, reset pacing plus account-scoped in-memory/UI state, then reload the normal logged-out startup
  projection. Preserve account files, settings, queues, village caches, and saved login; only an explicit Login may
  admit a new browser session.
- Use the existing path provider. Never derive data paths from the executable working directory.
- `ProjectRootLocator` uses the versioned solution file in source/CI and `config/bot.json` in deployed runtime;
  source tests must not depend on ignored runtime configuration.
- Interruptible writes use the atomic file helper. Retry bounded transient lock/sharing failures.
- Quarantine and log corrupt queue/state files instead of silently overwriting them.
- The editable building-template library lives in root-level `building_templates/building_templates.json`, beside
  `docs/`. On first load, copy a valid legacy `config/building_templates.json` there without deleting the legacy file.
  Every library save and successful load also synchronizes one ignored, shareable `.tbot-template.json` file per
  template in that folder, named from the visible template name; a private manifest may remove only files previously
  created by this synchronization and must never remove manual exports. The updater preserves the entire folder.
  Shared building-template files use the versioned `.tbot-template.json` exchange format and contain no account,
  server, village, or player identity. Import conflicts are matched only by template ID; validate each template
  independently and never guess at a newer schema. An automatically inserted resource-field prerequisite upgrades
  exactly one matching field, choosing the highest current level first; explicit `All ...` template rows retain
  their normal all-matching-fields behavior. While editing a template, selecting a building or target level runs the
  same configured storage-capacity preflight as the normal construction queue; after confirmation, required Warehouse
  and Granary rows are inserted immediately before the affected row instead of waiting until the template is queued.
  Queueing an older template combines storage prerequisites from resource-field and building actions into one
  confirmation; planning remains stepwise and no queue items are written before that combined confirmation succeeds.
  Saving also runs the full template and storage preflight. Accepted storage repairs are inserted before their
  dependent rows but are not persisted until the user reviews the repaired template and clicks Save again.
  Template editing, prerequisite repair, validation, and estimates use a synthetic standard new-village baseline:
  4/4/4/6 resource fields at level 0, Main Building level 1, and starter storage. Location restrictions are checked
  against each real target village when queueing. For buildings that allow multiple instances, ascending Auto rows
  continue the most recent compatible instance while an equal or lower target starts another instance; repeated
  explicit slots remain the same instance. Conditional duplicates may use an earlier row's projected max-level
  upgrade to satisfy their duplicate threshold. The Official internal-building set is Warehouse, Granary, Cranny,
  Great Warehouse, and Great Granary; the Great variants remain unavailable until their plan/WW eligibility can be
  verified without guessing. Multi-village template queueing never navigates to fill missing
  snapshots; unavailable targets remain unselected, existing active village queue work is projected first, storage
  additions are confirmed once across all selected villages, and the final cross-village insert is atomic.
- New settings require the complete pipeline: model, defaults, load/save, ViewModel, UI, and tests.
- Resource bulk-upgrade payloads must capture the four checkbox values currently visible for the selected village;
  explicitly commit their two-way WPF bindings before reading `SelectedUpgradeTypes` at the queue boundary.
- Synthetic `desktop_runtime_manual:*` history rows are classified by their domain. Unknown manual runtime names
  default to Account, never Construction; only explicit resource/building operations may use Construction.
- Daily details task counts come from the account-scoped task-activity journal, never queue-row status or timestamps.
  Record completed task handlers, successful manual operations, and typed `work_queued` actions; construction counts
  require `QueuedOrInProgress` or `ConfirmedComplete` evidence, while waits, failures, cancellations, and
  already-satisfied observations never count as work performed. Do not fabricate activity before the journal exists.
- Account `Manual login` is account-scoped and permits an empty password. It opens the Official lobby
  without submitting credentials, blocks the desktop behind a `Login done`/`Cancel` confirmation, verifies
  the live lobby before continuing, and temporarily permits browser popups, user-opened tabs, and authentication
  network resources for that wait. The normal consent/ad-domain block must remain active during manual login;
  the manual-auth flag must never enable the CMP/ad stack because it can create short-lived Chrome targets after
  login. `www.travian.com` is the visible lobby login source and must be allowed to call `window.open` for external
  authentication, while game-world hosts remain blocked outside the manual wait. Manual-login browsers launch without
  Chromium's native popup blocker because identity providers may open asynchronously after a trusted click; ordinary
  accounts retain it. Changing an account's manual-login setting must replace any existing browser session because
  that popup-blocker policy is fixed at browser launch. This is safe only while the independent route policy keeps
  CMP/ad domains blocked throughout manual login. Source-level
  popup permission must be derived from the active confirmation wait, never permanently from the account setting. The clean post-login
  game context restores request, script, and page-handler blocking before it renders Travian, then preloads Dorf1
  before the lobby context closes. Never expose an `about:blank` replacement between those contexts; it looks like
  a consent popup flashing even when no popup page was created. Cancel must disable the exception and close the
  shared browser. Every normal main context also installs CMP UI suppression at document start, before its visible
  page is created; manual lobby authentication can otherwise prime an in-page consent overlay that is recreated
  during later read-only status passes. Bonus videos remain isolated in their separate browser context.
- Lobby `Play now` uses a trusted click and waits for the configured game origin. If a lobby-owned request fails
  with a verified Chromium proxy error before that origin commits, end the wait early and retry exactly once only
  while the same fresh world card remains visible and actionable in the lobby. Reapply SSO consent suppression to
  both the current document (`#cmpwrapper` included) and future navigations before each click; never retry after the
  game origin commits or from a stale locator.
- Portable Settings profiles use the versioned `.tbot-settings.json` format and an explicit scalar/allowed-hours
  allowlist. They must remain anonymous and portable: never include account/server/proxy identity or credentials,
  tribe-specific Brewery options, village/list/queue/object identifiers, runtime state or usage, manual server-reset
  overrides, update-notification preferences, or detailed browser logging. Import merges into the current validated
  form draft and remains unsaved until the user uses the normal Settings Save action.
- Embedded Village settings have no Save step: persist each row or group-toggle change immediately; bulk
  "Check all" changes persist each affected row and publish one consolidated settings-changed notification.
- Farming requires confirmed active Gold Club. Dashboard and Village settings must project Farming as OFF and
  non-clickable when Gold Club is false or unknown, and execution gating must enforce the same rule.
- Hero attribute automation uses account-scoped absolute maximums (0-100) keyed by attribute; missing or invalid
  values default to 100. Read the four live Official attribute inputs before every plus click, never cross a maximum,
  and do not requeue point spending when the latest complete snapshot shows every configured maximum is reached.
- Demolition is a village-scoped queue group: start one Official `table#demolish` step, persist the server timer plus its random delay as `NextAttemptAt`, and never poll or sleep through it in the browser. Submit the Official form with a trusted click, tolerate only its expected navigation-context replacement, wait for the returned Main Building page, and require its active timer as confirmation; never revisit the same Main Building merely to submit. It has no per-village group toggle; an explicitly queued demolition is always group-enabled, while the village's master Auto toggle still controls automation.
- Persist village identity by coordinates/key, not display name. Names may collide or change; queue items retain
  their target village identity.
- Duplicate village names are valid. Fresh Official sidebar `data-did` plus `.coordinateX/.coordinateY` values
  are authoritative: never deduplicate by name, never overwrite fresh coordinates from a name-keyed cache, and
  never accept a same-name village switch without coordinate verification when coordinates are available.
- A village missing from a readable live sidebar is only a suspicion. Compare by `newdid`/coordinates, verify the
  account once against the player-profile village table, and only a non-empty profile result may authoritatively
  remove the village from the live UI, disable its automation, and pause its pending queue items; retention cleanup
  remains separate. Current-page jitter/UI sync is observation-only and must never navigate while the bot is paused;
  profile verification runs only from an explicit/login flow or active automation path. Remember a verified missing
  coordinate for the browser session so stale cache seeding cannot repeat the same profile navigation.
- Recurring browser timers (including resource jitter and inbox refresh) run only while continuous or queue
  automation is active. Login-state detection waits, with cancellation, for stable authenticated shell markers on
  the current page (including the global Hero control and active-village sidebar), never page-specific Dorf1/Dorf2
  content, and never navigates to Dorf1 merely to verify a session; an unresolved page is a transient read failure.
  Explicit user operations such as Login, scans, and refresh buttons remain allowed while automation is idle.
- The village status cache and queue use canonical coordinate keys. Legacy name-keyed entries are migrated only
  when coordinates can be resolved; active coordinates come from `#villageName[data-x][data-y]`.
- Per-village runtime caches are shared by the UI and background loop and must be synchronized. A display-name
  lookup is valid only when exactly one cached village has that name; duplicate names never use last-write-wins.
  A storage-only or non-dorf1 read with unknown production must preserve that coordinate-keyed village's last-known
  production rates, so cache persistence and login restore cannot turn valid forecasts into `-/h` / `Not filling`.
- A live Dorf1 resource-field level above 10 is definitive positive capital evidence. Apply it immediately by
  coordinates to the capital cache, shared village list, and returned UI status, clearing any former capital flag;
  levels at or below 10 never prove that a village is non-capital. Player-profile scans remain the account-wide check.
- Construction location rules are shared through `BuildingCatalogService.CanConstructInVillage` and enforced by
  pickers, template planning/repair, and the Worker guard. Stonemason's Lodge (gid 34) requires confirmed capital
  state; unknown is not permission. Great Barracks/Great Stable remain forbidden only in a confirmed capital.
- Multi-instance building eligibility comes from the catalog's `single_instance` metadata through
  `BuildingCatalogService.AllowsMultipleInstances`; UI pickers, template/repair planning, reconciliation, and Worker
  guards must not maintain separate duplicate allowlists. Resource fields are never treated as constructible copies.
- Queue status transitions are gated. `MarkDeferred` accepts only RUNNING items; Pending items use
  `UpdateDeferred`/`UpdatePending`. Check the returned boolean.
- A building upgrade must defer normally while an active `construct_building` item for the same village, slot,
  and building identity remains in the program queue; it must not reach the Worker as an identity-mismatch retry.
- A new building and its requested final level are one `construct_building` queue item. The construct payload carries
  the final target, survives slot fallback/reconciliation, and continues from level 1 through that target; templates,
  recovery, requirement repair, and storage preflight must not split this into separate construct/upgrade rows.
  Resource-field target upgrades likewise remain one queue item.
- Manual queue reordering persists atomically through `CreatedAt` and therefore controls FIFO selection in both Auto
  Queue and Continuous Loop. Extended row selection supports Ctrl/Shift; one move requires one group and priority,
  preserves selected order, and moves only the selected village's visible rows while hidden villages keep their
  relative order. A Running aggregate target may move: its already-started server action finishes, while remaining
  work follows the new position. Evaluate dependencies against the complete final order and show at most one
  `Move anyway` confirmation only when an explicitly linked requirement, or same-slot construct, crosses its
  dependent task. Do not infer warnings from catalog compatibility alone; the runtime guard remains final safety.
- New villages default to Auto enabled. The version-1 migration enables existing villages once; later manual
  Auto-off choices persist.

## Timing, cancellation, proxy and logging

- Normalize invalid timing ranges so minimum never exceeds maximum.
- Pass the active cancellation token through every cancellable operation. Never replace it with
  `CancellationToken.None`; cancellation is expected control flow, not an alarm.
- Sleeping/paused state must preserve work and must not start a competing loop.
- Session pacing may publish `Sleeping` only after browser shutdown succeeds and exact tracked Tbot browser
  process identities have been cleared. Shutdown failures keep the session out of `Sleeping`, retry cleanup,
  and must never fall back to killing Chrome by process name or executable path.
- Entering sleep closes the active browser session, including planned sleep entered before login.
- Continuous-loop wake requests from saved settings or newly enabled automation must also end an active idle break;
  humanized idle pacing must not delay newly requested work.
- A ready enabled task always wins over holding the current village for an imminent deferred task. The account-scoped
  Pacing setting `short_village_defer_seconds` may be 20, 60, or 90 seconds (default 60) and applies only when no task
  is ready in any village; changing it while the continuous loop runs requests a wake at the next safe boundary.
- A bulk resource-field scan reads active constructions and the compact build queue once on dorf1 and reuses that
  snapshot while evaluating candidates. The same dorf1 pass reads current stock, production, and storage capacity;
  combine those values with the catalog cost for each projected next level and discard resource-unaffordable offers
  before opening a build page. Open only the first affordable/live-check candidate in the selected order. If none is
  affordable, Hero/NPC recovery may inspect at most one representative offer; incomplete dorf1/catalog data falls
  back to live per-page checks. Refresh the snapshot only after a construction mutation starts a new scan iteration. Rank
  candidates by their projected level including an exact-slot queued upgrade. Retain a level confirmed during the
  current operation even when the refreshed live queue omits its slot identity, so a just-queued field is not probed
  again ahead of untouched lower-level fields. Persist that confirmed exact-slot projection on the queue task across
  defer/retry; clear it when Dorf1 reaches the projected level, or after its bounded review deadline only when the
  live construction queue is confirmed empty. Never replace this with name-only matching between identical fields.
  On dorf1, a resource overlay's exact-slot `underConstruction` class
  projects `visible level + 1` for planning across deferred task invocations; it does not replace the visible completed
  level and is not treated as a construction queue row. An upgrade-click redirect to dorf1 receives the normal page-load pacing
  once before the next candidate is selected. Immediately before either the normal resource upgrade click or its
  Construct Faster video action, re-read the live build page and require the requested URL slot, resource gid/name,
  current header/root level, exact next offered level, and button action slot/gid to agree. The offered level must not
  exceed the task target; any mismatch stops the action and returns to a fresh Dorf1 pass.
- Continuous Loop and Auto Queue share runtime-only village batching over the account queue: ready work is drained
  across groups in the verified browser village before normal work elsewhere. Ready Account work or `Priority > 0`
  may preempt, and an interrupted ready village resumes after the urgent work. Attempt count alone must never rotate
  away from ready work; deferred/unknown work ends the visit and never keeps a batch alive. Non-urgent utility work
  waits until no village has ready work, and preview/forecast selection must not mutate batch ownership. A single-level
  construction marked `in_progress` may yield to a later row only until its authoritative retry deadline; once due,
  select it for live revalidation so stale completion state cannot block that village's remaining template rows.
- Applying Session pacing settings while automation is active must take effect immediately: enabling starts its run
  timer, while disabling a scheduled sleep resumes the captured automation state.
- Raising or disabling Daily max while sleeping for the old daily limit must re-evaluate the restriction immediately.
  If the recorded runtime is below the new limit and Allowed hours permit running, wake with zero added sleep delay.
- Known queue deadlines are authoritative and may not be shortened by pacing.
- Smart Sleep and Session pacing are mutually exclusive account modes, though both may be disabled. Smart Sleep may
  close the browser only at a safe automation boundary when the shared forecast provides enough idle time; its saved
  wake uses the configured before/after window, while a missing deadline uses a bounded fallback wake that forces one
  Village Status Round. Allowed hours and Daily max remain hard boundaries for either enabled mode. Session-pacing
  sleep/wake callbacks may originate from an automation thread and must enter Desktop orchestration through the WPF
  dispatcher before reading or updating UI-owned state.
- Action pacing is mandatory. Persisted configuration and incoming payloads may change its delay ranges but may
  not disable it. Manual Catapult-wave preparation uses a dedicated 250–500 ms delay once per attack, then
  activates the first prepared tab
  and requires explicit `Send now` confirmation. Only the final confirmation burst uses the explicitly selected
  50–500 ms delay without general action pacing; cancel sends nothing and temporary wave tabs are closed.
- Proxy settings are account-scoped. Browser, HTTP client, tests, and bonus video use the same effective route.
  Never log credentials or place them in user-visible URLs.
  Proxy library/finder entries carry username and password separately; migrate legacy inline Host credentials
  before normalization. Serialize credentials with `ProxyParser.BuildServer` and use `BuildWebProxy` for HTTP
  probes/IP lookups so authentication is explicit. Connection matching is credential-sensitive, while account
  reuse protection remains endpoint-scoped. An account bound to a saved proxy persists its stable proxy ID and
  refreshes its serialized `ProxyServer` from that library entry, so credential changes cannot leave the account
  using a stale copy; keep separate IDs when accounts share an endpoint with different credentials. Account
  selection, editing, rotation, and recovery must preserve that binding and its credentials.
  Set the same parsed proxy explicitly on both Playwright browser launch and every browser context. Chromium can
  otherwise show its native authentication dialog or return `ERR_INVALID_AUTH_CREDENTIALS` for authenticated HTTP
  proxies; context credentials must answer the challenge without user input. Keep invalid-proxy and `NeverUseOwnIp`
  handling at browser launch so contexts can never introduce a direct-route fallback.
- Proxy Finder and Proxy Library classify a proxy as reliable only after three consecutive neutral HTTPS probes
  and two consecutive Travian reachability probes. All five probes use fresh connections and the active cancellation
  token; a single failed probe rejects the proxy. Only HTTP 2xx/3xx responses count as usable; blocked, proxy-auth
  and gateway/server-error responses do not.
- Retry only transient failures with bounded attempts. Apply configured pacing; do not add unbounded sleeps.
- Alarms represent actionable failures. Expected waiting/blocking and an explicitly retrying bounded transient
  attempt are normal status. Deduplicate identical alarms for 30 minutes; repeated occurrences update visible
  count without another alarm line.
- Build-estimate server-speed detection accepts both `5x` and lobby-style `X5` names. Before the account has a
  verified login, missing speed is expected and silently uses 1x; only an unparseable logged-in account alarms.
- Detailed browser logging is development-only and off by default. Trace semantic operations, emit exactly one
  end event per flow, and sanitize all secrets. Navigation/mutations use the traced adapters.
- Bonus-video audio muting is best-effort and retried during playback polling because provider controls may render
  after play starts; a missing, detached, or unactionable audio control must never fail the video flow.

## Browser, login and account access

- Validate bundled Chromium by its exact Playwright revision and executable, but do not hard-code the Windows
  archive directory name; supported Playwright versions have used both `chrome-win` and `chrome-win64`.
- Never install or ship `chromium_headless_shell` (~270 MB). Headless game automation does not exist; install with
  `install chromium --no-shell`, and the cleanup removes the shell folder at ANY revision. Any internal headless
  launch (currently only the proxy IP check) MUST set `Channel = "chromium"` — a plain `Headless = true` resolves
  to the shell and fails with "Executable doesn't exist at ...chromium_headless_shell-<rev>...".
- The session runs the user's system Chrome/Edge (`Channel` from `ResolveInstalledChromeChannel`) for the H.264/AAC
  codecs bonus videos need; bundled Chromium is only the fallback. Do not add a browser "warmup" launch — it warms
  a binary the session does not run. Bot-launched browser processes are therefore indistinguishable from the user's
  own by name or path: orphan cleanup MUST go through `LaunchedBrowserRegistry` (PID + start time + exe path, all
  three must match), never by process name or executable path alone.
- A system browser can occasionally exit with code 0 before Playwright creates a browser/context/page, reported as
  `TargetClosedException`. Retry that exact early launch closure once after a short cancellable delay; do not retry
  missing executables, driver failures, later page closures, or arbitrary Playwright errors at this boundary.
- `DOMContentLoaded` is sufficient only when followed by a required page-marker check.
- Full login starts in the Travian lobby and enters the owned world through SSO; never submit credentials to the
  configured game server or add direct-server fallback.
- Lobby navigation must wait for the delayed React world list or complete login form, then retry a missing/transient
  lobby state at most three times. Re-submit credentials only after a fresh lobby load confirms the login form.
- `Choose in lobby` is a one-time account resolution: save the concrete server only after verified game login, then
  keep it authoritative. Transient proxy/navigation failures must retry or fail without reopening the world picker
  or changing that account's server.
- Preserve filtered SSO state only in in-app session transitions. Real process startup and user exit clear every
  account's saved Playwright auth state.
- After Play now commits navigation to an Official game origin, rotate immediately to the clean in-app context that
  blocks the consent/ad stack. State filtering and the replacement context must use the resolved runtime game origin,
  not a stale configured base URL, so the selected world's SSO cookies survive the rotation.
- The Official mobile-version dialog can appear after Play now's first navigation wait expires. After confirming it
  with both mobile options off, wait again for the game origin before treating the current lobby URL as a failed SSO.
- Preserve the intentional headed/maximized anti-detection setup and `ViewportSize.NoViewport`.
- Read account language only on the configured game origin; lobby and browser-error documents are not evidence.
  Accept Travian's equivalent `en` and `en-US` English codes. After either language-dialog action verifies English,
  restore the exact automation mode that was running before the pause; do not start an idle bot.
- The one-time Gold Shop offer is a blocking announcement, not an automation action. Dismiss it after game-page
  navigation/reload only through the visible `data-context="oneTimeOfferAnnouncement"` dialog; never use a broad
  dialog-close selector.
- Synchronize `BotOptions.BaseUrl` from the active account before login and fail fast when their normalized origins
  differ. An account switch invalidates the browser-session generation so a late `OpenPageAsync` cannot resurrect
  the previous account after shutdown.
- Account-picker changes require confirmation only while both authenticated and backed by an open browser session;
  logged-out/no-browser changes switch saved account immediately without a dialog.
- Lobby world matching treats speed labels (`x3`, etc.) as optional display metadata but rejects an explicit
  conflicting speed. If neither cached wuid nor automatic name/host matching reaches the configured origin,
  interactive login shows every owned lobby world as selectable cards. The lobby-owned list is authoritative:
  after a manual choice reaches an authenticated Official game origin, atomically update that account's server name
  and URL in Manage and sync the active runtime config. A failed selection reopens the picker with remaining worlds;
  persist the selected wuid and any server correction only after authenticated game-page verification.
- A manually selected lobby world may temporarily differ from `BotOptions.BaseUrl` until verification persists the
  correction. During that login flow, every navigation, URL resolution, and server-keyed cache must use the resolved
  game origin rather than the stale configured base URL.
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
- Account holds are account-specific: a verified ban, restriction, challenge, or repeated unknown state stops only
  that account and preserves its queue/settings until manual re-enable. A Travian punishment page is evidence only:
  never click its Agree or Contact Support controls, and leave the browser open for manual review. Treat the
  Official sidebar ban warning plus its `/dorf1.php?action=stop` details link as an equivalent hard-stop signal;
  the jittered current-page refresh must turn it into an account hold and stop the rest of that refresh tick.
  Active task page-ready and resource-retry paths must probe the same signals before waiting or reloading, so a
  punishment response cannot spend tens of seconds in missing-widget recovery before the account hold is raised.
- A verified ban captures the last durable village structure once. After manual re-enable, the first Start bot runs
  a full dorf1+dorf2 recovery scan in strict read-only mode before any task generation, reward collection, queue
  reconciliation, construction-fill arming, or execution. Normal automation may resume only after the user's recovery choice.
- Detailed lifecycle, SSO, cleanup, and access rules: [browser/session ADR](adr/2026-07-18-browser-session-and-login.md).

## Feature implementation conventions

- Keep the WPF dispatcher limited to bounded presentation work: recurring ticks update countdowns from cached
  projections, expensive queue/overview calculations run from immutable snapshots, and persisted cache writes run
  through a serial latest-snapshot writer. Queue display refreshes are read-only; history is projected only when shown.

### Core and Worker

- Parse HTML/JSON into domain models before scheduling decisions.
- Map Oasis scan planning, filtering, pacing, retry, checkpoints, and results belong to `MapOasisScanOperation`; `TravianClient` only prepares the Official map page and reads map areas.
- Send Troops navigation and Rally Point-level checks route through `IRallyPointNavigator`; Catapult, Reinforcements, and Farm Lists keep their own action flows.
- Put resource, time, capacity, prerequisite, and queue calculations in Core.
- Worker owns browser interaction, timeouts, retries, cancellation, and operational logging.
- Prefer explicit result types for expected unavailable, deferred, and blocked states.
- Log account, village, operation, and failure stage without exposing secrets.

### Desktop

- UI text is English. Reuse theme resources and controls; do not hard-code near-match colors.
- Inactive/Travco and Map Oasis tools live directly on the Farming subtab. Merely viewing the subtab does not pause
  automation; the first browser-backed action starts an analysis session. Finishing it closes the external Travco
  browser tab only when one was actually opened, then resumes whichever automation mode the session paused. An active
  session keeps a green animated finish control visible in the global sidebar, so ordinary UI navigation never hides
  the action or implicitly closes a tab while analysis may still be running.
- Map SQL scan is a self-contained manual operation. It never starts a persistent Travco analysis session and exposes
  no Finish-session action after download, filtering, and list persistence complete.
- The Settings window is category-tabbed: General (including post-login automation), Pacing, Construction, Hero,
  Farming, Troops, Celebrations, and NPC / Trade. Town Hall per-village/queue controls belong under Celebrations;
  account-wide Gold/Silver limits belong under NPC / Trade. Town Hall and Brewery restart delays include the
  configured random delay after the live celebration timer; a confirmed missing Town Hall disables that village's
  Town Hall group instead of deferring an impossible task.
- Every editable numeric Settings field is validated before any config mutation. Decimal input uses invariant
  culture and requires a period; invalid format, out-of-range values, and Max below Min block Save/Sleep now with
  a warning focused on the offending field instead of silently substituting or clamping a value.
- Gold/Silver spending has two independent guards: a minimum remaining balance and a daily spending budget.
  Daily totals reset at 00:00 server time and persist per account/server so restart cannot reset the allowance.
- Hero, Town Hall, Brewery, and Smithy restart delays are independently toggleable and enabled by default. Hero
  reuses one session deadline after returning home or discovering a new adventure. Smithy delays only after an
  occupied queue slot frees; an empty queue starts immediately and Plus slots are filled together without delay.
- Optional per-building troop minimum ranges are village-scoped. Randomize one threshold per building/run and
  evaluate it from the current village resource snapshot plus the Official unit-cost catalog before navigating;
  recheck live costs and resources before submit, and alarm/skip on a catalog-to-live cost mismatch.
- Fast training queues can auto-refresh their building page while an amount is being prepared. Re-resolve and
  validate the live form after click pacing, retry preparation a bounded number of times, and accept a changed but
  still-positive Official maximum; never retry after the state-changing Train click because submission is ambiguous.
- Hero HP regeneration per day is only a scheduling estimate for low-HP adventure defers. A successful current-page
  HP read is authoritative and releases the deferred Hero task immediately once the threshold is met. That release
  is centralized in the shared UI HP-read helper, so login, quick re-login, browser restart, the manual refresh
  button, and the periodic tick all clear a stale regen-estimate countdown, not just the background tick.
- Hero crop anti-starve is account-configured but selected per coordinate-keyed village and runs only while the
  continuous bot is Running. A missing per-village entry defaults enabled; the account master defaults disabled.
  It is observation-driven: trusted resource snapshots from the existing jitter read and village scan cancel the
  action for non-negative production or schedule a local no-browser deadline for negative production. Only when
  that deadline reaches the configured trigger may one deduplicated live-confirmation task enter the queue; never
  create permanent per-village polling tasks. The live confirmation uses dorf1 stock/production. Transfers
  navigate through `/hero/inventory`, open the visible `.heroItem` containing `.item.item148`, fill only
  `input[name="crop"]`, and click the enabled dialog action whose normalized text is exactly `Transfer` (never
  `Transfer maximum`). The configured minimum hero crop is an absolute post-transfer reserve: transferable crop is
  at most `hero crop - minimum remaining`, in addition to the per-transfer maximum and granary free capacity.
  Post-transfer ETA verification allows 60 seconds of observation drift; actual transfer limits or failed stock
  verification still raise the anti-starve alarm.
- Account-wide construction behavior, including storage look-ahead and construction start delay, belongs in the
  Construction settings category rather than the Buildings workspace.
- Secondary explanations use the shared `i` tooltip when permanent text wastes space.
- Disable duplicate commands while running; marshal observable collections through the dispatcher.
- Marshal to the UI thread via the shared `MainWindow` helpers: `RunOnUi` (blocking) or `RunOrPostToUi`
  (fire-and-forget off-thread); do not hand-roll new `CheckAccess` guards with matching semantics.
- Manual operations matching the canonical begin/busy/complete/paused/fail shape go through
  `RunGuardedOperationAsync`; flows with extra state, dialogs, or custom cancel handling keep explicit blocks.
- Keep `DataGrid.RowHeight` unset or `Double.NaN`; the string `Auto` is not a WPF `Double`.
- Queue Active/History grids use star sizing with explicit per-column `MinWidth` and disabled user resizing in both
  the embedded panel and Pop out. A narrow viewport must scroll horizontally; never allow a header drag or an early
  hidden-tab measurement to collapse queued task columns into apparently blank rows.
- The Incoming attacks grid follows the same constraint: every column has an explicit `MinWidth` and user resizing
  is disabled, so grouped attack rows remain readable and a narrow viewport scrolls instead of collapsing columns.
- Enumerate mutable collections through immutable snapshots when sanitizing/exporting.
- Village Overview is read-only and uses cache/queue snapshots; opening it never navigates or scans.
- Village Overview construction totals consume the Queue tab's authoritative estimate rows. Rebuilding those
  rows must invalidate the overview projection, and opening the overview rebuilds the local Queue projection
  first; the 1 Hz render pulse may update countdowns/timestamps but must not make stale totals look refreshed.
- Overview projections show only real deadlines and never mutate queue or scheduler state.
- The Dashboard status line prioritizes a running queue item, then a scoped active browser workflow, then the
  read-only next-task forecast. Long-running workflows such as Village scan publish nested activity so an inner
  queue task can temporarily replace the label and the outer workflow is restored when that task completes.
- Village Overview Farming renders only the allowed `send_farmlists` queue state: `Ready`, `Running`,
  `Blocked`, or its `NextAttemptAt` countdown. Individual farm-list raid timers belong to the Farming panel
  and must not replace the dashboard-synchronized dispatch deadline in Overview.
- Village Overview Town Hall renders each live celebration on its own line as `Small: <timer>` or
  `Great: <timer>`. The Town Hall read preserves up to two exact active timers (including mixed modes) in
  account state; generic task names and resource-wait descriptions do not belong in that cell.
- The 1 Hz presentation pulse must not perform file I/O, replace stable ItemsSource collections, or rebuild
  unchanged rows. Cache configuration outside the pulse, derive countdowns from absolute deadlines, and apply
  only changed values; persistence and high-volume log writes run serially off the UI dispatcher.

### New features

1. Capture the relevant Official page/dialog state and identify stable scoped markers.
2. Add only verified Official selectors and use existing root-relative path helpers.
3. Parse into domain models; keep decisions/calculations outside browser and WPF code.
4. Reuse queue, cancellation, pacing, persistence, logging, and busy-state patterns.
5. Add focused parser/calculator tests and a regression test for the reported failure.
6. Verify retries, cancellation, secrets, persisted state, and publish output when applicable.
7. Record durable cross-cutting rules here; put feature decisions in an ADR and history in the archive.

## Construction and queue invariants

- Account reads (`status`, account/village snapshots, and village scans), automatic reward collection, account-wide
  reset detection, and free production-bonus activation are immediate Account tasks. They run before
  village/group work and must never enter Construction's strict queue order. Account is an always-on queue category,
  not a user-toggleable automation group; do not show it on the Dashboard or in per-village group settings.
- `ActiveConstructions` is the source of truth for occupied construction slots. A full queue is a normal blocked
  state, not an exception.
- A confirmed empty dorf1/dorf2 construction overview arms a short per-village immediate-fill burst: start all
  available official resource/building slots without the construction start delay, then resume normal humanized
  timing. Romans have one resource plus one building slot without Plus; Plus adds one flexible third slot (up to
  two resources or two buildings, three total). Romans preserve FIFO independently within the resource and building
  categories: when one category is full, its rows may be passed only to run the earliest ready row from the other
  category. Other tribes preserve one strict construction order. An in-progress aggregate such as
  `upgrade_all_resources_to_level` remains the head of the Roman resource category until it is complete.
- A confirmed empty overview gives the first stale resource `page_timer` head one immediate live validation so a
  free slot cannot idle behind an obsolete timer. Hero inventory is never polled for this: only an observed inventory
  increase wakes the first resource-deferred construction head per village; identical reads and transfer deductions do not.
- A new resource-defer snapshot replaces the previous snapshot's costs, current stock, production and capacity fields.
  If the live page cannot expose new costs, never reuse old requirements to wake that `page_timer` early.
- Construction follows visible per-village queue order subject to the Roman category rule above. A deferred head
  blocks later construction in the same applicable order; verified automatic prerequisite repair may be promoted
  only when a live slot is available.
- Check storage, prerequisites, available slots, and resources before a Build/Upgrade click.
- Storage-capacity blocks create the required Warehouse/Granary dependency at highest queue priority and keep the
  parent deferred. Queue-time storage preflight covers constructs, selected/max building upgrades, single/bulk
  resource upgrades, upgrade-all, and templates. It projects earlier same-village work, splits targets at each
  capacity boundary, and atomically inserts only the next required storage level immediately before the blocked
  stage. If Warehouse or Granary does not exist, offer to construct it in a verified free slot before upgrading it.
  The confirmation groups actions by the resource/construction stage and visually distinguishes construction from
  upgrades; the displayed order must match the queue insertion order. The account-scoped Construction setting can
  request 1-10 storage levels ahead (default 2); a triggered storage action targets the greater of the minimum level
  required by the cost and the current storage-building level plus that configured value.
- The account-wide Construction setting for crop-shortage recovery is enabled by default. Only the scoped Official
  `.upgradeBlocked > .errorMessage` text `Lack of food: extend cropland first!` triggers it; negative production alone
  does not. Keep the blocked construction head, prioritize at most two lowest-level cropland steps (including active
  ones), and resume that village's Construction queue only after a completed recovery step and a fresh positive crop
  production read. With recovery disabled, defer only that village's Construction head for 30 minutes and alarm.
- Resource `Upgrade to max` uses the level-10 staged plan only in non-capital villages. Capitals show that max-mode
  storage planning is unsupported and direct the user to choose an explicit `Upgrade all to level` target.
- Official storage blocks use `.upgradeBlocked > .errorMessage`; disabled actions can remain in the DOM with a
  CSS `disabled` class. Construction and upgrades share the same `storage_capacity` flow.
- Correlate Official queue rows by slot when present, otherwise normalized name plus level/count. Do not treat
  `.underConstruction`, `.buildDuration`, or `#building_contract` as queue rows.
- Resource-field names repeat. When the target slot is known and either queue source identifies a same-name row
  by another slot, never apply an unknown-slot same-name row to the target; use exact slot identity.
- Existing buildings and level-zero sites are distinct. Select exact building types and verify active village,
  target slot, and result before considering an action successful.
- Immediately before constructing any building, read the complete live dorf2 overview. Reuse the current dorf2 only
  when its path is correct and the page is not marked stale; otherwise navigate/reload before reading. Remove a stale construct
  when its exact target slot already has the intended building or when a single-instance building exists anywhere;
  rebind dependent upgrades to the confirmed live slot. A level-gated duplicate such as Cranny, Warehouse, or
  Granary must remain bound to its requested slot while it waits for the existing building to reach the required
  level; never collapse multiple duplicate constructs onto the existing building.
- Immediately before a normal building upgrade click or its Construct Faster action, re-read the live build page and
  require the requested URL slot, building gid/name, current header/root level, exact next offered level, and button
  action slot/gid to agree. The offered level must not exceed the task target; never fall back to another generic
  upgrade button when the exact level is absent.
- A matching active prerequisite below the required level defers its dependent construct until the active step
  finishes, even when Official omits the active slot id. Re-plan the remaining prerequisite levels from the next
  complete live overview; never terminal-fail the dependent construct during that intermediate state.
- After a successful hero resource transfer reloads the same verified build.php slot, retry its exact construct or
  upgrade action directly; do not restart through queue and dorf2 probes unless the direct action remains unavailable.
- An upgrade that confirms its planned slot is empty is not a successful no-op. Reconstruct the expected building in
  that exact slot without slot fallback, keep the upgrade pending, then continue its original target level.
- Fresh full dorf2 reads reconcile single-instance building upgrades by gid across the whole village, not only the
  queued slot: remove targets already reached and rebind unfinished targets to the confirmed live slot. Duplicate
  construct detection must report that effective slot before removing the stale construct. Run reconciliation before
  desktop queue/requirement defers at every live full-status entrypoint; a disk snapshot used only to repaint UI must
  never mutate the queue.
- Missing-building recovery may reconstruct only after a second complete 22-slot dorf2 read confirms that the expected
  gid/name is absent village-wide. An incomplete or identity-ambiguous read defers without adding a construct.
- Every village-status cache write for the same village must also replace the preferred UI building snapshot after
  partial-state merging; never let an older unknown-level snapshot override a newer live or merged read.
- Queued and direct `Load buildings` must both produce a full village status with Warehouse/Granary capacity. A
  dorf2 building snapshot must be merged with the same village's existing status, never replace it with null capacity.
- Release smoke tests must wait on `ReleaseSmokeContract.ReadyLogMarker`, not logs from optional/removed startup work.
  Bundled Chromium is validated structurally by exact Playwright revision before launch; keep a contract test for the
  PowerShell marker so application startup and the GitHub release workflow cannot silently drift apart again.
- Templates preserve resource scope, reservations, ordered prerequisites, atomic insertion, and runtime slot
  rebinding. Tribe-incompatible choices remain disabled.
- Catalog coverage is required for Romans, Teutons, Gauls, Egyptians, and Huns. Vikings are unsupported.
- Detailed queue, storage, click, and estimate rules: [construction ADR](adr/2026-06-20-construction-queue.md).

## Current pitfalls

- Account tribe and active-village tribe are different on special servers. Cache village tribe by stable identity;
  unknown tribe is deferred, never borrowed from another village/account. Per-village Smithy option dialogs resolve
  their troop catalog from the target row's canonical village key, never from the Dashboard's selected village.
- Verify active village after switching and before state-changing actions. Missing villages are quarantined until
  confirmed, not deleted after one incomplete refresh.
- Hero ownership and current location are separate. Scope transfers to the active dialog and verify the target.
- Read an away Hero's ETA from Hero Attributes, never from Rally Point troop movements. Use the displayed timer
  directly for an explicit return to the home village; double every outbound movement timer (adventure, raid,
  attack, reinforcement, or another destination) to include the return leg. A live Attributes analysis must project
  movement, away-state, and return time together with the attribute snapshot and defer an existing due `hero_manage`
  item immediately, so login updates the Hero countdown without a second Attributes visit after Start.
- Empty building slots contain one contract per available type; scope cost reads and transfer clicks to the exact
  `#contract_building{gid}`.
- Cache only data with an owner, invalidation rule, and safe stale behavior. Incomplete refreshes must not erase
  the last valid snapshot or fabricate zero/empty state.
- Construction mutations use the short fresh-read cache; read-only observations may use the longer cache but
  never past a known completion deadline. Navigation and state-changing clicks invalidate both.
- A resource construction with unknown slot identity may prove queue occupancy and timing, but never that a
  specific known resource slot is already in progress merely because its repeated field name and level match.
  Confirm the exact slot from queue identity or from that slot's own build page.
- Construction Queue Reconciliation plans only from confirmed full live status and applies all pending-item
  changes atomically; cache or local timers are never reconciliation evidence.
- Deferred resource-gated waits are re-estimated LIVE on every resource read (jitter included), not left on
  the worker's one-shot ETA: `RefreshDeferredConstructionWaitsAsync` / `RefreshDeferredTroopTrainingWaitsAsync`
  re-read current resources + production, recompute the wait, update the UI timer, and release when the live
  threshold is actually met (e.g. farming income makes a "build at 80%" fire at the real time, not a stale
  20h). Both are triggered from `CacheVillageStatus`, so they run for ANY read village, not only the selected
  one. The troop recompute needs storage capacities — with capacity 0 the eval falsely reports "ready", so a
  light current-page read (no caps) fills caps/production from the village cache but keeps the LIVE current
  resources; buildings are never cache-filled (empty is handled leniently, a stale list could wrongly exclude).
- Village scan finishes ready, automation-enabled work for the freshly read village before applying the
  inter-village delay. Task permission still comes from village Auto and group settings. Sweep wait reconciliation
  is awaited before selection so a newly released task runs during the same visit, and selection requires the exact
  canonical village key. It has no normal per-village attempt cap; already urgent-classified work may preempt and the
  scanned village then resumes, while each queue item is attempted at most once during that visit.
- A manual Village scan "Scan now" clears the persisted round deadline. An active continuous loop consumes a
  forced-sweep request at its next safe boundary; without an active loop the scan runs in its own manual operation
  scope and does not start the full continuous loop.
- Account scan uses a new transient scan scope on every dialog open (Dorf1 and Dorf2 selected by default) and reuses
  the Village scan page readers. It must not persist those one-off choices or alter the sweep schedule.
  Villages are visited in randomized order and the browser remains on the final naturally scanned village; do not
  add a return-to-start navigation.
- A Village scan Dorf1 read is authoritative for the visible `.buildingList` construction queue and the
  active village population in `#sidebarBoxActiveVillage .population span`. Both update cache/UI and queue
  decisions even when Dorf2 scanning is disabled.
- The same Dorf1 sweep visit checks the existing Official Questmaster and Daily Quest claimable markers. When the
  corresponding auto-collect setting and village automation allow it, `collect_tasks` and
  `collect_daily_quests` run before other village work; afterward the selected sweep scope is re-read because
  rewards can change resources.
- After every building-mutation task the desktop re-reads and caches the just-worked village's complete building
  overview. Prefer the already-loaded post-click Dorf2 only when it supplies all 22 distinct slots, an authoritative
  construction-queue observation and coordinates matching the queue item. Merge that snapshot with retained Dorf1
  state; otherwise fall back to the full Dorf1+Dorf2 read. Never use a storage-only quick-skip: an upgrade reports
  QueuedOrInProgress on every climb pass and a build that finishes while the loop is on another village returns
  AlreadySatisfied, so omitting the complete Dorf2 read freezes cached building levels.
- An authoritative Dorf1/Dorf2 construction-overview row reports the target level being built. The desktop may
  therefore promote the matching cached building (same slot and gid/name) to at least `target - 1`, but must
  never downgrade it. Persist that confirmed floor so clearing an optimistic queue target cannot reveal an old
  pre-queue level while the long-running worker task is still active.
- Before any building construct or upgrade click, verify that the live slot identity matches the queued gid/name
  (gid first, normalized name fallback). A mismatched occupied slot is never `AlreadyExists` and must never be
  mutated; fail with both queued and live identities so a stale/rebound payload cannot upgrade another building.
- `Failed` queue items are history regardless of whether they are runtime-only; they are not active, movable, or
  included in active queue estimates.
- Same rule for resource fields (dorf1): after a resource-upgrade task,
  `RefreshResourceStatusAfterResourceMutationAsync` re-reads the just-worked village's fields
  (`resourceOnly:true, forceCurrentVillage:true`) and `CacheVillageStatus`es them, repainting the resource
  UI only when it is the selected village. Do not reinstate a log-line "fast update" of the displayed rows:
  it patched the SELECTED village from another village's log lines and never cached, so field levels went
  stale / cross-contaminated in a multi-village account.
- Production-bonus (Advantages tab) scan reads the running bonus's percent AND `.timerReact` countdown from
  its `.bonusInfo` "+N% active for:" label — NOT from `.bonusDuration` (that holds only the auto-prolong
  checkbox). Timers come in a long form with a day suffix ("5d 15:52:56"); `ParseTimerToSeconds` extracts
  the `Nd` days then parses the `hh:mm:ss` remainder.
- Construction start-delay transition memory is village-scoped by `data-did` or coordinates, never display name;
  duplicate village names must not share a humanize deadline.
- Persisted account analysis may seed the stable village list. Cold start without a snapshot reads the profile;
  later full logins merge the live sidebar so new/renamed villages are found without another profile visit.
- A transient village refresh that returns only part of an already verified list must merge fresh rows into the
  existing list instead of shrinking the Dashboard; only an explicitly complete login list may remove villages.
- Before Continuous Loop or Auto Queue performs state-changing work, compare the live sidebar membership with the
  Desktop's known villages. A mismatch must be verified once on the player profile; its authoritative result removes
  lost villages from the live UI and pauses their pending work. Failed verification blocks mutation and retries with
  bounded backoff; repeated identical sidebar evidence must not spam profile navigation.
- New-account analysis is account+server scoped. A pending first-login analysis forces hero inventory, hero
  attributes, and new-village startup until all three succeed; legacy account snapshots are already initialized.
- Browser activity statistics are account-scoped: lifetime counters persist; session counters do not.
- Build troops `% resources` checkboxes use OR semantics: at least one resource must be selected, any selected
  resource at or above the percentage threshold releases training, and deferred waits use the earliest selected
  resource ETA. This trigger never replaces the normal all-resource affordability, NPC, or hero-resource checks.
- New Build troops settings default all three training buildings to `% resources` at 90%, with Wood, Clay and Iron
  selected and Crop unselected. Troop-settings sync copies all three building rules plus shared resource/fallback
  settings from one source village to selected targets, but never changes a target village's Build troops ON toggle.
  Saved village training rules are authoritative for the next order. Refresh pending payloads by canonical village
  key without deleting tasks or resetting their deadlines; paused tasks resolve the latest rules on execution too.
  Capture the task account/village at execution, override stale queued training rules, and check the saved snapshot
  again after click pacing immediately before Train. A changed snapshot defers normally without clicking; orders
  already submitted to Travian remain untouched.
- Build troops `maximum` amount mode must click Travian's numeric `.details .cta a[href='#']` shortcut beside the
  selected troop input and verify Travian filled the advertised amount; do not type that maximum manually. The
  existing paced Train-button click remains the submit action after the shortcut succeeds.
- Dashboard B/S/W troop indicators represent effective per-village Build troops configuration, never training
  queue activity: green means Auto + Build troops + that building toggle are enabled and the building exists;
  amber means effectively enabled but the building is missing or its status is unknown; muted means disabled.
- Map SQL `Skip own villages` resolves the in-game owner name from the active page's `.content > .playerName`;
  never substitute the login email/account key. Add the resolved name to the normalized ignored-player filter so
  every village owned by that player is excluded, and fail the import safely when the checked filter cannot resolve it.
- Bulk messages must classify every Send as verified sent, one missing player, or a visible/timeout error. Remove
  missing players one at a time and retry the same batch; an emptied batch continues to later batches. Cache only
  recipients from a verified send. The analysis preview shows the summed map.sql village population per player in
  the exact selected send order.
- Farm-list exact timers get a 5-15s render margin; unreadable disabled timers use an estimated 60s wait.
- "Individual schedule" and "Shared schedule" send only UI-enabled farm lists ONE AT A TIME via
  `SendFarmListsSequentiallyAsync`: click each list's Start,
  then wait for that list's `.farmListStatus` "N/M being raided" numerator to rise (or its Start to disable)
  before the next individual click so a failed list is detected before advancing.
  The wait between clicks is the "Send farmlists" action pacing (`FarmListStepDelayMin/MaxSeconds`, default
  1-4s, on the Settings pacing tab). "Send all" instead performs one click on Travian's
  `button.startAllFarmLists` control, using the established real-click-with-JS-fallback flow, and ignores UI toggles.
  In "Individual schedule", every list requires an account-scoped Min/Max interval keyed by stable `lid`.
  When a list has no valid saved pair, copy and persist `ContinuousFarmDispatchDelay` as its initial values;
  those values are then independent and empty/partial edits are invalid rather than a runtime fallback.
  Persist the randomly selected `NextSendAtUtc` with `LastSentAtUtc`, advance only confirmed sends, and reuse
  that deadline after restart. Runtime edits and successful manual sends recalculate from the latest successful
  dispatch and wake the existing farming task. "Shared schedule" ignores individual deadlines and uses the global
  whole-round delay for enabled lists; "Send all" uses the same whole-round delay for every account list.
  The Farm lists reset action copies the current global Min/Max default to every real list; the existing row-change
  persistence then recalculates each saved individual deadline from its latest successful dispatch.
- Farm-list rows dedupe/merge by stable `lid` (data-list), never by display name — two villages can hold
  same-named lists that a name key would collapse into one row/group. Rows are grouped in the UI by the owning
  `.villageWrapper` ordinal (read per analyze), not by name, so two villages that share a display name stay in
  separate groups; the heading label is the village name plus coordinates. The farm page exposes no village
  id/coordinates on the wrapper, so coordinates are resolved from the known village list by name and only when
  that name is unique (a duplicated name is ambiguous → name only). A village rename just re-groups next read.
- Moving a loss target uses the live-verified Official route `/build.php?id=39&gid=16&tt=99` and row-edit
  contract: `td.openContextMenu` → `.entry.edit` → `.dialog.basic.slotDialog`, then
  `select[name='listId']`, `input[name='isActive']`, and `button.save`. Keep the list `lid` as the stable
  destination identity; display name is only for rebind/recreation when a configured list disappears.
  If the React context menu does not render, retry opening it once with the existing synthetic fallback and
  treat both Playwright and system timeouts as a row-level retry. Confirm a duplicate override at most once;
  if the saved state remains ambiguous, refresh once and verify slot id, destination list id, and disabled state
  before allowing another mutation attempt.
- Farm-list loss handling is configured independently for red (`attack_lost`) and yellow
  (`attack_won_withLosses`) results. Each color has its own move toggle, destination identity, and rollover base
  name; moving requires that color's non-oasis deactivation toggle. Oasis deactivation has independent red/yellow
  selections and never moves oasis targets. Enabling a move toggle may start destination setup only while the user
  is already logged in; logged-out attempts are reverted with a login-required dialog and must never start login or
  browser work. Legacy combined settings seed both colors and both destinations.
- Analyzed farm lists persist per account (`FarmListsSnapshotPath`) and are restored into the panel at
  startup / account switch so it is never blank; restored timers are re-based on the capture time and
  `_lastFarmListsAnalysisAt` stays `MinValue` so a real re-analyze still fires when due.
- Never stack Add-target dialogs: a canceled/failed add-farms run can leave the dialog open, and the reopen
  dispatch fires even behind an overlay, so opening a new one produces two stacked dialogs whose top
  `#dialogOverlay` intercepts every click on the form inputs (coordinate click times out). `OpenAddRaidFormAsync`
  closes any lingering dialog before opening, and a single target's fill/save exception is skipped (bounded
  consecutive-failure abort) instead of failing the whole batch.
- Reused Add-target dialogs must replace X/Y through the traced input path and re-read both fresh fields as an exact
  pair before validation. Retry replacement only a bounded number of times and never click Save while either
  coordinate differs from the requested value.
- A Farm Lists overview read refreshes the Official Farm Lists page exactly once before reading its React state,
  even when the browser is already on that page. Wait for the refreshed page and list wrappers before projecting
  counts and capacities into Add farms or the Desktop UI; later retry attempts may reuse the hydrated page.
- A rendered empty Official Farm Lists page is identified by `#rallyPointFarmList .farmListCount .nominator`
  containing zero. Treat that explicit count as complete immediately and skip the reopen retry; missing
  `.farmListWrapper` elements alone are not proof that the React page finished rendering.
- When Travian leaves a valid Add-target lookup unresolved, close/reopen the form and retry that same coordinate once
  before marking it failed. Definitive invalid-coordinate, occupied-oasis, duplicate, and verified Save outcomes are
  never retried; an exhausted lookup retry must state that Save was not attempted.
- Add farms target protection reads the resolved `.targetWrapper .player` and `.targetWrapper .alliance` before Save.
  The account player is always excluded; the optional current-alliance and account-scoped player/alliance lists use
  normalized exact matching. Missing identity retries the lookup once and must never click Save. Cache resolved
  protection decisions only for the current Add farms run so a blocked coordinate is not reopened for each target list.
- Program-created farm lists carry the account-scoped Create-popup preference `Only create reports with losses`,
  defaulting enabled when absent. Before Create, set and verify `#createFarmListForm input[name='onlyLosses']` with
  a direct input click (the wrapping label can be covered by `.onlyLossesSelection`); use a short actionability wait
  with a trusted forced-click fallback. A missing or unverifiable checkbox is logged but must not block list creation.
- Hero settings are execution-authoritative from the latest saved account settings. A queued `hero_manage` payload
  is only a snapshot and must never overwrite changed HP, revive, ointment, attribute, adventure-selection, or
  continuous-adventure controls while the task was waiting. `spend_hero_attribute_points` likewise always uses the
  latest attribute priority and maximums.
- Hero runtime state is published as one structured Worker update (`HeroRuntimeStatus`). The Hero page and the
  Village overview icon must consume that same update so away/dead/reviving state cannot diverge between views.
- Hero Attributes navigation is required only when the sidebar signals new points or no known attribute snapshot
  exists. Successful point allocation invalidates memory and disk, and incomplete DOM reads never overwrite a valid
  snapshot. Hero HP uses the global SVG first and opens Attributes only when that live signal is unavailable.
- Automatic ointment use is triggered only for a home, living Hero with an available adventure when HP reaches or
  falls below the configured adventure minimum. Identify the inventory item by Official Travian's `item106`
  contract (with `inventory_5` as a fixture-backed fallback), pace both clicks, and verify live HP after Use. A
  confirmed empty ointment inventory creates a silent, persisted 12-hour account+server cooldown; HP regeneration,
  changed adventure count, and process restart must not bypass it. A later real inventory read finding ointments
  clears the cooldown immediately.
- Hero inventory resources are an account+server persisted last-known snapshot. Quick re-login, process restart,
  and account switching restore it; incomplete inventory reads never replace it with fabricated zeroes. When no
  snapshot has ever been captured, construction/resource actions may open their existing resource-transfer dialog,
  read the live inventory, and close it without transferring before continuing the original action. A cached empty
  inventory is not permanent truth: after a persisted randomized cooldown, the next real resource-blocked action may
  re-open that exact current-page dialog, refresh the inventory and transfer in the same dialog when allowed. Repeated
  confirmed-empty reads back off from 15–30 to 30–45 and then 45–60 minutes per account+server; never navigate to the
  Hero inventory page or probe more than once inside the cooldown.
- React task-tab changes use Action pacing's click delay before the DOM click. Do not put the delay after the
  General/Village tab click; that makes a Collect-to-tab transition effectively instantaneous.
- Bonus-video failures use shared protected timing, typed cooldowns, account proxy routing, and sanitized logs.
  See [bonus-video ADR](adr/2026-07-18-bonus-video.md).
  Consentmanager may render after initial page readiness. Initial isolated-video flows observe it for a bounded
  window, wait for its overlay to stop intercepting input after acceptance, and retry a trusted trigger click once
  only when Playwright confirms that the CMP overlay blocked that click; never force-click through the overlay.
  Start playback only through the exact visible provider play control after ancestry, geometry, and center hit-testing;
  never use a blind video-area/iframe-center click because a partially rendered player may expose an advertiser link.
  Optional audio muting is strictly best-effort and defaults enabled through General settings. Click only the exact
  visible `.atg-gima-audio-button-enabled:not(.atg-gima-hidden)` icon after bounded geometry, ancestry, and center
  hit-testing prove that exact icon owns the click. A direct visible HTML `<video>` without provider audio controls
  may instead be muted through its media properties. Never click its wrapper, the video area, use force/JS click
  fallback, or let a missing, unsafe, or failed audio control interrupt or fail the video lifecycle.
  The provider may autoplay without rendering a play button. Verified active HTML-media playback is a trusted start
  signal: poll for autoplay or a safe play control for at least 20 seconds after the player appears, then begin the
  normal protected completion wait and attempt optional muting instead of closing the isolated browser.
  Isolated video browsers keep Chrome's native popup blocker and suppress `window.open`, `_blank`, and external-protocol
  escapes before their first page. Include every isolated launch in PID+start-time ownership tracking; cleanup may
  terminate only recorded identities and must never kill Chrome by name or executable path alone.
- One `activate_production_bonus` run is a contiguous four-resource batch: after its initial cooldown gate,
  attempt every resource found activatable before returning control to other automation. A failure or newly
  created internal video cooldown for one resource must not stop the remaining resources in that same batch.
- Diagnostics use shared busy/cancel behavior, sanitize settings/logs/paths/URLs/auth/proxy data, and never present
  partial output as a successful archive. Screenshots may contain visible game data.
- The Dashboard active-village border represents verified live browser state only. Queue selection/Running state
  must never pre-mark a task's target village; update it only after a successful browser village verification.
- Incoming Attack monitoring may navigate to Rally Point only while Continuous Loop or Auto Queue is running; being
  logged in is not sufficient. With Plus, the global village-list attack markers are read from active automation
  snapshots even outside Dorf1; only a real Dorf1 read may authoritatively clear the active village. A newly appeared
  Plus marker triggers an immediate detail read only when that village has no confirmed movement-count history. A
  confirmed village is read again only when a live red Dorf1 count exceeds its retained high-water mark. The active village's Dorf1 signal requires the hostile red
  `img.att1` marker inside `.villageInfobox.movements #movements` (movement labels and `def1`/`att2` must never
  signal an attack), while a Plus village overview uses
  `.listEntry.village.attack[data-did]`. A nullable signal list means neither Dorf1 nor the Plus village overview
  was read and must preserve prior signals; a completed Dorf1 read without an active-village signal is authoritative for that village and
  immediately clears both pending and confirmed attack rows for it, even if Plus signals another village. Rally
  Point detail reads first inspect the fixed Dorf2 slot 39. An apparently empty slot must be confirmed by one reload;
  a confirmed missing/destroyed Rally Point uses the visible red Dorf1 timers as a non-authoritative fallback and must
  not wait for Rally Point filter controls. Incomplete or ambiguous Dorf2 reads continue to the normal detail attempt.
  Point details open `gid=16&tt=1&filter=1&subfilters=1`, wait for the
  filter controls, and are read only after the parent `button.iconFilterActive img.filterCategory1` and exactly
  `button.iconFilterActive img.subFilterCategory1` are active while subcategories 2/3 are inactive. Never treat the
  parent `filterCategory1` as an extra subfilter: enabling the parent, enabling subcategory 1, and disabling
  subcategories 2/3 are separate navigations and must be verified against fresh DOM after each click. When the
  incoming overview exposes `.paginatorTop .paginator a.next`, follow it until no visible next-page link remains,
  verify the filter on every page, and combine movements by movement id. A late Rally
  Point result must not restore state cleared by a newer Dorf1 read.
  Filter/read failures never clear known attacks. Exact movements persist per account+world, use the Travian
  movement id when available, and expire at their server-derived absolute arrival. After a successful Rally Point
  read, do not replace an arrived confirmed movement with a pending warning. Pending Dorf1 signals with known arrival
  times expire when their last arrival passes; legacy pending signals backed only by arrived confirmed history are
  discarded on restore.
  Retain the confirmed movement-count high-water mark until an authoritative clear Dorf1 read. Countdown drift,
  landed movements, Plus marker repeats, and periodic timers must not reopen Rally Point; read details again only when
  the live red Dorf1 movement count exceeds that mark. Only an unconfirmed/failed signal receives the bounded
  ten-minute retry. In each Rally Point movement,
  `td.role` names the source village; the leading text of `td.troopHeadline` before `raids/attacks <target>` names
  the source player. Monitoring enablement defaults on for every village and persists per account+world by canonical
  village key. A disabled village ignores new Dorf1 signals and Rally Point results and is ineligible for Troop
  Evasion, but its already-confirmed rows remain visible and persisted until their arrival. Persist the confirmed
  movement-count high-water mark separately from visible rows, including after the user presses `Clear list`, so the
  same movements are not fetched again after a restart or manual list clear.
  The Incoming attacks village toggles and Dashboard > Village settings `Attack scan` column are two views of the
  same per-account/world setting; bulk changes must persist once and refresh both views without clearing confirmed rows.
- Troop Evasion consumes Incoming Attack state; it must never introduce a parallel signal source. Target Dorf1 is
  re-read before Rally Point details: only red `img.att1` rows qualify, their timers are the fallback if Rally Point
  fails, and a clear target Dorf1 read cancels pending evasion and skips Rally Point. Evasion settings and successful
  protection windows persist atomically per account+world by coordinate key; corrupt files are quarantined. Automatic
  dispatch is high-priority safe-boundary work gated by Continuous Loop or Auto Queue but independent of Village Auto.
  Destination coordinates and movement type are global per account+world; village enablement, troop slots, and Hero
  selection remain per village. The global `Evade for` filters default to both Raid and Attack, persist per
  account+world, and gate scheduler candidates by authoritative movement type; an unknown Dorf1 fallback is eligible
  only while both filters are enabled. Enabling an incomplete village is rejected with the themed warning dialog and
  a concrete list of missing coordinates, troop/Hero selection, movement type, or incoming-type selection. Sync
  settings copies every troop-slot and Hero choice from one village to explicitly selected target villages, but never
  changes their individual enabled state.
  Dashboard > Village settings `Troop evade` is a projection of the same per-village evasion setting and must use the
  normal completeness validation, themed warning, immediate persistence, and bidirectional UI synchronization.
  The first `#ok` and final `#confirmSendTroops` are separate one-shot state changes. Reinforcements confirm immediately;
  Raid/Attack confirms only when a round trip cannot return before the triggering arrival plus 15 seconds, and never at
  or after that arrival. Cancellation before final Confirm creates no protection state.
  A live Dorf1 `.villageInfobox.units #troops td.noTroops` observation is authoritative evidence that no troops are at
  home: a jitter/status observation no older than two minutes may skip only the initial lead-time milestone. The
  one-minute and thirty-second retries always switch to the source Dorf1 and recheck live before Rally Point, so troops
  that returned after the initial observation are still considered. Missing unit markup means unknown, not empty.
  Troop presence and its own observation timestamp must be merged together and are not restored across process restart.
- Construction timers shown in the village overview are Travian's raw slot finishes. Scheduling, loop wake-up,
  and `Next task` use the effective availability time: raw finish plus the already persisted construction-humanize
  delay (and existing race buffer). Forecasts must reuse the live selector without mutating queue, rotation, or
  pacing state; normal construction navigation must not start exactly when the raw timer expires. Select and persist
  the normal construction delay before navigating to the task village; the worker then consumes that one-shot decision
  without randomizing again. Login-fill and pre-sleep-fill keep their explicit early-fill exceptions. Login never
  forces or reschedules a Village scan: it may fill only the live-verified browser village, while an independently due
  scan may fill free slots as it naturally visits each village. Full slots retain their persisted queue-humanize extra
  so later navigation still waits for the effective deadline.
- Every target-specific live confirmation that a building or resource entered Travian's construction queue publishes
  that authoritative overview snapshot to Desktop immediately. Update the coordinate-owned village cache and green
  construction-slot icons before the enclosing Worker task finishes; retain the validated current-Dorf2 post-task
  read with full Dorf1+Dorf2 fallback as backup.
- A complete live Dorf2 overview may reconcile a pending ordinary-slot construct whose requested slot was manually
  occupied by another building. Rebind the construct and its dependent upgrades atomically to the lowest confirmed
  empty slot 19-38, without stealing slots reserved by other queued constructs. Incomplete or unknown slot state never
  authorizes a move. If a complete overview confirms that all ordinary slots are occupied, fail the construct into
  History with one actionable alarm so later queue work can continue; if empty/unknown slots still exist but none is
  safely assignable, keep and defer the item without clicking or consuming failure retries. A stale upgrade for a
  multi-instance building may rebind only when exactly one live instance remains below its target and no active
  construct still owns the queued slot; never guess between multiple unfinished instances. Unresolved safety alarms
  contain village, task, queued/live slot identity, and every unknown ordinary slot. Normal resource and
  construction-slot waits remain non-alarm status.
  Official empty slots still contain a clickable `a.emptyBuildingSlot`; that link is explicit empty evidence, not
  occupancy. Treat `emptyBuildingSlot`, `g0`, and `data-gid=0` as empty before applying generic link evidence.
- An automation run captures Worker's actual `BrowserGeneration`; never mirror or synthesize that generation in
  Desktop. Runtime-item reconciliation identifies village scope with `BotOptionPayloadKeys.TargetVillageKey` and
  must preserve an existing pending item's authoritative `NextAttemptAt` when refreshing payload or priority.
- Human session-log and alarm lines carry captured account, task, village, and coordinate context. Establish the
  context at queue/Worker execution seams and update it only from verified active-village identity; missing values
  are rendered as `-`, never guessed from the selected UI village. Desktop keeps the original raw message separate
  for status parsers and adds context only to the displayed/file/alarm line. Detailed browser traces retain their
  existing structured context instead of receiving a duplicate suffix.

## Target architecture

- Smaller domain services for construction, farming, hero, map, messages, and account state.
- One deep Desktop orchestration module owns Continuous Loop and Auto Queue policy and runtime state;
  `LoopController` retains lifecycle/cancellation and Worker retains Official Travian browser actions.
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
- [Continuous automation orchestration](adr/2026-08-14-continuous-automation-orchestration.md)
- [Troop evasion deadlines](adr/2026-08-22-troop-evasion.md)

## Arkiverad historik

Äldre beslut och detaljerad historik finns i:

- [Pre-compression snapshot, 2026-07-14](history/engineering-notes-2026-07-14-pre-compression.md)
- [Engineering notes archive](history/engineering-notes-archive.md)

Before deleting or shortening a rule, confirm that its detail exists in the snapshot, archive, or an ADR.
