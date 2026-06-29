# Engineering Notes - TbotUltra

> Las detta innan du andrar selektorer, sokvagar, konfiguration eller serverlogik.
> Filen ar styrande och ska hallas kort, aktuell och under 300 rader.

Se aven `docs/ARCHITECTURE.md` (fil- och funktionskarta), `AGENTS.md`, `CLAUDE.md`, `README.md`.
Djupa mekanismdetaljer ligger i `docs/adr/` och historiken i `docs/history/`.

## 1. Projektoversikt

| Projekt | Ansvar |
|---|---|
| `TbotUltra.Core` | Konfiguration (`BotOptions`, `ServerFlavor`), task-payloads och kataloger. Ingen browser eller UI. |
| `TbotUltra.Worker` | Spelautomation via Playwright. `TravianClient` ager serverinteraktion och `BotTaskRunner` kor tasks. |
| `TbotUltra.Desktop` | WPF-UI med `MainWindow`-partials och ViewModels. `LoadBotOptions()` laser config via `BotOptionsFactory`. |

Beroenden: `Desktop` -> `Worker` -> `Core`.

```powershell
dotnet build TbotUltra.sln
.\scripts\Run-Tests.ps1
```

## 2. Official och SS-Travi

Official ar huvudmalet. SS-Travi finns kvar som legacy-flavor for kvarvarande
paths/selektorer, men nya funktioner ska rikta Official om inget annat sags.

### ServerFlavor

1. `ServerFlavor` harleds alltid fran `BaseUrl`-host.
2. `*.ss-travi.com` ar `SsTravi`; allt annat ar `Official`.
3. Flavor far inte bindas fran config eller cachas separat.
4. Lagg inte tillbaka `[ConfigurationKeyName("server_flavor")]`.
5. Lagg inte tillbaka SS-only floden utan separat beslut.
6. Kontrollera `[flavor]`-loggen vid misstankt fel serverbeteende.

Detaljer: [ADR 2026-06-01](adr/2026-06-01-server-flavor.md).

### Sokvagar

Anvand flavor-aware helpers i `TravianClient.Selectors.cs` nar URL skiljer; anropa dem i
`GotoAsync(...)`, hardkoda inte en variants path i flodeslogiken.

```csharp
private string HeroAdventuresPath =>
    _config.IsPrivateServer ? Paths.HeroAdventures : "/hero/adventures";
```

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

- Selektorandringar ar additiva: behall SS/legacy-selektorn och lagg Official som fallback.
  Ersatt inte en fungerande SS-selektor utan verifierad anledning.
- Scope:a breda selektorer till ratt widget/dialog for att undvika falska traffar.
- Official React-sidor maste vanta pa ett synligt/handlingsbart nyckelelement; DOM-narvaro ensam
  racker inte for klick. Anvand `await WaitForPageReadyAsync(ct)` nar hela sidan maste vara laddad —
  den kastar (TimeoutException, sista felet som inner) efter uttomda retries; anropare ska defer:a/retry:a.
- Official `/messages/write` ar klassiskt form-DOM: recipient `#receiver`/`name=an`, subject `name=be`, body `textarea#message`.
- Official `map.sql`/`x_world`: player id ar kolumn 7; skicka aldrig den som namn. Player name ar kolumn 8,
  alliance name kolumn 10, population kolumn 11 (0-baserat: 7,9,10).
- Verifiera nya Official-selektorer live och gor en snabb SS-regressionskontroll.
- Official farmlist loss cleanup laser `tr.slot`, `td.target`, `td.openContextMenu` och last-raid
  klasser (`attack_lost*`, `attack_won_withLosses*`); matcha inte SVG-paths for loss state.

```js
document.querySelector('#stockBarWarehouse, .warehouse .capacity .value')
```

## 3. Konfiguration och konto-state

- `bot.json` innehaller endast verkligt globala program-/servervarden. `ServerFlavor` ar aldrig en sparad setting.
- Konto-/byspecifika val sparas i `config/accounts/<account>/settings.json`. Konto-overlay appliceras ovanpa
  global config; saknad overlay betyder defaults, aldrig ett annat kontos varden.
