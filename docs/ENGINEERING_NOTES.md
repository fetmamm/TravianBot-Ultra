# Engineering Notes - TbotUltra

> Las detta innan du andrar selektorer, sokvagar, konfiguration eller serverlogik.
> Filen ar styrande och ska hallas kort, aktuell och under 300 rader.

Se aven `AGENTS.md`, `CLAUDE.md`, `README.md` och `docs/REFACTOR_PLAN.md`.

## 1. Projektoversikt

| Projekt | Ansvar |
|---|---|
| `TbotUltra.Core` | Konfiguration (`BotOptions`, `ServerFlavor`), task-payloads och kataloger. Ingen browser eller UI. |
| `TbotUltra.Worker` | Spelautomation via Playwright. `TravianClient` ager serverinteraktion och `BotTaskRunner` kor tasks. |
| `TbotUltra.Desktop` | WPF-UI med `MainWindow`-partials och ViewModels. `LoadBotOptions()` laser config via `BotOptionsFactory`. |

Beroenden: `Desktop` -> `Worker` -> `Core`.

Grundkommandon:

```powershell
dotnet build TbotUltra.sln
dotnet test
```

## 2. Official och SS-Travi

Bada servervarianterna stods i samma kodbas. Official-stod ska laggas som ett lager ovanpa
befintligt SS-stod, inte som fork eller separat adapterarkitektur.

### ServerFlavor

1. `ServerFlavor` harleds alltid fran `BaseUrl`-host.
2. `*.ss-travi.com` ar `SsTravi`; allt annat ar `Official`.
3. Flavor far inte bindas fran config eller cachas separat.
4. Lagg inte tillbaka `[ConfigurationKeyName("server_flavor")]`.
5. Privatserverfunktioner, exempelvis Natar-floden, gate:as med `_config.IsPrivateServer`.
6. Kontrollera `[flavor]`-loggen vid misstankt fel serverbeteende.

Detaljer: [ADR 2026-06-01](adr/2026-06-01-server-flavor.md).

### Sokvagar

Anvand flavor-aware helpers i `TravianClient.Selectors.cs` nar URL skiljer:

```csharp
private string HeroAdventuresPath =>
    _config.IsPrivateServer ? Paths.HeroAdventures : "/hero/adventures";
```

Anropa helpern i `GotoAsync(...)`; hardkoda inte en variants path i flodeslogiken.

| Sida | Official | SS/legacy |
|---|---|---|
| Hero adventures | `/hero/adventures` | `/hero_adventure.php` |
| Hero inventory | `/hero/inventory` | `/hero_inventory.php` |
| Player profile | `/profile/{id}` | `/spieler.php` |
| Messages | `/messages` | `/nachrichten.php` |
| Reports | `/report` | `/berichte.php` |
| Statistics | `/statistics` | `/statistiken.php` |
| Village overview | `/village/statistics` | `/dorf3.php` |
| Rally point tabs | `gid=16&tt=N` | `t=N` |

### Selektorer och React

- Selektorandringar ar additiva: SS/legacy-selektorn behalls och Official laggs till som fallback.
- Ersatt inte en fungerande SS-selektor utan verifierad anledning.
- Scope:a breda selektorer till ratt widget/dialog for att undvika falska traffar.
- Official React-sidor maste vanta pa ett synligt eller handlingsbart nyckelelement.
- DOM-narvaro ensam ar inte tillracklig for klick pa React-sidor.
- Verifiera nya Official-selektorer live och gor en snabb SS-regressionskontroll.
- Anvand `await WaitForPageReadyAsync(cancellationToken)` nar hela sidan maste vara laddad.

Exempel:

```js
document.querySelector(
  '#stockBarWarehouse, .warehouse .capacity .value'
)
```

## 3. Konfiguration och konto-state

- `bot.json` ar global fallback.
- Konto-/byspecifika val sparas i `config/accounts/<account>/settings.json`.
- Konto-overlay appliceras ovanpa global config.
- Kontobyte ar full UI/cache-reset, men respektive kontos separata ko och settings ska bevaras.
- All ko- och slotbaserad UI-harledning ska filtreras till vald by eller uttryckligen globala items.
- Settings-fonstret far inte skriva konto-scopeade overlay-varden tillbaka till global config.
- `ServerFlavor` ar aldrig en sparad setting.

For en ny dashboard-bool ska hela configkedjan uppdateras:
`BotOptionPayloadKeys` -> `BotOptions` -> `BotOptionsFactory` ->
`BotOptionsPayloadApplier` -> `BotConfigStore.AccountScopedKeys` -> UI.

