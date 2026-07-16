# Plan: Mänskligare session-livscykel — stäng browsern vid sleep + lobby-login ("Play now")

## Context

Målet är att ta bort två bot-avslöjande mönster i hur boten hanterar sin session:

1. **Logout vid sleep.** Idag klickar boten logout varje gång den går ner i sleep
   ([MainWindow.SessionPacing.cs:239](src/TbotUltra.Desktop/MainWindow.SessionPacing.cs) →
   `LogoutCoreAsync(clearSavedSession:false)`) och behåller sedan webbläsaren öppen. En explicit
   `auth/logout` vid varje sömngräns (dussintals ggr/dygn) är ett maskin­regelbundet server-fotavtryck.
   En riktig spelare klickar nästan aldrig logout — de stänger webbläsaren och sessionen tonar ut till
   offline via serverns inaktivitets-timeout.

2. **Direkt server-login.** Idag loggar boten in direkt på spelserverns URL. Majoriteten av spelare går
   via travian.com-lobbyn: loggar in en gång, väljer sin spelvärld och klickar **"Play now"** som SSO:ar
   in i spelet. Det är det mänskligare flödet.

Tidigare humaniseringsarbete är redan implementerat och dokumenterat i `docs/ENGINEERING_NOTES.md`
(stealth-flaggor/`navigator.webdriver`/per-konto-viewport, riktiga Playwright-klick på farmlist-knappar,
farmlist send-all completion-fix, +15% video-completion-fix, alarm-exkludering för video-network-loggar).

**Beslut (bekräftade med användaren):** lobby-login är primärt med direkt server-login som fallback;
world-id (wuid) auto-lärs; manage-sidans UI ändras inte (fältet "Username/email" + Password + Server
räcker — Travians login tar e-post *eller* kontonamn i samma fält).

---

## Del A — Sleep stänger webbläsaren istället för att logga ut

**Fil:** [MainWindow.SessionPacing.cs](src/TbotUltra.Desktop/MainWindow.SessionPacing.cs) (~rad 231–249),
[MainWindow.Session.cs](src/TbotUltra.Desktop/MainWindow.Session.cs) (`LogoutCoreAsync`, rad 367).

Byt sleep-grenen från `LogoutCoreAsync(clearSavedSession:false)` till en ny `CloseBrowserForSleepAsync()`:

1. Spara sessionen till disk **före** stängning så den inloggade cookien garanterat finns kvar:
   exponera `BrowserSession.SaveStateAsync()` ([BrowserSession.StorageState.cs:11](src/TbotUltra.Worker/Infrastructure/BrowserSession.StorageState.cs))
   via `IDesktopBotService`/`BotTaskRunner` (ny tunn passthrough, alternativt en `SaveStateAndShutdownAsync`).
   *Not:* varje task sparar redan state ([BotTaskRunner.cs:530](src/TbotUltra.Worker/Services/BotTaskRunner.cs)),
   men ett explicit spar precis före stängning gör det robust.
2. Anropa befintlig `_botService.ShutdownAsync(AppendLog)` ([BotTaskRunner.cs:228](src/TbotUltra.Worker/Services/BotTaskRunner.cs))
   som disposear webbläsaren helt och rensar session-cache. Behåll StorageState-filen (rensa **aldrig** vid sleep).
3. Kör samma UI-state-reset som `LogoutCoreAsync` gör idag (`_isLoggedIn=false`, `NotifySessionPacingOnlineStopped()`,
   knappar, status) — men **utan** `ExecuteLogoutAsync` och utan att radera sparad session. Bryt ut den
   gemensamma UI-reseten ur `LogoutCoreAsync` till en privat hjälpare som båda återanvänder.

Proxy-change-grenen ([SessionPacing.cs:251-265](src/TbotUltra.Desktop/MainWindow.SessionPacing.cs)) kör redan
`ShutdownAsync` — den blir nu ett specialfall av samma väg (behåll dess extra proxy-spar-logik).

**Wake:** ingen ändring behövs. [SessionPacing.cs:298](src/TbotUltra.Desktop/MainWindow.SessionPacing.cs)
→ `TryWakeLoginWithRetryAsync` → `ExecuteLoginFlowAsync` öppnar ny webbläsare, återställer StorageState →
`LoginAsync` kortsluter med "already logged in" ([Login.cs:42](src/TbotUltra.Worker/Services/Automation/Core/TravianClient.Login.cs))
när spel-sessionen fortfarande gäller. Gått ut → faller tillbaka till full login (Del B) automatiskt.

**Oförändrat:** manuell Logout-knapp = riktig logout ([Session.cs:346](src/TbotUltra.Desktop/MainWindow.Session.cs));
kontobyte = logout + close för ren isolation.

