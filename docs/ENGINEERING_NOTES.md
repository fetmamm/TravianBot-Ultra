# Engineering Notes — TbotUltra

> **Läs detta innan du ändrar selektorer, sökvägar eller serverlogik.**
> En levande fil för konventioner, beslut och fallgropar. Fyll på löpande — lägg nya
> rader i **Beslutslogg** och **Kända fallgropar** med datum. Håll den kort och konkret.

Relaterat: `docs/REFACTOR_PLAN.md` (refaktoreringsanalys), `AGENTS.md` (instruktioner för AI-agenter), `README.md`.

---

## 1. Arkitektur (kort)

| Projekt | Ansvar |
|---|---|
| `TbotUltra.Core` | Konfiguration (`BotOptions`, `ServerFlavor`), task-payloads, trupp-/byggnadskataloger. Ingen browser/UI. |
| `TbotUltra.Worker` | Spelautomation via Playwright. `TravianClient` (partial, ~15 filer i `Services/Automation/`) äger all server-interaktion. `BotTaskRunner` kör tasks. |
| `TbotUltra.Desktop` | WPF-UI. `MainWindow` (många partials) + ViewModels. `LoadBotOptions()` läser `bot.json` → `BotOptionsFactory`. |

Beroenden: `Desktop` → `Worker` → `Core`.

---

## 2. Två servervarianter — Official vs SS-Travi ⭐

Boten stödjer **både** officiella Travian Legends-servrar (T4.6) **och** SS-Travi-privatservrar
ur **samma kodbas**, valt vid körning av `ServerFlavor`-flaggan.

### Grundregler (lätta att göra fel — gör inte fel)

1. **`ServerFlavor` härleds ALLTID från `BaseUrl`-host.** Aldrig från config, aldrig cachad.
   `*.ss-travi.com` → `SsTravi`, allt annat → `Official`. Se `BotOptions.ServerFlavor`
   (computed property) och `ServerFlavorDetector.FromBaseUrl`.
   - ❌ Lägg **inte** tillbaka `[ConfigurationKeyName("server_flavor")]`-bindning — det orsakade
     en bugg där ett gammalt värde i `bot.json` gjorde att SS feltolkades som Official.

2. **Selektorändringar är ADDITIVA.** SS-selektorn provas **först**, officiell läggs till som
   **fallback** — ersätt aldrig en SS-selektor. Mönster:
   ```js
   // SS uses #stockBarWarehouse; official (T4.6) uses .warehouse .capacity .value.
   document.querySelector('#stockBarWarehouse, .warehouse .capacity .value')
   ```

3. **Sökvägar som skiljer → flavor-aware helper** (i `TravianClient.Selectors.cs`):
   ```csharp
   private string HeroAdventuresPath => _config.IsPrivateServer ? Paths.HeroAdventures : "/hero/adventures";
   ```
   Använd helpern i `GotoAsync(...)`, inte `Paths.X` direkt, för sidor som skiljer.

4. **Privatserver-only features gate:as bakom `_config.IsPrivateServer`** (t.ex. Natar-farming),
   så de göms/inaktiveras på officiell.

5. **React-sidor** (officiella `/hero/adventures`, `/hero/attributes`, `/auctions/*`) renderas
   klient-sida → **vänta in render** innan du läser/klickar (`WaitForFunctionAsync` på ett
   nyckelelement), och **verifiera live** — de går inte att härleda säkert ur sparad HTML.

### URL-skillnader (officiell vs SS)

| Sida | Officiell (T4.6) | SS / legacy |
|---|---|---|
| Hero adventures | `/hero/adventures` | `/hero_adventure.php` (+ `/hero.php?t=3`) |
| Hero inventory | `/hero/inventory` | `/hero_inventory.php` |
| Player profile | `/profile/{id}` (redirect från spieler) | `/spieler.php` |
| Messages | `/messages` | `/nachrichten.php` |
| Reports | `/report` | `/berichte.php` |
| Statistics | `/statistics` | `/statistiken.php` |
| Village overview | `/village/statistics` | `/dorf3.php` |
| Rally point-flikar | `build.php?id=39&gid=16&**tt**=N` | `build.php?id=39&**t**=N` |
| Marketplace-flikar | `build.php?id=..&gid=17&**t**=N` | (samma `t=`) |
| dorf1 / dorf2 / karte | samma `.php` | samma |

### Markup-skillnader värda att minnas

- **Stam:** officiell taggar `div.buildingSlot`/`img.building` med stamklass (`gaul`, `roman`, …).
  SS/ikon-baserat. Läs stam från klassen (säkrast).
- **Plus:** officiell quick-links (`villageQuickLinks`) är **gröna** med Plus, **guld** utan
  (knappen är `disabled` i båda — färgen är signalen).
- **Resurser/lager:** officiell `.warehouse/.granary .capacity .value`; SS `#stockBar*`.
- **Bylista/byte:** officiell `div.listEntry.village[data-did]` (ingen `newdid`-länk); SS `a[href*="newdid"]`.
- **Hero away:** officiell `i.heroRunning` (dorf1) / `.heroState i.statusRunning` + `span.timerReact` (adventures).
- **NPC trade:** officiell knapp `button.exchange[value="Exchange resources"]` → dialog (`#npc`,
  `name="desired0..3"`, `button[value="Distribute remaining resources."]`, `#npc_market_button`).

---

## 3. Konventioner

- `bot.json` är global fallback. Konto-/by-specifika UI-val ska sparas i
  `config/accounts/<account>/settings.json` och läsas som overlay ovanpå `bot.json`.
- **Kontobyte = full reset** — inget från gamla kontot ska ligga kvar laddat/cachat.
- Bygg: `dotnet build TbotUltra.sln`. Test: `dotnet test src/TbotUltra.Worker.Tests/...` (+ Desktop.Tests).
- Diagnostik: `[flavor]`-raden vid login visar `ServerFlavor`/`IsPrivateServer`/`baseUrl`
  (döljs i Clean-loggläge).

---

## 4. Beslutslogg (ADR — append-only)

- **2026-06-08** — **Travco inactive search använder en separat tab och DTO-baserad DOM-läsning.**
  Official-only-flödet återanvänder den synliga browser-contexten. Popupen väljer by, dagar och sortering;
  sparning läser aktuell DOM eller samtliga resultatsidor och återgår till sida 1. Playwright-resultat
  deserialiseras först till muterbara DTO-klasser för att undvika `return type mismatch` med records.

- **2026-06-08** — **MainWindow-close avslutar WPF-applikationen explicit efter cleanup.**
  Shutdown-flödet behåller cancel, väntan på bakgrundsjobb och browser-cleanup med timeout, men avslutar
  därefter via `Application.Shutdown()` i stället för endast `MainWindow.Close()`. Ett kvarvarande dolt eller
  modellöst fönster kan därför inte hålla processen vid liv när huvudfönstret har stängts.

- **2026-06-08** — **Hero-attribut sparas per konto/server och loggbrus filtreras.**
  Senast lästa `HeroAttributeSnapshot` sparas under kontots cache och laddas till Hero-UI vid start och
  kontobyte. Login-felet `Current page state is 'unknown'` ligger kvar i alarmhistoriken men auto-kvitteras.
  Clean-läget döljer no-action hero-status, `hero_manage STARTED` och `LoginAsync started`.

- **2026-06-08** — **Click-delay körs endast när Action pacing är aktiverad.**
  `DelayBeforeClickAsync` returnerar direkt när `action_pacing_enabled` är av, så den centrala click-delayen
  följer Settings-checkboxen vid samtliga anrop.

- **2026-06-08** — **Shutdown steg 4–8: spårade bakgrundsjobb, processväntan och popup-cancel.**
  MainWindows fristående warmup-/refresh-/loopjobb registreras nu i en ägd `BackgroundTaskTracker` med
  gemensam shutdown-token. Stängning stoppar och inväntar jobben innan browser-sessionen disponeras och
  nekar nya jobb efter att shutdown startat. Captcha-solverns Python-processträd dödas och inväntas vid
  timeout/cancel. Fönsterlokala CTS i Add Farms och Catapult Waves avbryts/disponeras vid close, och
  modeless popups stängs explicit under app-shutdown.

- **2026-06-08** — **Queue clear använder ofiltrerad konto-kö.**
  `Clear account queue` utgick tidigare från `QueueDataGrid.ItemsSource`, som är filtrerad till vald by,
  och rensade därför bara den by som visades. Account-clear läser nu alla aktiva poster direkt från den
  konto-scopeade queue-storen och omfattar automatiskt samtliga nuvarande/framtida byar. `Clear village
  queue` läser samma ofiltrerade store men tar endast poster vars target-village matchar vald by.

- **2026-06-08** — **Shutdown steg 1–3: async browser-close, robust Playwright-dispose och komplett ägar-cleanup.**
  `MainWindow_Closing` blockerar inte längre UI-tråden med `Task.Wait`; första close avbryts, timers och
  loop-CTS stoppas, browser-shutdown inväntas asynkront (15 s timeout), därefter stängs fönstret igen.
  Alla MainWindow-timers stoppas, inklusive resource/debounce/queue/captcha. DI-rootens `ServiceProvider`
  disponeras i `App.OnExit`, vilket även disponerar singleton-`LoopController`. `BrowserSession.DisposeAsync`
  städar context, browser och Playwright oberoende så ett fel inte hoppar över resterande cleanup, och
  `OpenPageAsync` städar delvis skapade resurser om initiering misslyckas.

- **2026-06-08** — **Kontobyte bevarar båda kontonas separata köer.**
  `JsonQueueStore` var redan dynamiskt konto-scopead, men `ResetForAccountSwitchAsync` anropade
  `ClearAccountScopedUiState`, som alltid körde `ClearQueue`. Vid dropdown-byte rensades gamla kontots fil;
  via Accounts-fönstret kunde aktiva kontot redan vara bytt och då rensades nya kontots fil. UI-reset tar
  nu `clearQueue`; program-reset använder `true`, kontobyte använder `false`. Efter byte läses det nya
  kontots befintliga kö direkt och eventuella orphaned `Running`-poster återställs till `Pending`. Den gamla
  konto-settings-rensningen anropas inte längre vid byte, eftersom den via Accounts-fönstret kunde träffa
  det nyvalda kontots redan separata village/farm-inställningar.

- **2026-06-08** — **Task/Daily Quest collection följer aktiv by och bryter inte by-dräneringen.**
  Bakgrundssignalen köade tidigare `collect_tasks`/`collect_daily_quests` utan target-village och
  continuous-schedulern prioriterade dem globalt före by-rotationen. Resultatet kunde bli att `/tasks`
  öppnades efter ett byte till en annan by där village-rewarden inte fanns. Collect-items taggas nu med
  `ActiveVillage` från samma current-page-status som signalen lästes på. De körs direkt endast om boten
  fortfarande arbetar i den byn; annars väntar de tills nuvarande by saknar redo arbete.

- **2026-06-08** — **Pause bot fryser session pacing; Stop bot återställer den.**
  `SessionPacer` har nu ett explicit `Paused`-läge som sparar återstående körtid. Start efter paus
  fortsätter samma nedräkning i stället för att skapa en ny varierad session. Endast bekräftad
  **Stop bot** (samt appstängning) gör full `Reset`. Hero resource max-use-defaulten är åter 5000
  både i `BotOptionsFactory` och standardkonfigurationen.

- **2026-06-08** — **Village overview tappar inte byggkö-status vid en partiell DOM-miss.**
  Dashboardens Buildings-ikoner drevs enbart av `ReadBuildQueueAsync`; om den breda kö-selektorn
  tillfälligt gav noll poster skrevs `ActiveBuildCount=0` trots att den oberoende
  `ReadActiveConstructionsAsync` samtidigt såg pågående byggen. Statusläsningarna använder nu maxvärdet
  från båda läsningarna. Active-construction-parsern deduplicerar inte längre på byggnadsnamn, eftersom
  två samtidiga uppgraderingar kan ha identiskt namn och ska räknas som två separata köplatser.

- **2026-06-07** — **Byggnads-kö: sluta slå ihop nivå-uppgraderingar + krav-medveten kö + kaskad-borttagning.**
  Rotorsak (rapporterad bugg): `EnqueueBuildingUpgradeTaskCoalesced` (`MainWindow.Buildings.Queueing.cs`)
  slog ihop alla `upgrade_building_to_level` för samma slot till EN max-nivå och **skapade ett nytt item
  sist i kön** → MB→3 hamnade efter byggnader som krävde MB3. Fix: (1) nivå-uppgraderingar slås **inte**
  längre ihop — varje köas som eget item på sin köplats (samma byggnad kan köas flera gånger, t.ex. MB→3 nu,
  MB→10 senare). Endast `upgrade_building_to_max` dedupliceras per slot (två max är meningslöst). Workerns
  `UpgradeBuildingToLevelAsync` loopar redan upp till target, så MB→3 följt av MB→10 bygger 1→3 sedan 3→10.
  (2) En köad (ej byggd) `construct_building` räknas nu som **nivå 1** mot krav i `MissingRequirements`
  (ny `QueuedConstructProvidesRequirement`) — så en köad kravbyggnad låser upp den beroende byggnaden
  (construct+upgrade-kedjor täcktes redan av projektionen). (3) Ny `CascadeRemoveUnsatisfiedBuildingQueueItems`
  körs efter manuell köborttagning (`QueueRemoveButton_Click`): tar bort construct-items vars krav inte längre
  uppfylls (built + kvarvarande kö) och föräldralösa uppgraderingar (tom slot utan köad construct), kaskadat
  tills kön är stabil. Projektionen (`BuildProjectedBuildingStatus`) tar redan max-target över flera köade
  upgrades per slot. Endast vald by + by-lösa items berörs (kräver `_lastBuildingStatus`).

- **2026-06-07** — **Hero-inventory "Max use limit" (per resurs).** Ny toggle (default på) + fält (default
  5000) i Hero/Adventures inventory-kortet (`HeroViewModel.HeroResourceMaxUseEnabled/PerResource`,
  config-nycklar `hero_resource_max_use_*`). Worker: `TryHeroResourceTransferForConstructionAsync` läser
  per-resurs-shortfall (cost − stock); om någon resurs skulle dra mer än limiten från hjälten skippas
  överföringen och `ComputeHeroUseLimitDeferSecondsAsync` beräknar väntan tills byn producerat nog (stock
  ≥ cost − limit). **Produktionen läses från cachen** (`ReadCachedProductionByHourForActiveVillageAsync`),
  ALDRIG från build-sidan (den saknar `#production` → annars långsamma misslyckade retries). Väntan
  vidarebefordras via `_heroTransferOverLimitWaitSeconds`; `ReadUpgradeResourceWaitSnapshotAsync`
  **early-returnar** då direkt (inga sid-läsningar) med `queue_wait_seconds` som driver construction-
  countdownen. Icke-blockerande, loggas en gång (ej spam). Per resurs, inte totalt. Tog bort "Hero
  inventory updated."-texten.

