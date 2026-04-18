# Tbot_ultra

## Syfte
Skapa ett program som kan köras lokalt på datorn för att automatisera uppgifter i spelet Travian.

Programmet ska kunna arbeta självgående, vara diskret och ha ett kösystem där olika uppgifter kan läggas till, tas bort och flyttas i ordning.

## Mål
- Programmet ska vara stabilt och enkelt att felsöka.
- Programmet ska vara enkelt att bygga vidare på och utöka med fler funktioner.
- Programmet ska kunna upptäcka fel och hantera dem utan att fastna.
- Programmet ska kunna automatisera återkommande uppgifter i Travian på ett pålitligt sätt.
- Programmet ska kunna hantera flera typer av uppgifter genom ett kösystem.

## MVP / Första version
Funktioner som ska prioriteras först:
- Logga in
- Läsa in kontots byar
- Läsa av byggnader och resursfält
- Uppgradera resursfält
- Uppgradera byggnader
- Hantera kö för uppgifter
- Kunna lägga till, ta bort och ändra ordning på uppgifter i kön
- Grundläggande felhantering och retry-logik

## Funktioner
### Konto och grunddata
- Logga in
- Hantera flera konton och byta mellan dem
- Läsa in antal byar
- Läsa av vilken stam användaren är
- Läsa av nivå på byggnader och resursfält

### Bygg och utveckling
- Uppgradera resursfält
- Uppgradera byggnader
- Bygga nya byar

### Trupper
- Bygga trupper
- Uppgradera trupper
- Forska fram nya trupper

### Hjälte
- Kontrollera hjältestatus
- Skicka hjälten på äventyr
- Återuppliva hjälten om den är död

### Militär och resurser
- Skicka trupper som förstärkning
- Skicka trupper som attack
- Skicka resurser
- NPC-handla

### Informationsinsamling
- Scanna kartan efter oaser
- Scanna spelare på servern samt deras byar och koordinater
- Läsa rapporter

### Skydd och notifieringar
- Notifiera vid attacker
- Skicka iväg trupper vid attack för att skydda dem

## Kösystem
Programmet ska ha ett kösystem där:
- Uppgifter kan läggas till
- Uppgifter kan tas bort
- Uppgifter kan pausas
- Uppgifters ordning kan ändras
- Programmet vet vilken uppgift som ska köras härnäst
- Misslyckade uppgifter kan försökas igen enligt regler

## Krav på kod och struktur
- Koden ska vara stabil och enkel att felsöka
- Koden ska vara enkel att utöka med nya funktioner
- Undvik onödigt komplex logik
- Behåll samma struktur och stil i projektet
- Ändra inte fungerande kod i onödan
- Gör små och tydliga ändringar hellre än stora omskrivningar
- Lägg till tydlig felhantering där det behövs

## Edge cases
- Sidan svarar långsamt
- Fel sida är vald
- Sidan hänger sig
- Sidan laddar inte korrekt
- Element finns inte där de förväntas finnas
- Användaren är utloggad
- Konto/session har gått ut
- En uppgift kan inte utföras just nu
- Data som läses in är ofullständig eller felaktig
- Samma uppgift riskerar att köras två gånger
- Köobjekt blir ogiltigt eller saknar data

## Framtida funktioner
Funktioner som inte måste byggas först men som kan läggas till senare:
- Skicka attacker automatiskt
- Automatiska farms/farmlistor
- Avancerad scanning av kartan
- Avancerad rapportanalys