- Aldre konto-scopeade varden i `bot.json` migreras en gang till kontots `settings.json` och tas bort globalt.
- Reinforcement send-intervall/variation ar account-scopeat; `Queue now` skickar direkt, auto-gruppen skapar en deferred runtime-post baserat pa senaste lyckade send.
- Ko, bycache, Smithy, troop training, hero/cache och ovrig runtime-state anvander kontoavgransade paths.
- Kontobyte ar full UI/cache-reset, men respektive kontos separata ko och settings ska bevaras, och
  `bot.json`:s konto-scopeade pekare (by-/farmlist-namn/ids) rensas via `ClearPersistedAccountScopedConfig`
  sa de inte lacker till nasta konto.
- Borttagning av ett inaktivt konto far inte blockeras av det aktiva kontots ko; aktivt konto skyddas medan dess ko har arbete.
- All ko- och slotbaserad UI-harledning filtreras till vald by eller uttryckligen globala items.
- Settings-fonstret far inte skriva konto-scopeade overlay-varden tillbaka till global config.
- Config-/cache-stores skriver via `AtomicFile.WriteAllText` (temp-fil + `File.Move`); nya stores ska folja samma monster.

For en ny dashboard-bool ska hela configkedjan uppdateras:
`BotOptionPayloadKeys` -> `BotOptions` -> `BotOptionsFactory` ->
`BotOptionsPayloadApplier` -> `BotConfigStore.AccountScopedKeys` -> UI.

Detaljer: [ADR 2026-06-05](adr/2026-06-05-multi-village.md), [ADR 2026-06-06](adr/2026-06-06-dashboard-overview.md).

## 4. Nya funktioner

### Worker

- Stateless DOM-/texttolkning ska ligga i en egen parser/calculator utan I/O och ha enhetstester.
- Navigation och klick stannar i en tunn `TravianClient`-partial som delegerar parsing; gor inte
  `TravianClient` storre med ny stateless logik.
- Bevara fungerande navigations- och klickordning om beteendeforandring inte uttryckligen kravs.
- Registrera nya tasks via befintlig handler-dictionary i `BotTaskRunner`.
- Selektorer ska vara additiva och paths flavor-aware. Logga tillrackligt med kontext for framtida felsokning.
- Hero-resurstransfer ar per-by/per-konsument gated: Village settings `Hero res.`,
  `HeroResourceUse{Construction,Smithy,Brewery,TownHall}` och `HeroResourceMaxUse*` (per by; Construction default true, Smithy/Brewery/Town Hall false, max limit alltid aktiv med default 5000). Generisk build-sidlogik i
  `TryHeroResourceTransferOnCurrentBuildPageAsync`; construction/brewery anropar via tunna gated wrappers,
  town hall anropar via egen tunn wrapper, smithy har egen per-trupp-DOM (`TryHeroResourceTransferForSmithyTroopAsync`). Nybyggnation ska prova
  hero-transfer direkt pa construct-sidan innan en queue-kontroll navigerar till `dorf2`.
- Town Hall celebration ar `gid 24`, alla stammar, per-by `QueueGroup.TownHallCelebration`.
  Mode ar account-default `small`/`big` med per-by override; `big` faller tillbaka till `small` under
  Town Hall level 10. Big-start-selector ska live-verifieras forst nar en level 10 Town Hall finns.
  Small-start logic ska vara scope:ad till `.build_details` och small-celebration-raden; verifiera Official
  och SS live innan selectorandringar markeras som bekraftade.

### Desktop

- Anvand ViewModel/service-grans enligt `TroopTrainingViewModel` som mall. Lagg inte ny doman-/sekvenslogik
  i `MainWindow` om den kan agas av VM/service.