## 4. Nya funktioner

### Worker

- Stateless DOM-/texttolkning ska ligga i en egen parser/calculator utan I/O och ha enhetstester.
- Navigation och klick stannar i en tunn `TravianClient`-partial som delegerar parsing.
- Gor inte `TravianClient` storre med ny stateless logik.
- Bevara fungerande navigations- och klickordning om beteendeforandring inte uttryckligen kravs.
- Registrera nya tasks via befintlig handler-dictionary i `BotTaskRunner`.
- Selektorer ska vara additiva och paths flavor-aware.
- Nya funktioner ska logga tillrackligt med kontext for framtida felsokning.

### Desktop

- Anvand ViewModel/service-grans enligt `TroopTrainingViewModel` som mall.
- Lagg inte ny doman- eller sekvenslogik i `MainWindow` om den kan agas av VM/service.
- Async-handlers ska delegera till befintlig guarded async-hjalpare och logga ovantade fel.
- Loop- och CTS-livscykel ska agas av `LoopController`; skapa inte nya spridda CTS-falt.
- Dashboard-checkboxar foljer befintligt suppress-flagga + load/save-monster.
- Dashboard `Clear timers` ar vald-by-scope: gor uppskjutna tasks korbara nu och rensar
  volatila Construction/Smithy/Troops/Hero-timers, men tar aldrig bort Queue-sidans poster.
- En `queue_full`-defer blockerar all senare construction i samma by tills den tidigaste
  aktiva byggnaden ar fardig. Plus ger tva platser, annars en; inga romar-specialfall har.
- Queue-full ska loggas med bynamn samt exakt nasta retry i servertid och sekunder kvar.
- Djup queue-full-diagnostik anvander `[construction-queue:verbose]` och doljs i Clean-laget.
- Dashboardens byggikoner anvander live `ActiveConstructions` som auktoritativt antal.
  Queue-full-poster ar bara boolesk occupancy-fallback och far aldrig summeras som byggnader.
- Queue-sidans `Build time`/`Cost`-kolumner och totalsumman ar best-effort-estimat ur
  `buildings_catalog.json` (1x), skalat med serverhastigheten fran `ResolveServerSpeed()`
  (regex `(\d+)x` ur servernamnet; fallback 1x + engangs-`ALARM:`). Endast construction-tasks
  estimeras (`EstimateForQueueItem`); ovriga lamnas blanka. Saknad nivadata/okand byggnad ger blank
  + engangslarm, aldrig blockering. Kosidan visar numera endast `Cost` (Build time-kolumn/total borttagna);
  byggtid visas bara i slot-popupen och `Upgrade to...`-fonstret.
  Nuvarande niva for fleruppgraderingar finns bara for den laddade byn; annars estimeras endast malnivan.
- Byggtiden skalas ocksa med huvudbyggnadens rabatt `0.964^(MB-1)` i `BuildSecondsFor`
  (`mainBuildingLevel`). MB lases byspecifikt fran den laddade byns slots (`ResolveMainBuildingLevel`,
  gid 15); okand MB -> default niva 1 (ingen rabatt). Nar byns byggnader skannas anropar
  `PopulateBuildingsTab` en `RequestQueueUiRefresh` (skyddad av `_isRefreshingQueueUi` mot rekursion)
  sa redan kolagda poster rakas om med ratt MB.
- `ResolveServerSpeed` provar forst servernamnet (`(\d+)x`, t.ex. "10x") och faller annars tillbaka
  pa server-URL:ens hastighetssubdoman (`\.x(\d+)`, t.ex. `ts100.x10...`). Misslyckas bada -> 1x + engangs-`ALARM:`.
- Ateranvand `SettingInfoIconStyle` for forklarande infoikoner.
- Langre listor ska ligga i en begransad `ScrollViewer`, inte expandera resten av dashboarden.
- Map Oasis Analyzer anvander den inloggade Official-sessionens `POST /api/v1/map/position`
  med zoom level 3. Skanningen serialiseras genom `BotTaskRunner`, har retry/pacing och parsern
  ska forbli browserfri och enhetstestbar.
- Skanningscheckpoint och senast kompletta resultat lagras konto-/serverspecifikt under
  `config/accounts/<account>/cache/map-oasis/`; checkpoint ateranvands endast med samma filter.
- Oaslistor ateranvander kontoavgransade `travco_lists.json`; oasfalt ar valfria sa aldre
  Travco-listor och Official-importens koordinatflode forblir kompatibla.
