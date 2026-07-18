# ADR: Construction queue, build-status och estimat

## Status

Aktivt beslut, 2026-06-20. Detaljerna bakom de korta reglerna i
`ENGINEERING_NOTES.md` (sektion 4 Desktop + sektion 5 "Byggnader och ko").

## ActiveConstructions som auktoritativ kalla

- Dashboardens byggikoner anvander live `ActiveConstructions` som auktoritativt antal.
  Queue-full-poster och lokala retry-timers far aldrig anvandas som bevis pa ett aktivt bygge.
  Gul waiting-status far bara visas medan `ActiveConstructions` innehaller en faktisk Travian-byggko;
  tom browserko ska ge gra lediga byggplatser aven om en programtask fortfarande ar deferred.
- Construction-status skiljer pa `Unknown`, bekraftat tom och aktiv ko. Endast en bekraftad
  dorf1/dorf2-lasning far frigora en `queue_full`-blockerare direkt; okand status behaller senaste retry.
- `ActiveConstructions` far automatiskt rensas endast av en bekraftad tom dorf1/dorf2-lasning
  (`ActiveConstructionsFromOverview=true`). Lokal `FinishUtc`, cache-load, UI-tick och partial reads far
  aldrig rensa browserns senaste construction-snapshot. Undantag: den manuella `Clear timers`-knappen.
- Utgangen `ActiveConstructions` fran cache ar `Unknown`, inte aktiv ko: behall snapshoten for senare
  bekraftelse men visa den inte som aktiv rad/ikon och rakna inte `ActiveBuildCount`.
- Nar en lokal construction-timer nar noll ska den endast begara en ny Travian-lasning. Den far inte
  minska aktivt antal eller markera kon tom lokalt.
- Construction-kortets live byggtid och aktiva antal ska harledas fran samma `ActiveConstructions`
  som Queue-fliken. Resurs-, krav- och retry-vantan visas separat och far inte blandas in i kotiden;
  gamla `ActiveBuildCount`/`BuildQueueRemainingSeconds` far inte ensamma visa aktiv ko.
- Queue-flikens Travian byggkö ska använda samma byspecifika `ActiveConstructions`; Smithy-rutan ska
  använda samma `SmithyUpgradeStatus.ActiveUpgrades` som ikoner och loopstatus. Båda visar målnivå och
  `FinishUtc` med programmets serverklocka; inga separata UI-källor. Queue-rutorna visar fasta platser:
  två construction-platser (tre för romare) och två Smithy-platser, med sekundvis nedräkning från
  samma absoluta sluttider. Lediga bekräftade platser visas som `Ready`.

## Queue-full, defer och blockering

- En `queue_full`-defer blockerar all senare construction i samma by tills den tidigaste
  aktiva byggnaden ar fardig. Plus ger tva platser, annars en; inga romar-specialfall har.
- Queue-poster kor bara nar byns Auto-toggle ar pa och postens automation group ar pa for samma by.
  Avstangda byar/grupper ignoreras i scheduler/auto-queue och ligger kvar tills anvandaren slar pa dem igen.
- Construction-defer som betyder `queue full`, `already queued` eller `still in progress` ar ko-upptagning,
  inte resursbrist; resursrefresh far inte aterstalla den vantan. Endast verklig `queue full`/`blocked by
  queue` far blockera hela byn. `already queued/still in progress` blockerar bara samma uppgift, sa senare
  bygguppgifter kan anvanda en ledig Plus-slot. Queue-full retry synkas mot byns levande byggstatus.
  Aldre defer-poster utan aktuell klassificeringsversion ska valideras om av Worker, men hogst en per by
  nar en tidigare post redan har bekraftat full ko; annars orsakas en `dorf2`-reload per gammal post.
- Continuous Loop och Auto Queue ska anvanda samma byspecifika construction-valjare. En bekraftat ledig
  Travian-plats gor att en framtida `queue_full`-post valideras direkt av Worker; resurs- och kravvantan
  behaller ordningen, medan ett redan pagande mal kan hoppas over for en senare Plus-uppgift.
- Per-slot state fran kon maste alltid filtreras per vald by.
- Partiella current-page-resurslasningar utan bygg-DOM far inte nollstalla en cachad aktiv byggko.
- `load_buildings_snapshot` ar en lasning och far inte blockeras som ett bygge.
- `BuildQueueIdentityFingerprint` far inte innehalla tickande countdown-text.
- Resource upgrade-all ska returnera `queue_wait_seconds` direkt vid resursbrist.
- En resursdefer som bara bär Travians sid-timer (`wait_reason=page_timer`, inga `upgrade_required_*`
  i payloaden) kan inte räknas om mot en resurssnapshot. Den återupptas (retry nu) endast när byns
  lager är fulla (alla fyra vid kapacitet), eftersom en resurs-dump (hero/farm/NPC) då gör den cachade
  timern stale och byn annars idlar ut en nedräkning som inte längre gäller. Ett fullt men ändå för dyrt
  bygge omklassas till `storage_capacity` (annan reason), så återupptagningen kan inte loopa.