- Async-handlers ska delegera till guarded async-hjalpare (`AsyncUi.GuardAsync`) och logga ovantade fel.
  Sista skyddsnat: App.xaml.cs `DispatcherUnhandledException` satter `e.Handled=true` + loggar till disk,
  och `MainWindow.OnDispatcherUnhandledExceptionForLog` speglar felet i in-app-loggen. Lagg inte tillbaka
  ra `async void` utan tackning.
- Loop- och CTS-livscykel ska agas av `LoopController`; skapa inte nya spridda CTS-falt.
- Dashboard-checkboxar foljer befintligt suppress-flagga + load/save-monster. Ateranvand `SettingInfoIconStyle`
  for infoikoner; langre listor ligger i en begransad `ScrollViewer`, inte expandera resten av dashboarden.
  Automation-loop-kort sparar per-by gruppvarde fore wake och vacker bade Continuous Loop och vantande AutoQueue.
- Aktivitetstimers sparas som absoluta UTC-sluttider och raknas om vid cache-load; utgangna poster rensas
  som stale. `Clear timers` ar vald-by-scope, tar aldrig bort Queue-sidans poster och vacker inte loopen.
  Som manuell aterstallning rensar den ocksa vald bys construction-snapshot (`ActiveConstructions`) och
  nollstaller deferred construction-retries sa ett fastnat "waiting" utan faktiskt bygge kan brytas.
- Construction- och byggkologik (ActiveConstructions som SOT, queue-full-defer, storage-capacity, estimat):
  [ADR 2026-06-20 construction-queue](adr/2026-06-20-construction-queue.md).
- Smithy-upgrade och trupptraning (DOM, per-by-payloads, automation groups):
  [ADR 2026-06-20 smithy-troop-training](adr/2026-06-20-smithy-troop-training.md).
- Village Overview och byval visar kapitalen forst; ovriga byar behaller Travian-listans DOM/sidebar-ordning.
  Profiltabellens ordning far inte anvandas (Official kan sortera efter population); las sidebarordningen
  fore profilnavigation och anvand profilen endast for att berika bydata.
- Map Oasis Analyzer och kartparsning: [ADR 2026-06-20 map-oasis-scan](adr/2026-06-20-map-oasis-scan.md).
  Analyze map oasis ska visa en warning-confirmation fore scan eftersom flodet ar high-volume.
- Travco-tabben ar seg: `SetDefaultTimeout(30000)`. "Save all pages" kor `ScrapePageWithRetryAsync`
  (3 forsok med reload + backoff) och `ResolveTotalPagesAsync` vantar in resultattabellen fore sidantalet
  lases, sa en seg sida inte tyst kapar listan till sida 1. Se [ADR 2026-06-09](adr/2026-06-09-farmlists-and-travco.md).

### Kvalitetsregel

En ny formaga ska kunna enhetstestas till stor del utan browser. God-klasserna ska krympa eller sta still, inte vaxa.

## 5. Aktiva fallgropar

### Browser och navigation

- Village-switch ska kanoniseras till `dorf1.php?newdid={id}` utan extra `id`-parametrar och verifieras som inloggad.
- `SwitchToVillageAsync` foredrar sidebar-href:en framfor en inskickad/cachad URL nar bynamnet ar kant —
  cachade payload-URL:er kan ha fel newdid som tyst inte byter by. Verifiera mot begart namn eller stabila
  koordinater (namnbyte), vid miss: retry via sidebar/coords, annars kasta sa tasken inte kor pa fel by.
- Official T4.6 bylista ar React-renderad UTAN `dorf1.php?newdid=`-ankare. Ratt newdid finns i
  `div.listEntry.village[data-did="<id>"]` (aktiv = `.active`) med rent namn i barn-`span.name`.
  `TryGetVillageHrefFromSidebarAsync` laser `data-did` (exakt namnmatch — korta namn far inte substring-matcha)
  och bygger `/dorf1.php?newdid=<id>`. spieler.php-fallbacken kan ge fel id; foredra alltid `data-did`.
