# Engineering Notes - TbotUltra

> Las detta innan du andrar selektorer, sokvagar, konfiguration eller serverlogik.
> Filen ar styrande och ska hallas kort, aktuell och mellan 200-300 rader.

Se aven `docs/ARCHITECTURE.md` (fil- och funktionskarta), `AGENTS.md`, `CLAUDE.md`, `README.md`.
Djupa mekanismdetaljer ligger i `docs/adr/` och historiken i `docs/history/`.

## 1. Projektoversikt

| Projekt | Ansvar |
|---|---|
| `TbotUltra.Core` | Konfiguration (`BotOptions`), task-payloads och kataloger. Ingen browser eller UI. |
| `TbotUltra.Worker` | Spelautomation via Playwright. `TravianClient` ager serverinteraktion och `BotTaskRunner` kor tasks. |
| `TbotUltra.Desktop` | WPF-UI med `MainWindow`-partials och ViewModels. `LoadBotOptions()` laser config via `BotOptionsFactory`. |

Beroenden: `Desktop` -> `Worker` -> `Core`.

```powershell
.\scripts\Build-Check.ps1
.\scripts\Run-Tests.ps1
```

Lokala verifieringskommandon ska anvanda isolerad output i `temp_build_out/` sa de kan koras medan
Desktop-appen ar oppen. Kor inte raw `dotnet build TbotUltra.sln` for Codex/local verify nar appen kan vara igang.
`Directory.Build.props` styr vanlig `bin`/`obj`-output till `temp_build_out/dotnet/`. Build- och testskripten
ateranvander normalt sina `latest`-mappar; anvand `scripts/Clean-tbot-temp.ps1` for central rensning.
Rensningsskriptet ska alltid bevara `temp_build_out/DOM/`, som innehaller manuellt sparade sidunderlag.

## 2. Official-only

Official Travian Legends ar enda malet framåt. Lagg inte tillbaka alternativa
servervarianter, runtime-switchar for servertyp eller selectorfallbacks for andra servertyper.

Servervariant far inte sparas i config, anvandas for runtime-beteende eller laggas tillbaka i `BotOptions`.

### Sokvagar

Runtime-path helpers i `TravianClient.Selectors.cs` ar Official-only. Anropa helpers i
`GotoAsync(...)` och hardkoda inte duplicerade URL:er i flodeslogiken.

| Sida | Official path |
|---|---|
| Hero adventures | `/hero/adventures` |
| Hero inventory | `/hero/inventory` |
| Hero attributes | `/hero/attributes` |
| Messages | `/messages` |
| Write message | `/messages/write` |
| Reports | `/report` |
| Rally point tabs | `build.php?id=39&gid=16&tt=N` |

### Selektorer och React

- Selektorandringar ska vara Official-scope:ade och live-verifieras nar de ror React-sidor.
  Lagg inte till legacy-fallback utan separat beslut.
- Scope:a breda selektorer till ratt widget/dialog for att undvika falska traffar.
- Official React-sidor maste vanta pa ett synligt/handlingsbart nyckelelement; DOM-narvaro ensam
  racker inte for klick. Anvand `await WaitForPageReadyAsync(ct)` nar hela sidan maste vara laddad —
  den kastar (TimeoutException, sista felet som inner) efter uttomda retries; anropare ska defer:a/retry:a.
- Official `/messages/write` ar klassiskt form-DOM: recipient `#receiver`/`name=an`, subject `name=be`, body `textarea#message`.
- Official automation kraver normalt Travian UI-language `en-US`; gate:a direkt efter login-confirm pa `html[lang]`/`body[data-language]`/`Travian.Game.language` fore feature-signals, lokaliserad DOM-parsing eller tasks kors. Settings > General `Automatically check language` ar default true och ar enda avstangningsbrytaren.
- Auto-language via `/options` ska ocksa satta `hideContextualHelp=true` och `option_night_mode=false` fore language-select + save; varje state-andrande steg ska ha click action pacing.
- Report PNG-capture ar Official `/report*` + oppnad rapport `#reportWrapper .role.attacker`; blur scope:as till
  `.role.attacker/.role.defender .troopHeadline` och `.header .subject`, aldrig rapportlistan.
