# Plan: Travco inactive search + saved lists

## Context
Farming targets on official Travian servers can be found via the external site
`travcotools.com/en/inactive-search/`. Today this is fully manual. The goal is a button on the
**Farming** page that opens a popup which drives Travco in a browser tab (auto-filling the
account's server, capital coordinates, days inactive = 2, results per page = 100, then Search),
shows a login-style loading overlay while it works, and lets the user save the result page as a
named, reusable list (Distance / Account / Village / Pop / Coordinates) with per-row selection.
These lists are the foundation for a later step: importing them into Travian farm lists.

**Confirmed decisions:** Official servers only (Travco has no SS-Travi servers — auto-match by
host, error if no match). `Pop` = latest day's population column. Coords/days/server are
auto-filled; the user only clicks **Inactive search**.

## Architecture fit
- The shared visible Chromium session lives in `BotTaskRunner` (`_sharedVisibleSession` /
  `_sharedVisiblePage`), serialized by `_sessionGate`
  ([BotTaskRunner.cs:63-71](src/TbotUltra.Worker/Services/BotTaskRunner.cs), reuse pattern from
  `ExecuteWithClientAsync` / `AcquireClientLeaseAsync`). A Travco tab is a **second `IPage` in the
  same context**, opened with `_sharedVisiblePage.Context.NewPageAsync()` (same approach as
  `TravianClient.Catapults.cs`). It is **owned by `BotTaskRunner`** (`_travcoPage`) so it survives
  across the popup's multiple operations; `TravianClient` instances are per-lease and unsuitable.
- Travco is a public site (no Travian login needed), so the tab only needs the browser context,
  not `LoginAsync`.
- Loading overlay: reuse `Views/BusyOverlayControl` (same control used for login,
  `MainWindow.Session.cs ShowBusyOverlay/HideBusyOverlay`) **embedded inside the popup window**.
- Persistence: per-account JSON file, same pattern as `VillageSettingsStore`
  ([VillageSettingsStore.cs](src/TbotUltra.Desktop/Services/VillageSettingsStore.cs)) with
  `System.Text.Json`, CamelCase, and the shared `FileIoLock` retry.
- Pause/restart: reuse `_loopController` graceful-stop calls and the run-mode flags from
  `MainWindow.Toolbar.cs` (`StartLoopButton_Click`).

## Changes

### 1. Worker — Travco automation
- **New** `src/TbotUltra.Worker/Services/Automation/TravcoInactiveSearch.cs` — static helper taking
  an `IPage` (the Travco tab). Not Travian-flavor logic, so it stays out of `TravianClient`.
  - `RunSearchAsync(page, serverHost, x, y, daysInactive, resultsPerPage, ct)`:
    `GotoAsync(inactive-search url)` → select server `<option>` matching `serverHost` (host of
    `BaseUrl`; throw a clear "server not on Travco (official only)" error if none) → fill X/Y →
    select days inactive → set results-per-page = 100 → set sorting = Distance → click Search →
    wait for the results table.
  - `ScrapePageAsync(page, ct)` → `IReadOnlyList<TravcoRow>` via `EvaluateAsync` over result rows:
    Distance, Account, Village, latest-day Pop, Coordinates (parsed from the village cell/karte
    link), in DOM order (page is distance-sorted). Also read current Travco page number for the
    default list name.
  - Selectors to be confirmed by live DOM inspection during implementation (browser MCP); log each
    step for future debugging per CLAUDE.md.
- **New** record `TravcoRow(double? Distance, string Account, string Village, long? Pop, string Coordinates)`
  and `TravcoScrapeResult(int PageNumber, IReadOnlyList<TravcoRow> Rows)` in Worker domain.
- **Modify** [BotTaskRunner.cs](src/TbotUltra.Worker/Services/BotTaskRunner.cs): add `_travcoPage`
  field and three gated methods (each `await _sessionGate.WaitAsync` like `ShutdownAsync`):
  - `OpenTravcoAndSearchAsync(options, x, y, daysInactive, log, ct)` — ensures a shared visible
    session/context exists (reuse `AcquireClientLeaseAsync` plumbing or open a context directly),
    creates/reuses `_travcoPage`, calls `RunSearchAsync`.
  - `ScrapeTravcoPageAsync(log, ct)` — calls `ScrapePageAsync` on `_travcoPage`.
  - `CloseTravcoTabAsync(log)` — closes `_travcoPage` and nulls it. Also null/close it in
    `ShutdownAsync`.
- **Modify** Desktop bot-service wrapper (`IBotService`/`BotService`, the `_botService` used in
  `MainWindow.Farming.Manual.cs`) — add passthroughs for the three methods above.

### 2. Persistence
- **New** `src/TbotUltra.Desktop/Services/TravcoListStore.cs` (mirror `VillageSettingsStore`):
  per-account `config/accounts/<account>/travco_lists.json`. Records:
  `TravcoSavedList { Id, Name, CreatedUtc, Rows[] }`, row
  `{ Distance, Account, Village, Pop, Coordinates, Selected }`. Methods: `LoadAll`, `Save(list)`,
  `Delete(id)`, `InvalidateCache` (call from `ClearAccountScopedUiState`; do **not** delete the
  file on account switch — it is per-account memory).
- **New** helper `AccountStoragePaths.TravcoListsPath(root, account)`
  ([AccountStoragePaths.cs](src/TbotUltra.Core/Accounts/AccountStoragePaths.cs)).

### 3. Desktop UI
- **New** `src/TbotUltra.Desktop/Models/TravcoListRow.cs` — `INotifyPropertyChanged` row with
  read-only Distance/Account/Village/Pop/Coordinates + observable `Selected` (pattern from
  `VillageSettingsRow`).
- **New** `TravcoToolsWindow.xaml` + `.xaml.cs` (popup, owner = MainWindow):
  - Buttons: **Inactive search**, **Save page as list** + name `TextBox` (default
    `Travco page {n}`), **Open** (load selected saved list into the grid), **Delete**.
  - Saved-lists `ListBox` (left) + results `DataGrid` (right) with a `DataGridCheckBoxColumn`
    bound to `Selected` (default true) and read-only columns Distance/Account/Village/Pop/Coords.
  - Embedded `BusyOverlayControl` shown during search/scrape, hidden when the page finished loading.
  - Window-close (`X` or a Close button) → `AppDialog.ShowCustom` Yes/No confirm; on Yes →
    `CloseTravcoTabAsync`, close window, signal MainWindow to restart the bot if it was running.
- **New** `src/TbotUltra.Desktop/MainWindow.Farming.Travco.cs`:
  - `TravcoInactiveSearchButton_Click` → `BlockIfSessionSleeping`, capture run mode
    (continuous vs queue, from `ContinuousRunToggleButton.IsChecked` / `_autoQueueRunning` /
    `_loopTask`), gracefully pause via `_loopController.RequestLoopStop/RequestQueueStop`, open the
    popup.
  - Provide callbacks the popup invokes: run search (capital coords from capital cache via
    `ApplySelectedVillageToOptions`/capital state; server host from `BotOptions.BaseUrl`), scrape,
    save/open/delete list (via `TravcoListStore`), and on confirmed close restart the previously
    running mode (`StartContinuousLoopRunner()` or `TriggerQueueAutoRunAsync()`).
- **Modify** [MainWindow.xaml](src/TbotUltra.Desktop/MainWindow.xaml) Farming-tab left-column
  `WrapPanel` (~line 1529): add button **"Travco inactive search"** wired to the handler.
- **Modify** `ClosePopupWindows` / closing path to also close the Travco popup + tab.

## Out of scope (next step)
Importing saved lists into Travian farm lists (explicitly the following step).

## Verification
1. `dotnet build TbotUltra.sln` (close the running app first — DLL lock).
2. `dotnet test src/TbotUltra.Worker.Tests/...` and Desktop tests (store/scrape unit-testable parts).
3. Live (official server): login, Start bot, click **Travco inactive search** → bot pauses, popup
   opens, click **Inactive search** → loading overlay shows, new tab auto-fills server + capital
   coords + days 2 + results 100 + Search, overlay hides when loaded. Confirm coords/server are
   correct in the tab (browser MCP).
4. **Save page as list** with default + custom name; verify `travco_lists.json` rows
   (Distance/Account/Village/Pop/Coords, Selected=true), distance order matches the page.
5. Create several lists; **Open** shows rows; toggle row selection; **Delete** removes a list.
6. Close popup → Yes confirm → tab closes, popup closes, bot restarts in the prior mode. (No → stays.)
7. SS-Travi account: search reports the clear "official servers only" error and does not crash.
