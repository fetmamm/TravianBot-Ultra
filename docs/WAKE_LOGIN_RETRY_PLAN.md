# Fix: bot fastnar i idle när wake-login misslyckas efter session sleep

## Context (rotorsak från loggen 20260705_233206)

Händelsekedja natten 2026-07-05 → 2026-07-06:

1. `00:13:35` — Session pacing la botten i sömn (18 min), utloggad som vanligt.
2. `00:32:08` — Pacern vaknade: `CompleteSleepAndWake()` satte `Phase = Disabled`, stoppade timern och triggade `WakeRequested` → `HandleSessionPacingWakeRequestedAsync` → `ExecuteLoginFlowAsync`.
3. `00:34:54` — Själva inloggningen lyckades ("login confirmed").
4. `00:34:57–00:36:05` — Post-login-snapshotens första steg, `ReadHeroInventoryResourcesAsync` → `GotoAsync('/hero/inventory')`, fick Playwright-timeout (20s) 3 gånger i rad (retry-delayen är bara 400/800 ms, så ett tillfälligt nätverks-/serverhäng på ~1 min äter alla 3 försök). `InvalidOperationException` kastades ur hela `ExecuteLoginAndLoadPostLoginSnapshotAsync`.
5. I `ExecuteLoginFlowAsync` fångades felet av catch-blocket → `_isLoggedIn` sattes ALDRIG till true (raden ligger efter snapshot-anropet).
6. Tillbaka i `HandleSessionPacingWakeRequestedAsync`: `if (!_isLoggedIn) return;` → tyst retur. Pacern var redan `Disabled` med stoppad timer → ingen ny sömn, ingen ny väckning, inget nytt loginförsök. Botten stod stilla i 8+ timmar (bara 20s-ticks som bailar på `_isLoggedIn == false`).

Två brister: (a) wake-vägen har ingen retry när login misslyckas — den ger upp permanent; (b) hela login-flödet faller på ett icke-kritiskt snapshot-steg (hero inventory).

## Ändringar

### 1. Retry-loop i wake-hanteraren (huvudfixen)

Fil: `src/TbotUltra.Desktop/MainWindow.SessionPacing.cs`, `HandleSessionPacingWakeRequestedAsync` (rad ~271).

Ersätt:
```csharp
await ExecuteLoginFlowAsync();
if (!_isLoggedIn)
{
    return;
}
```
med en retry-loop:
- Kör `ExecuteLoginFlowAsync()`; om `_isLoggedIn` → fortsätt som idag (resume loop/queue).
- Annars: vänta med stigande delay **2, 5, 10, 15 min, därefter 30 min per försök — utan övre tak** (botten ska överleva natten; timeouts av den här typen är övergående). Motiv: hellre evig, gles retry med tydliga loggar än att vakna till en död bot.
- Före varje nytt försök, avbryt loopen om: `_isLoggedIn` (användaren loggade in manuellt), `IsSessionSleeping` (ny sömn påbörjad), `_accountSwitchInProgress`.
- Logga varje varv tydligt: `[pacing] wake login failed (attempt N) — retrying in X min.`
- Loopen ligger kvar inom `_sessionPacingWakeInProgress = true` (skyddar mot dubbla wake-hanterare; `ExecuteLoginFlowAsync` har redan egen `_loginInProgress`-guard mot manuella klick).
- `Task.Delay` med `_appShutdownToken` om en sådan finns i klassen, annars vanlig delay (kolla vid implementation; avbrottsvillkoren ovan räcker funktionellt).

### 2. Gör hero-inventory-läsningen icke-fatal i post-login-snapshoten

Fil: `src/TbotUltra.Worker/Services/BotTaskRunner.Session.cs`, privata `LoadPostLoginSnapshotAsync` (rad ~121–125).

Wrappa `client.ReadHeroInventoryResourcesAsync(...)` i try/catch (`OperationCanceledException` re-throwas):
```csharp
try { heroInventory = await client.ReadHeroInventoryResourcesAsync(...); }
catch (OperationCanceledException) { throw; }
catch (Exception ex) { log($"[hero-inventory] post-login read failed (continuing without it): {ex.Message}"); }
```
`heroInventory` förblir `null` → `skipOverviewNavigation` blir `false` → `ReadAccountSnapshotAsync` gör sin vanliga dorf1-hopp, resten av snapshoten fortsätter. Hero-inventory är redan ett opt-in-analyze-steg (`PostLoginAnalyzeHeroInventory`), så partial snapshot är rätt beteende — exakt det här steget ska inte kunna fälla hela inloggningen.

### 3. (Liten) längre backoff vid timeout i navigations-retryn

Fil: `src/TbotUltra.Worker/Services/Automation/Core/TravianClient.RetryPolicy.cs`.

I båda `RetryAsync`-varianterna: när `lastError` är/innehåller `TimeoutException`, använd längre delay mellan försöken (t.ex. `5000 * attempt` ms istället för `400 * attempt`). En 20s-nav-timeout betyder nästan alltid segt nät/server — 400 ms paus hinner inte återhämta något. Låt övriga fel behålla dagens snabba delay.

## Filer som ändras
- `src/TbotUltra.Desktop/MainWindow.SessionPacing.cs` — retry-loop i wake-hanteraren.
- `src/TbotUltra.Worker/Services/BotTaskRunner.Session.cs` — try/catch runt hero-inventory-läsningen.
- `src/TbotUltra.Worker/Services/Automation/Core/TravianClient.RetryPolicy.cs` — timeout-medveten backoff.

## Verifiering
- Bygg: appen kan vara igång → kompilera via `dotnet build -p:OutDir=<temp>` eller bygg `TbotUltra.Worker.Tests` (känd DLL-lock annars).
- Kör befintliga tester: `dotnet test src/TbotUltra.Worker.Tests` (+ Desktop.Tests om de bygger utan appen igång).
- Manuell rimlighetskontroll: starta appen, sätt kort run/sleep i session pacing, koppla ur nätverket under väckningen → loggen ska visa retry-raderna och botten ska logga in när nätet är tillbaka.