- By-identitet (`GetVillageKey`/`VillageKey`/`VillageSettingsStore`) nycklas pa KOORDINATER (`xy:X|Y`),
  inte newdid: koordinater ar stabila/unika och overlever omdop, annars splittras per-by-installningar i
  tva poster. Fallback: newdid (`did:N`), sen namn (`name:..`). `VillageSettingsStore` kanoniserar via
  koordinater (`CanonicalKey`) och migrerar/slar ihop gamla `did:`-poster vid inlasning. Koobjekt bar bara
  namn/url -> namnbaserad nyckel; `NormalizeKey`/`ResolveCanonicalKey` mappar `name:..` till `xy:..`.
- Koposter stamplar den stabila koordinatnyckeln (`target_village_key`) vid enqueue
  (`ApplySelectedVillageToPayload`/`BuildVillageRuntimePayload`); `GetQueueItemVillageKey` laser den FORST
  (fallback namn/url for gamla poster). Annars resolvas en ny by med ATERANVANT namn (forlorad + omgrundad)
  till fel by och posterna gating:as/pausas bort. Forlorade byar rensas retention-baserat: en by som ar
  bekraftat saknad (koordinatidentitet) ur login/scan-listan i `LostVillageRetention` (3 dygn) prunas och
  dess kvarvarande Pending/Paused-koposter tas bort (`ConfirmedMissingSinceUtc` ->
  `GetVillagesConfirmedMissingSince`/`RemoveVillages` + `ConfirmedVillageQueueReconciler.RemoveItemsForVillages`).
- Login ska anvanda action pacing och vanta pa full sidladdning. Login-state `unknown` under navigation ar
  normalt en transient ladd-race; captcha, `manual_step` och `logged_out` ar inte det.
- Playwright `Target crashed` ar transient: kassera shared browser-session, defer:a queue-posten kort och
  lat nasta operation skapa en frisk session. `BrowserFailureClassifier.IsTargetCrash` klassar aven
  stangd sida/kontext (`...has been closed`, `page is closed`, `Cannot navigate to closed page`) som krasch.
  Lagg ALDRIG till `Execution context was destroyed` dar — det ar en ofarlig navigerings-race som retry:as.
- Playwrights native popup-blocker ska vara aktiv i live-sessionen; botens egna extra flikar anvander
  `NewPageAsync`. Innan `StorageStateAsync` sparas ska transienta ad/consent-origins rensas ur live-contexten.
  Bonus-video ska koras i isolerad temp-browser utan native popup-blocker och aldrig ladda ad-stack i main context;
  rena background-/DOM-prober ska inte spara storage state efter lasningen.
  Travco ska oppnas i isolerad browser-context, aldrig i Travian-contexten.
- Official resource/production text kan innehalla bidi-markers och Unicode-minus (`\u2212`); DOM-number parsers
  maste strippa `\u202A-\u202E`/`\u2066-\u2069` och normalisera minus innan `Number(...)`.
- Session i `Sleeping` far inte vackas av refresh, login/logout, scan, test, bybyte eller auto-run.
  Continuous-loopens keep-alive (`MaybeKeepBrowserFreshDuringContinuousLoopAsync`) gate:ar pa `IsSessionSleeping`.
- Portable single-file-builden maste innehalla `.playwright` och satta `PLAYWRIGHT_DRIVER_PATH`.

### Byggnader och ko

Full mekanik i [ADR construction-queue](adr/2026-06-20-construction-queue.md) och
[ADR smithy-troop-training](adr/2026-06-20-smithy-troop-training.md). Styrande kortregler:

- Per-slot state fran kon maste alltid filtreras per vald by. Partiella resurslasningar utan bygg-DOM far
  inte nollstalla en cachad aktiv byggko. `load_buildings_snapshot` ar en lasning, inte ett bygge.
- `ActiveConstructions` (browserbekraftad) ar SOT for aktiv byggko; defer-poster och lokala timers ar
  harledd vantan, inte bevis. Endast en bekraftad tom dorf1/dorf2-lasning far rensa snapshoten/frigora
  en `queue_full`-blockerare (undantag: manuell `Clear timers`).