- Bulk messages far aldrig skriva till systemspelarna `Multihunter`, `Natar` eller `Natars`; filtrera bade vid analys och direkt fore send.
- Bulk messages ska hantera Official-dialogen `The name X does not exist.` genom att klicka OK, rensa recipient-faltet, ta bort X ur aktuell batch och forsoka igen utan att cacha X som skickad.
- Bulk messages UI satter `Max recipients` fran senaste `map.sql`-analysens spelarantal; fallback/default ar 5000.
- Continuous farming ar per-by: `send_farmlists` runtime-items ska stampas med byn dar Farming-gruppen ar
  enabled och workern ska byta till den byn innan farmlistan skickas. Village-less legacy farming-items ska
  inte valjas av loop-pickern.
- Official `map.sql`/`x_world`: player id ar kolumn 7; skicka aldrig den som namn. Player name ar kolumn 8,
  alliance name kolumn 10, population kolumn 11 (0-baserat: 7,9,10).
- Verifiera nya Official-selektorer live.
- Official farmlist loss cleanup laser `tr.slot`, `td.target`, `td.openContextMenu` och last-raid
  klasser (`attack_lost*`, `attack_won_withLosses*`); matcha inte SVG-paths for loss state.

```js
document.querySelector('.warehouse .capacity .value')
```

## 3. Konfiguration och konto-state

- `bot.json` innehaller endast verkligt globala program-/servervarden. Servervariant ar ingen sparad setting.
- Konto-/byspecifika val sparas i `config/accounts/<account>/settings.json`. Konto-overlay appliceras ovanpa
  global config; saknad overlay betyder defaults, aldrig ett annat kontos varden.
- Aldre konto-scopeade varden i `bot.json` migreras en gang till kontots `settings.json` och tas bort globalt.
- Reinforcement send-intervall/variation ar account-scopeat; `Queue now` skickar direkt, auto-gruppen skapar en deferred runtime-post baserat pa senaste lyckade send.
- Ko, bycache, Smithy, troop training, hero/cache och ovrig runtime-state anvander kontoavgransade paths.
- `build_troops`-queueitems ska alltid snapshotta vald bys `TroopTrainingPayload`; annars faller workern
  tillbaka till global/default troop-training config (t.ex. 50% resources) i stallet for konto+by-overriden.
- Build troops `timed` ar per-by/per-byggnad: efter lyckad training defer:as samma queue item med
  slumpad `timed_min_minutes`-`timed_max_minutes` delay. Default ar 30-180 min.
- `build_troops` sparar sina ko-avlasningar (scan + efter submit) i `TravianSessionCache.TroopQueueSnapshot*`
  per by/byggnadstyp. `ReadTroopTrainingQueuesAsync` ateranvander snapshoten (max 90s, samma aktiva by)
  sa post-build UI-refreshen inte navigerar om till dorf2 + barracks/stable/workshop som tasken nyss besokte.
  Post-build-refreshen kor ocksa `refreshBuildingsBeforeRead: false` — trupptraning andrar inte byggnadslistan.
- Kontobyte ar full UI/cache-reset, men respektive kontos separata ko och settings ska bevaras, och
  `bot.json`:s konto-scopeade pekare (by-/farmlist-namn/ids) rensas via `ClearPersistedAccountScopedConfig`
  sa de inte lacker till nasta konto. Rensningen kors FORST i `ResetForAccountSwitchAsync` (fore
  logout/shutdown) sa en krasch mitt i bytet inte lamnar kvar pekarna. Bakgrunds-ticken (20s) bail:ar
  pa `_accountSwitchInProgress` sa den aldrig loggar in gamla kontot under bytet.
- Borttagning av ett inaktivt konto far inte blockeras av det aktiva kontots ko; aktivt konto skyddas medan dess ko har arbete.
- All ko- och slotbaserad UI-harledning filtreras till vald by eller uttryckligen globala items.
- Settings-fonstret far inte skriva konto-scopeade overlay-varden tillbaka till global config.
- Quick re-login (Settings > General, `post_login_quick_relogin_enabled`, per konto): login <10 min efter
  senast SLUTFORDA fulla post-login-stacken (`post_login_last_full_login_at`, skrivs fore CompleteOperation)
  hoppar over snapshot+analyzes och bekraftar bara sessionen + laddar persisterade cacher. Kontobyte
  paverkas inte (timestampen ar account-scoped).