---

## Del B — Lobby-login primärt, direkt server-login som fallback

Nytt inloggningsflöde i Worker. Lägg i ny partial `TravianClient.LobbyLogin.cs` och koppla in det överst i
`LoginAsync` ([TravianClient.Login.cs](src/TbotUltra.Worker/Services/Automation/Core/TravianClient.Login.cs)):
prova lobby-login först, fall tillbaka till nuvarande direkt-login vid fel.

**Live-verifierade selektorer/URL:er (från lobbyn idag):**
- Lobby: `https://lobby.legends.travian.com/account`
- Världskort: `div.gameworld.owner[data-wuid="<GUID>"]`, servernamn i `.gameworldName`, knapp `button.playNow`.
- "Play now" gör **same-tab SSO** direkt till `<base_url>/dorf1.php`, färdiginloggad. Ingen ny flik, ingen
  captcha i det steget (captcha finns bara vid lobbyns e-post/lösenord-login).
- Lägg URL:er/selektorer i [TravianClient.Selectors.cs](src/TbotUltra.Worker/Services/Automation/Core/TravianClient.Selectors.cs).

**Flöde:**
1. Navigera till lobbyn. Är sessionen redan giltig → kortslut (redan på account-sidan).
2. Ej autentiserad → fyll "Email address / account name" (från `AccountOptions.Username`) + Password och
   submit. Återanvänd befintlig credential-/klick-logik (`FillLoginCredentialsWithPacingAsync`,
   `TryClickFirstVisibleEnabledAsync` i Login.cs). Captcha/utmaning → befintligt `Challenge`-tillstånd
   och account-hold (ENGINEERING_NOTES "Account access").
3. Matcha rätt världskort (se **Auto-lärt world-id** nedan).
4. Klicka `button.playNow` i kortet med **riktig** Playwright-`ClickAsync` (samma trusted-klick-mönster som
   farmlist-knapparna).
5. Vänta på same-tab-navigering till spelservern; verifiera att landad host == kontots `base_url`-host **och**
   att befintliga inloggnings-markörer finns (`dorf1.php`-probe). Fel host → försök inte fler klick, gå till
   fallback.
6. **Fallback:** lobby onåbar / värld ej hittad / host-mismatch → kör nuvarande direkt server-login oförändrat.

---

## Del C — Manuell logout återvänder till lobbyn

Den manuella Logout-knappen förblir en **riktig** logout (beslutat), men med lobby-modellen ska den landa
på lobbyn — precis som spelets egna logout-länk:
`<a class="...logout" onclick="Travian.api('auth/logout'); return false;">`.

`LogoutAsync` ([Login.cs:204](src/TbotUltra.Worker/Services/Automation/Core/TravianClient.Login.cs)) klickar redan
det kontrollen (`Travian.api('auth/logout')`) via `TryTriggerLogoutAsync`. Problemet: bekräftelsen
(`WaitForLoggedOutAsync` / `IsLoggedOutPageVisibleAsync`, rad 278–299) letar idag bara efter **spelserverns**
egen login-form. Eftersom en lobby-SSO-session redirigerar till **lobbyn** vid logout måste bekräftelsen
även godta lobbyn som giltigt utloggat/återvänt-läge:

- Utöka de utloggade-markörerna (`IsLoggedOutPageVisibleAsync` + relevanta `Selectors`/`Paths.LogoutCandidates`)
  så att landning på `lobby.legends.travian.com/account` (eller lobbyns login-vy) räknas som lyckad logout,
  inte bara spelserverns login-form.
- **Verifiera live under implementation:** klicka spelets logout och observera exakt redirect-mål (lobby vs
  spel-login) — designa markörerna efter vad `auth/logout` faktiskt landar på för en lobby-SSO-session.
- Detta gäller den *manuella* logouten. Sleep (Del A) klickar aldrig logout alls — den stänger webbläsaren.

---

## Auto-lärt world-id (wuid)

`data-wuid` är den stabila nyckeln (lobbykortets GUID = spelserverns `server=<GUID>`), men finns **bara** i
lobbyn — inte i spelsidan. Därför auto-lärs den:

- **Lagring:** i Worker-sidans per-konto-`AccountAnalysisStore`
  ([AccountAnalysisStore.cs](src/TbotUltra.Worker/Services/Accounts/AccountAnalysisStore.cs)) — samma
  Worker-skrivbara plats som redan cachar tribe/goldclub. Lägg `WorldUid` på `AccountAnalysisSnapshot`.
  (Undviker att röra credential-env-pipelinen och kräver ingen Desktop write-back / UI-ändring.)