- Kartparsern tar endast `did == -1` med titel `{k.fo}` eller `{k.bt}` och tolkar bonusarna
  `{a:r1}`-`{a:r4}` i tile-texten. `{k.bt}`/`uid` betyder occupied; okanda kombinationer ignoreras.
- Koordinater las via `position.x/y` (kan ocksa ligga top-level eller som strangar). Tile-texten har
  Unicode bidi-tecken (U+202D/U+202C) som maste strippas (kategori `Format`) fore regex.
- Lediga oaser har djur (`{k.animals}`, enheter `u31`-`u40`); ockuperade har `uid`/`aid` och
  `{k.spieler}`/`{k.allianz}`/`{k.volk}` men inga djur. Bada falten ar valfria i sparade listor.
- Official `map.sql` innehaller endast byar och far inte anvandas som oaskalla.
- Travco-tabben ar seg: den satter `SetDefaultTimeout(30000)` (kontextens default ar 15s). "Save all
  pages" kor `ScrapePageWithRetryAsync` (3 forsok med reload + backoff per sida) och `ResolveTotalPagesAsync`
  vantar in resultattabellen fore sidantalet lases, sa en seg sida inte tyst kapar listan till sida 1.

### Kvalitetsregel

En ny formaga ska kunna enhetstestas till stor del utan browser. God-klasserna ska krympa
eller sta still, inte vaxa.

## 5. Aktiva fallgropar

### Browser och navigation

- Village-switch ska kanoniseras till `dorf1.php?newdid={id}` utan extra `id`-parametrar och verifieras som inloggad.
- `SwitchToVillageAsync` foredrar sidebar-href:en framfor en inskickad/cachad URL nar bynamnet ar kant —
  cachade payload-URL:er kan ha fel newdid som tyst inte byter by. Verifiera mot det *begarda* bynamnet
  (inte bara "andrades nagot"); vid miss: retry via sidebar, annars kasta sa tasken inte kor pa fel by.
- Official T4.6 bylista ar React-renderad UTAN `dorf1.php?newdid=`-ankare. Ratt newdid finns i
  `div.listEntry.village[data-did="<id>"]` (aktiv = `.active`) med rent namn i barn-`span.name`. `TryGetVillageHrefFromSidebarAsync`
  laser `data-did` (exakt namnmatch — korta namn som "BI"/"PI" far inte substring-matcha) och bygger `/dorf1.php?newdid=<id>`.
  spieler.php-fallbacken kan ge fel id; foredra alltid `data-did`.
- By-identitet (`GetVillageKey`/`VillageKey`/`VillageSettingsStore`) nycklas pa KOORDINATER (`xy:X|Y`), inte newdid.
  Koordinater ar stabila och unika per by och overlever omdop; samma by kan annars ses under flera newdid
  (t.ex. spieler.php-fallbacken) och da splittras dess per-by-installningar i tva poster (dashboard och
  village settings visar olika). Fallback: newdid (`did:N`), sen namn (`name:..`). `VillageSettingsStore`
  kanoniserar varje post via koordinater (`CanonicalKey`) och migrerar/slar ihop gamla `did:`-poster vid
  inlasning (behaller posten med uttryckligt grupp-val, annars senast sedd). Koobjekt bar bara namn/url, sa
  deras nyckel ar namnbaserad och `NormalizeKey`/`ResolveCanonicalKey` mappar `name:..` till `xy:..`.
- Login ska anvanda action pacing och vanta pa full sidladdning.
- Login-state `unknown` under navigation ar normalt en transient ladd-race; captcha, `manual_step` och `logged_out` ar inte det.
- Session i `Sleeping` far inte vackas av refresh, login/logout, scan, test, bybyte eller auto-run.
- Portable single-file-builden maste innehalla `.playwright` och satta `PLAYWRIGHT_DRIVER_PATH`.

### Byggnader och ko

- Per-slot state fran kon maste alltid filtreras per vald by.
- Partiella current-page-resurslasningar utan bygg-DOM far inte nollstalla en cachad aktiv byggko.
- Construction-defer som betyder `queue full`, `already queued` eller `still in progress` ar ko-upptagning,
  inte resursbrist; resursrefresh far inte aterstalla den vantan. Endast verklig `queue full`/`blocked by
  queue` far blockera hela byn. `already queued/still in progress` blockerar bara samma uppgift, sa senare
  bygguppgifter kan anvanda en ledig Plus-slot. Queue-full retry synkas mot byns levande byggstatus.
  Aldre defer-poster utan aktuell klassificeringsversion ska valideras om av Worker, men hogst en per by
  nar en tidigare post redan har bekraftat full ko; annars orsakas en `dorf2`-reload per gammal post.