- Config-/cache-stores skriver via `AtomicFile.WriteAllText` (temp-fil + `File.Move`); nya stores ska folja samma monster.
- All fil-IO under OneDrive-synkade Documents ska retry:a bade `IOException` och `UnauthorizedAccessException`
  (transient ERROR_ACCESS_DENIED fran OneDrive/antivirus). Finns i `AtomicFile.RetryFileIo`,
  `JsonQueueStore.RetryFileIo` och `BrowserSession.ReplaceStorageStateWithRetryAsync`.
- Korrupt `queue.json` kastas inte langre for evigt: `JsonQueueStore.LoadMutable` karantaniserar filen
  (`queue.json.corrupt-<stamp>`), loggar och fortsatter med tom ko.
- Post-defer construction-refresh (`RefreshConstructionStatusAfterDeferAsync`) laser byggko+storage fran
  AKTUELL sida (tasken har precis reload:at dorf2) och merge:ar in i village-cachen — ingen dorf1+dorf2-runda.
  Full lasning (`RefreshConstructionStatusAsync`) ar bara fallback vid fel.
- Anvand aldrig `CancellationToken.None` for operationer som tar worker-session-gaten (post-task/manuella
  refreshes): ta token fran metodens parameter eller `LoopController.AcquireSessionScopeToken()` (cancellas
  av stop/kontobyte, ater-armas lazily for nasta operation).
- Tidsintervall styrs med min/max-minuter (inte bas+variation%): farm-dispatch
  (`continuous_farm_dispatch_delay_min/max_minutes`, default 30-90), reinforcements-send
  (`reinforcements_send_min/max_minutes`, default 60-120), session pacing run/sleep
  (`session_pacing_run_min/max_minutes` 40-100, `session_pacing_sleep_min/max_minutes` 20-60).
  Gamla nycklar (`*_variation_percent`, `session_pacing_max_run_minutes`, `reinforcements_send_interval_hours`)
  ar borttagna och ignoreras; Daily max behaller sin egen variation. Schema-granser ar exakta hela timmar.
- Session pacing run/sleep-timern far bara startas av aktiv automation (`Start bot`). Login kan fortfarande
  ga direkt till planned sleep vid schema/daily-limit; open browser och manuella funktioner som bulk messages
  far inte starta pacing/sleep-timern.
- Automatisk session pacing sleep far inte avbryta en aktiv manuell operation; den ska skjutas upp tills
  operationen ar klar. Detta galler bl.a. bulk messages, Travco, map oasis scan och Add farms to list.
- Defer-orsaker ska konsumeras typat: `TaskWaitException.ReasonCode` (`TaskWaitReasons.*`), harledd pa ETT
  stalle (`BotTaskRunner.TaskHandlers.DeriveTaskWaitReason`). Sniffa inte `ex.Message` i Desktop for nya
  fall — lagg till en reason-kod i stallet. Farm-send-deferrals (cooldown/not ready/renamed) kastar
  `TaskWaitException` direkt via `BuildContinuousFarmDefer` (loggas DEFERRED, aldrig FAILED, ingen retry-burn);
  meddelandet behaller `queue_wait_seconds`/`continuous_farm_next_list_index`-tokens som Desktop laser.
  `troops_blocked=<key>`-tokens ar avsiktligt maskinprotokoll och far inte omformuleras.
- Items som recovras fran Running vid start defereras ~120s (`JsonQueueStore.RecoveredRunningItemDefer`):
  kraschen kan ha skett efter browser-aktionen men fore defer-persist, sa direkt re-run riskerar dubbelkorning.
- Interaktiva vantloopar (captcha/manuell login) ar tidsbegransade av `ManualInteractiveWaitMaxDuration`
  (30 min) — de haller session-gaten och far aldrig vara obegransade. `BotTaskRunner.ShutdownAsync` vantar
  max 15s pa gaten och force-stanger sedan browsern (fast operation far target-closed och slapper gaten sjalv).

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
- Selektorer ska vara Official-scope:ade och path helpers Official-only. Logga tillrackligt med kontext for framtida felsokning.
- Hero-resurstransfer ar per-by/per-konsument gated: Village settings `Hero res.`,
  `HeroResourceUse{Construction,Smithy,Brewery,TownHall}` och `HeroResourceMaxUse*` (per by; Construction default true, Smithy/Brewery/Town Hall false, max limit alltid aktiv med default 5000). Generisk build-sidlogik i
  `TryHeroResourceTransferOnCurrentBuildPageAsync`; construction/brewery anropar via tunna gated wrappers,
  town hall anropar via egen tunn wrapper, smithy har egen per-trupp-DOM (`TryHeroResourceTransferForSmithyTroopAsync`). Nybyggnation ska prova
  hero-transfer direkt pa construct-sidan innan en queue-kontroll navigerar till `dorf2`.
  Tomt hero-inventory: Official oppnar INGEN dialog utan visar en 5s rod toast
  (`.toast.toastError .text` = "There are no resources to transfer from the Hero Inventory.").
  Dialog-vantan race:ar dialog mot toasten, cachar tomt inventory och skippar — ingen full timeout.
