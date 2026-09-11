# Tbot Ultra

Tbot Ultra automates actions on Official Travian worlds while keeping account- and world-specific game state separate.

## Language

**Map Oasis Scan**:
A resumable search of Official map areas for oases matching a selected scope and filter.
_Avoid_: Map crawl, oasis scrape

**Map Oasis Checkpoint**:
The account- and world-specific saved progress and partial results of one Map Oasis Scan. It resumes a matching scan after an unexpected failure; user cancellation produces a partial result and clears the checkpoint.
_Avoid_: Scan cache, temporary scan state

**Map Oasis Scan Result**:
The list of oases found by a Map Oasis Scan together with completion information. The oasis list remains the input to Farm Lists and other downstream uses.
_Avoid_: Map data response, scan output

**Construction Queue Reconciliation**:
The application of a confirmed live village construction status to pending construction queue items, including satisfied targets, slot rebinding, and required dependencies.
_Avoid_: Queue repair, build queue sync

**Construction Queue Head**:
The first pending construction item for one village. If it is deferred, later construction items for that village do not start, even when a compatible Travian construction slot is free.
_Avoid_: First task, next build

**Immediate-fill Burst**:
The short construction-start mode armed only by a confirmed empty Official construction overview. It starts all eligible free slots without the normal construction delay; a partially occupied queue always uses normal pacing.
_Avoid_: Fast mode, fill slots

**Storage-capacity Dependency**:
The minimum verified Warehouse or Granary construct/upgrade required before a construction queue head can proceed. It may be inserted immediately before that head when a live construction slot is available; the original item stays deferred.
_Avoid_: Storage bypass, capacity fix

**Missing Storage Building**:
A Warehouse or Granary that does not exist in a village. Its construction is a storage-capacity dependency but requires explicit user confirmation because it consumes a specific building slot.
_Avoid_: Automatic storage construction, missing capacity

**Stale Resource Wait Validation**:
One immediate live resource check for a resource-deferred Construction Queue Head after a confirmed empty construction overview. It may release a stale `page_timer`; it never polls Hero inventory.
_Avoid_: Timer bypass, resource refresh loop

**Live-verified Free Construction Slot**:
A construction slot treated as free only after a live, complete Official construction overview confirms it. Unknown or partial status is not evidence of availability.
_Avoid_: Available-looking slot, optimistic ready state

**Construction Card Status**:
The compact construction state. A live Travian construction timer takes priority; when multiple constructions are active it shows the nearest completion and active count, with no separate queue label. If none is active, it shows the Construction Queue Head's concrete wait (for example `Res: 00:12:34`), then blue `Empty queue` or gray `Unknown`.
_Avoid_: Unexplained timer, build intent

**Storage-capacity Card Message**:
The concrete storage prerequisite shown when no live construction is active: `Warehouse needed: level N`, `Granary needed: level N`, or `… needed — confirm` when constructing the missing storage building requires approval.
_Avoid_: Storage, capacity wait

**Construction Requirement Card Message**:
The compact unmet building prerequisite shown when no live construction is active, for example `Req: Main Building 3`.
_Avoid_: Requires, prerequisite wait

**Construction Retry Card Message**:
The countdown shown when a transient construction attempt has a scheduled retry and no live construction is active, for example `Retry: 00:00:30`.
_Avoid_: Error, stuck

**Empty Construction Queue Card Message**:
The blue `Empty queue` status shown when no construction is active, no construction item is waiting, and free slots have been live-verified. It replaces the green `Ready` state for this case.
_Avoid_: Ready, idle

**Construction Start-delay Card Message**:
The countdown shown while an eligible Construction Queue Head waits only for the normal humanized construction pacing delay, for example `Waiting: 00:00:12`.
_Avoid_: Starting, ready

**Plus Resource Slot Fill**:
When `Upgrade all resources to level` finds an eligible resource field and a Plus construction slot is free, it retains normal humanized pacing but must queue the field before the currently active construction finishes.
_Avoid_: Immediate bulk fill, wait for build completion

**Queue Delay Bound**:
The Plus queue humanization percentage is strictly below 100%, both when configuration is saved and when it is used by the Worker. The delay is additionally bounded to end before the active construction finishes.
_Avoid_: 100% delay, missed Plus slot

**Construction Queue Order**:
A free Plus resource slot may receive a resource upgrade only when Travian permits it without bypassing an earlier queued construction. Resource queue filling never starts a later Barracks or other building task ahead of the existing queue.
_Avoid_: Queue bypass, out-of-order construction

**Plus Slot Verification**:
Before deferring a resource upgrade until an active construction finishes, uncertain Plus status or slot occupancy is re-read from the live Official construction overview.
_Avoid_: Cached Plus state, assumed full queue

