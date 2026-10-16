# Betald version av Tbot Ultra — issue #131

## Mål och beslut

Alla ska kunna ladda ned programmet öppet. Användaren skapar ett Tbot-konto med verifierad e-post och lösenord, antingen i programmet eller på hemsidan. Licensen gäller per person, för alla personens Travian-konton, på högst två registrerade programkopior totalt. Båda kopiorna får köras på samma dator. Användaren kan själv ta bort en kopia från kontot.

- **Nya användare:** Tre kalenderdagars full tillgång från första automationsstart. Därefter högst tre timmars aktiv automation per dag.
- **Befintliga användare:** En personlig engångskod ger tre kalendermånaders full tillgång från första automationsstart efter inlösen. Koden ska lösas in och automationen startas inom 30 dagar från lansering. Dessa användare får inte även tredagarsperioden.
- **Betalning:** Förbetalda engångsköp utan automatisk förnyelse: 1 månad €2,99, 2 månader €4,99, 6 månader €13,99 och 12 månader €24,99. Köpt tid läggs efter återstående full tillgång.
- **Gratisgräns:** Tre timmar delas mellan båda programkopiorna och återställs kl. 00.00 i tidszonen Europe/Stockholm. Stoppad automation, Smart Sleep, inställningar och avstängt program förbrukar ingen tid. Manuellt startade botåtgärder räknas. En pågående åtgärd får avslutas säkert när gränsen nås; ingen ny startar.
- Programmet visar en diskret, tydlig nedräkning under provperiod och tremånadersperiod samt återstående gratistid och nästa återställning därefter. Gränser och datum beräknas av servern.

## Genomförande i ordning

1. **Konton och licensserver:** Använd Supabase Auth för inloggning och verifieringsmejl. Lagra licensperioder, köp, kopieregistreringar, engångskoder, daglig förbrukning och administratörsändringar i Supabase. Servern är ensam källa till licensbeslut; hemliga nycklar finns aldrig i programmet eller hemsidans klientkod. Använd `tbotultra@gmail.com` som anpassad SMTP-avsändare i Supabase Auth under den första hobbylanseringen.
2. **Programmet:** Lägg licenskontrollen vid automationsstart och alla manuella botåtgärder. Låt `AutomationDesk` samordna stopp och återstart enligt projektets befintliga arkitektur; återanvänd inte användarens konfigurerbara `SessionPacer`-gräns som licensgräns. Mät aktiv tid på servern med korta, tidsbegränsade körningsrättigheter så att samtidig användning i två kopior förbrukar den gemensamma kvoten. Visa konto-, licens- och tidsstatus i UI.
3. **Betalning:** Köpknappen öppnar Stripe Checkout i webbläsaren. En serverfunktion skapar köpet för den inloggade användaren. En verifierad Stripe-webhook lägger till tid exakt en gång per betalt köp; återbetalning eller bestridd betalning återkallar tiden från det köpet. Logga händelserna och visa exakt slutdatum. Manuella tilldelningar och rättelser görs i Supabases adminpanel med ändringslogg.
4. **Distribution och privat källkod:** Skapa ett separat offentligt GitHub-repo med endast färdiga Windows-versioner, kontrollsumma, versionsinformation och nedladdningsinstruktioner. Bygg från det privata källkodsrepot och publicera dit med snävt avgränsad behörighet. Ändra uppdateraren, som i dag pekar på källkodsrepot. Publicera först en övergångsversion via den gamla kanalen och testa uppdatering till den nya; gör källkodsrepot privat först därefter. Äldre redan spridda versioner kan inte återkallas tekniskt.
5. **Hemsidan sist, före lansering:** Bygg en liten sida på Cloudflare Pages från det privata källkodsrepot. Publik del: beskrivning, paketpriser och länk till GitHub Releases. **Mina sidor:** skapa konto, logga in, återställ lösenord, se licens och köp, köpa mer tid samt hantera registrerade programkopior. Den använder samma Supabase-konto och Stripe-flöde som programmet. Börja på `pages.dev`; en egen domän kan kopplas senare.
6. **Lansering:** Testa köp och återbetalningar i Stripe testläge, säkerhetskopiera licensdata, publicera sekretess- och köpvillkor, publicera hemsida och program, och skicka de personliga koderna via Discord.

## Viktiga gränssnitt och felhantering

Klienten behöver serveranrop för **licensstatus, kopieregistrering, start/förnyelse/stopp av aktiv tid, kodinlösen och skapande av Checkout-session**. Varje kopia har ett eget beständigt kopie-ID, även när två kopior finns på samma dator. Både program och hemsida använder samma kontoid och får endast läsa eller ändra den inloggade användarens uppgifter.

Vid serveravbrott får en tidigare verifierad betald licens användas i högst 24 timmar, aldrig efter sitt slutdatum. Provperiod och gratisnivå pausas tills servern åter kan kontrollera tiden. En ny provperiod ges högst en gång per konto och, som skydd mot enkelt missbruk, per dator; legitima undantag kan ges manuellt. Maskinidentifieringen är ett praktiskt hinder, inte ett säkert bevis på fysisk dator eller ett absolut kopieringsskydd.

## Verifiering och antaganden

Testa särskilt kalendermånader och sommartid, två samtidiga kopior på samma dator, stopp av en tredje kopia, gränsen mitt i en åtgärd, sömn och manuella botåtgärder, omstart och nätavbrott, dubbla eller försenade webhooks, återbetalning, kodens tidsgräns, kopiebyte samt uppdatering från gamla GitHub-repot. Godkänn inte lansering förrän ett fullständigt testköp ger rätt licens i både programmet och Mina sidor.

De angivna europriserna behandlas som **slutpriser inklusive tillämplig skatt**. Stripe Managed Payments är förstahandsval för internationella köp, men betalfunktionen öppnas först efter att Stripe bekräftat att kontot och produkten är berättigade. Gratisnivåerna för [Cloudflare Pages](https://developers.cloudflare.com/pages/functions/pricing/) och [Supabase](https://supabase.com/docs/guides/platform/billing-on-supabase) samt Gmail SMTP är startval för cirka 50 användare; följ faktisk förbrukning och säkra återställbara kopior av betalda licenser. En egen domän kan väljas senare.
