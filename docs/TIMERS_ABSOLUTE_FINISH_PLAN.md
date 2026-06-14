# Plan: Timers as absolute finish timestamps (survive restart/sleep)

## Context
Today every queue/process timer is stored only as a **remaining-seconds countdown**
(`RemainingSeconds`, `TimeLeftSeconds`, `SecondsUntilReturn`, …). Countdowns are only
valid while the program runs; across a restart, sleep, or a long offline period they
become meaningless. The persisted village cache (`village_cache.json`) even *strips* all
timer values on save (`VillageCacheStore.StripVolatile`), so nothing timer-related
survives a restart — the only exception is `QueueItem.NextAttemptAt`, which already uses
an absolute `DateTimeOffset`.

Goal: every timer is captured as an **absolute finish timestamp** at read time, using
**Travian server time** as the primary clock and `DateTimeOffset.UtcNow` as fallback, so
status can be reconstructed after any downtime. On startup, a finish time already in the
past means the object is treated as **finished/stale** (not "still running") until a real
page scan confirms the truth. The live page always remains source of truth.

Decision (confirmed with user): **additive, low-risk** — add new finish-timestamp fields
alongside the existing countdown fields; do **not** rip out `RemainingSeconds`. Existing
live UI keeps working; persistence switches to absolute times.

Covers: building construction, resource fields, troop training, smithy upgrades,
hero adventures/return/revive, brewery celebration, farm-list timers.

## Design

### 1. Shared value object + server-time base (foundation)
Add one reusable record in `src/TbotUltra.Worker/Domain/TravianModels.cs`:

```csharp
public sealed record TimerSnapshot(
    int RemainingSeconds,        // duration as read from the page
    DateTimeOffset ReadAtUtc,    // when it was read
    DateTimeOffset FinishUtc,    // absolute finish = base time + remaining
    bool FromServerTime);        // true = Travian server time, false = UtcNow fallback
{
    public static TimerSnapshot FromRemaining(int remainingSeconds, DateTimeOffset? serverTimeUtc);
    public int RemainingSecondsAt(DateTimeOffset now);   // max(0, FinishUtc - now)
    public bool IsFinishedAt(DateTimeOffset now);        // FinishUtc <= now
}
```

`FromRemaining` uses `serverTimeUtc` when supplied (sets `FromServerTime=true`), else
`DateTimeOffset.UtcNow`. Base time = chosen clock; `FinishUtc = base + remaining`.

Expose the client's current server clock so parse sites can build snapshots. In
`TravianClient.cs` (`_serverTimeUtc` already exists, set by `RecordServerTime`) add:

```csharp
public DateTimeOffset? CurrentServerTimeUtc => _serverTimeUtc;
```

### 2. Capture absolute finish at every parse site (additive field per record)
Add a nullable `TimerSnapshot?` (or a list, where several timers exist) to each
timer-bearing record, populated where it is constructed from parsed seconds:

| Record (TravianModels.cs) | New field | Built in |
|---|---|---|
| `ActiveConstruction` | `Finish` (`TimerSnapshot?`) | `TravianClient.Buildings.cs` `ReadActiveConstructionsAsync` |
| `TroopTrainingQueueStatus` | `Finish` | `TravianClient.TroopTraining.cs` |
| `SmithyUpgradeStatus` | `ActiveUpgradeFinishes` (`IReadOnlyList<TimerSnapshot>`) | `SmithyPageParser` callers in `TravianClient` |
| `HeroStatus` | `AdventureReadyFinish`, `ReturnFinish`, `ReviveFinish` | `TravianClient.Hero.cs` |
| `BreweryCelebrationStatus` | `Finish` | `TravianClient.BreweryCelebration.cs` |
| `FarmListOverview` | `Finish` | `TravianClient.cs` farm-list read |
| `VillageStatus` | `BuildQueueFinish` (for `BuildQueueRemainingSeconds`) | where build-queue remaining is aggregated |

