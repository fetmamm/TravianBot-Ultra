# Logganalys: onödiga/dubbla navigeringar — TbotUltra_Log_20260716_182058.txt

## Context
Generell logganalys (enligt `docs/GENERAL_LOG_ANALYZER.md`) av en ~70 min session (18:20–19:30, 3 login/sleep-cykler, 4 byar: 00 SRW, 01 PHAL, 02 KONG, 03 DRU). Målet är att hitta onödiga navigeringar och robotlika mönster — inte prestanda. Totalt: **83 GOTO + 4 RELOAD**, varav **26 bybyten (newdid)** — alltså går ~1/3 av alla sidladdningar åt till bybyten.

---

## Fynd

### 1. Bybytes-pingpong: round-robin över byar i tät följd — **Hög**
**Problem:** Loopen plockar tasks från olika byar direkt efter varandra. Exempel 18:49:21–18:49:35: PHAL → DRU → KONG på 14 sekunder (3 bybyten). Värst 19:12:43–19:12:58: byter till DRU, deferrar **3 s** (humanize), loopen kör då KONG-task (nytt byte), och 12 s senare byter tillbaka till DRU.
**Varför onödigt:** En människa gör klart det den håller på med i en by innan den byter. 26 bybyten för ~16 lyckade klick är extremt robotlikt, och varje byte kostar en dorf1-load + sidebar-scan.
**Förslag:** (a) Gruppera körbara köobjekt per by i loop-picken — kör alla ready tasks för aktuell by innan byte. (b) Om en task deferrar kort (< ~60–90 s, t.ex. humanize-delay), låt loopen vänta kvar i byn istället för att plocka en task i en annan by.
**Påverkan: Hög** (störst enskild källa till navigeringar).

### 2. Bybyte → omedelbar defer p.g.a. humanize-delay som beräknas först efter navigering — **Hög**
**Problem:** `upgrade_all_resources_to_level` byter by, skannar dorf1, räknar ut humanize-delay ur kösituationen — och deferrar direkt utan att göra något (18:24:00 → defer 109 s; 18:36:46 → 18 s; 18:49:27 → 37 s; 19:12:43 → 3 s). Fyra hela bybyten vars enda resultat var "vänta X sekunder".
**Varför onödigt:** Kö-sluttiden var redan känd — `construction-queue` loggar själv `retryAt`/`waitSeconds` från förra besöket, och byggköns sluttid ändras inte av sig själv.
**Förslag:** Beräkna humanize-delayen från cachad kö-sluttid (village-cache/queue-trackern) **innan** bybyte; navigera först när tasken faktiskt kan klicka. Alternativt: spara den framräknade delayen vid föregående defer så nästa körning schemaläggs rätt direkt.
**Obs:** cachen används bara för *schemaläggningen* (när det är lönt att åka dit). Själva klickbeslutet baseras fortfarande på en färsk sidläsning väl på plats — ingen live-läsning av kö eller resurser ersätts av cache vid själva åtgärden.
**Påverkan: Hög**.

### 3. `upgrade_building_to_level` går alltid dorf2 → build.php, aldrig direkt — **Medel/Hög**
**Problem:** Varje försök loggar `slot 40: current page snapshot unavailable; reading dorf2 overview` och gör dorf2-goto/-reload + build.php-goto (18:31:20, 18:37:32+18:38:00, 18:49:33, 18:59:53, 19:13:39, 19:28:17 — 7 ggr bara för KONG slot 40).
**Varför onödigt:** Slot→gid och nuvarande nivå finns i byggnads-cachen (`Loaded cached buildings/fields for 7 village(s)`), och byggsidan visar själv nivå/kö-status. dorf2-hoppet tillför bara kö-läsning som även den finns cachad.
**Förslag:** Gå direkt till `build.php?id=X` när cache-snapshot finns och läs status där; fall tillbaka på dorf2-läsning endast när cachen saknas/är stale. (Filer: `TravianClient.Navigation.cs` / construction-nav-bucketlogiken.)
**Får INTE tas bort (viktigt):**
- **Byggnadsöversikten från dorf2** är källan till vilka byggnader som finns + nivåer. `load_buildings_snapshot`-tasken (18:31:10, `ReadBuildingsStatusAsync`, "Loaded 22 building slots") behålls orörd, och upgrade-flödets dorf2-läsning behålls som fallback när snapshot-cachen saknas/är stale — det är bara det *ovillkorliga* dorf2-hoppet vid varje försök som tas bort.
- **Byggkö-status** visas bara på dorf1/dorf2, inte på build.php. Om dorf2-hoppet hoppas över måste kö-gaten komma från cachad kö-data (queue-trackerns `retryAt`/sluttider) med färskhetsgräns; vid stale cache eller osäkerhet görs en riktig dorf1/dorf2-läsning som idag.
- **Post-click-verifieringen** påverkas inte: klicket på build.php redirectar ändå till dorf2 (syns i loggen som `from='...dorf2.php?id=40&gid=33'`), så kö-läsningen efter klick sker utan extra navigering och ska vara kvar.
**Påverkan: Medel/Hög** (~6–7 sparade dorf2-loads per timme, plus mer mänskligt: man klickar på byggnaden man ska uppgradera, inte via översikten varje gång).

