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

---

## 5. Kända fallgropar / regressions

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