Each is filled via `TimerSnapshot.FromRemaining(seconds, client.CurrentServerTimeUtc)`.
`RemainingSeconds`/`TimeLeftSeconds` stay untouched for live display.

### 3. Persist absolute finish times (durable state)
`village_cache.json` (`VillageCacheStore`) is the existing per-account durable home for
`VillageStatus` and is the single vehicle reused here.

- **Stop stripping the finish data.** In `VillageCacheStore.StripVolatile`, keep the new
  `TimerSnapshot` fields (and the `ActiveConstructions` / `TroopTrainingQueues` lists that
  now carry them). Continue to strip pure-volatile display values (current resource
  amounts, gold/silver, `BuildQueueRemainingText`, etc.).
- `TimerSnapshot` serializes cleanly as JSON (ints + `DateTimeOffset` + bool), matching the
  existing camelCase `JsonSerializerOptions`.
- **Smithy / brewery / farm-list** are not on `VillageStatus` today. To let their timers
  survive too, add additive `SmithyUpgradeStatus?` / `BreweryCelebrationStatus?` /
  `IReadOnlyList<FarmListOverview>?` fields to `VillageStatus` so they ride the same cache
  save/load. (Rationale, 1 sentence: reusing the one existing per-village cache avoids new
  store plumbing and keeps all per-village timers in one durable file.)

### 4. Restore on startup / resume (don't assume "still running")
When the cache is loaded (`VillageCacheStore.Load`, consumed in
`MainWindow.VillageWorking.cs` `LoadVillageCacheForActiveAccount`), reconcile every
restored timer against **server-now** (`GetServerNow()` / `ResolveQueueServerTimeOffset`
in `MainWindow.ServerClock.cs`):

- `RemainingSeconds = TimerSnapshot.RemainingSecondsAt(serverNow)`.
- If `IsFinishedAt(serverNow)` → treat as **finished/stale**: clear the active timer/entry
  and mark the village as needing a fresh scan; never present it as still in progress.
- Otherwise keep it with the recomputed remaining for display.

A fresh page scan always overwrites the cached snapshot (source of truth) — already true
since scans replace `_villageStatusCacheByName` entries via `CacheVillageStatus`.

## Critical files
- `src/TbotUltra.Worker/Domain/TravianModels.cs` — new `TimerSnapshot`; additive fields on the records above.
- `src/TbotUltra.Worker/Services/Automation/TravianClient.cs` — `CurrentServerTimeUtc`; farm-list + smithy capture.
- `src/TbotUltra.Worker/Services/Automation/TravianClient.Buildings.cs` — `ActiveConstruction.Finish`, build-queue finish.
- `src/TbotUltra.Worker/Services/Automation/TravianClient.TroopTraining.cs` — troop-training finish.
- `src/TbotUltra.Worker/Services/Automation/TravianClient.Hero.cs` — hero finish times.
- `src/TbotUltra.Worker/Services/Automation/TravianClient.BreweryCelebration.cs` — brewery finish.
- `src/TbotUltra.Desktop/Services/VillageCacheStore.cs` — retain finish data in `StripVolatile`.
- `src/TbotUltra.Desktop/MainWindow.VillageWorking.cs` (`LoadVillageCacheForActiveAccount`, `CacheVillageStatus`) + `MainWindow.ServerClock.cs` — recompute-on-load / stale handling.

## Verification
1. `dotnet build` the solution; confirm no breaks from the additive record fields.
2. Add unit tests (mirroring `SmithyPageParserTests`) for `TimerSnapshot`:
   `FromRemaining` with/without server time (source flag + `FinishUtc` math),
   `RemainingSecondsAt` clamping to 0, `IsFinishedAt` boundary.
3. End-to-end manual run: log in with an active build + troop-training + smithy upgrade
   running; confirm `village_cache.json` now contains `finishUtc` values (not stripped).
4. Close the app, wait/advance past one timer's finish, relaunch: the finished item shows
   as done/needs-rescan (not "still running"), and an unfinished item shows correctly
   reduced remaining time. A live scan then refreshes all values from the page.