### 4. Bounce: lämnar build-sidan och återvänder direkt — **Medel**
**Problem:** Två varianter:
- 18:59:57: på `build.php?id=19`, hero-transfer-offer detekteras → navigerar till dorf2 → 2 s senare tillbaka till `build.php?id=19`.
- 19:16:35: hero-transfer klar på `build.php?id=9` → går till dorf1 enbart för att läsa kön/beräkna humanize-delay → deferrar 40 s → 19:17:20 tillbaka till `build.php?id=9`.
**Varför onödigt:** Informationen som hämtas på mellansidan (kö-status) finns på build-sidan eller i cache; att studsa bort-och-tillbaka på samma sida inom sekunder är ett tydligt botmönster.
**Förslag:** Låt flödet efter hero-transfer/blocked-analys stanna kvar på build-sidan och läsa kö-gaten där (eller ur cachen), och navigera bara bort om nästa åtgärd faktiskt ligger på annan sida.
**Påverkan: Medel**.

### 5. Hero-äventyr efter bonusvideo: adventures → dorf1 → adventures på 2 s — **Medel**
**Problem:** 19:29:46: isolerade videobrowsern stängs, huvudbrowsern går till dorf1 (`main browser returned to dorf1 after isolated video browser`), och 2 s senare tillbaka till `hero/adventures` för att skicka hjälten.
**Varför onödigt:** Nästa steg (välja/skicka äventyr) ligger på sidan man just lämnade. dorf1-hoppet är en ren mellanlandning.
**Förslag:** I `IncreaseAdventuresToHardAsync`-flödet (`BrowserSession.BonusVideo.cs`): när nästa åtgärd är äventyrsutskick, återvänd direkt till/stanna på `hero/adventures` istället för dorf1.
**Påverkan: Medel**.

### 6. Post-click-verifiering hittar aldrig kö-posten → fallback-läsningar + fel defer-orsak — **Medel**
**Problem:** 16 lyckade klick men **32** rader `queue changed but no <X> entry was found` — verifieringen misslyckas för i princip varje byggnad/fält (Palisade, Cropland, Iron mine, Clay pit, Warehouse...), trots att kön bevisligen ändrades och nivåerna stegar upp (Palisade 2→3→4→5...).
**Varför onödigt:** Namnmatchningen mot köposten är trasig (matchar aldrig). Det triggar extra dubbel-läsningar av kön, "did not confirm"-defers med gissad väntetid istället för läst kö-tid, och onödiga återbesök.
**Förslag:** Felsök matchningen (trolig orsak: köpostens text-/namnformat på Official skiljer sig från förväntat, t.ex. lokaliserat namn eller nivåformat). Rätta selektorn/jämförelsen så bekräftelsen fungerar; då försvinner fallback-läsningarna och deferschemat blir korrekt.
**Påverkan: Medel** (färre extra läsningar + korrektare schemaläggning ⇒ färre återbesök).

### 7. `ReadVillageStatusAsync` läser dorf1+dorf2 direkt efter att en task nyss varit där — **Medel**
**Problem:** 18:49:42: KONG-tasken var på dorf2 18:49:34 och slutade på build-sidan 18:49:39; 3 s senare kör statusläsningen dorf1 → dorf2 igen. Samma mönster 18:29:26, 19:13:13, 19:19:28 (5 körningar totalt).
**Varför onödigt:** Samma sidor läses om inom sekunder utan att något hunnit ändras (förutom det egna klicket, vars effekt redan lästes av tasken).
**Förslag:** Ge statusläsningen en färskhets-gräns: hoppa över dorf1/dorf2-besök om samma sida lästes < N min sedan (återanvänd senaste DOM-läsning/cache). Mönstret finns redan delvis: `construction-refresh: current-page refresh used; skipped full dorf1+dorf2 read` — utöka samma princip till `ReadVillageStatusAsync`.
**Påverkan: Medel**.

### 8. `hero_manage` navigerar till adventures trots att hjälten är borta — **Låg/Medel**
**Problem:** 18:36:25: "unassigned points signal detected" → går till `hero/adventures` → konstaterar hero away → deferrar 3127 s. Hela besöket gav bara information som redan syns i sidhuvudet (hjältestatus/away visas på alla sidor och loggas redan vid start: `[hero] home village=... away=False`).
**Varför onödigt:** Away-status kan läsas från aktuell sida innan navigering.
**Förslag:** Läs hero-away/HP-indikatorn från topbaren på nuvarande sida först; navigera till hero-sidorna endast när en åtgärd faktiskt är möjlig.
**Påverkan: Låg/Medel**.