**Uncertain Plus Slot Retry**:
If the live construction overview cannot confirm whether a Plus resource slot is free, the worker retries after a bounded short delay instead of treating the queue as full. The retry is rate-limited to avoid repeated navigation and logging.
_Avoid_: Full-queue assumption, request spam

**Uncertain Plus Slot Backoff**:
An uncertain Plus resource slot is retried at most three times. The delay is randomized within 20–60 seconds, then doubles for each subsequent retry (40–120, then 80–240 seconds). After the third failed verification, the worker returns to its ordinary construction pass.
_Avoid_: Fixed retry cadence, unlimited retry loop

**Affordable Bulk Resource Candidate**:
For `Upgrade all resources to level`, the first eligible candidate in the selected order whose catalog cost is affordable from the current Dorf1 stock snapshot. Resource-blocked candidates are filtered locally before build-page navigation; the selected page remains the final authority.
_Avoid_: First blocked field, bulk stop

**Bulk Resource Wait Deadline**:
When no affordable bulk-resource candidate can start, the resource wait is the earliest locally calculated affordability deadline among eligible candidates, with a live representative check only when recovery or incomplete data requires it.
_Avoid_: First wait, strategy wait

**Representative Bulk Resource Offer**:
The first resource field at one exact resource type and level that is checked during a bulk upgrade. If it remains resource-blocked after permitted recovery, equivalent fields do not require separate checks in that pass.
_Avoid_: Every identical field, repeated resource check

**Construction Card Waiting Color**:
Amber/yellow is used for `Res:`, `Req:`, `Waiting:`, and `Retry:` Construction card messages. Blue is reserved for `Empty queue`.
_Avoid_: Green waiting state, mixed status colors

**Demolition Sequence**:
A village-scoped queued target that lowers one building level at a time. Each confirmed Travian demolition stores an absolute next-attempt deadline made from the server timer plus the configured human delay.
_Avoid_: Blocking demolish loop, demolition sleep

**Village Status Round**:
One randomized pass through every known village that reads the enabled live game state, updates the per-village status cache and lets already-enabled automation react before moving to the next village. It leaves the browser on the final naturally visited village; the next round is scheduled separately.
_Avoid_: Village scan loop, village refresh

**Incoming Attack**:
One hostile attack or raid movement travelling toward an owned village. A Dorf1 marker is an immediate signal; the movement becomes authoritative only after the Rally Point incoming filter provides its exact arrival.
_Avoid_: Village under attack, attack history

**Troop Evasion**:
Deadline-driven protective dispatch of selected at-home troops from a configured village after the existing Incoming Attack monitor confirms a hostile red movement. It is neither a normal queue item nor a second attack detector.
_Avoid_: Dodge queue, attack scanner

**Automation Pass**:
One between-actions iteration shared by Continuous Loop and Auto Queue. It may execute at most one eligible queued action before the next selection.
_Avoid_: Loop tick, Auto Queue iteration

**Send Troops**:
The verified Official Travian flow that opens the Rally Point troop form and prepares troop dispatch for combat or Farm Lists.
_Avoid_: Manual Farming, Natar farming

**Manual Farming**:
The removed desktop-only manual farming UI and its saved preferences. It is not Send Troops, Farm Lists, Catapults, or Reinforcements.
_Avoid_: Manual attack flow

**Sleep Snapshot**:
What was actually running (logged-in state, continuous loop, queue auto-run) when session sleep began, captured so the next wake restores the same state instead of always starting the continuous loop. Modeled by the `SleepSnapshot` record; the pure wake decisions live in `SessionWakeDecisions`.
_Avoid_: pre-sleep flags, wake state

**Smart Sleep**:
A bot-behavior mode that closes the browser when a trusted next automation deadline leaves a sufficient sleep opportunity, then restores the prior automation state within its Smart Sleep Wake Window. When no work deadline exists, it uses a randomized fallback check. Smart Sleep and Session Pacing are mutually exclusive, but both may be disabled.
_Avoid_: adaptive session pacing, idle break

**Smart Sleep Wake Window**:
The configurable period before and after a trusted next automation deadline within which Smart Sleep randomly schedules login. Allowed hours and the daily online limit remain hard boundaries.
_Avoid_: fixed wake delay, always-late wake

**New Account Analysis**:
The account-and-world-specific first-login initialization that reads hero inventory, hero attributes, and missing
new-village status. It remains pending until all three reads succeed.
_Avoid_: every-login analysis, hero inventory cache

**Update Notification Acknowledgement**:
The application-wide saved release version that the user has already handled in the new-version notification.
It suppresses only that release; a later release is eligible to notify again.
_Avoid_: update mute, per-account update state