- **2026-06-01** — Officiell-server-stöd byggs som **lager i ett repo** med flavor-flagga,
  **inte** en fork eller `IServerAdapter`-refaktor. Skäl: undvik dubbel-underhåll (~80 % delad kod).
- **2026-06-01** — `ServerFlavor` är en **computed property från `BaseUrl`**, aldrig config-bunden.
  Skäl: config-bindning gjorde att en stale `server_flavor` feltolkade SS som Official.
- **2026-06-01** — Behåll SS-selektor-fallbacks även om SS fasas ut (inerta/ofarliga på officiell);
  ta hellre bort **Natar-featuren** + tagga en `ss-stable`-punkt än att rensa spridda selektorer.
- **2026-06-01** — `Tribe` är stabil per konto/server och får seedas från account analysis-cache.
  `GoldClubEnabled` får bara latched-cachas när det är `true`; `false` ska kunna omprövas.
- **2026-06-02** — Hero-resurstransfer vid resursbrist (official-only, opt-in `HeroResourceTransferEnabled`,
  default OFF). När en upgrade är blockad av resurser klickar boten `.inlineIcon.resource.transfer`
  (öppnar `div.resourceTransferDialog`), låter Travian auto-fylla beloppen och klickar "Transfer selected"
  (`.actionButton.preSelected button`). Sidan laddas om → upgrade-loopen återanalyserar. Försöks **före**
  NPC-trade när båda är på. Integrerat på samma 5 ställen som `TryNpcTradeForConstructionAsync`.
- **2026-06-02** — Hero inventory-resurser (item145/146/147/148 → `.count`) läses från `/hero/inventory`
  via `ReadHeroInventoryResourcesAsync`. Visas i Hero-fliken (4 fält + Refresh), valbar post-login-läsning
  (`PostLoginAnalyzeHeroInventory`, default ON). Adventures-kortet flyttat upp i Settings-kortet.
- **2026-06-02** — Hero inventory cachas i minnet (statisk dict keyed `account|baseUrl`, som hero-attribut-
  snapshoten). Uppdateras vid varje full läsning och efter en transfer (drar av de auto-fyllda beloppen, ingen
  extra navigering). Statiskt event `TravianClient.HeroInventoryUpdated(account, resources)` → Desktop
  uppdaterar Hero-fältens UI live (filtrerat på aktivt konto, avregistreras i `OnClosed`). Ingen
  proaktiv "skippa om tomt"-logik ännu — Travian visar bara `.transfer`-ikonen när hjälten har resurser,
  så tom inventory ger naturligt ingen transfer.
- **2026-06-02** — Proaktiv grind (nivå 3) före transfer: läser bristen (kostnad från transfer-ikonens
  `targetResourceAmount`-onclick minus lager `#l1..#l4`) och jämför mot cachad inventory. Täcker hjälten
  inte hela bristen för *alla* korta resurser → hoppa över utan att öppna dialogen (undviker att spendera
  hjälteresurser på en transfer som ändå inte låser upp uppgraderingen). Saknas cache eller går datan inte
  att läsa → fall tillbaka till reaktivt beteende (öppna dialogen).
- **2026-06-02** — `UpgradeAllResourcesToLevelAsync` defers immediately on `BlockedByResources` after
  hero-transfer/NPC attempts fail. The page's resource ETA is returned via `queue_wait_seconds` instead of
  scanning the remaining resource slots, which prevents long-running resource tasks from log-spamming while
  waiting for production.
- **2026-06-02** — Hero resource-transfer detection on official build pages treats `upgradeBlocked` +
  `.inlineIcon.resource.transfer.fillUp` as enough proof of resource shortage. The transfer dialog can render
  either as `div.resourceTransferDialog` or as `#dialogContent` with the "Transfer resources" header; selectors
  must support both before clicking "Transfer selected".
- **2026-06-02** — After a successful hero resource-transfer during `UpgradeAllResourcesToLevelAsync`, re-check
  the same build slot page in place. Do not bounce through `dorf1.php` unless the current page is no longer the
  expected slot; Travian's `&reload=auto` page is already the right context once the dialog closes.
- **2026-06-02** — Daily Quests auto-collect is official-only and opt-in
  (`AutoCollectDailyQuestsEnabled`, default OFF). The 16s dashboard refresh checks the current page for
  `a.dailyQuests .indicator` with `!`, queues `collect_daily_quests`, opens the React dialog, clicks
  `collectRewards`, collects clickable `button.collect.collectable` rewards, then closes the dialog.
- **2026-06-02** — Hero attribute priority default is flavor-aware: official servers default to
  `resources,fighting_strength,offence_bonus,defence_bonus`; SS/legacy keeps the old combat-first order.
  `hero_stat_priority` is account-scoped, so explicit user reordering is preserved per account.
- **2026-06-03** — Hero resource-transfer dialog resyncs hero inventory cache from the dialog's live
  `.count` values before clicking "Transfer selected", then deducts the transferred amounts after confirm.
  This keeps Hero-tab inventory and the next proactive transfer decision closer to Travian's live state
  without navigating to `/hero/inventory`.
- **2026-06-03** — Session/action pacing added. Session pacing is Desktop-only via `SessionPacer`
  (`DispatcherTimer`, global `bot.json` keys, default ON) and controls continuous-run sleep/logout/wake.
  Action pacing is shared through `BotOptions` + `ActionPacer` and is applied at central low-risk points:
  before continuous-loop tasks, after `GotoAsync`, between manual farm sends, and as a floor for loop waits.
  The Settings section is named "Bot behavior". The old visible "Act more human" checkbox was removed, but `human_like_enabled` remains in
  `BotOptions`/Worker for backward compatibility with existing manual-farm delay behavior.
- **2026-06-03** — Official hero HP on `/hero/attributes` is read from the rendered health
  `attributeBox`; the percent text can contain bidi formatting marks between digits and `%`, so the
  parser strips Unicode direction marks before parsing. Wait for a health row with a percent value,
  not only a generic health icon.
- **2026-06-03** — Continuous-loop construction scheduling no longer lets Desktop's cached build-queue
  snapshot be final authority when a construction task is due now. If no inline `queue_wait_seconds`
  defer is active, the ready task is allowed to reach Worker, where `CheckQueueOrDeferAsync` re-reads
  live Travian Plus + active slots before clicking or deferring. This avoids stale/unknown Plus state
  blocking the possible second construction slot.
- **2026-06-03** — Logout på official T4.6: logout-kontrollen är ett `<a>` **utan href** och utan text
  (bara en SVG) som kör `Travian.api('auth/logout')` via `onclick`. Gamla `LogoutTriggers`
  (`a[href*='logout']`, `a:has-text('Logout')`) matchade den **inte** → boten föll tillbaka till legacy
  `/logout.php`. Additivt tillagt: `a[onclick*='auth/logout']`, `a.layoutButton.logout`, `a.logout[onclick]`.
  Kontrollen är dessutom ofta **gömd bakom en meny** → en vanlig `ClickAsync` faller på actionability och
  timeoutar (15s × retries). Därför **dispatch:as klick-eventet** (`DispatchEventAsync(selector, "click")`)
  i `TryTriggerLogoutAsync`, vilket kör elementets egen `onclick`/navigering utan att vänta på synlighet
  (funkar även för SS href-länkar). Utloggning bekräftas **positivt** via `WaitForLoggedOutAsync` (väntar
  in login-scenen: `#loginScene`/`body.login`/lösenordsfält) i stället för "frånvaro av inloggad-markörer"
  — en sida som fortfarande renderar lästes annars som falsk utloggning.

- **2026-06-03** - Hero low-HP defer is capped to 30 minutes even when regen math estimates many hours.
  Manual attacks/adventures can change HP/status outside the bot, so the loop must periodically re-read live
  hero state instead of sleeping until the full theoretical regen time. Official hero-away ETA parsing now
  prefers `.heroState .timerReact` before whole-page timer text so `Arrival in 00:21:15 at 13:45` uses the
  countdown, not another page clock.
- **2026-06-03** - Hero continuous loop treats `0` adventures as an idle polling state, not a blocked group.
  If Hero is enabled, keep it enabled and only enqueue `hero_manage` after the early adventure-count refresh
  reports `> 0`; this avoids spam without overriding the user's toggle.
- **2026-06-03** - Pause state owns the Start/Pause button label. After a graceful pause request finishes,
  keep the button as `Start bot` while `LoopStopRequested`/`QueueStopRequested` is still set, even if
  continuous mode remains toggled on.
- **2026-06-03** - `[pacing]` log lines are Clean-mode verbose noise. Keep important session sleep/wake
  milestones readable without the `[pacing]` tag so Clean mode shows only those.
- **2026-06-03** - Hero HP regen default is 40%/day. Hero settings are account-scoped and persist immediately
  from the Hero panel; the global Settings popup must save only global keys so Cancel/Save/close cannot
  overwrite account-scoped Hero values during the following config reload.
- **2026-06-03** - Gold/silver read fallback with cached values is `[resources:verbose]`, not an alarm. A missing
  cache still remains visible as an actionable alarm.
- **2026-06-03** - Session sleep is an offline state. While `SessionPacer` is sleeping, browser/automation
  actions are blocked and only `Run now` may wake execution; queue-only actions may still add/edit pending
  work for the next wake. Bot behavior pacing settings are account-scoped. Defaults: sleep 30 min,
  variation 30%.
- **2026-06-03** - Daily Quests collect must treat `a.dailyQuests .indicator` as a cheap queue signal only.
  The React dialog is final authority: wait for dialog/buttons to render visibly, pace before the first collect click,
  and after closing wait for the topbar signal to clear. If it remains `!`, reload the current page once so the
  16s refresh does not repeatedly queue stale 0-reward `collect_daily_quests` tasks.
- **2026-06-03** - Questmaster `/tasks` auto-collect must wait for visible rendered tab/buttons before clicking.
  React may place task elements in the DOM before they are laid out; only visible enabled `Collect` buttons should be clicked.
- **2026-06-03** - Session-sleep logout hardening. (1) The ~16s background resource-snapshot tick is now gated on
  `IsSessionSleeping` (`ShouldRunBackgroundResourceSnapshotRefresh`), so sleep can no longer auto-relogin via
  `ensure-logged-in`. (2) `HandleSessionPacingSleepStartingAsync` flips `_isLoggedIn/_browserSessionLikelyOpen/
  _inboxAutoEnabled` false BEFORE logout (mirrors `ResetForAccountSwitchAsync`) so a thrown logout no longer leaves
  the gates stale-true. (3) `LoginStateAsync` does the cheap `login.php` URL check before `TryDismissContinuePromptAsync`,
  and `FindContinuePromptLocatorAsync` passes the capped `timeoutMs` to `InnerTextAsync`/`GetAttributeAsync`. Root cause
  of the observed `session logout failed: Timeout 15000ms exceeded`: element-text reads fell back to the 15s default
  action timeout while the page was navigating (logout redirect + a concurrent ungated background refresh).
- **2026-06-03** - `BotConfigStore` file I/O is now serialized behind a static `FileIoLock` with FileShare.ReadWrite
  reads and a short IOException retry. bot.json/settings.json were read+written from many concurrent contexts (UI
  dispatcher, continuous loop, background `Task.Run` refreshes) with unsynchronized `File.ReadAllText`/`WriteAllText`,
  causing intermittent `The process cannot access the file ... because it is being used by another process`.
- **2026-06-03** - Build/upgrade render-timing hardening. Both `UpgradeBuildingToLevelAsync` and
  `UpgradeBuildingToMaxAsync` now call the existing `EnsureExpectedBuildSlotPageAsync` (slot-URL check +
  `WaitForBuildSlotContextAsync` on `#build/#contract/.upgradeBuilding/...` with one reload retry) AFTER
  `GotoAsync(BuildBySlot)` and BEFORE reading duration/clicking. GotoAsync only awaits DOMContentLoaded, which
  did not guarantee the upgrade button was rendered (slow pages / official `&reload=auto` timer pages); the gate
  is instant when the page is already ready, so the happy path is not slowed. Construct already gates inside
  `ClickConstructBuildingButtonAsync` (left unchanged). Diagnostics: `ClickUpgradeToLevelButtonAsync` now logs
  slot/flavor/url on failure and distinguishes "no selector matched" from a click error; the final
  `could not find 'Upgrade to level N'` message includes `flavor`, `url`, and the analyzer candidate summary.
- **2026-06-03** - Empty building slots no longer report the misleading `could not find 'Upgrade to level N' button.
  Reason: CanUpgrade (Detected candidate 'construct building ...')`. `DetectBuildPageStateAsync` now returns
  `EmptyConstructionSlot` (structural, language-independent: `[id^="contract_building"]` present and no
  `.upgradeButtonsContainer`/"upgrade to level"), and both `UpgradeBuildingToLevelAsync`/`UpgradeBuildingToMaxAsync`
  return a clear "slot is empty — construct the building first" message. Root cause: `upgrade_building_to_level` was
  queued for an unbuilt slot whose page shows a construction menu, not an upgrade button.

- **2026-06-03** — UI-tema Fas 0 (förberedande, utseendeneutralt). Ny `Themes/Palette.xaml` med namngivna
  `SolidColorBrush`-tokens satta till **exakt dagens ljusa hex** (surfaces/borders/text/accent/semantik/
  tooltip + `ShadowColor`). Registrerad i `App.xaml` före `Tooltips.xaml`. De fyra befintliga
  `Themes/`-ordböckerna (`Buttons`, `Toggles`, `Badges`, `Tooltips`) pekar nu om sina färger via
  `DynamicResource` till tokens. Mål: en enda plats för färg så ett mörkt tema senare kan vändas där.
  Inga visuella eller funktionella ändringar (DynamicResource resolvar upp till `Application.Resources`).
  Färger i `MainWindow.xaml`/övriga XAML + code-behind är **inte** tokeniserade ännu (Fas 1+).
