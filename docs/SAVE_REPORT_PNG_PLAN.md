# Feature: Save report as PNG

## Context

Användaren vill kunna spara en öppnad Travian-stridsrapport som PNG. Användaren navigerar själv till rapporten i browsern, klickar en knapp i appen (fliken Messages/Reports → Reports-rutan), och en PNG av `#reportWrapper` sparas till `<projektrot>\Reports\`. Popup med instruktioner, "Save report"-knapp (grön), "Open reports folder"-knapp och två blur-checkboxar (Hide attacker / Hide defender, default av). Fungerar bara när boten är pausad och användaren står på en rapportsida — annars alarm-popup med OK.

DOM-referenser (verifierade i `temp_build_out/DOM/report_wrapper.txt` + `report_page_report.txt`):
- Rapportsidans URL-path börjar med `/report` (`/report/offensive` osv).
- Öppnad rapport = `#reportWrapper` som innehåller `.role.attacker` (rapportlistan utan öppnad rapport saknar wrappern).
- Attacker-namnrad: `#reportWrapper .role.attacker .troopHeadline` → `span.inline-block` (allians, R€LAX), `a.player` (volvo240), `a.village` (940).
- Defender-namnrader: alla `#reportWrapper .role.defender .troopHeadline` med samma struktur (ken10497/Aries/WS). Reinforcement-block har bara texten "Reinforcement", inga namn/länkar → påverkas ej av selektorerna.
- Rubriken `.header .headline .subject` ("940 attacks Aries") innehåller BÅDA by-namnen → blurras när någon av checkboxarna är ikryssad.

## Återanvändning (befintliga mallar)

- `SavePageHtmlWindow` + `SavePageHtmlWindowSaveRequestedAsync` i [MainWindow.Resources.Actions.cs:500] — exakt samma flöde (popup → läs aktuell sida via botservice → spara fil → Open folder-knapp). Kopiera mönstret.
- `ReadCurrentPageHtmlAsync`-kedjan: `IDesktopBotService` → `DesktopBotService` → `BotTaskRunner.VillageReads.cs:238` (`ExecuteWithClientAsync`, `interactive: false`, `saveStateMode: BrowserStateSaveMode.Skip`) → `TravianClient.Resources.cs:143`. Samma kedja för PNG-funktionen.
- Screenshot: Playwright finns redan (`_page.ScreenshotAsync` i `TravianClient.cs:303`). Elementshot: `_page.Locator("#reportWrapper").ScreenshotAsync(new LocatorScreenshotOptions { Path = ... })`.
- Alarm/OK-popup: `AppDialog.Show(owner, msg, title, MessageBoxButton.OK, MessageBoxImage.Warning)`.
- Grön knapp: `Background="{DynamicResource AccentBrush}" Foreground="White"` (som `StartLoopButton`, MainWindow.xaml:210).
- Tooltip-info-ikon: `SettingInfoIconStyle` i `Themes/Badges.xaml`.
- "Öppna mapp": `Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true })` (SavePageHtmlWindow.xaml.cs:63).
- Bot-körs-detektion: `_autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted)` (mönster från MainWindow.xaml.cs:1029).

## Nya filer

### 1. `src/TbotUltra.Worker/Services/Automation/Features/TravianClient.ReportPng.cs`
Ny partial (följer `TravianClient.ProductionBonus.cs`-konventionen):
```csharp
public sealed record ReportPngResult(bool IsReportPage, string Url, string? FilePath);

public async Task<ReportPngResult> SaveReportScreenshotAsync(
    string filePath, bool hideAttacker, bool hideDefender, CancellationToken cancellationToken)
```
Steg (med `Notify("[report-png] ...")`-loggar):
1. Läs `_page.Url`; kolla path börjar med `/report`. Kolla `#reportWrapper .role.attacker` finns (`_page.QuerySelectorAsync`). Annars → `new ReportPngResult(false, url, null)` (ingen exception — fel sida är ett normalt fall).
2. Om `hideAttacker`/`hideDefender`: injicera `<style id="tbotReportPngBlur">` via `_page.EvaluateAsync` med CSS:
   - attacker: `#reportWrapper .role.attacker .troopHeadline span.inline-block, ... a.player, ... a.village { filter: blur(7px); }`
   - defender: samma under `.role.defender` (träffar ALLA defender-block; reinforcement-block saknar länkar → opåverkade).
   - endera ikryssad: `#reportWrapper .header .subject { filter: blur(7px); }` (rubriken innehåller båda by-namnen).
3. `await _page.Locator("#reportWrapper").ScreenshotAsync(new() { Path = filePath });`
4. `finally`: ta bort style-elementet (try/catch — får inte fälla operationen).
5. Returnera `new ReportPngResult(true, url, filePath)`.

### 2. `src/TbotUltra.Desktop/SaveReportPngWindow.xaml` + `.xaml.cs`
Popup enligt `SavePageHtmlWindow`/`ProductionBonusSettingsWindow`-stil (Width≈520, `SizeToContent="Height"`, `NoResize`, `CenterOwner`, `ThemeChrome.EnableEarlyDarkTitleBar`). Innehåll:
- Steg-för-steg-text, egna rader:
  1. Navigate to the report you want to save
  2. Click "Save report" button
  3. This will generate a PNG of the report and save it in the "Reports" folder
  4. If you are on the wrong page the program will tell you so