- **Matchning:** finns cachad `WorldUid` → matcha `.gameworld[data-wuid="<uid>"]` exakt. Saknas den →
  best-effort på normaliserat `.gameworldName` mot serverns namn/host (t.ex. host `ts50.x5.arabics…` ≈
  "Arabics 50"), klicka Play now, och **verifiera via host** (steg 5). Vid lyckad host-verifiering: spara
  kortets `data-wuid` som `WorldUid` för exakt matchning nästa gång.
- Har användaren flera kort som matchar namnet: prova i tur och host-verifiera; bara rätt host accepteras.

---

## StorageState måste behålla lobby-/auth-cookies

⚠️ `KeepHostForAccount` ([BrowserSession.StorageState.cs:153](src/TbotUltra.Worker/Infrastructure/BrowserSession.StorageState.cs))
behåller kontots host + föräldradomän (`.travian.com`) + sub-hostar men **släpper syskon**. `lobby.legends.travian.com`
är ett syskon och skulle strippas → lobby-sessionen skulle inte överleva close→öppna, vilket tvingar full
lobby-login (captcha-risk) vid varje re-login.

- Utöka `KeepHostForAccount` (eller lägg en explicit allowlist bredvid den) så Travians lobby-/auth-infrastruktur
  behålls: `lobby.legends.travian.com` och `*.legends.travian.com` samt travian.com-login/auth-hosten.
  **Behåll** befintlig strippning av (a) andra spelserver-syskon (cross-server-isolering) och (b) consent-cookies
  (`IsConsentStorageName`). Auditera exakta auth-cookie-hostar under implementation (kolla vilka hostar som
  håller lobby-session-cookien efter login).
- Spel-sessionens cookie ligger på kontots egen host/`.travian.com` och behålls redan → normal wake kortsluter
  ändå utan lobby. Lobby-cookien behövs bara för re-login efter att spel-sessionen gått ut (captcha-besparing).

---

## Filer som ändras (översikt)

- **Del A:** `MainWindow.SessionPacing.cs`, `MainWindow.Session.cs` (bryt ut UI-reset ur `LogoutCoreAsync`),
  `IDesktopBotService.cs` + `DesktopBotService.cs` + `BotTaskRunner.cs`/`BotTaskRunner.Session.cs`
  (exponera `SaveStateAsync`/`SaveStateAndShutdownAsync`).
- **Del B:** ny `TravianClient.LobbyLogin.cs`, `TravianClient.Login.cs` (koppla in lobby-först + fallback),
  `TravianClient.Selectors.cs` (lobby-URL/selektorer).
- **Del C:** `TravianClient.Login.cs` (`IsLoggedOutPageVisibleAsync`/`WaitForLoggedOutAsync`) +
  `TravianClient.Selectors.cs` (lobbyn som giltig utloggad-markör).
- **Auto-learn:** `AccountAnalysisSnapshot` (Worker.Domain) + `AccountAnalysisStore` (läs/skriv `WorldUid`).
- **Cookies:** `BrowserSession.StorageState.cs` (`KeepHostForAccount` allowlist).
- **Ingen UI-ändring** på manage-sidan (bekräftat).

## Verifiering

- **Sleep-close:** kör "Sleep now" → bekräfta i loggen att webbläsaren stängs (`ShutdownAsync`/`[browser-session]`)
  och att **ingen** `Starting logout`/`auth/logout` sker. Wake → `[login] already logged in as '...'` (ingen
  credential-login) och loop/queue återupptas.
- **Lobby-login (färsk):** rensa spel-sessionen, logga in → bekräfta navigering lobby → Play now på **rätt**
  värld → landar på `<base_url>/dorf1.php` inloggad; bekräfta att `WorldUid` sparats i analys-storen; andra
  inloggningen matchar direkt på wuid.
- **Fel-värld-skydd:** verifiera att host-kontrollen avvisar ett kort som SSO:ar till fel host och går till fallback.
- **Cookie-persistens:** stäng hela appen och starta om → fortfarande inloggad (bevisar att StorageState behåller
  spel- + lobby-cookies).
- **Manuell logout → lobby (Del C):** klicka appens Logout → bekräfta att spelet loggar ut via
  `Travian.api('auth/logout')`, landar på lobbyn och att appen registrerar det som lyckad logout (ingen timeout).
- **Fallback:** simulera lobby-fel (t.ex. blockera lobby-host) → bekräfta att direkt server-login tar över.
- `dotnet build TbotUltra.sln -c Release` + `dotnet test` (738 Worker + 422 Desktop). Lägg fokuserade
  enhetstester för wuid-matchning/normalisering och för `KeepHostForAccount` (lobby behålls, syskon-spelserver
  strippas).
