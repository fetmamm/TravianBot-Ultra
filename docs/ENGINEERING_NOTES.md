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

- `bot.json` är **global** (delas av alla konton) — by-/farmlist-pekare läcker mellan konton.
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
| NPC trade (öppna→fördela→Redeem) | ✅ (verifiera live) |
| Natar gömt på officiell | ✅ |
| Hero-attribut (auto-tilldela poäng) | ⛔ React — kräver capture när hjälten har poäng |
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
