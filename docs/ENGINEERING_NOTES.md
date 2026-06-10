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
- Ateranvand `SettingInfoIconStyle` for forklarande infoikoner.
- Langre listor ska ligga i en begransad `ScrollViewer`, inte expandera resten av dashboarden.
- Map Oasis Analyzer laser `{BaseUrl}/map.sql` via HTTP, cachar per server under
  `Data/Maps/<host>/map.sql` och parsern ska forbli browserfri och enhetstestbar.
- Oaslistor ateranvander kontoavgransade `travco_lists.json`; oasfalt ar valfria sa aldre
  Travco-listor och Official-importens koordinatflode forblir kompatibla.
- `map.sql`-parsern anvander kolumnerna `x`, `y`, `landscape`, `type` och `player_id`;
  endast `type == 3`, kanda landscape-ID:n och valda oastyper tas med.

### Kvalitetsregel

En ny formaga ska kunna enhetstestas till stor del utan browser. God-klasserna ska krympa
eller sta still, inte vaxa.

## 5. Aktiva fallgropar

### Browser och navigation

- Village-switch ska kanoniseras till `dorf1.php?newdid={id}` utan extra `id`-parametrar och verifieras som inloggad.
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
