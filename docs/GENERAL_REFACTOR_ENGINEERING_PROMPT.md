# Prompt: Komprimera ENGINEERING_NOTES.md

Du ska refaktorera `ENGINEERING_NOTES.md` så att filen blir kort, aktuell och användbar som styrande dokumentation för projektet.

## Mål

`ENGINEERING_NOTES.md` ska vara maximalt **400 rader**.

Filen ska innehålla det som en utvecklare eller AI-agent behöver läsa innan den gör ändringar i projektet.

## Viktigt

- Gör inga funktionella kodändringar.
- Ändra inte projektets beteende.
- Kapa inte bara bort information.
- Bevara viktig information genom att flytta äldre detaljer till arkiv-/ADR-filer.
- Behåll svensk text där texten redan är på svenska.
- Håll `ENGINEERING_NOTES.md` kort, konkret och styrande.

## Uppgift

1. Läs igenom hela `ENGINEERING_NOTES.md`.

2. Dela upp innehållet i:
   - Aktiva arkitekturregler
   - Viktiga kodningskonventioner
   - Official-first regler och kvarvarande legacy-grenar
   - Selektorregler
   - Aktuella kända fallgropar
   - Nuvarande målarkitektur
   - Historiska beslut
   - Detaljerad ändringshistorik

3. Skriv om `ENGINEERING_NOTES.md` till en kortare version på max 400 rader.

4. Behåll särskilt:
   - Projektöversikt
   - Arkitektur: `Core`, `Worker`, `Desktop`
   - Regler för Official-only-beteende
   - Additiva selektorändringar
   - Flavor-aware paths
   - Regler för nya features
   - Regler för parsers, ViewModels och `TravianClient`
   - De viktigaste fallgroparna som fortfarande gäller
   - Länkar till arkiverade ADR-/historikfiler

5. Flytta äldre beslut och lång historik till separata filer.

Föreslagen struktur:

```text
docs/
  adr/
    2026-06-03-ui-theme.md
    2026-06-05-multi-village.md
    2026-06-06-dashboard-overview.md
    2026-06-08-shutdown-cleanup.md
    2026-06-09-farmlists-and-travco.md
  history/
    engineering-notes-archive.md
```

6. Om ett beslut fortfarande påverkar hur kod ska ändras:
   - behåll en kort sammanfattning i `ENGINEERING_NOTES.md`
   - flytta detaljerna till en ADR-fil
   - länka från `ENGINEERING_NOTES.md` till ADR-filen

7. Om något bara är ändringshistorik:
   - flytta det till `docs/history/engineering-notes-archive.md`

8. Lägg gärna till en kort sektion i `ENGINEERING_NOTES.md` som heter:

```markdown
## Arkiverad historik

Äldre beslut och detaljerad historik finns i:

- `docs/adr/`
- `docs/history/engineering-notes-archive.md`
```

## Krav efter ändring

Efter refaktoreringen ska du kontrollera:

- `ENGINEERING_NOTES.md` är max 300 rader
- äldre viktig information finns kvar i ADR-/historikfiler
- länkarna från `ENGINEERING_NOTES.md` fungerar
- inga kodfiler har ändrats

## Rapportera efteråt

Skriv en kort rapport med:

- antal rader före
- antal rader efter
- vilka filer som skapades
- vilka sektioner som kortades ned
- om något togs bort helt och varför
- bekräftelse på att inga kodfiler ändrades
