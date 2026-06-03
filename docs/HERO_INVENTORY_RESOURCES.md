# Hero Inventory — auto-resynk från transfer-dialogen

## Context

Boten visar och använder hjältens inventarie-resurser (wood/clay/iron/crop) både i Hero-fliken
och för att avgöra om en blockerad uppgradering kan lösas med en hjälte­transfer. Dessa värden
hålls i en **in-memory cache** som mellan fulla avläsningar kan **drifta** — särskilt om användaren
pausar boten, spelar manuellt och använder hjälte­resurser, och sedan startar boten igen.

Användaren har upptäckt att "Transfer resources"-dialogen redan innehåller hjältens **faktiska
aktuella mängder** per resurs (`<div class="count">…</div>`). Idag läser boten bara de
*auto-ifyllda transfer-beloppen* från dialogen, inte de faktiska saldona. Målet är att använda
dialogens `.count`-värden som en gratis automatisk resynk varje gång hjälte­transfer-funktionen
körs — utan extra navigering.

---

## Nuvarande beteende (analys)

**Hur lagras hjälte­inventariet?**
- Modell: `HeroInventoryResources(Wood,Clay,Iron,Crop)` — [TravianModels.cs:119](src/TbotUltra.Worker/Domain/TravianModels.cs:119).
- Cache: statisk `CachedHeroInventoryByKey` keyed `account|serverUrl`, **endast i minnet, ej diskpersisterad** —
  [TravianClient.HeroResourceTransfer.cs:252](src/TbotUltra.Worker/Services/Automation/TravianClient.HeroResourceTransfer.cs:252).
- UI: `HeroViewModel` (4 strängfält + status), uppdateras via `ApplyInventory` —
  [HeroViewModel.cs:140](src/TbotUltra.Desktop/ViewModels/HeroViewModel.cs:140).

**Används interna/cacheade värden?** Ja. Två uppdateringsvägar:
1. **Full avläsning** `ReadHeroInventoryResourcesAsync` läser `/hero/inventory`
   (`.item145..148 .count`) → `UpdateHeroInventoryCache` (auktoritativt) —
   [TravianClient.Hero.cs:2222](src/TbotUltra.Worker/Services/Automation/TravianClient.Hero.cs:2222).
2. **Efter en transfer**: `DeductFromHeroInventoryCache` drar bara av de auto-ifyllda transfer-beloppen
   (beräknat, ingen ny avläsning) — [TravianClient.HeroResourceTransfer.cs:350](src/TbotUltra.Worker/Services/Automation/TravianClient.HeroResourceTransfer.cs:350).
   Den proaktiva grinden `HeroCoversShortfall` läser cachen för att avgöra om dialogen ens ska öppnas.

**När synkas mot verkliga värden?**
- Post-login full avläsning: `PostLoginAnalyzeHeroInventory` — **default i koden är `true`**
  ([BotOptions.cs:90](src/TbotUltra.Core/Configuration/BotOptions.cs:90),
  [BotOptionsFactory.cs:52](src/TbotUltra.Core/Configuration/BotOptionsFactory.cs:52)) — körs via
  [BotTaskRunner.cs:274](src/TbotUltra.Worker/Services/BotTaskRunner.cs:274). *(OBS: ENGINEERING_NOTES säger
  "default OFF" — noten är inaktuell mot koden.)*
- Manuell "Refresh hero inventory"-knapp (`RefreshHeroInventoryCoreAsync`).
- Efter transfer: **endast subtraktion**, ingen verklig avläsning.

**Görs faktisk avläsning efter login?** Ja, post-login (när påslaget, default true). Annars bara via
Refresh-knappen.

**Finns risk för drift? — Ja.** Cachen är i minnet och resynkas bara vid full avläsning. Mellan
avläsningar:
- Manuell hjälte­resursanvändning upptäcks inte → cachen ligger för högt.
- Varje transfer förvärrar med ren subtraktion (felet ackumuleras).
- Pausa → spela manuellt → återuppta **utan ny login** → cachen är stale tills nästa fulla avläsning.

**Korrigeras det automatiskt?** Delvis. Cachen självläker vid nästa fulla avläsning (post-login eller
manuell refresh), men **inte** under en pågående session utan ny avläsning.

**Konsekvens:** Den proaktiva grinden kan fatta fel beslut (hoppa över en möjlig transfer, eller öppna
dialogen i onödan) och UI:t visar stale siffror. Inte katastrofalt — Travian visar bara transfer-ikonen
när hjälten faktiskt har resurser och auto-fyller verkliga belopp vid bekräftelse — men *beslutet* och
*UI:t* kan bli fel.

**Flera källor ur synk?** Det finns i praktiken **en** sanningskälla (`CachedHeroInventoryByKey`) med
UI:t som nedströms spegel via `HeroInventoryUpdated`-eventet. Hjälte-*attribut*-snapshotens `Resources`
är en annan sak (attributpoäng, inte inventarie) och krockar inte. UI uppdateras redan korrekt via eventet.

