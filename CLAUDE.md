## Läs först
- Läs och följ `docs/ENGINEERING_NOTES.md` innan du ändrar selektorer, sökvägar eller serverlogik.
- Den innehåller projektets konventioner, regler för de två servervarianterna (Official/SS-Travi), beslutslogg och kända fallgropar — följ dem och uppdatera filen löpande med relevanta delar.
- `docs/ARCHITECTURE.md` är fil- och funktionskartan: var varje feature bor + namn-/strukturkonventioner. Använd för att hitta rätt fil snabbt och håll den uppdaterad när strukturen ändras.

## Kodregler
- Försök att inte ändra kod som inte behöver ändras.
- Ändra bara det som är nödvändigt för att lösa uppgiften.
- Hellre robust och lätt kod att felsöka än "perfekt" men svårförståelig kod.
- Försök att använda samma struktur, stil och namngivning som redan finns i projektet.
- Skriv tydlig och praktisk kod framför smart men svårläst kod.
- All kod skrivs på engelska. UI ska vara på engelska.
- Duplicera inte kod i onödan. Försök återanvända om det går.
- Skriv kod som går att återanvända och är enkel att underhålla och felsöka
- Skriv loggar i nya funktioner så det enkelt går att felöka i framtiden

## Hjälpfunktioner
- Skapa inte hjälpfunktioner enbart för att minska antalet kodrader.
- Skapa endast en ny hjälpfunktion om minst ett av följande gäller:
  - Logiken används på flera ställen.
  - Hjälpfunktionen representerar ett tydligt domänkoncept eller en affärsoperation.
  - Hjälpfunktionen kapslar in komplex logik, felhantering, retry-logik eller tillståndshantering.
- Undvik att skapa hjälpfunktioner som:
  - Endast omsluter ett enskilt anrop till ett ramverk eller bibliotek.
  - Bara sparar några få kodrader.
  - Endast används på ett ställe och inte förbättrar läsbarheten.
  - Skapar onödiga lager av abstraktion.
- Prioritera läsbarhet där funktionen anropas framför överdriven abstraktion.

## Vid ändringar
- Ändra inte befintliga funktioner i onödan.
- Behåll befintligt beteende om jag inte uttryckligen ber om något annat.
- Gör små, lokala ändringar hellre än stora omskrivningar.
- Om en funktion redan fungerar, bygg vidare på den istället för att skriva om den.
- Ändra endast de filer och metoder som är direkt relevanta för uppgiften.

## Felsökning och robusthet
- Hantera vanliga felfall om det är relevant.
- Lägg hellre till tydliga kontroller än att anta att allt alltid finns.
- Om något kan gå fel, gör det tydligt varför.
- Hantera edge cases tydligt.
- Lägg till retry-logik där det behövs.

## Kommunikation
- Om det finns flera rimliga lösningar, välj en och motivera kort (1 mening).
- Om viktig information saknas, ställ en kort fråga istället för att gissa.
- Gör inga antaganden om krav som inte uttryckligen angivits.
- Utöka inte uppgiften med egna förbättringar om det inte efterfrågas.

## Svarformat
- Svara så kort som möjligt.
- Inga långa förklaringar.
- Ingen introduktion eller sammanfattning.
- Förklara ändringen kort (max 2–4 meningar).
- Om svaret kan vara under 20 rader, ska det vara det.
-- Om en plan ska presenteras kan svaret vara längre.