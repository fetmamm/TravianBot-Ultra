# ADR: Official farmlists och Travco

## Status

Aktivt beslut, 2026-06-09.

## Beslut

- Official farmlists lases fran renderad React-markup under `#rallyPointFarmList`.
- Analyze Farmlists anvander huvudfonstrets gemensamma, avbrytbara loading-overlay.
- Create Farmlists analyserar befintliga listor fore skapande, stoppar dubblettnamn och verifierar varje skapad lista.
- Official har hogst 100 farms per lista; kapacitet lases om fore varje add.
- Official Add Farms anvander sparade Travco-listor.
- Add target kor sekventiellt, verifierar exakt list-id, vantar pa React Save och fortsatter efter valideringsfel.
- Travco DOM-resultat lases som `JsonElement` och konverteras darefter till domanmodeller.

## Konsekvenser

Browseroperationer ska kunna avbrytas och UI visar sammanlagd progress. Koordinatdubbletter
filtreras och stale UI-kapacitet far aldrig kunna overskrida servergransen. Full detaljhistorik
finns i `docs/history/engineering-notes-archive.md`.