## Construct/upgrade-klick och verifiering

- Exkludera payment-knappen `Open shop` fran upgrade-/construct-kandidater.
- Official tom slot identifieras via `#contract_building*` utan `Upgrade to level N`; anvand inte enbart `.upgradeButtonsContainer`.
- Construct ska verifiera ratt `build.php?id=<slot>&category=<n>` och renderade `#contract_building*` fore klick.
- Vid misslyckat construct-klick ska resursbrist och krav lasas innan ko-/progresskontroller som navigerar till `dorf2`.
- Vid klassad resursbrist pa en befintlig byggnads build-sida ska hero-transfer provas direkt; navigera till `dorf2` endast om direkt transfer inte loser klicket.
- Construct/upgrade-success ska vara malspecifik: slot-level, matchande aktiv konstruktion eller timerfri
  kotext for samma byggnad. Annan byggko eller tickande timer ar inte bevis.
- Saknade byggkrav ar temporar defer, inte permanent failure.

## Storage-capacity-dependency

- Official `upgradeBlocked` med `Extend warehouse/granary first` ar storage-capacity, inte vanlig
  resource-wait. Worker returnerar `wait_reason=storage_capacity` och desktop later
  `StorageCapacityDependencyPlanner` kopa Warehouse/Granary-dependency fore originaltasken.
- Om exakt byggkostnad overskrider live Warehouse-/Granary-kapacitet klassas vantan som
  `storage_capacity`. Originaltasken defer:as medan en markerad dependency med hogsta koprioritet
  uppgraderar relevant lager direkt till forsta nivan som ger tillracklig total kapacitet. Saknas lagret
  eller ar alla exemplar maxade konstrueras ett nytt
  i forsta lediga vanliga byggslot (19-38). Ingen ledig slot pausar originaltasken och skriver `ALARM:`.
  Parent aterupptas forst nar dependency-nivan ar bekraftad fardig; aktiva Travian-byggen styr vantetiden.

## Build-time- och kostnadsestimat

- Queue-sidans `Build time`/`Cost`-kolumner och totalsumman ar best-effort-estimat ur
  `buildings_catalog.json` (1x), skalat med serverhastigheten fran `ResolveServerSpeed()`
  (regex `(\d+)x` ur servernamnet; fallback 1x + engangs-`ALARM:`). Endast construction-tasks
  estimeras (`EstimateForQueueItem`); ovriga lamnas blanka. Saknad nivadata/okand byggnad ger blank
  + engangslarm, aldrig blockering. Kosidan visar både `Time` och `Cost` per post samt totalsummor.
  Byggtid visas även i slot-popupen och `Upgrade to...`-fonstret.
- `upgrade_all_resources_to_level` summerar alla nivåsteg för exakt 18 kända fält i den laddade byn;
  en ofullständig fältsnapshot lämnas blank för att undvika en för låg totalsumma.
  Nuvarande niva for fleruppgraderingar finns bara for den laddade byn; annars estimeras endast malnivan.
- Nar flera uppgraderingar av SAMMA slot ligger i kon (t.ex. RP till niva 2,3,...,10) far varje rad bara
  sitt eget steg, inte hela vagen fran nuvarande niva: `SumLevelsWithQueueCoverage` spar hogsta redan
  kolagda malniva per by+slot (`queuedCoverage`, endast Pending/Running/Paused) sa rad + totalsumma inte
  dubbelraknar de delade lagre nivaerna.
- Byggtiden skalas ocksa med huvudbyggnadens rabatt `0.964^(MB-1)` i `BuildSecondsFor`
  (`mainBuildingLevel`). MB lases byspecifikt fran den laddade byns slots (`ResolveMainBuildingLevel`,
  gid 15); okand MB -> default niva 1 (ingen rabatt). Nar byns byggnader skannas anropar
  `PopulateBuildingsTab` en `RequestQueueUiRefresh` (skyddad av `_isRefreshingQueueUi` mot rekursion)
  sa redan kolagda poster rakas om med ratt MB.
- `ResolveServerSpeed` provar forst servernamnet (`(\d+)x`, t.ex. "10x") och faller annars tillbaka
  pa server-URL:ens hastighetssubdoman (`\.x(\d+)`, t.ex. `ts100.x10...`). Misslyckas bada -> 1x + engangs-`ALARM:`.

## Loggning

- Construction-ko ska loggas per tillstandsandring och by, inte per blockerad ko-post. Behall klassificering,
  vald retry och betydande timersynk; lyckad intern persistens och varje enskild blockerad kandidat ar brus.
- Queue-full ska loggas med bynamn samt exakt nasta retry i servertid och sekunder kvar.
- Djup queue-full-diagnostik anvander `[construction-queue:verbose]` och doljs i Clean-laget.

## Konsekvenser

`ActiveConstructions` (browserbekraftad) ar enda sanningskallan for aktiv byggko; lokala timers och
defer-poster ar harledd vantan, inte bevis. All ny construction-/queue-UI maste lasa samma byspecifika
SOT. Full bakgrund finns i `docs/history/engineering-notes-archive.md`.