- Construction-defer som betyder `queue full`/`already queued`/`still in progress` ar ko-upptagning, inte
  resursbrist; resursrefresh far inte aterstalla vantan. Endast verklig `queue full` blockerar hela byn.
- Storage-capacity (`Extend warehouse/granary first`) ger `wait_reason=storage_capacity`;
  `StorageCapacityDependencyPlanner` uppgraderar/bygger relevant lager fore originaltasken.
- Construct/upgrade-klick: exkludera `Open shop`, verifiera ratt `build.php?id=&category=` + renderade
  `#contract_building*` fore klick, las resursbrist/krav innan ko-/progresskontroller som navigerar till
  `dorf2`, och krav success vara malspecifik (slot-level/matchande aktiv konstruktion). Saknade krav = temporar defer.
- Byggnadsuppgradering far ateranvanda aktuell `build.php?id=N` for slot snapshot endast nar sidan ar ratt slot,
  inte stale och kan lasa niva + namn/gid; annars ska flodet falla tillbaka till `dorf2`.
- Construct mot en slot som redan har byggnaden (stale construct-task, eller fast specialslot RP=39/Wall=40 som
  finns fran grundning) far INTE ALARM:a pa saknad construct-choice-DOM. `ConstructBuildingAsync` lasar slotens
  build-sida live (`TryReadExistingBuildingOnSlotBuildPageAsync`); bekraftad befintlig byggnad (level >= 1, ingen
  `#contract_building*`) -> returnera "already exists at slot" som klassas `ConstructionTaskOutcome.AlreadyExists`
  och desktop TAR BORT posten ur kon (`HandleQueueItemSucceededAsync`). Maste vara live-bekraftat fore borttagning.
- `gid 13` ar Smithy (ingen separat Armoury pa `gid 12`). Smithy/trupptraning anvander browserbekraftade
  SOT-koer (`SmithyUpgradeStatus` / traningskon) och unit-id-ikon for truppidentitet.

### Hero och React-dialoger

- Hero-attributens defaultordning ar `resources,fighting_strength,offence_bonus,defence_bonus` pa bada
  varianterna. UI-ordningen sparas konto-scopeat och anvands oforandrad vid poangtilldelning.
- Background resource-refresh far no-navigation-kolla `i.levelUp.show` och ko:a `spend_hero_attribute_points`
  nar auto-assign och Hero-gruppen ar pa; dedupe:a endast aktiv `spend_hero_attribute_points` sa deferred
  `hero_manage` for adventures inte blockerar attributpoang, och vacka en sovande Continuous Loop sa poangen
  inte vantar pa nasta intervall. Official-attributklick maste scope:a plus-knappen till exakt input-falt
  (`productionPoints` for resources).
- Hero away avgors av travel-signaler/timer fore `heroHome`. Las `.heroState .timerReact` fore oscope:ad sidtext.
  Dead/reviving Official-widget kan sakna home-link; los home village via `HeroAttributesPath` innan `[herohome]`.
  Health kan innehalla bidi-tecken; rensa dem fore numerisk parsing.
- Resource transfer-dialogen kan renderas i `#dialogContent` utan wrapper, ibland lamna inputs pa 0 och
  anvanda CSS-klassen `disabled`; fyll exakt shortfall manuellt vid behov, klicka Official icon/confirm via
  paced JS nar React-element ar instabila, och stang alltid aktivt sa kvarvarande `#dialogOverlay` inte blockerar senare klick. Efter transfer: invanta hydrerad DOM/reload.
- Noll adventures ska inte automatiskt stanga av anvandarens Hero-toggle.
- Official Add target ska fylla X och Y som separata Playwright-interaktioner. Vid Default troops ska
  koordinatfaltet blur:as och en neutral yta klickas innan flodet vantar pa aktiv Save. Stegen anvander
  konto-scopead `farm_list_step_delay_*_seconds` (default 1.5-4 s, under Action pacing/Loop).