- `load_buildings_snapshot` ar en lasning och far inte blockeras som ett bygge.
- Construction-ko ska loggas per tillstandsandring och by, inte per blockerad ko-post. Behall klassificering,
  vald retry och betydande timersynk; lyckad intern persistens och varje enskild blockerad kandidat ar brus.
- `BuildQueueIdentityFingerprint` far inte innehalla tickande countdown-text.
- Resource upgrade-all ska returnera `queue_wait_seconds` direkt vid resursbrist.
- Exkludera payment-knappen `Open shop` fran upgrade-/construct-kandidater.
- Official tom slot identifieras via `#contract_building*` utan `Upgrade to level N`; anvand inte enbart `.upgradeButtonsContainer`.
- Vid misslyckat construct-klick ska resursbrist och krav lasas innan ko-/progresskontroller som navigerar till `dorf2`.
- Construct ska verifiera ratt `build.php?id=<slot>&category=<n>` och renderade `#contract_building*` fore klick.
- Saknade byggkrav ar temporar defer, inte permanent failure.
- `gid 13` ar Smithy; det finns ingen separat Armoury pa `gid 12`.
- Smithy troop-upgrade: Official-knappen ar `button[value="Improve"]` med `onclick ... action=research&t=tN`
  (SS/legacy: "Upgrade"); matcha pa `action=research`/text, aldrig bara knapptext. Identifiera trupp via
  `img.unit.uNN` eller `t=tN`, inte radordning (oresearchade trupper saknar rad). Pagaende research lases
  ENBART fran `table.under_progress .timer`; radens `.inlineIcon.duration` ar byggtid, inte progress.
  "Research is already being conducted." = ko upptagen; "Exchange resources"/"Enough resources on" = resursbrist.
  Valda trupper + malnivaer sparas konto-scopeat i `smithy_upgrade.json` och skickas som task-payload
  `smithy_upgrade_targets="u21=20;..."`; tom payload = no-op. Stateless tolkning i `SmithyPageParser` (Core, enhetstestad).
  Den kontinuerliga loopen injicerar payloaden per by (`MainWindow.ContinuousLoop`) — utan den blir loop-tasken no-op.
  Vanta in actionable sida med `WaitForPageReadyAsync` fore radlasning. Resursbrist: om `hero_resource_transfer_enabled`
  och Official klickas truppens egen `.inlineIcon.resource.transfer` och "Transfer selected" bekraftas endast nar Travian
  aktiverar den (hero racker); ett forsok per trupp/korning. Maste smithyn sjalv byggas och byggkon ar full -> defer pa
  `UpgradeBuildingToMaxAsync`-resultatets `queue_wait_seconds` (ingen idé att forsoka nar byggkon ar full). Plus: tva
  forbattringar kan ko:as om raden fortsatt visar Improve efter forsta klicket.
  Worker emitterar `[smithy-queue] timers_seconds=...` (verkliga `under_progress`-timers) varje gang den
  ar pa Smithyn; desktop cachar dem per arbetande by (`_smithyQueueByName`), tander de tva Smithy-ikonerna
  i village overview och driver Smithy-kortets timer fran den verkliga kon — INTE defer-vantan (gamla
  buggen visade ~9min defern istallet for 2h/4h-kon).
- Village settings-popupens "Automation groups"-kolumn speglar dashboardens loop-kort per by och sparas i
  samma `VillageSettingsStore.EnabledGroups`; avbockad grupp slutar koras for byn (`IsGroupEnabledForVillage`).

### Hero och React-dialoger

- Hero away avgors av travel-signaler/timer fore `heroHome`.
- Las `.heroState .timerReact` fore oscope:ad sidtext.
- Health kan innehalla bidi-tecken; rensa dem fore numerisk parsing.
- Resource transfer-dialogen kan renderas i `#dialogContent` utan wrapper.
- En transfer-dialog maste alltid stangas aktivt; kvarvarande `#dialogOverlay` blockerar senare klick.
- Efter transfer ska samma build-slot invanta hydrerad DOM och vid behov laddas om en gang.
- Noll adventures ska inte automatiskt stanga av anvandarens Hero-toggle.
- Official Add target ska fylla X och Y som separata Playwright-interaktioner. Vid Default troops
  ska koordinatfältet blur:as och en neutral yta i dialogen klickas innan flodet vantar pa aktiv Save.
