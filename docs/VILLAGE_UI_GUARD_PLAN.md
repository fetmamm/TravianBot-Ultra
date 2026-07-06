# Fix: UI uppdateras med fel bys data när boten jobbar i annan by än den valda

## Context / Analys — JA, buggen finns

Användarens observation stämmer. Arkitekturen har redan rätt mönster: **cacha alltid datat för datats by, måla bara om synligt UI om datats by == vald by i dropdownen**. Guarden finns (`IsStatusForSelectedVillage`, [MainWindow.VillageWorking.cs:891](src/TbotUltra.Desktop/MainWindow.VillageWorking.cs)) och används korrekt av 20s-ticken (`ApplyResourceStatusToUi`, Resources.Snapshot.cs:156–167), buildings-panelen (`PopulateBuildingsTab`, Buildings.cs:1023), deferred construction-refresh (DeferredRefresh.cs:439/480) och byggkö/smithy (via per-by-cachen). **Men fyra vägar skriver UI utan guard:**

| # | Väg | Fil | När den drabbar |
|---|-----|-----|-----------------|
| 1 | `ApplyStorageStatusToUi` — skriver storage-forecasts + `_lastResourceStatusForUi` utan by-koll | [Resources.Snapshot.cs:177](src/TbotUltra.Desktop/MainWindow.Resources.Snapshot.cs) | Anropas efter **byggtask-success i automationen** (QueueExecution.cs:159/165/227) och efter defer-quick-read (DeferredRefresh.cs:425) — läser bottens aktuella sida (940) → skriver storage-UI medan 740 visas. **Detta är användarens huvudfall.** |
| 2 | `LoadResourcesButtonClickAsync` — `ApplyResourceRowsAndVillageStatus(status)` direkt | [Resources.Actions.cs:37–39](src/TbotUltra.Desktop/MainWindow.Resources.Actions.cs) | Läser `forceCurrentVillage: true` (= browserns by, dvs. bottens) och målar råvarufält/status oskyddat |
| 3 | `LoadBuildingsButtonClickAsync` — samma + `ApplyStorageForecasts` | MainWindow.Buildings.cs:75–76 | Samma som #2 |
| 4 | `RefreshTroopTrainingUiAfterBuildAsync` → `RefreshTroopTrainingQueuesAsync` → `_troopTrainingViewModel.ApplyStatus(...)` | [MainWindow.TroopTraining.cs:1207/1245/1254](src/TbotUltra.Desktop/MainWindow.TroopTraining.cs) | Efter `build_troops`-task i automationen (QueueExecution.cs:187/477) — träningsköer för bottens by skrivs till troop-UI oskyddat |

Extra detalj i #1: `ApplyStorageStatusToUi` kör `MergeResourceStatusForUi` FÖRE ev. guard — den sätter `_lastResourceStatusForUi` till andra byns status (vid komplett storage-snapshot). Den variabeln är basen för alla senare UI-merges → förgiftar även guardade vägar. Guarden måste alltså in FÖRE merge.

OBS avsiktligt beteende som INTE ska röras: `ApplyCurrentVillageToUiAsync` (VillageWorking.cs:517) målar aktiva byn OCH synkar dropdownen först — det är login/Switch village-flödet, by-design.

## Ändringar (allt i `src/TbotUltra.Desktop/`, återanvänder befintliga `IsStatusForSelectedVillage`, `CacheVillageStatus`, `SetActiveWorkingVillageFromStatus`)

### 1. Guard i `ApplyStorageStatusToUi` — `MainWindow.Resources.Snapshot.cs:177`
Först i metoden (före `MergeResourceStatusForUi`):
```csharp
SetActiveWorkingVillageFromStatus(status);
CacheVillageStatus(status);
if (!IsStatusForSelectedVillage(status))
{
    AppendLog($"[storage-refresh] skipped UI update from {source}: data is for '{status.ActiveVillage}', another village is selected. Cache updated.");
    return;
}
```
Datat går alltså alltid in i per-by-cachen (dashboard-ikoner + byggkö-cache uppdateras som idag via `UpdateCachedTimerStatus` hos anroparna, som redan är by-nycklade), men synligt storage/produktion rörs bara för vald by. Samma mönster som `ApplyResourceStatusToUi` (rad 156–167).

