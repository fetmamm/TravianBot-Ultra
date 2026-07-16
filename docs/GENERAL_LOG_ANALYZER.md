## Generellt prompt för att analysera loggfil och identifiera onödiga / dubbla navigationer för att optimera funktioner och göra mer mänskliga.

Analysera loggfilen och identifiera möjligheter att minska onödiga navigeringar, sidbesök, omladdningar, sidläsningar och duplicerade åtgärder.

Logfil: "C:\Users\jespe\Documents\GitHub\Tbot_ultra_new\logs\TbotUltra_Log_20260716_221725.txt"

Målet är inte att göra programmet snabbare, utan att göra funktionerna smartare, mer mänskliga och att minska onödigt beteende.

Leta särskilt efter:

- Samma sida som öppnas flera gånger inom kort tid
- Samma information som läses flera gånger utan att något verkar ha förändrats
- Navigeringar där programmet lämnar en sida och kort därefter återvänder till samma sida
- Onödiga refresh/omladdningar
- Upprepade analyser av samma data som skulle kunna återanvändas
- Flera funktioner som besöker samma sida oberoende av varandra
- Navigeringskedjor som skulle kunna förkortas
- Åtgärder som skulle kunna slås ihop till ett enda sidbesök
- Beteenden som verkar onaturliga eller onödigt robotlika

För varje förbättringsförslag ska du redovisa:

1. Problembeskrivning
2. Varför beteendet verkar onödigt
3. Förslag på optimering
4. Bedömd påverkan (Låg/Medel/Hög)

Sammanfatta även:

- Mest besökta sidor
- Mest frekventa åtgärder
- Möjliga dubbelläsningar av information
- Möjliga dubbla navigeringar
- De 10 bästa optimeringsmöjligheterna sorterade efter förväntad nytta

Fokusera på arbetsflöde, navigering och logik. Fokusera inte på kodstil, prestandaoptimeringar eller mikroskopiska tidsvinster om de inte samtidigt minskar antalet navigeringar eller gör beteendet mer mänskligt.