- Official Add target anvander konto-scopead `farm_list_step_delay_*_seconds` mellan interna steg;
  standard ar 0,1-0,3 sekunder och installningen ligger under Action pacing/Loop.
- Add Farms-progress ska visa lyckade tillagg separat fran kontrollerade och saknade koordinater.
  Saknad by far inte rakna upp Added; Official live-count kontrolleras fore varje forsok for max 100.
- Add Farms mal ar antal lyckade tillagg, inte antal forsok. Invalid/duplicate ska forbruka nasta
  kandidat tills malet nas, listan blir full eller kallistan tar slut. Invalid kan rensas efter bekraftelse.

### UI, cache och loggning

- Full village-status ska spara produktionssnapshot; tom ny data far inte skriva over bra cache.
- Pause bevarar sessionens aterstaende pacing; endast Stop/reset nollstaller den.
- Continuous toggle kan vara pa medan runner ar pausad; knappen ska da visa `Start bot`.
- Huvudfonstrets gemensamma `BusyOverlay` ska alltid doljas i operationens `finally`, aven efter lyckad korning eller cancel.
- Diagnostisk pacing loggas med `[pacing]`, men viktiga sleep/wake-handelser ska vara synliga i Clean mode.
- Cached currency pa sidor utan valuta ar verbose; avsaknad av bade live- och cachevarde ar alarm.
- Daily Quest-indikatorn kan vara stale; verifiera dialogen och refresha vid behov.
- Questmaster-klick kräver synligt och handlingsbart element, inte bara DOM-närvaro.

Full bakgrund och regressionsdetaljer:
[engineering-notes-archive.md](history/engineering-notes-archive.md).

## 6. Official-status

| Omrade | Status |
|---|---|
| Login, dorf1, dorf2, upgrade | Stods |
| Profil, tribe, Plus | Stods |
| Rally point och egna trupper | Stods |
| Hero adventures, inventory, attributes | Stods; verifiera React-floden live |
| Inbox, Tasks, Daily Quests | Stods; verifiera React-floden live |
| NPC trade och hero resource transfer | Stods; verifiera live |
| Natar | Endast SS-Travi |
| Auctions | Kraver live-testning |
| Farm lists | Official kraver Gold Club |

## 7. Recept for Official-stod

1. Spara renderad HTML via appens `Save Page HTML` i korrekt tillstand.
2. Jamfor Official-markup mot SS och nuvarande selektorer.
3. Lagg till Official som fallback och anvand flavor-aware path vid behov.
4. Isolera stateless parsing och lagg till fokuserade tester.
5. Kor build och relevanta tester.
6. Verifiera live pa Official och gor en snabb SS-regressionskontroll.
7. Uppdatera denna fil endast med fortsatt styrande regler; lagg detaljer i ADR/historik.

## 8. Malarkitektur

Ingen omskrivning och inget nytt ramverk. Refaktorera stegvis och beteendebevarande:

1. Fortsatt centralisera threading och CTS i `LoopController`.
2. Anvand guarded async-monstret for UI-handlers.
3. Flytta residual code-behind till tematiska partials/services.
4. Extrahera stateless parsers ur `TravianClient` med tester.
5. Infor ViewModel-granser panel for panel.

Lamna `TravianClient`-partialernas fungerande navigations-/sekvenslogik orord vid ren refaktorering.
Detaljer och matningar finns i `docs/REFACTOR_PLAN.md`.

## 9. Dokumentationsregler

- Hall denna fil under 300 rader.
- Behall endast aktiva regler, aktuella fallgropar och lankar har.
- Nya arkitekturbeslut dokumenteras kort har och utförligt i `docs/adr/`.
- Ren andringshistorik laggs i `docs/history/engineering-notes-archive.md`.
- Uppdatera `README.md` nar en anvandar- eller utvecklarrelevant funktion forandras.

## Arkiverad historik

Aldre beslut och detaljerad historik finns i:

- [ADR-katalogen](adr/)
- [Fullt Engineering Notes-arkiv](history/engineering-notes-archive.md)
- [Server flavor och Official/SS](adr/2026-06-01-server-flavor.md)
- [UI theme](adr/2026-06-03-ui-theme.md)
- [Multi-village och konto-state](adr/2026-06-05-multi-village.md)
- [Dashboard overview](adr/2026-06-06-dashboard-overview.md)
- [Shutdown och cleanup](adr/2026-06-08-shutdown-cleanup.md)
- [Farmlists och Travco](adr/2026-06-09-farmlists-and-travco.md)