- Town Hall celebration ar `gid 24`, alla stammar, per-by `QueueGroup.TownHallCelebration`.
  Mode ar account-default `small`/`big` med per-by override; UI visar `big` som "Great" enligt Travian.
  `big` faller tillbaka till `small` under Town Hall level 10. Start-/resource-scope ska ligga i
  `.build_details` och matcha small- eller Great-celebration-raden.
- +15% production bonus-video aterforsok styrs av daglig server-reset (server-lokal hel timme) + anvandarens
  delay, inte 24h efter aktivering. Disabled purple video-knapp betyder vanta till nasta reset.
- Reset-timmen lases fran Daily Quests-dialogen: raden `(Next reset at HH:MM ...)` (`DailyResetDomParser`).
  `read_daily_reset`-tasken oppnar dialogen aven utan claimable-signal, `collect_daily_quests` piggybackar
  samma token gratis. Desktop sparar timmen per konto i `ProductionBonusStateStore.DetectedResetHour` och
  koar en read forsta gangen / sa lange timmen ar okand (`TryQueueReadDailyResetHour`). Manuell override
  finns i Settings > General ("Daily server reset", global bot.json, OFF default) och vinner over auto.

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
  som stale. `Clear timers` ar vald-by-scope, tar aldrig bort Queue-sidans poster och vacker en korande loop.
  Som manuell aterstallning rensar den ocksa vald bys construction-snapshot (`ActiveConstructions`) och
  nollstaller deferred construction-retries sa ett fastnat "waiting" utan faktiskt bygge kan brytas.
- Construction- och byggkologik (ActiveConstructions som SOT, queue-full-defer, storage-capacity, estimat):
  [ADR 2026-06-20 construction-queue](adr/2026-06-20-construction-queue.md).
- Construct-tasks vars krav matchar en aktiv `ActiveConstructions`-prereq ska defer:as tills prereqens
  `FinishUtc` passerat (plus liten buffer), sa andra byar/tasks kan koras i stallet.
- Construct-tasks vars krav saknas och inte matchar same-village queued/active prerequisite ska terminal-failas
  fore Worker. Worker-sidans missing requirements ska vinna over resursbrist/hero-transfer pa construct-sidan.
- Innan Desktop-guarden defer:ar/failar ett `construct_building` ska target-byn live-lasas (dorf1/dorf2)
  och cacheas; cached status far inte ensam avgora nybyggnation nar browsern star i annan by.
- Om live-lasningen visar att ett `construct_building` saknar krav efter t.ex. katapultskada ska Desktop
  forsoka auto-reparera kravet fore terminal fail: promota befintlig same-village prereq-ko, annars ko:a
  saknad kravbyggnad/uppgradering overst och tagga posten `auto_added_by=construction_requirement_repair`.
- Smithy-upgrade och trupptraning (DOM, per-by-payloads, automation groups):
  [ADR 2026-06-20 smithy-troop-training](adr/2026-06-20-smithy-troop-training.md).
- Village Overview och byval visar kapitalen forst; ovriga byar behaller Travian-listans DOM/sidebar-ordning.
  Profiltabellens ordning far inte anvandas (Official kan sortera efter population); las sidebarordningen
  fore profilnavigation och anvand profilen endast for att berika bydata.
- Map Oasis Analyzer och kartparsning: [ADR 2026-06-20 map-oasis-scan](adr/2026-06-20-map-oasis-scan.md).
  Analyze map oasis ska visa en warning-confirmation fore scan eftersom flodet ar high-volume.
