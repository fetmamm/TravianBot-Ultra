## Läs först
- Läs och följ `docs/ENGINEERING_NOTES.md` innan du ändrar selektorer, sökvägar eller serverlogik.
- Den innehåller projektets konventioner, regler för de två servervarianterna (Official/SS-Travi), beslutslogg och kända fallgropar — följ dem och uppdatera filen löpande med relevanta delar.

## Kodregler
- Försök att inte ändra kod som inte behöver ändras.
- Ändra bara det som är nödvändigt för att lösa uppgiften.
- Hellre robust och lätt kod att felsöka än "perfekt" men svårförståelig kod.
- Försök att använda samma struktur, stil och namngivning som redan finns i projektet.
- Skriv tydlig och praktisk kod framför smart men svårläst kod.
- All kod skrivs på engelska. UI ska vara på engelska.
- Duplicera inte kod i onödan. Försök återanvända om det går.
- Skriv kod som går att återanvända och är enkel att underhålla och felsöka
- Skriv loggar i nya funktioner med mer så det enkelt går att felöka i framtiden

## Vid ändringar
- Ändra inte befintliga funktioner i onödan.
- Behåll befintligt beteende om jag inte uttryckligen ber om något annat.
- Gör små, lokala ändringar hellre än stora omskrivningar.
- Om en funktion redan fungerar, bygg vidare på den istället för att skriva om den.
- Ändra endast de filer och metoder som är direkt relevanta för uppgiften.
- README ska uppdateras när något ändras som är värt att beskriva.

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