- **2026-06-03** — UI-tema Fas 1 klar (fortfarande utseendeneutralt). **Alla** färgliteraler i XAML är nu
  flyttade till `Palette.xaml`-tokens via `DynamicResource` — `MainWindow.xaml` (0 kvar), alla `Views/*`
  och alla dialogfönster. `Palette.xaml` är enda stället med hex. Tokens utökade med semantiska grupper
  (success/warning/info/danger), terminal, resursfält-tinter och slate-accenter (Settings/Accounts).
  Värden oförändrade → ingen visuell/funktionell skillnad. **Kvar till mörker-vändningen:** (1) sätt mörka
  värden i `Palette.xaml`, (2) ~151 färgsättningar i **code-behind/VM** (`MainWindow.LoopIndicators.cs`,
  `TroopTrainingViewModel.cs`, m.fl.) använder fortfarande literaler och måste läsa via `FindResource`.
- **2026-06-03** — UI-tema Fas 2 klar (utseendeneutralt). Ny `Themes/ThemeColors.cs`-hjälpklass läser
  palett-penslar/-färger per nyckel (`ThemeColors.Brush(key)` / `.Get(key)`, magenta-fallback vid typo).
  Alla `Color.FromRgb(...)`/`BrushConverter`-literaler i code-behind/VM är nu ersatta med token-uppslag
  (LoopIndicators, login/logout, nav-selektion, AutomationLoop-dot, Logging, SessionPacing, Farming,
  QueueUi-popout, AppDialog, BuildingConstructChoice, CatapultWave, ResourcesViewModel, samt badge-
  properties i `TroopTrainingViewModel`/`TroopTrainingBuildingOption` som nu returnerar `Brush` i stället
  för hex-`string`). `Palette.xaml` utökad med code-behind-nyanser (amber/waiting/emerald/gold m.fl.).
  Namngivna WPF-penslar (`Brushes.SeaGreen/DarkOrange/Green/Red/Gray/...`) lämnades — fungerar på
  både ljus/mörk. Bygger rent, 40 Desktop-tester gröna, utseende oförändrat. **Allt är nu tokeniserat
  (XAML + C#) — mörker-vändningen är ett en-fils-byte i `Palette.xaml`.**
- **2026-06-03** — UI-tema Fas 3: **mörkt tema applicerat.** `Palette.xaml` vänd till mörka värden
  (bakgrund #0D1117, kort #161B22, ljus text, **grön accent #238636** för login/start/toggles enligt
  konceptbilden). Ny `Themes/BaseControls.xaml` med implicita bas-stilar (TextBlock/Label/CheckBox/
  RadioButton/TextBox/PasswordBox/ComboBox/DataGrid/TabControl/TabItem/Window) ger mörka defaults där
  explicit färg saknas — annars blir WPF:s svarta default-text osynlig mot mörk bakgrund. Registrerad i
  `App.xaml` efter Palette. `ToggleSwitchStyle` fick en `Foreground`-setter (explicit Style ersätter den
  implicita). För att återgå till ljust: återställ de ljusa hex-värdena i `Palette.xaml` (git-historik).
  **Kvar/known limitations (kräver om-templating, ej gjort):** native WPF-chrome är inte fullt temad —
  context-menyer och scrollbars använder fortfarande system-(ljus)färger; default-Button/TabItem-mallar
  (Aero2) hedrar bara `Background` delvis så de kan se något ljusa ut. Detta är nästa polering om ett
  komplett mörkt tema önskas.
- **2026-06-03** — UI-tema: **ComboBox mörk-templatad.** Default Aero2-mallen renderade ljus ruta +
  ljus popup med osynlig text. `BaseControls.xaml` har nu full `ComboBox`- + `ComboBoxItem`-mall (mörk
  ruta, pil, mörk dropdown-popup `SurfaceBrush`, highlight `SelectedRowBgBrush`) via tokens. Stängd ruta
  och lista är nu läsbara i mörkt läge.
- **2026-06-03** — UI-tema: **native-chrome polerad mörk.** Platt mörk `Button`-mall i `Buttons.xaml`
  (honererar per-knapp Background/BorderBrush via `TemplateBinding`; hover/press/disabled via opacity —
  ersätter Aero2-gradienten). Mörk `TabItem`-mall i `BaseControls.xaml` (markerad flik = grön underlinje
  + `SurfaceBrush`). Ny `Themes/ScrollBars.xaml` (flat mörk track + rundad thumb, inga pilknappar),
  registrerad i `App.xaml`. Enkla mörka `ContextMenu`/`MenuItem`-defaults. Bygger rent, 40 tester gröna.
  Mörka temat är nu i stort sett enhetligt (Slider/DatePicker o.d. om-templatas vid behov).
- **2026-06-03** — UI-tema: **resterande inputs mörk-templatade.** Ny `Themes/Inputs.xaml` med mallar för
  `ProgressBar` (token-bar/-track; storage-bars binder fortfarande egen Foreground/Background), `Slider`
  (grön thumb), `CheckBox` (mörk ruta, grön bock) och `RadioButton` (grön prick). De enkla CheckBox/
  RadioButton-stilarna flyttades från `BaseControls.xaml` hit. `ToggleSwitchStyle`-checkboxar behåller
  sin egen stil. Registrerad i `App.xaml`. Bygger rent, 40 tester gröna. **Mörkt tema komplett.**
- **2026-06-03** — UI-tema: dialog-/fönster-fixar. (1) Implicit `Window`-bakgrund slog inte igenom för
  separata dialog-fönster (medan implicit TextBlock gjorde det) → klientytan blev vit. Lade därför
  **explicit** `Background="{DynamicResource AppBackgroundBrush}"` på alla dialog-`Window`-rötter.
  (2) `BusyOverlayControl` hade `Background="White"` (namngiven, missades av hex-passet) → `SurfaceBrush`.
  (3) **Mörk OS-titelrad** för alla fönster via DWM: `App.OnStartup` registrerar en class-handler på
  `Window.Loaded` som sätter `DWMWA_USE_IMMERSIVE_DARK_MODE` (attr 20, fallback 19) — kosmetiskt,
  fel sväljs. Detta tar "ljus programram". Bygger rent, 40 tester gröna.
- **2026-06-03** — UI-tema: **rotorsak till ljusa dialog-knappar** — `Buttons.xaml`/`Toggles.xaml`/
  `Badges.xaml` var bara mergade i `MainWindow.xaml`s resurser, så separata dialog-fönster fick WPF:s
  ljusa default-knappar. Flyttade dessa tre till `App.xaml` (app-brett); tog bort `Window.Resources`-
  blocket i `MainWindow.xaml` (keyed-stilar resolvas via StaticResource upp till App). Nu är alla
  dialog-knappar mörka (building-popups, test functions, settings, support, bekräftelser). Dessutom:
  implicit `ListBox`/`ListBoxItem` (Accounts-listan var vit) och järn-stapeln ljusades upp
  (`IronTextBrush` #94A3B8 → #8FB6E0) för bättre kontrast. Bygger rent, 40 tester gröna.
- **2026-06-03** — UI-tema: fler dialog-/grid-fixar. App-brett mörkt `DataGrid` (+ `DataGridColumnHeader`,
  `DataGridCell`: rader/alternering/headers/markering) i `BaseControls.xaml` — Server list-popupen hade
  ljus grid. Queue "Pop out"-fönstret (skapas i kod) fick `Background = AppBackgroundBrush` + marginal.
  Alarm-loggens bakgrund matchar nu Log-loggen (`AlarmBgBrush` #110D0D → #020617). Järn-kortets/-staplens
  bakgrund ljusades (`IronCardBgBrush` #1A2230 → #243A52) så den inte blandar in i app-bakgrunden.
  OBS: bygget kan bara slutföras när den körande appen är stängd (DLL-lås), kompileringen är ren.
- **2026-06-03** — UI-tema: tre småfixar. (1) `ProgressBar` indeterminate-animationen (loading) slutade
  röra sig — min custom-mall hade bara determinate. Tog bort mallen, behåller bara färg-setters så WPF:s
  default-mall (med animation) används; storage/farmlist binder egen Foreground/Background. (2) Delete-
  knappen i Server list klipptes (implicit `DataGridCell`-padding + knapp-marginal > kolumnbredd) →
  kolumn 90→110, knapp `Margin=0 Height=26` centrerad. (3) Reset settings-knappen hade vit rand
  (`BorderBrush=InkBrush`, ljus i mörkt tema) → `PrimaryButtonBrush` (grön, matchar bakgrund). Bygger rent, 40 tester gröna.

- **2026-06-04** — UI-cleanup (utseende/layout, inget bot-beteende ändrat). (1) Dashboard topbar-korten
  Session/Time har nu fast `Width` (210/180) i stället för `MinWidth` så de inte hoppar när status/klocka
  uppdateras. (2) Villages-listans mellersta spacer-kolumn `*`→`Auto` (header + rad) så Village-kolumnen får
  all slack (längre bynamn, mindre klipp) och Pop/Coords/Capital trycks åt höger; shared size groups
  oförändrade. (3) NPC trade exponeras som en master-toggle på Dashboard "Auto settings" (under Allow gold
  spending) via ny `TroopTrainingViewModel.NpcTradeMasterEnabled` (get = `IsAnyNpcTradeEnabled`, set = sätter
  både `NpcTradeEnabled` och `NpcTradeConstructionEnabled`). Per-feature-toggles/trösklar ligger kvar på
  NPC/Trade-fliken, nåbar via kugghjuls-knappen som flyttades till NPC trade-raden. (4) Tog bort
  "Continuous loop uses N enabled group(s)…"-texten i Auto loop-rutan; `UpdateAutomationLoopSummaryText`
  uppdaterar nu bara kolumnlayouten. (5) Sidebar "Building"→"Buildings" (flik-headern var redan "Buildings").
  (6) Buildings-fliken: ny tom platshållar-ruta "Construction settings" i högerkolumnen (260px) i
  `BuildingsPanel.xaml`. Bygger rent, 40 Desktop-tester gröna.

- **2026-06-04** — Status-state efter reset/kontobyte. `RequestLoopStop`/`RequestQueueStop` rensas bara
  vid start av en ny körning, så en reset/kontobyte (som stoppar automation) lämnade flaggorna satta och
  `UpdateExecutionStateIndicator` visade "State: paused" trots idle/utloggad. Fix: `ClearAccountScopedUiState()`
  anropar nu `ClearLoopStopRequest()`/`ClearQueueStopRequest()` före `UpdateExecutionStateIndicator()`
  (täcker både programreset och `ResetForAccountSwitchAsync`). Statenamn oförändrade. Den vanliga
  pause-vyn (flaggor satta + pending köobjekt → "paused") påverkas inte eftersom kön finns kvar då.
- **2026-06-04** — Stop-knappen är nu röd-tonad (`DangerBg/Border/Text`, samma princip som gula Pause) och
  öppnar en bekräftelse via `AppDialog.ShowCustom` ("Cancel"/"Stop", default Cancel) innan hard-stop, med
  texten att stopp även rensar aktiv kö för alla byar. Skyddar mot oavsiktligt stopp. `StopBotButton_Click`
  kör stop endast vid Stop-valet.

- **2026-06-04** — Village-identitet & refresh (rename-säkring). (1) Desktop-mergen
  (`MainWindow.Dashboard.Villages.cs`) nycklar nu byar på stabilt id i stället för namn: `GetVillageKey`
  parsar `newdid` ur switch-URL:en (fallback coords → namn). En omdöpt by mappar därför till sin tidigare
  cache-post (behåller Url/coords/population) i stället för att dyka upp som en ny by. (2) Worker
  `ReadVillagesPreferCacheAsync`-mergen matchar `prior` på `newdid` (ny `TryParseNewdid`) före namn, så
  coords inte tappas och rename känns igen som samma by.

- **2026-06-04** — 16s-refreshen plockar nu upp byändringar från sidan den står på (ingen extra navigering).
  Rotorsak (verifierad mot `temp_build_out/DOM/dorf1_multiple_villages.txt`): den befintliga current-page-
  avläsningen `ReadVillagesFromCurrentPageAsync` letade bara `a[href*="newdid="]`, men **official T4.6**
  renderar bylistan som `div.listEntry.village[data-did]` med `href="#"` (id i `data-did`, inte i någon
  newdid-länk). Därför hittade sidebar-läsningen 0 byar på official och föll tillbaka på cache → namnbyten/
  nya byar syntes aldrig. Additivt tillägg (SS-newdid-grenen körs först, official som fallback): parsa
  `.listEntry.village[data-did]` → bygg `dorf1.php?newdid=<did>`, läs `.name`, coords ur
  `.coordinateX/.coordinateY` (ny `parseSignedInt` som strippar bidi-marks + normaliserar U+2212-minus),
  aktiv-population ur `.population span`. `VillagesCacheTtl` 60→15s så prefer-cache-läsningen faktiskt läser
  om den (navigeringsfria) sidebaren varje tick. Desktop: `RefreshResourceSnapshotForUiAsync` anropar nu
  `TryUpdateDashboardVillagesFromStatus(status)` efter `ApplyResourceStatusToUi` — uppdaterar dropdown +
  Dashboard **bara** när byset ändrats (signatur-jämförelse) och **aldrig** när sidan saknar läsbar by-info
  (skriver inte över). Signaturen nollas vid kontobyte. Den tidigare manuella "Refresh villages"-knappen +
  `RefreshAccountVillagesAsync`-plumbingen är borttagen (behövdes inte; befintliga avläsningar räcker när de
  läser rätt markup). Worker bygger rent, 323 Worker-tester gröna; Desktop-bygget kräver att appen stängs
  (DLL-lås), kompileringen är ren.

- **2026-06-04** — 16s-refreshen uppdaterar nu **adventures** och **aktiv bys population**, plus UI-fixar.
  (1) Adventures: `VillageStatus` fick `int? AdventureCount`; `ReadCurrentVillageResourceStatusAsync` läser
  hero-indikatorn från current page via befintliga `ReadHeroSidebarStatusAsync` (billig, ingen navigering)
  och sätter `AdventureCount` (null när indikatorn inte hittas → skriv inte över). Desktop applicerar i
  `RefreshResourceSnapshotForUiAsync` via `ApplyHeroAdventureAvailability` så Dashboard `Adv:` och Hero-
  fliken (båda binder `HeroVm.AdventureCountText`) hålls i synk i auto-loop. (2) Population: official renderar
  aktiv bys population i `#sidebarBoxActiveVillage div.population > span` (ej i list-entryn) — official-grenen
  i `ReadVillagesFromCurrentPageAsync` läser nu därifrån. Desktop `BuildMergedVillageSelectionItems` tar
  `activeVillageName` och låter **aktiv** by använda inkommande (sanna) population (`preferExistingPopulation:
  false`) så cache skrivs över; övriga byar behåller visat värde. By-signaturen inkluderar nu population så
  ändringen triggar omritning. (3) Storage-text: `ResourcesViewModel.FormatCurrentMaxText` ger nu
  "1 500 / 25 000" (mellanrum runt `/`). (4) `BuildingSlotActionsWindow` "Upgrade to max": ersatte den
  template-ersättande `<Button.Style>` (saknade BasedOn → föll till Aero2-mallen med ljus hover) med direkta
  Background/Foreground/BorderBrush-attribut (warning-tokens) så den ärver den platta mörka knappmallen.
  (5) SupportWindow: mailformuläret borttaget (+`SendButton_Click`), "Create diagnostics file" är
  `IsEnabled="False"` (ses över senare), Discord = blå (`Info*`-tokens) och GitHub = grön (`Success*`-tokens,
  samma fade som Start bot). Worker bygger rent, 323 Worker-tester gröna; Desktop kompilerar rent
  (full build/Desktop-tester kräver att appen stängs pga DLL-lås).

- **2026-06-04** — Externt startade uppgraderingar visas nu i UI + ny Village settings-popup.
  (1) `VillageStatus` fick `IReadOnlyList<ActiveConstruction>? ActiveConstructions`, populerad från Travians
  egen bygg-lista via befintliga `ReadActiveConstructionsAsync(allowNavigationToBuildings:false)` i både
  full-läsningen (`ReadCurrentVillageStatusAsync`, login/bybyte) och current-page-läsningen
  (`ReadCurrentVillageResourceStatusAsync`, 16s-tick). Desktop matchar dem till slots på normaliserat
  namn + (mål − 1) via `BuildExternalUpgradeTargetsBySlot` och sätter `PendingTargetLevel` — byggnader i
  `PopulateBuildingsTab` ("Level X (Y)") och resursfält i `BuildResourceRows` ("(Y)"), samma format som
  programmets egna köade uppgraderingar. Bara uppgraderingar av befintliga slots (construct/nya hoppas över).
  Endast **entydiga** matchningar visas: kan en construction matcha fler än en slot (t.ex. flera croplands på
  samma nivå) hoppas den över i stället för att gissa fel fält. (2) Ny **Village
  settings**-knapp i Dashboard-headern (vänster om Reset program) öppnar `VillageSettingsWindow` — en DataGrid
  med Village/Pop/Coords + checkbox-kolumner Hero res/NPC/Build troops/Upgrade troops/Farming
  (`VillageSettingsRow`). Checkboxarna är ej kopplade till automation ännu; fönstret är grunden för
  per-by-inställningar framåt. Worker bygger rent, 323 Worker-tester gröna; Desktop kompilerar rent
  (full build/Desktop-tester kräver att appen stängs pga DLL-lås).

- **2026-06-05** — Multi-village steg 1: per-by "enabled for automation" + persistens (ingen kö-/loop-
  ändring ännu). Ny `VillageSettingsStore` (Desktop/Services, samma konto-scopade mönster som
  `BotConfigStore`: projectRoot + active-account-provider + FileIoLock-retry) sparar per-konto i ny fil
  `config/accounts/<account>/villages.json` (helper `AccountStoragePaths.VillageSettingsPath`). Post per by:
  `{ key, name, coordX, coordY, isCapital, isEnabled, lastSeenUtc }`, nycklad på samma stabila by-nyckel som
  Desktop redan använder (`GetVillageKey`, newdid→coords→namn) så rename behåller valet. `Merge` (nya byar:
  capital ON, övriga OFF; kända byar behåller `isEnabled`, identitet uppdateras, tar **aldrig** bort byar som
  saknas i en partiell sid-läsning, skriver bara vid faktisk ändring), `GetEnabled` (okänd by → default =
  isCapital), `SetEnabled` (idempotent no-op när värdet är oförändrat → togglens Checked/Unchecked-handler
  spammar inte filen vid varje listombygge), `InvalidateCache` (anropas i `ClearAccountScopedUiState`;
  villages.json **raderas inte** vid kontobyte — det är just minnet vi vill behålla). `VillageSelectionItem`
  fick settbar `IsEnabledForAutomation` (minimal INotifyPropertyChanged) som tvåvägsbinds mot en ny toggle-
  kolumn ("Auto") i Dashboard Villages-listan (`ToggleSwitchStyle` + info-ikon). `BuildMergedVillageSelectionItems`
  (båda overloads) anropar `ApplyVillageEnabledState` (merge + applicera). Toggle är **ännu inte** kopplad till
  worker-loop/kö (steg 3–4). Skiljt från `VillageSettingsWindow`/`VillageSettingsRow` (per-feature-rutnät, också
  oanslutet) — villages.json kan bli hemvist för de flaggorna senare. Build rent, 47 Desktop-tester gröna.

- **2026-06-05** — Multi-village steg 2: **en köfil per konto** (lagring + migrering; ingen loop-/rotations-
  ändring ännu). Tidigare global `config/queue.json` (delad av alla konton) flyttas till
  `config/accounts/<account>/queue.json` (helper `AccountStoragePaths.AccountQueuePath` +
  `LegacyGlobalQueuePath`). `JsonQueueStore` fick en `Func<string>`-konstruktor; `_queuePath`/`_lockPath`
  är nu **computed properties** som resolvar providern per operation (storen har ingen minnescache av kön →
  byte av aktivt konto pekar automatiskt om till rätt fil, inget att invalidera). Den gamla `string`-
  konstruktorn delegerar till providern → Worker-DI (`Program.cs`, fortf. global, kontolöst fristående
  runner) och tester oförändrade. Desktop konstruerar storen med `() => AccountQueuePath(root,
  activeAccount)`. Ny `QueueMigration` (Desktop/Services) flyttar vid start ett ev. gammalt globalt
  queue.json → aktiva kontots fil (klobbrar inte befintlig), gammal fil → `.bak`, körs före startup-
  clear/recover. `AccountDeletionService` städar redan hela konto-katalogen (queue.json medräknad). Val
  bekräftat med användaren (ändrat från tidigare "fil per by": en by är en payload-egenskap på tasken,
  rensa-per-by = enkelt filter). UI-filtrering per by + sluta-rensa-kön-vid-bybyte ligger i steg 3 (när
  loopen blir by-medveten). Build rent, Worker 324 + Desktop 50 tester gröna.

- **2026-06-05** — Multi-village steg 3: by-rotation i den **manuella kön** + kö-vy per by (rör INTE
  continuous-loopens bräckliga sekvenslogik; autonom rotation = steg 4). Ny `QueueVillageRotation`
  (Desktop/Services, ren/testad): dränerar en bys redo-tasks innan rotation till nästa by; när nuvarande
  by bara har deferred tasks → gå vidare till nästa by (användarens rotationsregel). Bevarar scheduler-
  ordningen (priority/FIFO) inom byn — ändrar bara VILKEN by som töms först. By-nyckel ur task-payloadens
  `TargetVillage*` via befintliga `GetVillageKey` (nya helpers `GetQueueItemVillageKey/Name` i
  `MainWindow.Dashboard.Villages.cs`); tasks utan by = en "default"-grupp. `ExecuteQueuedItemsNowAsync`
  (AutoQueue) använder selektorn via fält `_autoQueueRotationVillageKey` (nollas vid körningsstart, loggar
  `ROTATE to village`); varje task växlar fortf. till sin egen by via `BotTaskRunner` — rotationen håller
  bara runnern på en by i taget i stället för att interfoliera byar per global priority/FIFO. `StopAndClear
  ForVillageChangeAsync` → `StopForVillageChangeAsync`: stoppar aktiv körning men rensar **inte** längre
  kön (andra byars köade arbete överlever bybyte; med en kö/konto + by-taggade tasks ska de inte tappas).
  Kö-UI fick en "Village"-kolumn (aktiv + historik + popout) från payloadens `TargetVillageName`
  (`QueueItemRow.VillageName`). Enable-togglarna är **ännu inte** kopplade till exekvering (steg 4). Build
  rent, Worker 324 + Desktop 54 tester gröna.

- **2026-06-05** — Multi-village: Auto-toggeln **flyttad** från Dashboard till Village settings-popupen
  (spara plats på Dashboard, användarens önskemål). Dashboard Villages-listan är tillbaka till
  Villages/Pop/Coords (ingen toggle-kolumn). `VillageSettingsWindow` fick en **"Auto"-checkboxkolumn
  längst till vänster** (vänster om Village) wired till `VillageSettingsStore`: `VillageSettingsRow`
  bär nu `IsEnabledForAutomation` (INotifyPropertyChanged) + `KeyInfo` (stabil by-nyckel); fönstret tar
  en `Action<VillageSettingsRow>`-callback och prenumererar på radens PropertyChanged → persist via
  `PersistVillageEnabledFromSettingsRow` → `VillageSettingsStore.SetEnabled`. Seedas från
  `GetEnabled(keyInfo)` när popupen öppnas. Övriga checkboxkolumner (Hero res/NPC/…) fortf. oanslutna.

- **2026-06-05** — Multi-village steg 4a: enable-togglarna **kopplade till exekvering** (filter; per-by
  runtime-generering/autonom loop-rotation = kvarvarande 4b, kräver ändring i bräcklig loop + live-test).
  Ny `VillageSettingsStore.IsEnabledByKey(key, defaultIfUnknown)` (okänd/by-lös → default true). Ny
  `IsQueueItemVillageEnabled(QueueItem)` (by-lösa tasks alltid tillåtna). `QueueVillageRotation.SelectNext`
  tar nu en `isVillageEnabled`-predikat och hoppar över inaktiverade byars tasks; AutoQueue skickar
  `IsQueueItemVillageEnabled`. Continuous-loop-selektorn (`SelectNextQueueItemForContinuousLoop`,
  ren urvalslogik — ej sekvenslogiken) filtrerar både utility- och gruppitems på
  `IsQueueItemVillageEnabled`. Effekt: en inaktiverad by:s köade tasks körs inte (manuell kö + continuous);
  by-lösa/legacy-tasks opåverkade. Build rent, Worker 324 + Desktop 56 tester gröna.

- **2026-06-05** — Multi-village steg 4b: **by-rotation för continuous-loopens Construction-grupp**
  (autonomt läge). Tidigare höll loopen hela Construction-gruppen i strikt köordning — väntade en by:s
  bygge på resurser blockerades **alla** byars bygge. Nu roterar den per by: ny generisk
  `QueueVillageRotation.SelectByVillageRotation(items, villageKeyOf, perVillageSelector, ref key)`
  grupperar per by (bevarad ordning), dränerar nuvarande by och går vidare till nästa by när den
  inte ger något. Construction-grenen i `SelectNextQueueItemForContinuousLoop` använder den med
  `SelectNextConstructionQueueItem` som per-by-väljare (strikt ordning + slot-regler kvar inom byn);
  nytt fält `_continuousConstructionRotationVillageKey` (nollas vid loopstart). `HasReadyContinuous
  ConstructionItem` använder samma (+ `IsQueueItemVillageEnabled`-filter). **Rotorsak till att det
  fungerar utan per-by inline-wait-dict:** en construction-defer kör redan `MarkQueueItemDeferred`
  (sätter item-NextAttemptAt per item) → per-by-väljaren hoppar över den deferred byn. Det enda som
  blockerade andra byar var den **globala** inline-wait-gaten i `IsConstructionGroupReady` — den är nu
  **borttagen som gate** (fältet `_constructionInlineWaitUntilUtc` behålls **bara** för dashboard-
  nedräkningen `ResolveConstructionGroupRemainingSeconds`). Enkel-by-beteende oförändrat. Troop-
  training/smithy/brewery per by = **ej** gjort (replikerar global config över byar → separat
  produktbeslut). Build rent, Worker 324 + Desktop 59 tester gröna.

- **2026-06-05** — Multi-village: **vald by (vy) frikopplad från aktiv by (där boten jobbar)** + Switch
  village-knapp + kö-filter per by. (branch `multiple_villages`). Tidigare navigerade dropdown-val
  browsern och kunde stoppa körningen. Nu: **dropdown-val = ren vy/kö-kontext** (`VillageComboBox_
  SelectionChanged` → `ShowSelectedVillageFromCache`, ingen navigering, ingen stop/clear). Ny partial
  `MainWindow.VillageWorking.cs`: per-by-status-cache (`_villageStatusCacheByKey`, fylls vid login/Switch/
  continuous-construction-läsning) så Buildings/resurser för vald by visas utan att navigera; "aktiv
  arbetsby" (`_activeWorkingVillageKey/Name`) = byn i browsern, sätts vid login, vid Switch village, och
  **live när en kö-task börjar köra** (`MarkActiveWorkingVillageFromQueueItem` i `ExecuteSingleQueueItem
  Async`) + från 16s-refreshens `status.ActiveVillage`. Gates så live-refresh inte skriver över vald bys
  vy: `PopulateBuildingsTab` och `ApplyResourceStatusToUi` returnerar tidigt om statusens by ≠ vald by
  (obestämd by → måla, blanka aldrig defensivt). `VillageSelectionItem.IsActiveWorkingVillage` (notify)
  → grön ram runt aktiv by i Dashboard-listan (`SuccessBrush`/`SuccessBgBrush`), återställs vid
  list-ombyggnad (`ApplyActiveVillageHighlight`). Ny **Switch village**-knapp (Dashboard-topbaren,
  vänster om Village settings) → `SwitchToActiveVillageAsync`: när idle navigerar+läser+sätter aktiv;
  när körning pågår seedar den rotationsnycklarna (`_autoQueueRotationVillageKey`/
  `_continuousConstructionRotationVillageKey`) så loopen prioriterar byn (browsern ägs av loopen, ingen
  UI-navigering då). **Queue-tabben** fick en by-dropdown (`QueueVillageComboBox`) synkad med
  huvuddropdownen (`SyncQueueVillagePicker`, suppress-flagga mot loop); `RefreshQueueUi` filtrerar visade
  rader till vald by (`FilterQueueRowsForSelectedVillage`; by-lösa/globala tasks visas alltid; exekverings-
  state-flaggor använder fortf. ofiltrerade listan). Gamla auto-switch-on-select (`SwitchToSelected
  VillageAndRefreshAsync` + debounce-fälten) borttagen. OBS: kompilerar rent men full build/Desktop-tester
  kräver att appen stängs (DLL-lås). Live-verifiering krävs (official + SS).

- **2026-06-05** — Multi-village UX-fixar (branch `multiple_villages`). (1) **Login auto-switchade by**
  (rotorsak till "loggade in på grez men programmet stod på slav"): `ExecuteLoginAndLoadPostLoginSnapshot
  Async` kör `TrySwitchToTargetVillageAsync` på `options.TargetVillageName`, och login byggde options med
  `ApplySelectedVillageToOptions` (ofta stale capital) → browsern navigerade bort från landningsbyn. Fix:
  login använder nu **bas-options** (`LoadBotOptions()`, ingen target) → ingen auto-switch; läser byn
  Travian landar på och sätter dropdownen till den. (2) **Grön ram syns nu direkt efter login**:
  `ApplyPostLoginSnapshot` anropar nu `CacheVillageStatus` + `SetActiveWorkingVillageFromStatus` (full
  status har bylistan så nyckeln kan resolvas). Tidigare sattes aktiv by först när första tasken kördes
  (då från SELECTED, inte browserns by). (3) Aktiv-by-detektering robustare: `SetActiveWorkingVillage`
  resolvar nyckel från namn (`ResolveVillageKeyByName`) när explicit nyckel saknas; `ApplyActiveVillage
  Highlight` matchar på nyckel ELLER namn (resursläsning utan bylista blankar inte ramen). (4) **Switch
  village-knappen lyser grönt** (Start bot-stil, `AccentBrush`) när vald by ≠ aktiv arbetsby
  (`UpdateSwitchVillageButtonHighlight`). (5) Mellanrum mellan Switch village & Village settings
  (`Margin`). (6) Queue-tabbens by-dropdown **borttagen** → ersatt av en "Selected village: xxx"-text
  (`QueueSelectedVillageTextBlock`) synkad med Dashboard-dropdownen; filtret använder fortf. vald by.
  Build rent, Worker 324 + Desktop 59 tester gröna. Live-verifiering krävs.

- **2026-06-05** — Multi-village aktiv-by-fixar (branch `multiple_villages`). (1) **Aktiv by matchas nu
  på NAMN** (primärt) i stället för nyckel — nyckeln kunde skilja mellan dropdown-item och läst status
  (url/newdid-skillnader) vilket gjorde att (a) ingen by blev grön efter login och (b) Switch village var
  *permanent* grön. `ApplyActiveVillageHighlight` matchar namn || nyckel; `UpdateSwitchVillage
  ButtonHighlight` jämför namn. `SetActiveWorkingVillage` ignorerar tomt/"Unknown village" så en dålig
  läsning aldrig nollar markeringen. (2) Switch village lyser grönt (`AccentBrush`) **bara** när vald by
  ≠ aktiv by; töms (ClearValue) annars. (3) **Switch village fungerar nu under körning**: loopen äger
  browsern, så UI:t sätter en *pending switch* som loopen utför mellan tasks via
  `HonorPendingVillageSwitchAsync` (`NavigateToVillageResourceFieldsAsync`) — anropas överst i både
  continuous-loopen och AutoQueue-draineringen. Idle → navigerar direkt (som förut). (4) Grön ram syns
  direkt efter login (kvarstår från förra batchen: `ApplyPostLoginSnapshot` sätter aktiv by). Build rent,
  Worker 324 + Desktop 59 tester gröna. Live-verifiering krävs (särskilt att `ReadActiveVillageNameAsync`
  läser rätt landningsby).

- **2026-06-05** — Multi-village: resurs-uppgraderingar taggades inte med by → "-" i Queue-tabbens
  Village-kolumn. `QueueUpgradeAllResources` (`upgrade_all_resources_to_level`) och
  `EnqueueResourceUpgradeTaskCoalesced` (`upgrade_resource_to_level`) saknade `ApplySelectedVillage
  ToPayload(payload)` före `Enqueue` (byggnads-köandet hade det redan). Tillagt på båda. **Känd
  kvarvarande risk (ej fixad):** upgrade-koalescering (både resurs och byggnad) matchar befintliga
  köposter på slot-id **utan** by-filter → att köa samma slot i två byar kan koalescera fel by. Bör
  scopas till vald by i ett senare steg. Build rent.

- **2026-06-05** — Multi-village login/switch-fixar (branch `multiple_villages`). Symptom: efter login
  visade dropdownen den stale capital (SLAV) i stället för landningsbyn (GREZ), ingen grön ram, och
  Start bot navigerade till den valda (fel) byn. Rotorsaker: (a) post-login-snapshoten läser byar via
  profilsidan (`ReadAccountSnapshotAsync`) → `villageStatus.ActiveVillage` opålitlig; (b)
  `ApplyPostLoginSnapshot` lät `SyncDashboardVillageUiFromVillages` prioritera **nuvarande** dropdown-val
  (stale) före aktiv by. Fix: ny `ApplyCurrentVillageToUiAsync(options, token)` gör en **pålitlig**
  `ReadVillageStatusWithRetryAsync(forceCurrentVillage:true)`-läsning av byn browsern står i och driver
  dropdown (preferred=ActiveVillage), grön ram, buildings, resurser, tribe, count. Anropas (1) efter
  login (i `ExecuteLoginFlowAsync` direkt efter `ApplyPostLoginSnapshot`) och (2) av
  `HonorPendingVillageSwitchAsync` efter en switch under körning → UI uppdateras nu (buildings/resurser/
  lager/ram) när boten bytt by. Switch village under körning väcker dessutom continuous-loopen direkt
  (`_continuousLoopWakeRequested=1`) så bytet sker så snabbt som möjligt (fortf. mellan tasks, aldrig
  mitt i en). Build rent, Worker 324 + Desktop 59 gröna. Kvar att verifiera live att forceCurrentVillage-
  läsningen plockar rätt landningsby (om inte → `ReadActiveVillageNameAsync`-selektorerna i Worker).

- **2026-06-05** — Multi-village login: **rotorsak hittad i DOM-dump** (`temp_build_out/DOM/dorf1_DOM_100X.txt`).
  Official T4.6 (TS100) har **inget** `#villageNameField`; aktiv by ligger i sidebar-entryn
  `div.listEntry.village.active` med `<span class="name">GREZ</span>` + en separat `.coordinatesGrid`
  med bidi-wrappade koordinater. `ReadActiveVillageNameAsync` matchade `.villageList .active` och tog
  **hela entryns** textContent → `"GREZ‭(‭−27‬|‭−66‬)"` (namn + koords + U+202x-marks), som aldrig
  matchade den rena bylistan ("GREZ") → ingen grön ram, dropdown föll tillbaka till stale capital (SLAV),
  Start bot gick till SLAV. Fix (rätt ställe, ingen ny funktion): `ReadActiveVillageNameAsync` läser nu
  **`.listEntry.village.active .name`** först (ren namn-span) + en `clean()` som strippar bidi-marks
  (`‪-‮⁦-⁩‎‏`), normaliserar U+2212-minus och tar bort ev. avslutande
  `"(x|y)"`. Desktop: `ApplyPostLoginSnapshot` sätter nu dropdownen till **aktiv by** (preferred=
  ActiveVillage) i stället för stale val. Den tidigare extra långsamma post-login-läsningen
  (`ApplyCurrentVillageToUiAsync` i login-flödet, ~50s) **borttagen** — snapshotens nu rena ActiveVillage
  räcker. `ApplyCurrentVillageToUiAsync` finns kvar enbart för UI-refresh efter switch under körning.
  Build rent, Worker 324 + Desktop 59 gröna.

- **2026-06-05** — Multi-village: per-by-data + kö-vy robustare (namn-baserad). Symptom: efter en körning
  visade båda byar **samma** byggnader/resurser ("skrevs över"), och kön "försvann" i UI. Rotorsaker:
  (1) per-by-cachen nycklades på `GetVillageKey` (did/coords/namn) — url/newdid-skillnader mellan läsningar
  kunde splittra/krocka poster; (2) vid cache-miss (by aldrig läst) **rensades inte** detaljvyn → förra
  byns buildings/resurser låg kvar (= "samma status"); (3) kö-filtret matchade på by-NYCKEL som kunde
  skilja sig från radens → dolde poster. Fix: hela per-by-cachen nycklas nu på **bynamn**
  (`_villageStatusCacheByName`, `NormalizeVillageName` ignorerar tomt/"Unknown village"/"-"); `Cache
  VillageStatus` **merge:ar** så en resurs-bara-läsning behåller byggnader/resursfält från en tidigare
  full läsning; 16s-refreshen (`ApplyResourceStatusToUi`) cachar nu aktiv by varje tick (så byar "kommas
  ihåg" allt eftersom boten besöker dem); `ShowSelectedVillageFromCache` **rensar** detaljvyn vid
  cache-miss (`ClearVillageDetailUiForUncachedSelection`) i stället för att visa en annan bys data;
  `IsStatusForSelectedVillage` + `FilterQueueRowsForSelectedVillage` jämför nu på namn. OBS: den tomma
  `queue.json` efter testet kom från `queue_clear_on_shutdown` (default true) när appen stängdes — inte en
  bugg; deferred-poster fanns kvar under sessionen (loggen slutar med DEFER, inget Clear). Vill man behålla
  kön mellan omstarter: sätt `queue_clear_on_startup`/`_on_shutdown=false`. Build rent, Worker 324 +
  Desktop 59 gröna.

- **2026-06-05** — Multi-village persistens mellan sessioner. (1) **Byggkön sparas nu mellan omstarter**:
  `queue_clear_on_startup`/`queue_clear_on_shutdown` default ändrat `true`→`false` (`MainWindow.ServerClock`).
  Per-konto `queue.json` överlever stängning; rensas fortf. av Stop (med bekräftelse) och Clear-knapparna.
  (2) **Per-by buildings/resursfält cachas på disk**: ny `VillageCacheStore` (`config/accounts/<account>/
  village_cache.json`, helper `AccountStoragePaths.VillageCachePath`). Sparar bara durabel struktur
  (buildings, resource fields, bylista, tribe, capital) — volatila värden (resursmängder, lager, kö-timers,
  guld) **strippas** (`StripVolatile`) och uppdateras live efter start. `CacheVillageStatus` sparar bara
  vid **full läsning** (buildings/fält finns) så filen inte thrashas var 16:e sek. Laddas vid login
  (`LoadVillageCacheForActiveAccount` → fyller `_villageStatusCacheByName`) så en by skannad i en tidigare
  session minns sina byggnader direkt; rensas in-memory vid kontobyte (filen behålls per konto).
  Round-trip av `VillageStatus` via System.Text.Json verifierad i test. (3) **Kö-rensningsknappar**:
  "Clear active queue" → **"Clear account queue"** (bekräftelsepopup likt Stop, rensar hela kontots kö).
  Ny **"Clear village queue"** rensar bara den i dropdownen valda byns köposter (matchar på bynamn, rör ej
  globala/andra byar, ingen popup). Build rent, Worker 324 + Desktop 62 gröna.

- **2026-06-05** — Multi-village fixar: storage per by + ingen profil-läsning vid bybyte + state-knapp.
  (1) **State/knapp:** `UpdateExecutionStateIndicator` visade "Pause bot" när continuous-toggeln var på +
  kö fanns (persisterad efter omstart) trots att inget kördes. `continuousModeActive`-grenen visar nu
  "Pause bot" endast om något **faktiskt körs** (`loopRunning || _autoQueueRunning || functionExecution
  Running || hasRunningQueueItems`), annars "idle"/"Start bot". (2) **Storage följer vald by:**
  `ShowSelectedVillageFromCache` + `ApplyCurrentVillageToUiAsync` anropar nu `_resourcesViewModel.Apply
  StorageForecasts(...)`; cache-miss → `ResetStorageForecasts()` (tomma staplar "-"). `VillageCacheStore.
  StripVolatile` behåller nu `WarehouseCapacity/GranaryCapacity/ResourceStorageForecasts` så kapacitet
  minns mellan sessioner (current-mängder uppdateras live). (3) **Ingen profil-läsning vid bybyte:**
  `ReadCurrentVillageStatusAsync` läser bylistan via `ReadVillagesPreferCacheAsync` (sidebar/cache) i
  stället för `ReadVillagesAsync` (spieler.php). Bybyte läser nu bara dorf1/dorf2 av målbyn; capital tas
  från cache. Snabbare switch, ingen profil-navigering. Kompilerar rent (full build kräver att appen
  stängs). **KVAR (stort, eget pass):** per-by automation-loop-grupper (toggla Hero/Construction/… per by
  + visa timers från vald by) — kräver per-by grupp-enable-persistens + wiring i loopen + per-by timer-UI.

- **2026-06-05** — Multi-village steg #2a: **per-by auto-loop-grupp-toggles** (state + UI; användarens
  val "alla grupper per by" + "stegvis"). `VillageSettingsStore`-posten fick `EnabledGroups: string[]?`
  (null = ingen override → global default) + `GetEnabledGroups`/`SetEnabledGroups`. Dashboard auto-loop-
  togglarna speglar nu **vald by**: `ApplyAutomationLoopGroupsForSelectedVillage` laddar `_automation
  LoopTasks.IsEnabled` från byns override (annars `_defaultEnabledGroupKeys`, snapshot:ad vid
  `LoadAutomationLoopTasks`), anropas i `ShowSelectedVillageFromCache` + `ApplyCurrentVillageToUiAsync`.
  Toggling sparar per by (`SaveAutomationLoopGroupsForSelectedVillage` i slutet av `AutomationLoop
  ToggleButton_Click`, utöver den globala `PersistAutomationLoopTasksToConfig` som blir mall för nya byar).
  Loopen läser fortf. `_automationLoopTasks` → använder nu **vald/arbetande bys** grupper. **KVAR (steg
  #2b):** (1) per-task by-gating i construction-rotationen (så grupp av på by B blockerar B:s tasks även
  när A är vald), (2) per-by **timers** i auto-loop-grupperna (construction-timer skiljer per by — visas
  ännu från aktiv/senast lästa by). Build rent, Worker 324 + Desktop 62 gröna.

- **2026-06-05** — Multi-village: bybyte navigerade fortf. till profilsidan + resurser fylldes ibland inte.
  Rotorsak (verifierad i logg): `SwitchToVillageAsync` anropade `RefreshCapitalStateForActiveVillage
  Async` efter varje switch → `ReadIsCapitalAsync` navigerar till **spieler.php** (profil). Det var den
  extra (långsamma) profil-navigeringen, och dess sid-churn gjorde att den efterföljande resurs-läsningen
  ibland landade mitt i en navigering (resurser/storage "not filling"). Fix: **tog bort capital-checken i
  bybyte** (capital tas från cache + fast-detection: resursfält > nivå 10, samt login-analys) — switchen
  läser nu bara dorf1/dorf2 av målbyn. Idle-switch-grenen (`SwitchToActiveVillageAsync`) anropar nu också
  `ApplyStorageForecasts` + `ApplyAutomationLoopGroupsForSelectedVillage` (paritet med running-switch via
  `ApplyCurrentVillageToUiAsync`). `RefreshCapitalStateForActiveVillageAsync` finns kvar för login-analys
  (`ReadAccountAnalysisSnapshotAsync`). Build rent, Worker 324 + Desktop 62 gröna.

- **2026-06-05** — Multi-village steg #2b: per-task by-gating + per-by construction-timer. (1) **Per-task
  grupp-gating:** ny `IsQueueItemGroupEnabledForItsVillage(QueueItem)` (item:s by-nyckel → byns
  `EnabledGroups` annars `_defaultEnabledGroupKeys` → innehåller item:s grupp-nyckel). Lagd i continuous-
  loopens urval (`SelectNextQueueItemForContinuousLoop` grupp-where + `HasReadyContinuousConstruction
  Item`) så en grupp avstängd på by B blockerar B:s tasks även när A är vald (rotationen hoppar över B).
  OBS: top-level grupp-set kommer fortf. från vald by — om VALD by har en grupp av men en annan by har
  den på täcks det inte (mindre vanligt fall; union-grupp lämnat). (2) **Per-by construction-timer:** ny
  `ApplyConstructionTimerFromStatus(status)` sätter `_buildQueueActiveCount/_buildQueueRemainingSeconds`
  från vald bys status + `UpdateAutomationLoopRunningIndicators`; anropas i `ShowSelectedVillageFromCache`
  (cachad status; "-" vid miss), `ApplyCurrentVillageToUiAsync` och idle-switch. Build rent, Worker 324 +
  Desktop 62 gröna.

- **2026-06-05** — Multi-village fix: construction-timern var **samma för alla byar**. Rotorsak:
  `ResolveConstructionGroupRemainingSeconds` läste det **globala** `_constructionInlineWaitUntilUtc`
  (sattes till längsta deferred-wait över alla byar). Fix: timern beräknas nu från den **valda byns**
  deferred construction-köposter (deras `NextAttemptAt`, matchat på bynamn via `GetQueueItemVillageName`)
  — tidigaste wait, kombinerat med byns bygg-kö-timer. Det globala `_constructionInlineWaitUntilUtc`-
  fältet **borttaget** helt (skrevs men gav fel per-by-värde); `ApplyConstructionInlineWait` refreshar nu
  bara indikatorerna. Olika byar visar nu olika timers, och dropdown-byte visar vald bys timer (eller "-"
  om ingen). Build rent, Worker 324 + Desktop 62 gröna.

- **2026-06-05** — Multi-village fix (forts.): timern var FORTF. samma för alla byar. Riktig rotorsak:
  `UpdateAutomationLoopRunningIndicators` byggde varje grupp-korts `RemainingSeconds`/`QueuedCount`/state
  från `groupItems = queueItems.Where(Group==group)` — **ofiltrerat på by** → `deferred`-posten (raden som
  visas) var den globalt tidigaste, samma för alla byar. Fix: `groupItems` filtreras nu på **vald by**
  (`IsQueueItemForSelectedVillageOrGlobal` — by-lösa/globala grupper som hero/farm visas alltid). Nu visar
  varje grupp-kort vald bys timer/antal/state, och dropdown-byte uppdaterar dem (anropas via
  `UpdateAutomationLoopRunningIndicators` i switch/val-vägarna). Build rent, Worker 324 + Desktop 62 gröna.

- **2026-06-05** — Multi-village småfixar. (1) **Switch village-knappen** grön = nu mjuk/tonad (Start bot-
  stil: `SuccessBgBrush`/`SuccessBorderBrush`/`SuccessTextBrush`) i stället för solid `AccentBrush`.
  (2) **Construction "Ready"-flash vid bybyte (cross-village del):** `RefreshDeferredConstructionWaitsAsync`
  utvärderade ALLA byars deferred construction-items mot den lästa byns resurser och kunde nolla deras
  timer ("Ready") → re-defer. Nu filtreras den på den lästa byns (`status.ActiveVillage`) egna items
  (by-lösa inkluderade) så andra byars timers inte rörs. (Kvar/känt: en QUEUE-FULL-deferred post för
  SAMMA by kan fortf. kortvarigt visa "Ready" när dess resurser räcker — resurs-checken vet inte att
  blockeringen var bygg-kö-platser; self-correctar på nästa läsning. Kräver en defer-reason-flagga för
  att helt undvika.) Build rent, Worker 324 + Desktop 62 gröna.

- **2026-06-05** — Multi-village småfixar (de två kvarvarande "småsakerna"). (1) **Construction "Ready"-
  flash för SAMMA by (kö-full):** ny payload-flagga `upgrade_defer_reason` (`queue_full` | `resources`)
  stämplas vid varje construction-defer (`HandleQueueItemFailureAsync`, via `IsBuildQueueFullDeferMessage`
  som matchar "build queue full"/"blocked by queue"). `RefreshDeferredConstructionWaitsAsync` hoppar nu
  över items med reason `queue_full` så de inte återupptas ("Ready") bara för att resurserna råkar räcka —
  deras timer speglar en bygg-slot som frigörs, inte resurser. När kö-full-timern går ut omvärderar workern
  och om-stämplar reason (→ `resources` om det då är resursbrist). (2) **Union av grupper över byar:** den
  kontinuerliga loopens urval använde **vald bys** grupp-toggles som topp-gate, så en grupp som var av på
  vald by men på på en annan by aldrig kördes. Ny `GetContinuousLoopConsideredGroupsInOrder()` =
  union(vald bys live-toggles, alla enabled byars persisterade `EnabledGroups` ?? default) via ny
  `VillageSettingsStore.GetEnabledVillagesGroups()`. Används **endast** i item-urvalet
  (`SelectNextQueueItemForContinuousLoop`), INTE i runtime-genereringen (`EnsureContinuousLoopRuntimeItems`
  förblir vald-by-scopad, annars skulle troops/smithy genereras för fel by). Per-item-filtret
  `IsQueueItemGroupEnabledForItsVillage` gatear fortf. varje item till sin egen bys inställning. Build rent,
  Worker 324 + Desktop 62 gröna.

- **2026-06-05** — **Steg 4c klart: per-by runtime-generering för trupp-träning/smithy + brewery capital-
  only.** `EnsureContinuousLoopRuntimeItemsAsync` genererade tidigare `build_troops`/`upgrade_troops_at_smithy`/
  `run_brewery_celebration` **by-löst** (kördes i aktiv by) gatat på **vald bys** grupp-toggles. Nu:
  (a) `build_troops` (TroopTraining) och `upgrade_troops_at_smithy` (Troops) genereras **per enabled by**
  (`GetEnabledAutomationVillages` läser Dashboard-listan + `IsEnabledByKey`), gatat per by via
  `IsGroupEnabledForVillage(key, group)`, taggat med `TargetVillageName/Url` (`BuildVillageRuntimePayload`)
  så workern växlar dit (BotTaskRunner). Dubbelgenerering förhindras av `HasActiveTaskForVillage(task, by)`.
  Replikerar global trupp-config över byar (per-by distinkta inställningar = framtida UI, ej begärt).
  (b) **Brewery = endast capital** (bryggeriet finns bara i capital): genereras för capital-byn när den är
  enabled + dess Auto Celebration-grupp på + stammen stödjer det. (c) Tidig-retur-gaten använder nu
  `consideredGroups` (unionen) så loopen kör även när bara en icke-vald by har trupp-grupper på. Hero/
  farming/resource-transfer/reinforcements förblir kontot-globala (vald-by-gatade, oförändrat).
  (d) **Rotation för icke-construction-grupper:** `SelectNextQueueItemForContinuousLoop` roterar nu även
  TroopTraining/Troops över byar (`QueueVillageRotation.SelectByVillageRotation` +
  `SelectNextReadyGroupHead`, ny per-grupp-nyckel-dict `_continuousGroupRotationVillageKeys`, nollas vid
  loopstart) så en bys väntande item inte blockerar en annan bys redo item. Globala grupper (en village-
  nyckel = "") kollapsar till samma strikta head-ordning som förut. Compile rent (Desktop-exe copy-lock
  när appen kör = inte kompileringsfel), Worker 324 gröna.

- **2026-06-06** — **Dashboard-omdesign: överblick per by.** (1) **Layout:** rad 3 delas nu i en fast
  vänsterkolumn (360px) = Automation Loop (nu **listformat**, en rad/grupp: Order-cirkel · Toggle ·
  Namn+beskrivning+StateText/Queued · Status-badge) ovanpå **Auto settings** (flyttad hit från höger),
  och en `*`-högerkolumn = **Villages** i full höjd. Automation-ListBoxens `ItemsPanel` bytte från
  `VerticalFirstUniformGrid Columns=2` till vertikal `StackPanel`; korttemplaten bantades (tog bort den
  inline hero-adv-badgen och troop_training-3-timer-`ItemsControl` — den infon finns nu per by i
  Villages-listan). (2) **Villages-kolumner:** Name, Pop, Coords (behållna) + **Buildings** (2 slots, 3
  för Romare), **Troops** (Barracks/Stable/Workshop), **Hero**. Ikoner = mörka (idle) / gröna (aktiva).
  Källa: per-by-cachen `_villageStatusCacheByName` (icke-aktiva byar = senast inläst). `VillageSelectionItem`
  fick `BuildingSlots`/`TroopSlots` (`VillageActivitySlot`) + `IsHeroHome`; fylls i `ApplyVillageActivity
  Indicators` (kallas i `BuildMergedVillageSelectionItems` + efter varje `CacheVillageStatus`). Romare-koll
  = `status.Tribe` innehåller "Roman". (3) **Hero-hemmaby:** `HeroAttributeSnapshot.HomeVillageName` (nytt
  fält) läses från attributsidan (vid login) — `ReadHeroHomeVillageNameAsync` plockar `.heroState a[href*=
  "karte.php"]` ur "Home village is village X". `ApplyHeroSnapshotToUi` → `SetHeroHomeVillageName` → repaint.
  Compile rent (endast exe copy-lock när appen kör), Worker 324 gröna; Desktop-tester kräver stängd app.

- **2026-06-06** — **Dashboard-omdesign uppföljning (fixar efter visuell granskning).** (1) **Auto loop =
  äkta listrader:** korttemplaten ersatt med en rad `Toggle | Namn (+beskrivning) | Status-badge` (tog bort
  order-cirkeln och vänster-accentkanten) så allt får plats i 360px-kolumnen. (2) **Villages: Pop & Coords
  flyttade längst till höger** (kolumnordning Name · Buildings · Troops · Hero · Pop · Coords i både header
  och radmall). (3) **Buildings för icke-skannade byar (t.ex. GREZ):** den persistade cachen strippar
  `ActiveBuildCount`/`BuildQueue`, så en by boten inte skannat denna session saknade bygg-signal. Ny
  `BuildQueueFullConstructionCountByVillage` räknar construction-kö-items deferrade med reason `queue_full`
  per by (= server-bygg-kön upptagen → bygger) och lyser Buildings-slots även utan färsk skanning;
  kombineras med cachens `ActiveBuildCount` (max). (4) **Hero-hemmaby:** `.heroState` säger "Hero is
  currently in village X" (hemma) ELLER "Home village is village X" (borta på räd) — den gamla selektorn
  matchade bara "home village" och missade hemma-fallet. `ReadHeroHomeVillageNameAsync` tar nu **första
  `a[href*="karte.php"]`** i `.heroState` (täcker båda). På äventyr finns ingen by-länk → null → behåller
  senast kända. Build rent, Worker 324 + Desktop 62 gröna.

- **2026-06-06** — **Villages-överblick: hero 3-läge + Queue- och Smithy-kolumner.** (1) **Hero-ikon
  tre lägen:** grön = hjälten är hemma i byn, gul (`WarningBrush`) = hemmaby men hjälten är borta
  (äventyr/attack), mörk = ingen hjälte. Drivs av `VillageSelectionItem.IsHeroHome` + ny `IsHeroAway`.
  Worker: `ReadHeroHomeVillageInfoAsync` returnerar nu `(Name, Away)` — hemma upptäcks via
  "currently in village"/`i.statusHome`; nytt snapshot-fält `HomeVillageHeroAway`; desktop
  `SetHeroHomeVillageName(name, away)`. (2) **Queue-kolumn:** grön ikon när byn har köad construction,
  mindre grön (`SuccessBgBrush`/`SuccessTextBrush`) när tom — så användaren snabbt ser var de bör köa
  mer (`HasQueue` via `BuildVillagesWithConstructionQueue`). (3) **Smithy-kolumn:** två slots (två
  samtidiga uppgraderingar), samma stil som övriga; **UI-only** (ej inkopplad mot data ännu —
  `BuildSmithyActivitySlots` ger två idle-slots). Kolumnordning nu: Name · Buildings · Queue · Smithy ·
  Troops · Hero · Pop · Coords. (4) **Auto loop** nu äkta listrader (Toggle | Namn | Status). Build rent,
  Worker 324 + Desktop 62 gröna.

- **2026-06-06** — **Fixar: hero 4-läge, sticky gold/silver/tribe.** (1) **Hero-ikon röd vid död:** nytt
  `IsHeroDead` (röd, `DangerBrush`, vinner över grön/gul). Drivs av snapshot `HeroState` = Dead/Reviving.
  Ny `SetHeroState(name, away, dead)` ersätter `SetHeroHomeVillageName`; behåller senast kända hemmaby när
  namn saknas (hero borta/död-sidor namnger ibland ingen by) så färgen ändå hamnar rätt. Loggar
  `[hero] home village=… away=… dead=…` för felsökning. Hero-läsning styrs fortf. av checkboxen
  *Post-login: analyze hero* (+ hero-tasks i loopen) — ingen alltid-på-läsning tillagd. (2) **Gold/Silver
  blippade till "-"** på partiella status-reads: `SetCurrencyValueText` skriver inte längre över ett redan
  visat verkligt värde med "-"/tomt. (3) **Tribe blippade till "-"/Unknown:** ny `SetTribeText` ignorerar
  tomt/"Unknown"/"-" när en riktig tribe redan visas (tribe är fast per konto); alla 6 sättnings-ställen
  använder den. Build rent, Worker 324 + Desktop 62 gröna.

- **2026-06-06** — **Dashboard-layout omflyttad igen.** Automation Loop ligger nu **ensam i vänster-
  kolumnen i full höjd** (så alla grupp-rader får plats i en kolumn utan att en andra bildas). Höger-
  kolumnen är ett `Grid` med rader `[*, Auto]`: **Villages** (Grid.Row 0, ej längre full höjd) och
  **Auto settings** (Grid.Row 1, under Villages). Auto settings ligger kvar i dokument-ordningen FÖRE
  villages-bordern men placeras via `Grid.Row=1` → renderas under (slipper flytta hela blocket). Compile
  rent (endast exe copy-lock när appen kör). Hero-grön lämnad t.v. (programmet navigerar inte till
  attributsidan efter login om checkboxen är av — användarens beslut att låta checkboxen styra).

- **2026-06-06** — **Buildings-ikoner undercount + Auto loop tvåkolumn.** (1) **Build queue räknades fel:**
  `ReadBuildQueueAsync` dedupade kö-poster på `text`, så två samtidiga uppgraderingar med identiskt namn
  (t.ex. två fält till samma nivå) slogs ihop → bara en grön bygg-ikon. Nu räknas varje element i första
  träffande selektorn (ingen text-dedup); `ActiveBuildCount = buildQueue.Count` blir därmed korrekt. (2)
  **Auto loop bildade en andra kolumn vid >4 grupper:** `UpdateAutomationLoopColumns` satte 2 kolumner när
  fler än 4 syntes. Nu alltid **1 kolumn** (`VerticalFirstUniformGrid Columns=1`) — grupperna fyller neråt
  och scrollar vid behov, eftersom Auto loop-rutan numera är full höjd. Build rent, Worker 324 + Desktop 62
  gröna.

- **2026-06-06** — **Hero-attribut lästes fel på officiell (hero V2).** `ReadHeroInventorySnapshotAsync`
  + `WaitForAttributesTableAsync` använde gamla selektorer (`#availablePoints`, row-id `attributepower`,
  `input[name^="attribute"]`, `#attributesOfHero`, `.heroStatusMessage`) som inte finns på den moderna
  hero V2-attributsidan → allt blev 0. Nu läses modern layout först: free points `.pointsAvailable`,
  attribut via `input[name="power|offBonus|defBonus|productionPoints"]`, status via `.heroState`; legacy-
  selektorerna kvar som fallback (SS-Travi). `readDigit` strippar bidi-märken/tusentalsavgränsare.
  **Ordning vid login:** hero-attribut-analysen (`RefreshHeroStatsAsync`, styrd av *Post-login: analyze
  hero*) körs redan EFTER hero-inventory-läsningen (inventory läses i post-login-snapshoten, attribut i
  steg 4) — ingen ändring behövdes. Worker 324 gröna; Desktop-exe copy-lock när appen kör = ej kompfel.

- **2026-06-06** — **Hero settings + persisterad hemmaby + login-ordning.** (1) Settings-popupen: checkboxen
  "Analyze hero" → **"Analyze hero attributes"**, och **"Analyze hero inventory"** flyttad direkt under den.
  (2) **Login-ordning:** hero-attribut-analysen körs nu **steg 2** (direkt efter hero-inventory i post-login-
  snapshoten, FÖRE farmlists) i `MainWindow.Session.cs`. (3) **Persisterad hemmaby:** `VillageSettingsStore`
  sparar/laddar `HeroHomeVillageName` i villages.json (per konto); `LoadHeroHomeVillageForActiveAccount`
  seedar ikonen vid login innan första hero-läsningen, och `SetHeroState` persisterar varje gång hemmabyn
  ändras. Hemmabyn uppdateras vid varje attribut-läsning (login-analys + efter varje `hero_manage`-körning
  via `ReadHeroAttributesAsync`→`ApplyHeroSnapshotToUi`→`SetHeroState`). Build rent, Worker 324 + Desktop 62
  gröna.

- **2026-06-06** — **Snabb hemmaby-läsning i adventure/HP-pollen.** `client.RefreshAdventureCountAsync`
  (körs på dorf1 under continuous-loop-polling) anropar nu `NotifyHeroHomeFromDorf1Async`: läser hjälte-
  widgeten (`.heroStatus a[href*="build.php"][href*="id=39"]` → `newdid` = hemmaby; ikon-klass = state
  heroHome/heroRunning/dead), mappar newdid→namn via sidomenyns `.listEntry.village[data-did]` och Notifyar
  `[herohome] away=.. dead=.. name=..`. Desktop parsar raden (`HeroHomeRegex` i `TryApplyPlusStatusFromLog`)
  → `SetHeroState`. Så hemmabyn/ikonen uppdateras vid varje adventure-poll utan extra navigering, även när
  attribut-checkboxen är av. Build rent, Worker 326 + Desktop 62 gröna.

- **2026-06-06** — **Village overview-finputs + per-by NPC trade + README.** (1) Rubriken "Villages" →
  **"Village overview"**; ny **grön/grå rund indikator** till vänster om bynamnet (grön = enabled, grå =
  skippad) bunden till `IsEnabledForAutomation`. (2) Village settings-popupen: "Auto"-kolumnen är nu
  **toggle-switchar** (DataGridTemplateColumn + `ToggleSwitchStyle`) i stället för checkboxes; styr vilka
  byar boten kör. (3) **Per-by NPC trade:** `VillageSettingsStore` lagrar `NpcTrade` per by (default på);
  popupens NPC-kolumn seedas/persisteras (`GetNpcTrade`/`SetNpcTrade`, ny notifierande `NpcTrade` i
  `VillageSettingsRow` + andra callback i fönstret). Auto settings NPC-toggeln = **master** (config
  `NpcTradeEnabled`). Effektiv NPC = master AND per-by, satt som per-task payload `npc_trade_enabled` i
  `ApplySelectedVillageToPayload` + `BuildVillageRuntimePayload` (via `IsNpcTradeEnabledForVillageKey`);
  workern honorerar overriden (BotOptionsPayloadApplier). Så master av → NPC körs ingenstans även om alla
  byar är på. (4) README uppdaterad: ny "Multi-village support"-sektion + per-konto `accounts/<account>/`
  config (queue.json/villages.json/village_cache.json). Build rent, Worker 326 + Desktop 62 gröna.

- **2026-06-06** — **NPC trade borttagen som Automation Loop-grupp.** Eftersom NPC trade nu styrs av Auto
  settings-master + per-by-val ska den inte längre visas som en loop-grupp. `LoadAutomationLoopTasks`
  hoppar över `QueueGroup.NpcTrade` (visas ej i Auto loop-listan) och `DashboardFunctionListButton_Click`
  filtrerar bort den från Function list-popupen (`AllGroups.Where(g => g != NpcTrade)`). Build rent,
  Worker 331 + Desktop 62 gröna.

---

## 5. Kända fallgropar / regressions

- **Buildings-tabbens "queued"-färgning måste filtreras per by.** Kön är en-per-konto och varje item är
  taggat med målby. `PopulateBuildingsTab` byggde pending/queued-per-slot från ALLA byars köitems
  (`GetActiveQueueItems()` ofiltrerat) → en annan bys köade uppgraderingar färgade samma slotnummer (19-40
  finns i varje by) och gjorde dem oklickbara. Fix: filtrera köitems med `IsQueueItemForSelectedVillageOrGlobal`
  i `PopulateBuildingsTab`, och rensa de optimistiska `_buildingLastQueued*BySlot`-cacharna (slot-keyade,
  ej by-keyade) i `ShowSelectedVillageFromCache` vid bybyte. **Samma filter krävs i enqueue-vägen:**
  `EnqueueBuildingUpgradeTaskCoalesced` och `EnqueueBuildingConstructTaskCoalesced` byggde `relatedItems`
  enbart på slotId → en annan bys köade slot blockerade enqueue ("already has a queued upgrade", inget
  lades till) och construct-coalescen kunde t.o.m. RADERA den andra byns köitems. Båda filtrerar nu med
  `IsQueueItemForSelectedVillageOrGlobal`. Och `BuildProjectedBuildingStatus` defaultade till alla byars
  köitems → en annan bys köade construct/upgrade projicerades på denna bys slots (tom slot såg upptagen ut,
  eller en unik byggnad såg "already exists" ut) → construct-popupen visade "no buildings available".
  Defaultar nu till by-filtrerade köitems. Resurs-tabbens `GetQueuedResourceTargetsBySlot`
  (`MainWindow.Resources.UiState.cs`) hade samma ofiltrerade mönster för resursfält (slot 1-18) och
  filtrerar nu också med `IsQueueItemForSelectedVillageOrGlobal`. Regel: all per-slot UI-härledning från
  kön måste filtreras per vald by.
- **Hero-ikonens away-state (grön=hemma, gul=ute) läses på dorf1.** `NotifyHeroHomeFromDorf1Async`
  detekterade "away" via en enda widget-ikons klass och tvingade `away=false` så snart klassen innehöll
  `herohome`. På official Travian behåller ikonen en heroHome-liknande klass medan hjälten är på äventyr →
  ikonen fastnade grön. Fix: away läses nu från de kanoniska signalerna `heroRunning`/`statusRunning` eller
  en ankomst-countdown (`.heroStatus .timerReact`/`.heroState .timerReact`), och "home"-signalen
  (`heroHome`) litar vi bara på när ingen travel-signal finns. Timer-selektorn är scope:ad till hero-boxar
  så bygg-köns timers inte ger falsk away.
- **Full status-läsning måste cacha produktionen (annars tom resurs-UI efter by-byte).**
  `ReadCurrentVillageStatusAsync` läste produktion på dorf1 men sparade den INTE i per-by-cachen
  (`SaveCachedVillageResourceSnapshot`) — till skillnad från `ReadCurrentVillageResourceStatusAsync`. Vid
  ett by-byte är den fulla läsningen ofta enda gången dorf1 (och därmed produktionen) läses för den nya
  byn. Periodiska current-page-läsningar sker sedan på dorf2/build där produktion saknas → tom cache →
  `@-/h` och "not filling". Fix: full status-läsning sparar nu också snapshotet. `SaveCached` behåller
  befintliga värden när den nya läsningen är tom, så bra data skrivs aldrig över med blankt.
- **Village-switch måste använda kanonisk `dorf1.php?newdid=X` + verifieras.** En lagrad/sidebar-URL kan
  få med extra query-param från sidan den lästes på (t.ex. en byggslots `id=10`). URL:en
  `?newdid=25471&id=10` navigerar till sajt-roten, som servern serverar som **login-sidan** → ser ut som
  en utloggning och nästa läsning sker på fel/utloggad sida (fel värden/bygge i fel by). Fix:
  `CanonicalizeVillageSwitchUrl` reducerar alla switch-URL:er med newdid till `dorf1.php?newdid={id}`, och
  `SwitchToVillageAsync` verifierar efteråt att vi är `logged_in` (annars `EnsureLoggedInAsync(force)`).
  Switch drivs alltid av newdid — släng aldrig in andra `id`-param i switch-URL:en.
- **Login: vänta på full sidladdning + mänsklig pacing.** `LoginAsync`/`TryLoginUsingCurrentPageAsync`
  fyllde användarnamn+lösenord och submittade direkt utan paus, och väntade bara på `DOMContentLoaded`
  efter login → boten bytte sida på en halvladdad sida (såg omänskligt snabb ut) och nästa bakgrundstick
  läste ofta login-state `unknown`. Fix: `FillLoginCredentialsWithPacingAsync` lägger click-pacing
  (`ActionPacingClickMin/MaxSeconds`) mellan fälten och före submit, och efter login väntar vi på
  `LoadState.Load` + page-load-pacing. Allt styrs av befintliga pacing-inställningar (av när action-pacing
  är av).
- **`unknown` login-state är en godartad ladd-race, inte ett larm.** En sida som läses mitt i en
  navigering ger `LoginStateAsync()=="unknown"` → `EnsureLoggedInAsync` kastar "Not logged in. Current page
  state is 'unknown'." Det självläker nästa tick. Bakgrunds-resurs-refreshen loggar nu detta specifika fall
  som `[resource-refresh:verbose]` (icke-larm) via `IsTransientPageSettlingFailure`; captcha/manual_step/
  logged_out förblir larm.

- **Byggkö-fingerprint får inte innehålla nedräkningstimer:** `BuildQueueIdentityFingerprint` byggde
  identiteten på `BuildQueueItem.Text`, som innehåller den live tickande timern (`0:08:12`). Då ändrades
  fingeravtrycket vid varje läsning → `WaitForResourceLevelAdvanceAsync` tolkade det som "queue changed"
  → falskt `queued=True` efter ett klick som inte gjorde något (kö full). Resultat: `upgrade_all_resources_
  to_level` hoppade förbi defer-grenen och navigerade om till `build.php?id=N` i all oändlighet (spam var
  ~5:e sek när bara ett fält återstod under mål). Fix: `StripQueueTimerTokens` tar bort tid/nedräkning ur
  texten så identiteten bara speglar VILKA items som köar (namn+nivå). OBS: `BuildQueueFingerprint`
  (byggnadsflödet) inkluderar fortfarande `TimeLeft` medvetet — rör inte utan att verifiera samma risk.
- **Single-file + Playwright-driver (portable):** `PublishSingleFile=true` bäddar in
  `.playwright\node\win32_x64\node.exe` i exe:n istället för att lägga den löst → portable-mappen
  saknar drivern och Playwright kraschar med `Driver not found` (letar dessutom på fel plats, t.ex.
  parent-mappen). Två saker krävs: (1) release-workflowen kopierar hela `.playwright` från
  framework-bygget (`bin\Release\net8.0-windows\win-x64\.playwright`) in i paketet, och
  (2) `BrowserSession.ConfigureLocalPlaywrightEnvironment` sätter `PLAYWRIGHT_DRIVER_PATH` till
  `<projectRoot>\.playwright` före `Playwright.CreateAsync()`. `ms-playwright` (browsers) hanteras separat.
- **Hero-loop → `/hero/login.php`** = flavor är fel (servern tolkas som Official på en SS-server).
  Kontrollera `[flavor]`-raden. Grundorsak historiskt: config-bunden flavor.
- **Profil-koordinater:** selektor-omordning (prioritera `karte.php?x=`-länk) ändrade SS-beteende
  medvetet — resultatet är lika/bättre, men det är inte ren additiv.
- **React-sidor utan render-väntan** → läser tomt / "not clickable". Vänta alltid in render.
- **Resource upgrade-all + resursbrist:** när Travian visar `upgradeBlocked`/`not enough resources yet`
  ska tasken returnera `queue_wait_seconds` direkt. Att fortsätta scanna andra fält kan skapa en minut-loop
  med upprepade build.php-navigeringar och loggspam.
- **"Open shop" payment-decoy i upgrade-kandidatscan:** den gröna `button[value="Open shop"]`
  (`onclick=Travian.React.openPaymentWizard`) matchar bara på `green`-klassen och får inte väljas som
  upgrade-/construct-kandidat. Klick öppnar en modal vars `#dialogOverlay` sen blockerar alla klick →
  `click detected upgrade candidate … Timeout 15000ms`-loop. Exkluderas via `openPaymentWizard`/`open shop`
  (se `isPaymentShop` i JS-scannern + `IsGold` i `ExtractButtonCandidates`). Riktig knapp ligger alltid i
  `.upgradeButtonsContainer`/`.upgradeBuilding` med `value="Construct building"`/`"Upgrade to level N"`.
- **Tom slot vs upgrade-sida (Official):** construct-choice-sidan wrappar *varje* byggnad i en egen
  `.upgradeButtonsContainer` + `#contract_building{gid}`. Använd därför INTE `.upgradeButtonsContainer`-närvaro
  som "upgrade finns"-signal — då feldetekteras tomma slots som upgrade-bara och scannern matchar
  "Construct building" som falsk upgrade-kandidat. Tom slot = `#contract_building*` finns men ingen
  "Upgrade to level N"-text (se `DetectBuildPageStateAsync` + `IsEmptyConstructionSlotHtmlForTests`).
  Både construct- och upgrade-knappen delar onclick `action=build`, så skilj dem på TEXTEN, inte action.
- **Construct: byggnad ej byggbar än (krav):** saknas förkrav finns ingen "Construct building"-knapp, bara
  `span.buildingCondition.error` i `#contract_building{gid}`. `ConstructBuildingAsync` läser den
  (`ReadConstructRequirementErrorAsync`, ingen extra navigering) och returnerar
  `"... cannot be built yet. Missing requirements: … queue_wait_seconds=N"` → temporär defer (inte permanent
  block, inte retry-bränning, inga failure-artefakter). Skilj från resursbrist (`upgradeBlocked`) som hanteras
  separat före denna gren.
- **Hero transfer-dialog:** official React kan visa dialoginnehållet som `#dialogContent` utan synlig
  `div.resourceTransferDialog` wrapper i sparad HTML. Klicka bekräftelseknappen via `.actionButton.preSelected`
  eller texten "Transfer selected".
- **Hero transfer efter klick:** vänta kort på dialog-inputs, klickbar "Transfer selected", dialog-stängning
  och en liten settle-delay innan samma build-sida analyseras igen.
- **Byggnads-gids (Smithy = 13):** Travian (Official + SS) använder `gid 13 = Smithy`. Det finns ingen
  `gid 12` och ingen "Armoury" — tidigare kataloger hade felaktigt `12=Smithy`/`13=Armoury`, vilket fick
  construct att misslyckas (UI köade gid 12, serverns Construct-knapp var gid 13, och den gid-scopade
  klickningen vägrar "främmande" gid). Smithy ska vara 13 i `BuildingCatalogService` (BaseBuildings,
  requirements, SingleInstance), i `TravianBuildings`-parsekartan (`g13`) och i `config/buildings_catalog.json`.
  Byggnadsnamn ska matcha serverns exakta visningsnamn (verifierat mot DOM-dumparna): `gid 34 =
  "Stonemason's Lodge"`, `gid 37 = "Hero's Mansion"`. `NormalizeBuildingName` har alias så server- och
  kortformer matchar (t.ex. "stonemason's lodge"→"stonemason", "hero's mansion"→"hero mansion").
- **Hero transfer-dialog stängs inte av sig själv:** synthetiskt `button.click()` kan ignoreras av React /
  Travian kan lämna dialogen öppen. Lämna den ALDRIG öppen — kvarvarande `#dialogOverlay` blockerar nästa
  upgrade-/construct-klick (samma fel som "Open shop"-overlayen). `TryDismissResourceTransferDialogAsync`
  stänger aktivt via `#dialogCancelButton`/`button[aria-label="Close"]`, sedan Escape, och väntar in att
  dialogen försvinner.
- **Same-slot recheck efter hero transfer:** rätt `build.php?id=N` URL räcker inte alltid; official kan ha
  URL:en satt innan build-DOM är hydrerad. Vänta på `#build`/`#contract`/`.upgradeBuilding`; om det saknas,
  reloada samma build-sida en gång i stället för att kasta fel eller gå via `dorf1.php`.
- **Official hero HP:** health-raden på `/hero/attributes` kan renderas med bidi-tecken runt siffror
  (`‭78‬%`). Rensa direction marks före parse och läs från hela `.attributeBox`, inte närmaste `div.name`.
- **Continuous construction + Plus:** Desktop UI-status kan vara stale precis när looppen startar.
  Blockera bara på en explicit inline-defer (`queue_wait_seconds`); låt Worker göra live slot/Plus-check
  för redo construction-items.
- **Hero low HP:** regen estimates can be many hours at low HP (e.g. 18% with 20%/day). Keep the defer capped
  so live state is rechecked after manual attacks, ointments, level-ups, or changed settings.
- **Official hero away:** the adventures page can show both a countdown and an absolute arrival clock
  (`Arrival in 00:21:15 at 13:45`). Read `.heroState .timerReact` first and only then fall back to page text.
- **Hero 0 adventures:** do not disable the Hero automation group for this. The continuous loop already reads
  the adventure count before queueing `hero_manage`; leave the toggle as the user set it.
- **Pause button:** continuous mode can stay checked while the runner is paused. In that state the main button
  must say `Start bot`; do not derive `Pause bot` only from the continuous toggle.
- **Clean log + pacing:** tag diagnostic pacing lines with `[pacing]`; do not use that tag for user-important
  sleep/wake milestones that should remain visible in Clean mode.
- **Settings popup + account-scoped values:** use global-only save for SettingsWindow. It loads the merged
  overlay for display, but must not write account-scoped keys such as hero regen back through global settings.
- **Clean log + cached currency:** `Could not detect live gold/silver values ... Using cached values` is normal
  on pages without visible currency values. Keep it verbose; only the no-cache case should alert.
- **Session sleep:** do not let manual refresh, login/logout, scans, tests, village switches, or auto-run wake the
  browser while the status is `Sleeping`. Queueing is allowed, but execution must wait for `Run now` or scheduled wake.
- **Daily Quests stale signal:** after rewards are collected, official React may leave the topbar
  `a.dailyQuests .indicator` showing `!` until the page is refreshed. Do not trust that signal inside the collect
  task; verify the rendered dialog and refresh the current page if the signal does not clear after close.
- **Questmaster `/tasks` render timing:** visible/actionable checks are required before clicking collect buttons
  or the "General tasks" tab; DOM presence alone is too early on official React pages.

---

## 6. Officiell-server: sidstatus

| Område | Status |
|---|---|
| Login, dorf1 (resurser), dorf2 (byggnader), upgrade | ✅ |
| Profil (huvudstad + koords) | ✅ |
| Tribe-detektering, Plus-detektering | ✅ |
| RP send troops + egna trupper | ✅ |
| Hero auto-adventures (Explore→Continue, away-defer, timer) | ✅ |
| Inbox (olästa-räknare + mark-as-read) | ✅ |
| Auto collect tasks (Questmaster `/tasks`, båda flikar) | ✅ (verifiera live) |
| Auto collect daily quests (topbar-dialog) | ✅ (verifiera live) |
| NPC trade (öppna→fördela→Redeem) | ✅ (verifiera live) |
| Hero-resurstransfer vid resursbrist (opt-in, official) | ✅ (verifiera live) |
| Hero inventory-läsning (`/hero/inventory`, 4 resurser) | ✅ (verifiera live) |
| Natar gömt på officiell | ✅ |
| Hero-attribut (auto-tilldela poäng) | ✅ |
| Auctions (köp/sälj) | ⛔ React — live-testning |
| Farm list | ⏸ kräver Gold Club |

---

## 7. Recept: lägg till stöd för en ny officiell sida

1. **Spara HTML** av sidan via appens *Save Page HTML* (`temp_build_out/DOM/`), i rätt tillstånd
   (React-sidor: efter render / med rätt data).
2. **Jämför** mot SS-versionen och mot kodens nuvarande selektorer.
3. **Lägg till officiell selektor som fallback** (additivt) + ev. **flavor-aware path**.
4. `dotnet build` + `dotnet test`.
5. **Verifiera live** mot officiell — och en snabb SS-körning för att bekräfta ingen regression.
6. Uppdatera tabellen i avsnitt 6 + ev. Beslutslogg/fallgropar.

---

## 8. Hur nya funktioner ska byggas

**Gyllene regel:** ny kod får **inte** göra god-klasserna (`TravianClient`, `MainWindow`) större.
Extrahera hellre. Bygg beteendebevarande och testbart.

### Ny bot-förmåga (Worker)
- **Stateless parsing → egen klass + enhetstester.** Det som tolkar DOM/text till data ska ligga
  i en ren klass (t.ex. `XxxParser`/`XxxCalculator`) utan I/O, så den kan unit-testas. Lägg den
  **inte** som ännu en metod i `TravianClient`-monoliten.
- **Navigation/klick → tunn `TravianClient`-partial** som hämtar HTML och **delegerar** tolkningen
  till parsern. Håll sekvenslogiken kort.
- **Selektorer:** additiva + flavor-aware (se §2). Aldrig ersätt en SS-selektor.
- **Task:** registrera via `BotTaskRunner`-handler-dictionaryn (befintligt mönster).

### Nytt UI (Desktop)
- **ViewModel-mönster** — använd `TroopTrainingViewModel` som mall. Logik i VM/service,
  **inte** i `MainWindow`-code-behind.
- **Async-handlers via en `SafeInvokeAsync`-hjälpare** (try/catch → logg), inte rå `async void`
  (obevakade undantag kan krascha UI:t).
- **Loop/CTS-livscykel via `LoopController`** — inga nya spridda `CancellationTokenSource`-fält.

### Dashboard-settings-mönster (checkbox → bot.json)
- En ny bool-setting speglas **end-to-end** efter `HeroContinuousAdventures`: `BotOptionPayloadKeys`
  → `BotOptions` (sätt `= true` för default på) → `BotOptionsFactory` (`FromConfiguration` + `CloneWithOverrides`)
  → `BotOptionsPayloadApplier` (lokal var + `bool.TryParse`-case + fältet i retur-`BotOptions`)
  → `BotConfigStore.AccountScopedKeys` (settings är **konto-scopade**, inte globala).
- **Dashboard-checkbox** använder `x:Name` + `Checked/Unchecked`-handler (inte binding), eftersom
  Dashboard-fliken saknar egen VM. Mall: `MainWindow.Dashboard.Settings.cs` —
  `ApplyAutoCollectTasksConfigToUi(options)` sätter `IsChecked` under en `_suppress…`-flagga (annars
  skriver seedningen direkt tillbaka till `bot.json`); handlern gör `_botConfigStore.Load()` →
  sätt nyckel → `Save()`. Appliceras i `LoadBotOptions`-flödet i `MainWindow.xaml.cs`.
- **Periodisk auto-trigger** (t.ex. auto-collect tasks) hängs in i **16s-refreshen**
  (`HandleResourceSnapshotRefreshTickAsync`), official-gren, gated på settingen + en
  `HasActive…Task()`-dedup mot `GetQueueItemsForDisplay()` innan `EnqueueRuntime(...)`.
- **Info-ikon (i) per setting:** återanvänd `SettingInfoIconStyle` (Themes/Badges.xaml) —
  `<ContentControl Style="{StaticResource SettingInfoIconStyle}">` med en `ToolTip` per instans.
  Lägg en sådan bredvid **varje** ny setting-checkbox.
- **Scrollande listor i en ruta:** lägg listan i en `ScrollViewer` i en `Border` på en `*`-rad
  (Villages-rutan på Dashboard) så långa listor scrollar i stället för att tränga undan annat.

### Checklista för en ny feature
1. Stateless logik i egen, testad klass.
2. Selektorer additiva + flavor-aware; sökväg via flavor-aware helper om den skiljer.
3. `dotnet build TbotUltra.sln` + `dotnet test`.
4. **Verifiera live** på officiell **och** snabb SS-körning (ingen regression).
5. Uppdatera §6 (status) + ev. §4 (beslut) / §5 (fallgropar).

---

## 9. Målarkitektur / refaktoriseringsriktning

Vi gör **ingen omskrivning och inget nytt ramverk** — riktningen är **stegvis, beteendebevarande**
förbättring mot de mönster som redan finns. Se `docs/REFACTOR_PLAN.md` för mätningar och prioordning.

**Prioordning (från REFACTOR_PLAN, lägst risk först):**
1. Fortsätt `LoopController`-extraktionen (threading/CTS-livscykel).
2. Inför `SafeInvokeAsync` och flytta `async void`-handlers dit.
3. Flytta residual code-behind till tematiska partials/services.
4. Extrahera **stateless parsers** ur `TravianClient` (med enhetstester).
5. Inför VM-gränser panel för panel (`TroopTrainingViewModel` som mall).

**Lämna orört:** `TravianClient`-partialernas **navigations-/sekvenslogik** (fungerande men bräcklig).
Rör bara **stateless parsing** — inte klick-/navigeringsordningen.

**Riktmärken för "bra" framåt:**
- En ny förmåga ska kunna unit-testas till >50 % utan browser (parsing isolerad).
- Inga nya rå `async void` / spridda CTS-fält.
- God-klasserna ska **krympa eller stå still**, aldrig växa.