- "Queue wait threshold" ar BORTTAGEN (2026-07-03): ko-/resursvantor deferras ALLTID (fd "smart").
  Nyckeln `queue_wait_threshold_mode` finns inte langre (Settings-save stadar bort den);
  `ShouldDeferLongWait` ar borta — byggnadsuppgraderingar returnerar `queue_wait_seconds=N` direkt
  vid vantetid > 0, smithy deferrar alltid. Ateruppliva inte vanta-pa-plats (laser _sessionGate).
  `AllowSilverSpending`/`SilverLimit` anvands INTE av nagon automation (endast lasning for display);
  tooltipen i Settings sager det — koppla in dem eller ta bort dem om auktionsfunktioner byggs.
- Headless-lage ar BORTTAGET (2026-07-03): ingen `BotOptions.Headless`, ingen settings-checkbox,
  ingen headless-branch i `AcquireClientLeaseAsync`. Boten kor ALLTID den delade synliga
  browsersessionen. Ateruppliva inte nyckeln "headless" i bot.json (Settings-save tar bort den).
  Playwrights interna warmup-launch (Headless=true i BrowserSession.Warmup) ar orelaterad och kvar.
- `LoadBotOptions` ar cachad per (`BotConfigStore.Version`, aktivt konto): varje skrivning genom
  BotConfigStore bumpar `Version` (SaveJson/Delete). Skriv ALDRIG bot.json/account-settings forbi
  BotConfigStore — da ser cachen inte andringen. `EnvAccountStore.ReadValues` cachar .env pa
  timestamp+langd (invalideras av skrivningar och externa andringar). Bakgrund: Next task-previewn
  i 1s-ticken laste config fran disk varje sekund pa UI-traden (OneDrive-lagg).
- Tribe ar fast per konto och far ALDRIG nedgraderas fran en statuslasning: village-status kan bara
  `Tribe="Unknown"`/tom (t.ex. 20s-ticken under sleep). Bade `SetTribeText` och
  `ApplyTroopTrainingTribeState` ar skyddade — okand tribe ignoreras och nuvarande trooplistor behalls
  (annars byts dropdowns till generiska fallback-listan OCH fallback-namn persisteras i by-overrides).
  Anvand `TroopCatalog.IsKnownTribe` for kontrollen; skriv aldrig "Unknown" som Tribe i
  account-analysis-snapshots (`ResolveTribeForSnapshotWrite`). Fallback-listan (7 poster) har egen
  byggnadssplit i `ResolveTroopTypesForTribe` (3/2/2) sa Ram inte hamnar i Stable.
- "Troop settings"-popupen (TroopTrainingOptionsWindow) har expanderbara byrader: kompakt rad
  (enable + troop per byggnad) + chevron som visar ALLA settings for byn (max queue, amount, keep %,
  run trigger, timed min/max, resurscheckar, fallback wait). Cellerna ateranvander
  `TroopTrainingBuildingOption` (samma normalisering/binding som Troops-tabben); en by expanderad at
  gangen (fönstrets code-behind kollapsar ovriga). Auto celebration styrs ENDAST av Brewery-gruppens
  toggle pa dashboardens automation-kort (gruppen force-syncar `AutoCelebrationEnabled`); checkboxen
  pa Troops-tabben ar borttagen.
- Troops-tabbens "Celebration status"-badge speglar dashboardens Brewery-gruppradstimer: `RefreshAutomationLoopCardStates`
  pushar radens slutliga `RemainingSeconds` via `TroopTrainingViewModel.SyncBreweryCelebrationLoopWait` till
  `_breweryLoopWaitSeconds` (display-only fält). `AutoCelebrationTimerText`/badge-brushes anvander
  `EffectiveCelebrationTimerSeconds` = live celebration-timer forst, annars loop-speglingen (deferred "next try").
  Pushen sker bara pa dashboard-tabben + i capital; `TickCountdowns` raknar ner spegeln pa andra tabbar,
  och `ResetRuntimeState` nollar den. Rör INTE `AutoCelebrationRemainingSeconds`/`CanStart` (undviker feedback
  in i loopens egen timerkalla `ResolveBreweryCelebrationGroupRemainingSeconds`).
