# ADR: Map Oasis Analyzer och kartparsning

## Status

Aktivt beslut, 2026-06-20. Detaljerna bakom de korta reglerna i
`ENGINEERING_NOTES.md` (sektion 4 Desktop, Map Oasis). Travco-listornas scraping
ligger i [farmlists-and-travco](2026-06-09-farmlists-and-travco.md).

## Skanning och lagring

- Map Oasis Analyzer anvander den inloggade Official-sessionens `POST /api/v1/map/position`
  med zoom level 3. Skanningen serialiseras genom `BotTaskRunner`, har retry/pacing och parsern
  ska forbli browserfri och enhetstestbar.
- Skanningscheckpoint och senast kompletta resultat lagras konto-/serverspecifikt under
  `config/accounts/<account>/cache/map-oasis/`; checkpoint ateranvands endast med samma filter.
- Oaslistor ateranvander kontoavgransade `travco_lists.json`; oasfalt ar valfria sa aldre
  Travco-listor och Official-importens koordinatflode forblir kompatibla.

## Kartparsern

- Kartparsern tar endast `did == -1` med titel `{k.fo}` eller `{k.bt}` och tolkar bonusarna
  `{a:r1}`-`{a:r4}` i tile-texten. `{k.bt}`/`uid` betyder occupied; okanda kombinationer ignoreras.
- Koordinater las via `position.x/y` (kan ocksa ligga top-level eller som strangar). Tile-texten har
  Unicode bidi-tecken (U+202D/U+202C) som maste strippas (kategori `Format`) fore regex.
- Lediga oaser har djur (`{k.animals}`, enheter `u31`-`u40`); ockuperade har `uid`/`aid` och
  `{k.spieler}`/`{k.allianz}`/`{k.volk}` men inga djur. Bada falten ar valfria i sparade listor.
- Official `map.sql` innehaller endast byar och far inte anvandas som oaskalla.

## Konsekvenser

Kartdata kommer fran den live API-sessionen, inte `map.sql`. Parsern ar browserfri och enhetstestbar,
och sparade listor ar bakatkompatibla (valfria oasfalt). Full bakgrund finns i
`docs/history/engineering-notes-archive.md`.
