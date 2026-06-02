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
  (`PostLoginAnalyzeHeroInventory`, default OFF). Adventures-kortet flyttat upp i Settings-kortet.
- **2026-06-02** — Hero inventory cachas i minnet (statisk dict keyed `account|baseUrl`, som hero-attribut-
  snapshoten). Uppdateras vid varje full läsning och efter en transfer (drar av de auto-fyllda beloppen, ingen
  extra navigering). Statiskt event `TravianClient.HeroInventoryUpdated(account, resources)` → Desktop
  uppdaterar Hero-fältens UI live (filtrerat på aktivt konto, avregistreras i `OnClosed`). Ingen
  proaktiv "skippa om tomt"-logik ännu — Travian visar bara `.transfer`-ikonen när hjälten har resurser,
  så tom inventory ger naturligt ingen transfer.

---

## 5. Kända fallgropar / regressions

- **Hero-loop → `/hero/login.php`** = flavor är fel (servern tolkas som Official på en SS-server).
  Kontrollera `[flavor]`-raden. Grundorsak historiskt: config-bunden flavor.
- **Profil-koordinater:** selektor-omordning (prioritera `karte.php?x=`-länk) ändrade SS-beteende
  medvetet — resultatet är lika/bättre, men det är inte ren additiv.
- **React-sidor utan render-väntan** → läser tomt / "not clickable". Vänta alltid in render.

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