**Görs den föreslagna resynken redan?** **Nej.** `TryHeroResourceTransferForConstructionAsync` läser bara
`input[name="lumber/clay/iron/crop"]` (transfer-belopp), inte dialogens `.count` (faktiska saldon) —
[TravianClient.HeroResourceTransfer.cs:128-161](src/TbotUltra.Worker/Services/Automation/TravianClient.HeroResourceTransfer.cs:128).

---

## Rekommenderad lösning

Läs hjältens **faktiska** `.count`-saldon ur transfer-dialogen och använd dem som auktoritativ resynk
**innan** bekräfta-klicket, i `TryHeroResourceTransferForConstructionAsync`. Behåll den efterföljande
subtraktionen — den arbetar då mot ett nyss resynkat (korrekt) bas­värde. Ingen extra navigering.

Detta är beteendebevarande och additivt enligt AGENTS.md/ENGINEERING_NOTES (§2 selektorer, §8 logik).

### Ändringar

**1. Läs `.count` ur dialogen** — [TravianClient.HeroResourceTransfer.cs](src/TbotUltra.Worker/Services/Automation/TravianClient.HeroResourceTransfer.cs),
i `TryHeroResourceTransferForConstructionAsync`, direkt efter att dialog-inputs bekräftats finnas
(efter raderna som idag läser `transferred`, ~rad 128-161):
   - Ny JS-läsning som hittar dialogen (samma `div.resourceTransferDialog` / `#dialogContent`-mönster
     som redan används) och läser de fyra `.count`-värdena, **ankrade per resurs**. Mappa varje count
     till rätt resurs via samma container som dess `input[name="lumber|clay|iron|crop"]` (vi litar redan
     på dessa namn) snarare än ren positions­ordning — robustare mot markup­ändringar. Returnera JSON
     `{wood,clay,iron,crop}`.
   - Deserialisera till `HeroInventoryResources` (samma `PropertyNameCaseInsensitive`-mönster som
     `transferred`).
   - Om läsningen lyckas (icke-null): `UpdateHeroInventoryCache(actual)` → detta är auktoritativt och
     fyrar `HeroInventoryUpdated` → Hero-fliken uppdateras live (befintlig väg via
     [MainWindow.Hero.cs:320](src/TbotUltra.Desktop/MainWindow.Hero.cs:320)).
   - Logga: `[hero-transfer] inventory resynced from dialog: wood=… clay=… iron=… crop=…`.

**2. Behåll subtraktionen efter bekräftelse** — den befintliga `DeductFromHeroInventoryCache(transferred)`
(~rad 238-241) ligger kvar oförändrad. Eftersom cachen nyss satts till dialogens faktiska saldon blir
subtraktionen korrekt. (Alternativ: avstå subtraktion och förlita sig enbart på `.count` — men `.count`
visar saldot *före* transfer, så subtraktionen behövs för slutvärdet.)

**Ingen ändring behövs** i UI-lagret, eventet, eller den proaktiva grinden: grinden körs före dialogen
och fortsätter använda cachen som finns; efter denna ändring blir cachen korrekt inför *nästa* beslut.

### Filer som ändras
- `src/TbotUltra.Worker/Services/Automation/TravianClient.HeroResourceTransfer.cs` (enda kodändringen).
- `docs/ENGINEERING_NOTES.md`: en rad i Beslutslogg (§4) om dialog-resynk; passa på att rätta den
  inaktuella "default OFF"-noten för `PostLoginAnalyzeHeroInventory` (koden = default true).

---

## Verifiering

1. `dotnet build TbotUltra.sln` + `dotnet test`.
2. **Live (official-server, opt-in `HeroResourceTransferEnabled` på):** trigga en resursblockad
   uppgradering så transfer-dialogen öppnas. Bekräfta i loggen att
   `[hero-transfer] inventory resynced from dialog: …` matchar dialogens synliga `.count`-siffror, och
   att Hero-fliken uppdateras live till samma värden.
3. **Drift-scenario:** notera Hero-fliken → pausa → använd hjälte­resurser manuellt i spelet → återuppta
   och låt en transfer ske → bekräfta att Hero-fliken nu visar de korrekta (manuellt minskade) värdena
   utan en separat `/hero/inventory`-navigering.
4. Snabb SS-körning: hjälte­transfer är official-only (`_config.IsPrivateServer` returnerar tidigt),
   så ingen regression på SS.

## Riskbedömning
- **Låg.** Additiv läsning + ett befintligt cache-uppdaterings­anrop; ingen ändrad klick-/navigerings­ordning.
- Enda osäkerheten är dialogens `.count`-markup (ordning/struktur). Mildras genom att ankra count→resurs
  via input-namnen och genom live-verifiering (steg 2). Misslyckas läsningen faller koden tillbaka till
  dagens beteende (ingen resynk, subtraktion som förut).