- Server-pickern i Accounts kombinerar `OfficialServerCatalog` (inbyggda officiella varldar, grupperade per
  region: America/Arabia/Asia/Europe/International, varldar 1-9=1x, 20=2x, 30/31=3x, 50=5x, 100=10x enligt
  `ts{N}.x{speed}.{region}.travian.com`) med anvandarens custom-lista ("Custom"-gruppen overst). Officiella
  presets visas ALLTID, aven om en custom-post har samma URL (URL-val traffar custom forst i listordningen).
  Letterkodade specialvarldar (nys/ttq/rof) och regioner med oregelbundna namn (Nordics/Balkans) hanteras
  via custom-listan. `ServerCatalogStore`/ServerListWindow hanterar ENDAST custom-servrar; officiella
  presets ligger i kod. Servernamn ska ha hastigheten inom parentes ("America 100 (10x)") sa att
  speed-parsning (`ResolveServerSpeed`/`ServerSpeedLabel`) traffar hastigheten och inte varldsnumret.
  Dropdown anvander `ScrollViewer.CanContentScroll="False"` for pixelscroll — item-scroll ar hackig med
  grupprubriker.
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
- By-NAMN (visning) serveras ur `_cachedVillages` (worker, TTL 15s) och uppdateras annars bara vid
  by-*byte*/TTL — ett in-game namnbyte syns darfor stale. `[ui-sync]` laser aktivt bynamn FARSKT varje
  tick men bylistan ur cache, sa payloaden blir inkonsistent (ActiveVillage='1440' men listan 'New
  village') -> UI-namnet flimrar. `ReconcileActiveVillageNameInCacheAsync` (anropas i `TryEmitUiSyncSnapshotAsync`
  fore `ReadVillagesPreferCacheAsync`) matchar aktiv by pa KOORDINATER och skriver in det farska namnet i
  cachen inom en tick. Ren logik: `ReconcileRenamedActiveVillageByCoords` (matchar pa koord, aldrig namn,
  sa tvillingnamn aldrig doper om fel by). Snabbvag: hoppar koord-DOM-lasningen nar namnet redan finns i cachen.
- Desktopens by-status-cache (`_villageStatusCacheByName` + persisterad `VillageCacheStore`) ar NAMN-nycklad,
  sa ett namnbyte lamnar annars en foraldrad post under gamla namnet -> den omdopta byn visar "no data" tills
  nasta Switch village. `MigrateRenamedVillageStatusCacheEntries` (i `ApplyVillageEnabledState`, FORE
  `VillageSettingsStore.Merge`) upptacker namnbytet via KOORDINATNYCKELN: `GetStoredName` ger det gamla namnet
  som annu ligger kvar for koordinaten (Merge skriver over det direkt efter), och `MigrateVillageStatusCacheKey`
  flyttar cache-posten gammalt->nytt namn + sparar. Fungerar bade live och efter omstart (settings-store persisterar namnet).
- Koposter stamplar den stabila koordinatnyckeln (`target_village_key`) vid enqueue
  (`ApplySelectedVillageToPayload`/`BuildVillageRuntimePayload`); `GetQueueItemVillageKey` laser den FORST
  (fallback namn/url for gamla poster). Annars resolvas en ny by med ATERANVANT namn (forlorad + omgrundad)
  till fel by och posterna gating:as/pausas bort. Forlorade byar rensas retention-baserat: en by som ar
  bekraftat saknad (koordinatidentitet) ur login/scan-listan i `LostVillageRetention` (3 dygn) prunas och
  dess kvarvarande Pending/Paused-koposter tas bort (`ConfirmedMissingSinceUtc` ->
  `GetVillagesConfirmedMissingSince`/`RemoveVillages` + `ConfirmedVillageQueueReconciler.RemoveItemsForVillages`).
- Login ska anvanda action pacing och vanta pa full sidladdning. Login-state `unknown` under navigation ar
  normalt en transient ladd-race; captcha, `manual_step` och `logged_out` ar inte det.
- Post-login-landningen (dorf1) far INTE blocka pa hela browser-`load`-eventet med `_config.TimeoutMs`:
  Officials inloggningssida drar in tredjeparts ad/consent/video-iframes vars resurser kan hanga oandligt,
  sa `load` fyras aldrig aven om spelet ar klart -> ~20s bortkastad vantan + falskt "timeout"-alarm pa en
  redan laddad sida. `LoginAsync` vantar `DOMContentLoaded` (snabbt/palitligt) + kort best-effort `load`
  (`PostLoginLoadSettleTimeoutMs`=5s); en miss loggas `[login:verbose]` (aldrig alarm — `IsAlarmMessage`
  hoppar over `:verbose]`) eftersom `WaitUntilLoggedInAsync` redan bekraftat spel-shellen.