### 9. Video-flöden laddar samma sida i två browsers — **Låg**
**Problem:** 18:48:04 laddas `build.php?id=19` i huvudbrowsern, 18:48:05 laddas exakt samma URL i den isolerade videobrowsern. Samma sak 19:28:38/19:28:40 med `hero/adventures`.
**Varför onödigt (delvis):** Dubbelladdning inom 1–2 s. Dock är den isolerade browsern ett medvetet designval (3p-cookies/codec-krav för Official-videos), så en viss dubblering är svår att undvika.
**Förslag:** Behåll designen, men låt huvudbrowsern hoppa över sin egen "button state"-koll när beslutet att köra video redan är taget (chance-gaten rullas före navigeringen), eller vänta med huvudbrowserns load tills videon är klar (den behöver ändå gå till dorf2 för verifiering efteråt).
**Påverkan: Låg**.

### 10. Produktions-/fältscan körs om vid varje task-start — **Låg**
**Problem:** `UpgradeAllResourcesToLevelAsync` skannar 18 fält + läser produktion vid varje körning (18:24, 18:25, 18:36, 18:37, 18:49, 19:12...), även när förra körningen var < 1 min sedan.
**Varför onödigt:** Ingen extra navigering (läser aktuell dorf1-sida), men dubbelanalys av oförändrad data.
**Förslag:** Cacha fältscan/produktion några minuter per by och återanvänd inom samma loopvarv.
**Påverkan: Låg** (ingen navigeringsvinst, men mindre robotlikt DOM-tuggande).

---

## Sammanfattning

**Mest besökta sidor** (GOTO, exkl. reloads):
| Sida | Antal |
|---|---|
| dorf1.php (inkl. 26 newdid-byten) | 33 |
| build.php | 23 |
| dorf2.php | 17 |
| hero/adventures | 4 |
| lobby /account | 3 |
| hero/attributes, /statistics, /statistics/player/top10 | 1 vardera |

**Mest frekventa åtgärder:** bybyte via sidebar (26), pacing-väntor (174), build-/upgrade-klick (16 lyckade), hero-transfers (10 bekräftade), bonusvideos (2), login (3).

**Möjliga dubbelläsningar:** dorf2-översikten läses av både construction-task och `ReadVillageStatusAsync` inom sekunder (18:49:34→18:49:44); fältscan+produktionsläsning upprepas per task-körning; post-click-verifieringen läser kön 2 ggr per klick och misslyckas alltid (32 träffar).

**Möjliga dubbla navigeringar (bounces):** build19→dorf2→build19 (18:59:57, 5 s); build9→dorf1→(40 s)→build9 (19:16:35); adventures→dorf1→adventures (19:29:46, 2 s); DRU→KONG→DRU (19:12:43–58, 15 s).

**Topp 10 optimeringar efter förväntad nytta:**
1. Gruppera loop-picken per by; byt inte by för en task som ändå deferrar kort (fynd 1) — Hög
2. Beräkna humanize-delay från cachad kö-sluttid före bybyte (fynd 2) — Hög
3. Gå direkt till build.php via cachad slot→gid istället för dorf2-omvägen (fynd 3) — Medel/Hög
4. Fixa post-click-köverifieringens namnmatchning (fynd 6) — Medel
5. Stanna på build-sidan efter hero-transfer/blocked-analys, ingen dorf1/dorf2-studs (fynd 4) — Medel
6. Färskhets-gräns i `ReadVillageStatusAsync` (återanvänd nyss lästa dorf1/dorf2) (fynd 7) — Medel
7. Hoppa över dorf1-mellanlandningen efter adventure-video (fynd 5) — Medel
8. Läs hero-away från topbaren innan hero-sidnavigering (fynd 8) — Låg/Medel
9. Slopa huvudbrowserns förberedande sidladdning när videobeslut redan är taget (fynd 9) — Låg
10. Cacha fältscan/produktion per by inom loopvarvet (fynd 10) — Låg

**Positivt (redan bra):** quick re-login utan analyzes (<120 min), `already on '<by>' — no navigation needed` (12 undvikna byten), `construction-refresh: current-page refresh ... skipped full dorf1+dorf2 read`, idle-browse till statistik (mänskligt), humanize-delays och pacing.

## Verifiering (om åtgärder implementeras)
Kör en motsvarande session med dev-loggning och jämför: antal `[nav] GOTO`, antal `sidebar matched`-bybyten, antal `queue changed but no`-rader (ska bli 0), samt att inga bounce-mönster (X→Y→X < 60 s) förekommer i `[nav]`-tidslinjen.