### 2. Central guard i `ApplyResourceRowsAndVillageStatus` — `MainWindow.Resources.UiState.cs:133`
Lägg guard först i metoden:
```csharp
if (!IsStatusForSelectedVillage(status))
{
    AppendLog($"[resource-ui] skipped repaint: status is for '{status.ActiveVillage}', another village is selected.");
    return BuildResourceRows(status, includeQueuedTargets); // radlistan behövs av anropare för loggantal
}
```
Detta täcker #2 och #3 centralt och skyddar alla framtida anropare. Befintliga korrekta anropare passerar oförändrat: guardade vägar har redan kollat; `ApplyCurrentVillageToUiAsync` synkar dropdown FÖRE anropet (selected==active → guard släpper igenom); `ShowSelectedVillageFromCache` skickar cached-för-vald by.
- I `LoadResourcesButtonClickAsync` (Resources.Actions.cs:37) och `LoadBuildingsButtonClickAsync` (Buildings.cs:75): lägg även `SetActiveWorkingVillageFromStatus(status)` + `CacheVillageStatus(status)` före apply så manuell läsning aldrig kastas bort, och logga tydligt när repainten hoppar över (+ `_resourcesViewModel.ApplyStorageForecasts(status)` i Buildings.cs:76 flyttas in bakom samma guard-villkor).

### 3. Guard i troop-training-UI-skrivningarna — `MainWindow.TroopTraining.cs`
I `RefreshTroopTrainingQueuesAsync` runt de tre `_troopTrainingViewModel.ApplyStatus(...)`-ställena (1207/1245/1254): hoppa över UI-write när statusens by inte är den valda:
- 1245: `if (!IsStatusForSelectedVillage(effectiveStatus)) { logga + return/skip UI-write; }` — köerna ska fortfarande in i per-by-cachen (sker redan via `CacheVillageStatus`/`UpdateCachedTimerStatus` där det görs idag; verifiera vid implementation att cache-write ligger före guard).
- 1207/1254 (fallback-vägar med `_lastBuildingStatus`/syntetisk status): samma koll baserat på den by köerna lästes för; om byn är obestämbar → behåll dagens beteende (guarden släpper igenom null — medvetet, se nedan).

### 4. Ingen ändring av guardens null-beteende
`IsStatusForSelectedVillage` returnerar `true` när by-namn saknas ("Unknown village") — medvetet defensivt så en dålig läsning inte blankar UI:t. Behålls. (Nämns här så det inte "fixas" av misstag — en skärpning riskerar tomt UI för enbyskonton/transienta läsningar.)

## Filer som ändras
- `src/TbotUltra.Desktop/MainWindow.Resources.Snapshot.cs` — guard i `ApplyStorageStatusToUi` (före merge).
- `src/TbotUltra.Desktop/MainWindow.Resources.UiState.cs` — central guard i `ApplyResourceRowsAndVillageStatus`.
- `src/TbotUltra.Desktop/MainWindow.Resources.Actions.cs` — cache + logg i Load resources-knappen.
- `src/TbotUltra.Desktop/MainWindow.Buildings.cs` — cache + logg + guard runt `ApplyStorageForecasts` i Load buildings-knappen.
- `src/TbotUltra.Desktop/MainWindow.TroopTraining.cs` — by-koll runt `_troopTrainingViewModel.ApplyStatus`-skrivningarna.

## Verifiering
1. Bygg (appen kan köra): `dotnet build src/TbotUltra.Desktop/TbotUltra.Desktop.csproj -p:OutDir=<temp>`; `dotnet test src/TbotUltra.Worker.Tests` + `src/TbotUltra.Desktop.Tests`.
2. Manuellt (huvudfallet): starta continuous loop med by 940 aktiv, välj 740 i dropdownen. Låt boten slutföra en byggnation/träning i 940 → storage-staplar, råvarufält, produktion och troop-köer i UI ska INTE ändras till 940:s värden; loggen ska visa `skipped UI update ... data is for '940'`.
3. Växla dropdown till 940 → cachade färska 940-värden visas direkt (via `ShowSelectedVillageFromCache`).
4. Kontroll att inget regredierat: Login och Switch village målar fortfarande om hela dashboarden till aktiva byn (dropdown synkas först); Load resources/Load buildings på vald by fungerar som förut.