- Add Farms-progress visar lyckade tillagg separat fran kontrollerade/saknade koordinater. Mal ar antal
  lyckade tillagg, inte forsok: invalid/duplicate forbrukar nasta kandidat tills malet nas, listan blir
  full eller kallistan tar slut. Official live-count kontrolleras fore varje forsok for max 100.

### UI, cache och loggning

- Full village-status ska spara produktionssnapshot; tom ny data far inte skriva over bra cache.
  Resource-field snapshots ar kompletta forst vid 18 kanda slots med niva `>= 0` (niva 0 giltig for ny by);
  `null`/okand typ far inte skriva over cache eller driva ko-estimat.
- After-login-analys av nya byar jamfor hela sidebar-listan mot komplett cache (dorf1 fields + dorf2
  buildings), isolerar fel per by och aterstaller vald by efteråt. En bekraftad login-/scan-bylista far
  karantansatta saknade byar (disable automation + pausa pending poster) men inte ta bort historik/cache
  eller retarget:a. Account scan pausar runnern utan att rensa kon och aterstartar endast om aterstallningen bekraftades.
- Pause bevarar sessionens aterstaende pacing; endast Stop/reset nollstaller den. `Start bot` kor alltid
  continuous loop; manuella knappfloden far fortsatt kunna koras nar loopen ar idle.
- Conservative automation defaults ska vara styrande for nya/reset-installningar: session pacing pa
  (90 min run, 45 min sleep, 40% variation, 18h daily max), action pacing pa
  (task 1-6s, page load 1-3s, loop 6-30s), farming dispatch 20m/20%.
  High-volume scans och keep-alive ska ha jitter/pacing och far inte infora zero-delay bursts.
- State-changing JS/Evaluate-klick i build/hero/celebration-floden ska foregas av `DelayBeforeClickAsync`;
  reload-grenar ska anvanda samma page-load pacing som navigation.
- Huvudfonstrets gemensamma `BusyOverlay` ska alltid doljas i operationens `finally`.
- Diagnostisk pacing loggas med `[pacing]`, men viktiga sleep/wake-handelser ska vara synliga i Clean mode.
  Cached currency pa sidor utan valuta ar verbose; avsaknad av bade live- och cachevarde ar alarm.
- Daily Quest-indikatorn kan vara stale; verifiera dialogen och refresha vid behov. Questmaster-klick
  kraver synligt och handlingsbart element, inte bara DOM-narvaro.

Full bakgrund och regressionsdetaljer: [engineering-notes-archive.md](history/engineering-notes-archive.md).

## 6. Official-status

| Omrade | Status |
|---|---|
| Login, dorf1, dorf2, upgrade | Stods |
| Profil, tribe, Plus | Stods |
| Rally point och egna trupper | Stods; Official send-troops anvander `troop[tN]` + jQuery `.val(N)` for tillgangliga antal |
| Hero adventures, inventory, attributes | Stods; verifiera React-floden live |
| Inbox, Tasks, Daily Quests | Stods; verifiera React-floden live |
| NPC trade och hero resource transfer | Stods; verifiera live |
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

Seams finns nu (`IFarmingClient`/`IBuildingClient`/`IHeroClient`/`ICombatClient`/`ISessionClient`,
implementerade direkt av `TravianClient`). Collaborator-extraktion bakom dem ar medvetet
uppskjuten: gor en doman i taget med live-smoke-test mellan stegen, inte som obevakat svep.
Detaljer: [ADR 2026-06-25](adr/2026-06-25-travianclient-seams.md).

## 9. Dokumentationsregler

- Hall denna fil under 300 rader; behall endast aktiva regler, aktuella fallgropar och lankar har.
- Nya arkitekturbeslut dokumenteras kort har och utforligt i `docs/adr/`.
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
- [Construction queue och build-status](adr/2026-06-20-construction-queue.md)
- [Smithy och trupptraning](adr/2026-06-20-smithy-troop-training.md)
- [Map Oasis och kartparsning](adr/2026-06-20-map-oasis-scan.md)
- [TravianClient-seams och refaktorstatus](adr/2026-06-25-travianclient-seams.md)