- CheckBox **Hide attacker** (default unchecked) + förklaringstext: blurs the attacker's player name, village and alliance.
- CheckBox **Hide defender** (default unchecked) + förklaring: blurs all defenders' player names, villages and alliances.
- Knappar: **Save report** (grön: AccentBrush/vit text), **Open reports folder**, **Close**.
- Statusrad (`StatusTextBlock`) + `SetSaveInProgress(bool)`/`SetSaveResult(string)` som SavePageHtmlWindow.
- Event `SaveRequested(bool hideAttacker, bool hideDefender)` — MainWindow äger själva operationen (samma mönster som SavePageHtmlWindow).
- "Open reports folder": `Directory.CreateDirectory` + `Process.Start(... UseShellExecute = true)`, try/catch med AppDialog-varning.

### 3. `src/TbotUltra.Desktop/MainWindow.ReportPng.cs`
Ny MainWindow-partial (som `MainWindow.ProductionBonus.cs`):
- `private string ReportsPngDirectory => Path.Combine(_projectRoot, "Reports");`
- `internal void OnInboxSaveReportPngClicked()`:
  - `BlockIfSessionSleeping("Save report as PNG")` → return.
  - Bot körs-guard: `_autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted) || _loopController.HasActiveOperation` → `AppDialog.Show(this, "The bot must be paused to save a report as PNG. Stop the bot and try again.", "Save report as PNG", OK, Warning)` → return. (Användaren väljer själv om den vill pausa.)
  - `!_isLoggedIn` → AppDialog "Log in first..." → return.
  - Öppna `SaveReportPngWindow` (singleton-fält + Owner=this + Closed-avregistrering, som `_savePageHtmlWindow`).
- `SaveReportPngWindow_SaveRequested` → `GuardUiAsync` → async-metod som speglar `SavePageHtmlWindowSaveRequestedAsync`:
  - Samma guards igen (bot kan ha startats efter att popupen öppnades) — bot körs → AppDialog med paus-texten.
  - `BeginOperation("SaveReportPng")` + `Stopwatch` + CTS.
  - `Directory.CreateDirectory(ReportsPngDirectory)`; filnamn `report_{DateTime.Now:yyyyMMdd_HHmmss}.png`.
  - `await _botService.SaveReportScreenshotAsync(options, filePath, hideAttacker, hideDefender, AppendLog, token)`.
  - `result.IsReportPage == false` → `AppDialog.Show(popup, "You are not on a report page. Navigate to the report you want to save and try again.", "Save report as PNG", OK, Warning)` + `SetSaveResult("Not on a report page.")` + `FailOperation`/CompleteOperation med tydlig logg (välj `CompleteOperation` med "skipped: wrong page" — fel sida är inget programfel).
  - Lyckat → `SetSaveResult($"Saved {fileName}")` + `CompleteOperation`.
  - Catch `OperationCanceledException`/`Exception` som mallen.

## Ändrade filer

- `src/TbotUltra.Desktop/Views/InboxPanel.xaml` — i Reports-`StackPanel` (under `MarkReportsReadButton`): horisontell `StackPanel` med knappen **Save report as PNG** (`Width≈140`, `Margin="0,10,0,0"`) + `ContentControl Style="{StaticResource SettingInfoIconStyle}"` till höger med tooltip: "Saves the currently open combat report as a PNG image into the Reports folder. The bot must be paused and the report page must be open in the browser." OBS: knappen ska INTE bindas till `ActionsEnabled` (den ska vara klickbar när boten kör så att paus-popupen kan visas).
- `src/TbotUltra.Desktop/Views/InboxPanel.xaml.cs` — click-handler → `Host?.OnInboxSaveReportPngClicked()` (samma mönster som `MarkReportsReadButton_Click`).
- `src/TbotUltra.Desktop/Services/IDesktopBotService.cs` + `DesktopBotService.cs` — passthrough `SaveReportScreenshotAsync(options, filePath, hideAttacker, hideDefender, log, token)`.
- `src/TbotUltra.Worker/Services/BotTaskRunner.VillageReads.cs` (eller ny liten partial) — `SaveReportScreenshotAsync` via `ExecuteWithClientAsync(interactive: false, saveStateMode: BrowserStateSaveMode.Skip)`, som `ReadCurrentPageHtmlAsync`.
- `docs/ARCHITECTURE.md` — lägg till de nya filerna under respektive sektion.

## Beteendesammanfattning

| Situation | Resultat |
|---|---|
| Bot kör (loop/kö/operation) + klick på knappen | AppDialog: pausa boten först, OK |
| Ej inloggad | AppDialog: logga in först, OK |
| Session sleeping | Blockad via `BlockIfSessionSleeping` (loggrad) |
| Fel sida (ej `/report` + öppnad rapport) | AppDialog: navigera till rapporten, OK |
| Rätt sida | PNG av `#reportWrapper` → `<projektrot>\Reports\report_<timestamp>.png` |
| Hide attacker | Blur på attackerns allians/spelare/by + rubriken |
| Hide defender | Blur på ALLA defender-blocks allians/spelare/by + rubriken |

## Verifiering

1. Bygg när appen kör: `dotnet build src/TbotUltra.Desktop/TbotUltra.Desktop.csproj -p:OutDir=<temp>` (DLL-lås annars).
2. `dotnet test src/TbotUltra.Worker.Tests` (befintliga tester gröna).
3. Manuellt: starta appen, logga in, öppna en rapport i browsern → Messages/Reports → Save report as PNG → popup → Save report → kolla `Reports\report_*.png` (öppna via Open reports folder). Testa: fel sida (dorf1) → alarm-popup; bot igång → paus-popup; båda checkboxarna → namn/byar/allianser + rubrik blurrade, Reinforcement-raderna intakta.