- Playwright `Target crashed` ar transient: kassera shared browser-session, defer:a queue-posten kort och
  lat nasta operation skapa en frisk session. `BrowserFailureClassifier.IsTargetCrash` klassar aven
  stangd sida/kontext (`...has been closed`, `page is closed`, `Cannot navigate to closed page`) som krasch.
  Lagg ALDRIG till `Execution context was destroyed` dar — det ar en ofarlig navigerings-race som retry:as.
- Playwrights native popup-blocker ska vara aktiv i live-sessionen; botens egna extra flikar anvander
  `NewPageAsync`. Innan `StorageStateAsync` sparas ska transienta ad/consent-origins rensas ur live-contexten.
  Bonus-video (hero adventure och construct-faster) ska koras i isolerad temp-browser utan native popup-blocker
  och aldrig ladda ad-stack i main context; rena background-/DOM-prober ska inte spara storage state efter lasningen.
  Travco ska oppnas i isolerad browser-context, aldrig i Travian-contexten.
- Den isolerade bonus-video-browsern (`RunInIsolatedBonusVideoBrowserAsync`) MASTE alltid rivas ner. En frusen
  ad/video-renderer kan hanga ett Playwright-anrop (t.ex. `EvaluateAsync` i completion-loopen) FORBI sin egen
  timeout — 75s-deadlinen kollas bara i loop-toppen — sa `await action(...)` returnerar aldrig, `finally` kor
  aldrig och browsern lamnas oppen medan tasken staller sig (tva browsers + "Shutdown timeout waiting for
  background tasks"). Darfor hard-time-boxas HELA floret (`IsolatedBonusVideoMaxDuration`=120s via `WaitAsync`
  pa en linked-CTS) sa vi slutar vanta aven om action ignorerar token; finally stanger browsern (vilket
  avblockerar det hangande anropet) och en `TimeoutException` bubblar upp -> construct-faster bygger normalt.
  Aven `CloseAsync` time-boxas (`IsolatedBonusVideoCloseTimeout`=10s) sa en wedgad stangning inte kan ater-stalla tasken.
  Teardown stanger BROWSERN direkt (`videoBrowser.CloseAsync()`), inte contexten forst: `videoContext.CloseAsync()`
  forsoker stanga sidor gracefult och hanger pa en frusen ad/video-renderer (loggade "context cleanup failed:
  timed out" + brande close-timeouten), medan browser-close river ner context/sidor/process pa ~1s. Inget lases
  tillbaka fran denna browser sa inget behover flushas gracefult.
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
- Resource-field queue-dedupe maste matcha `ActiveConstruction.SlotId` nar Travian exponerar slot-id;
  samma namn (t.ex. flera Cropland) far bara vara fallback nar queued slot-id saknas.
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
- Construct-faster anvander Official-knappen `.upgradeButtonsContainer .section2 button.videoFeatureButton`;
  missing/disabled/short duration/random-skip faller igenom tyst. Efter video ska main-sidan navigera
  till farsk `dorf2.php` och verifiera ko/slot; saknas bevis forsoks video en gang till, sedan ALARM + vanlig build.
  `construct_faster_min_build_time_enabled=false` betyder att duration-gaten ar av och alla kvalificerade byggen provas.
- `#videoFeature` info-dialogen (25%-popup) har en `input[name="preference"]` "Don't show it again"-checkbox.
  `ConfirmConstructFasterVideoDialogAsync` kryssar i den fore "Watch video" (`.dialogButtonOk`) sa Official slutar
  visa popupen for kontot. Nar preferensen redan ar satt hoppar Official over info-skarmen och oppnar spelaren
  direkt (`#videoFeature.showVideo` / `#videoArea`) — da ska confirm returnera direkt utan att leta efter OK-knappen.
- Byggnadsuppgradering far ateranvanda aktuell `build.php?id=N` for slot snapshot endast nar sidan ar ratt slot,
  inte stale och kan lasa niva + namn/gid; annars ska flodet falla tillbaka till `dorf2`.
- Build-page header/dorf1/dorf2-level kan vara stale nar en tidigare niva ligger i byggkon; for fortsatt
  upgrade pa samma slot ar Official-knapptexten (`Upgrade to level N`) och cost/transfer-payload sann aktuell
  upgrade-offer. Hero-transfer-dedupe ska darfor nycklas pa offer (slot+niva/cost), inte bara slot.
- Construct mot en slot som redan har byggnaden (stale construct-task, eller fast specialslot RP=39/Wall=40 som
  finns fran grundning) far INTE ALARM:a pa saknad construct-choice-DOM. `ConstructBuildingAsync` lasar slotens
  build-sida live (`TryReadExistingBuildingOnSlotBuildPageAsync`); bekraftad befintlig byggnad (level >= 1, ingen
  `#contract_building*`) -> returnera "already exists at slot" som klassas `ConstructionTaskOutcome.AlreadyExists`
  och desktop TAR BORT posten ur kon (`HandleQueueItemSucceededAsync`). Maste vara live-bekraftat fore borttagning.
- Palace/Residence/Command Center ar mutual-exclusive per by; level 0/queued construct raknas som narvaro och ska
  blocka de andra redan i Desktop enqueue-gate, planners och Worker-sista skydd.
- Official appendar `&gid=<befintlig byggnad>` till `build.php?id=N` nar sloten ar UPPTAGEN (boten skickar aldrig
  `gid=` sjalv i construct-url:en). Matchar server-gid det efterfragade gid:et men level >= 1 inte kan bekraftas
  (nykonstruktion pagar, level 0), ska `ConstructBuildingAsync` defer:a med `queue_wait_seconds` tills bygget ar
  klart — inte faila mot saknad construct-choice-DOM.
- `gid 13` ar Smithy (ingen separat Armoury pa `gid 12`). Smithy/trupptraning anvander browserbekraftade
  SOT-koer (`SmithyUpgradeStatus` / traningskon) och unit-id-ikon for truppidentitet.

### Hero och React-dialoger

- Hero-attributens defaultordning ar `resources,fighting_strength,offence_bonus,defence_bonus`.
  UI-ordningen sparas konto-scopeat och anvands oforandrad vid poangtilldelning.
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
- Hero-transfer pa brewery-sidan MASTE scope:as till "Hold celebration"-raden (`.researches .research`):
  sidan visar aven byggnadens egen uppgraderingskostnad forst i DOM, sa oscopad shortfall/klick traffar
  uppgraderingen (fel belopp, fel dialog). Vid utebliven start defer:as med berknad ackumuleringstid
  fran celebrationens shortfall, inte fast 60s-retry.
- Noll adventures ska inte automatiskt stanga av anvandarens Hero-toggle.
- Official Add target ska fylla X och Y som separata Playwright-interaktioner. Vid Default troops ska
  koordinatfaltet blur:as och en neutral yta klickas innan flodet vantar pa aktiv Save. Stegen anvander
  konto-scopead `farm_list_step_delay_*_seconds` (default 1.5-4 s, under Action pacing/Loop). Owner-varde
  `-` i Add target betyder fri oas och far inte klassas som occupied. Efter Save ska success verifieras
  mot ratt `lid`/koordinat eller okat listantal; duplicate-confirmation ska cancelleras, inte OK:as.
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
  (run 40-100 min, sleep 20-60 min, 16h daily max), action pacing pa
  (task 0.8-2s, page load 0.6-1.6s, click 0.4-1.4s, loop 4-25s),
  farm-list step 1.5-4s, collect tasks/daily step 0.6-2s.
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
2. Jamfor Official-markup mot nuvarande selektorer.
3. Uppdatera Official-scope:ade selektorer/path helpers vid behov.
4. Isolera stateless parsing och lagg till fokuserade tester.
5. Kor build och relevanta tester.
6. Verifiera live pa Official.
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
- [UI theme](adr/2026-06-03-ui-theme.md)
- [Multi-village och konto-state](adr/2026-06-05-multi-village.md)
- [Dashboard overview](adr/2026-06-06-dashboard-overview.md)
- [Shutdown och cleanup](adr/2026-06-08-shutdown-cleanup.md)
- [Farmlists och Travco](adr/2026-06-09-farmlists-and-travco.md)
- [Construction queue och build-status](adr/2026-06-20-construction-queue.md)
- [Smithy och trupptraning](adr/2026-06-20-smithy-troop-training.md)
- [Map Oasis och kartparsning](adr/2026-06-20-map-oasis-scan.md)
- [TravianClient-seams och refaktorstatus](adr/2026-06-25-travianclient-seams.md)
