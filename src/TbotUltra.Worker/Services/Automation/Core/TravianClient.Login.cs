using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Configuration;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace TbotUltra.Worker.Services;

// Session/account surface of the TravianClient facade. The interface list is
// declared on this partial to co-locate the contract with the domain it covers.
public sealed partial class TravianClient : ISessionClient
{
    // Login function
    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (await CanUseRecentLoginSuccessAsync(now, cancellationToken))
        {
            return;
        }

        Notify($"[login] Account='{_account.Name}' server='{ServerUrl}' — starting");
        if (IsConfiguredGameOrigin(_page.Url))
        {
            var state = await LoginStateAsync();
            ThrowIfAccountAccessBlocked(state);
            if (state == AccountAccessState.LoggedIn)
            {
                MarkSessionLoggedIn();
                Notify($"[login] already logged in as '{_account.Name}'");
                await ConfirmExpectedLanguageIfEnabledAndRefreshAccountSignalsAsync(cancellationToken);
                return;
            }

            if (state == AccountAccessState.Unknown)
            {
                Notify("[login] state unknown on an existing game page; rechecking dorf1 before opening the lobby.");
                state = await VerifyUnknownAccessStateAsync(cancellationToken);
                ThrowIfAccountAccessBlocked(state);
                if (state == AccountAccessState.LoggedIn)
                {
                    MarkSessionLoggedIn();
                    Notify($"[login] already logged in as '{_account.Name}' after dorf1 recheck");
                    await ConfirmExpectedLanguageIfEnabledAndRefreshAccountSignalsAsync(cancellationToken);
                    return;
                }
            }
        }

        if (await TryLoginThroughLobbyAsync(cancellationToken))
        {
            if (_rotateAfterLobbyLoginAsync is not null)
            {
                Notify("[lobby-login] SSO confirmed; isolating lobby state before normal game automation.");
                _page = await _rotateAfterLobbyLoginAsync(cancellationToken);
                await GotoAsync(Paths.Resources, cancellationToken);
                if (!await WaitUntilLoggedInAsync(cancellationToken))
                {
                    ThrowIfAccountAccessBlocked(await LoginStateAsync());
                    throw new InvalidOperationException("Lobby login state did not survive the clean browser-context rotation.");
                }

                Notify("[lobby-login] clean game context verified in the existing browser.");
            }
            else if (!await WaitUntilLoggedInAsync(cancellationToken))
            {
                throw new InvalidOperationException("Lobby login did not produce an authenticated game session.");
            }

            if (!string.IsNullOrWhiteSpace(_pendingLobbyWorldUid))
            {
                var worldUid = _pendingLobbyWorldUid;
                _pendingLobbyWorldUid = null;
                new AccountAnalysisStore(_projectRoot).SaveWorldUid(_account.Name, ServerUrl, worldUid);
                Notify($"[lobby-login] clean game context verified and world UID '{worldUid}' saved.");
            }

            MarkSessionLoggedIn();
            Notify($"[login] success ({_account.Name}) — entered through the Travian lobby");
            await ConfirmExpectedLanguageIfEnabledAndRefreshAccountSignalsAsync(cancellationToken);
            return;
        }

        throw new InvalidOperationException(
            "Login through the Travian lobby did not reach the configured game world. Direct server login is disabled.");
    }

    private void MarkSessionLoggedIn()
    {
        _lastEnsureLoggedInAt = DateTimeOffset.UtcNow;
        _lastEnsureLoggedInSucceeded = true;
        _session.ConsecutiveUnknownAccessStates = 0;
    }

    private async Task ConfirmExpectedLanguageIfEnabledAndRefreshAccountSignalsAsync(CancellationToken cancellationToken)
    {
        await EnsureExpectedLanguageIfEnabledAsync(cancellationToken);
        await RefreshAccountFeatureSignalsAsync(cancellationToken);
    }

    private async Task EnsureExpectedLanguageIfEnabledAsync(CancellationToken cancellationToken)
    {
        if (!_config.AutomaticallyCheckLanguage)
        {
            Notify("[language:verbose] automatic language check disabled in settings.");
            return;
        }

        await EnsureExpectedLanguageAsync(cancellationToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        Notify($"[logout] account='{_account.Name}' — starting");
        _accountTribe = null;
        _cachedAccountTribe = null;
        _session.VillageTribes.Clear();
        _cachedGoldClubEnabled = null;
        await GotoAsync(Paths.Resources, cancellationToken);
        if (!await IsLoggedInAsync())
        {
            Notify($"[logout] {_account.Name} was already logged out");
            return;
        }

        var clicked = await TryTriggerLogoutAsync(cancellationToken);
        if (clicked)
        {
            // Official T4.6 logout is an AJAX call (Travian.api('auth/logout')) that then redirects to
            // the login scene. Wait for a positive logged-out marker (login form) before declaring success.
            Notify("[logout] clicked logout control — waiting for the login page to confirm sign-out.");
            if (await WaitForLoggedOutAsync(cancellationToken))
            {
                Notify($"[logout] {_account.Name} logged out successfully");
                return;
            }

            Notify("[logout] click did not confirm sign-out — trying the Official logout URL.");
        }

        // Fallback when the logout control is missing or the click did not take effect.
        foreach (var candidatePath in Paths.LogoutCandidates)
        {
            await GotoAsync(candidatePath, cancellationToken);
            if (await WaitForLoggedOutAsync(cancellationToken))
            {
                Notify($"[logout] {_account.Name} logged out via navigation to {candidatePath}");
                return;
            }
        }

        throw new InvalidOperationException("Logout did not complete successfully.");
    }

    // Triggers the logout control. The official T4.6 control is an <a> with only an onclick
    // (Travian.api('auth/logout')) and is often hidden behind a menu, so a normal actionability-gated
    // click times out. Dispatching the click event runs the element's own handler/navigation directly,
    // without waiting for visibility/stability.
    private async Task<bool> TryTriggerLogoutAsync(CancellationToken cancellationToken)
    {
        foreach (var selector in Selectors.LogoutTriggers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _page.Locator(selector).CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await _page.DispatchEventAsync(selector, "click");
                Notify($"[logout] triggered logout via '{selector}'.");
                return true;
            }
            catch (PlaywrightException ex)
            {
                Notify($"[logout] dispatch on '{selector}' failed: {ex.Message}");
            }
        }

        return false;
    }

    // Confirms sign-out by waiting for a positive logged-out marker (login form / login scene), not just
    // the absence of logged-in markers — a page that is still rendering would otherwise read as a false
    // logout. Returns true as soon as the page state is confirmed "logged_out".
    private async Task<bool> WaitForLoggedOutAsync(CancellationToken cancellationToken)
    {
        const int attempts = 20;
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsLoggedOutPageVisibleAsync(cancellationToken))
            {
                return true;
            }

            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
        }

        Notify("[logout] sign-out not confirmed yet (no login page detected).");
        return false;
    }

    private async Task<bool> IsLoggedOutPageVisibleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentUrl = _page.Url.ToLowerInvariant();
        if (IsLobbyAccountUrl(currentUrl))
        {
            return true;
        }

        if (currentUrl.Contains("login.php", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            foreach (var selector in Selectors.LoggedOutIndicators)
            {
                if (await _page.Locator(selector).CountAsync() > 0)
                {
                    return true;
                }
            }
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }

        return false;
    }

    internal static bool IsLobbyAccountUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.Equals("lobby.legends.travian.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/account", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> CheckLoggedInAsync(CancellationToken cancellationToken = default)
    {
        Notify("CheckLoggedInAsync started");
        cancellationToken.ThrowIfCancellationRequested();
        return await IsLoggedInAsync();
    }

    private async Task EnsureLoggedInAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && await CanUseRecentLoginSuccessAsync(now, cancellationToken))
        {
            return;
        }

        // Routine "already logged in" is silent — only abnormal states (recovery/relogin/failure) below
        // log, so ensure-logged-in doesn't emit 3 lines on every background tick.
        var loggedIn = await IsLoggedInAsync();
        _lastEnsureLoggedInAt = now;
        _lastEnsureLoggedInSucceeded = loggedIn;
        if (!loggedIn)
        {
            // Session may have expired during a long idle wait (Travian logs the user out and
            // redirects to login.php). Re-use the existing LoginAsync flow rather than throwing —
            // BotTaskRunner already calls LoginAsync at the start of every feature, so this just
            // covers the in-feature drop case (and the keep-alive idle path). Other non-logged-in
            // Unknown/unavailable states remain transient failures; an explicit logged-out state
            // is the only state that starts the normal Official login flow.
            var state = await LoginStateAsync();
            if (state == AccountAccessState.Unknown)
            {
                Notify("[ensure-logged-in] state unknown; verifying the canonical village page before failing.");
                state = await VerifyUnknownAccessStateAsync(cancellationToken);
                Notify($"[ensure-logged-in] retry state={state} url='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
            }

            ThrowIfAccountAccessBlocked(state);
            if (state == AccountAccessState.LoggedIn)
            {
                _lastEnsureLoggedInAt = DateTimeOffset.UtcNow;
                _lastEnsureLoggedInSucceeded = true;
                Notify($"[ensure-logged-in] recovered after transient login-state miss url='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
                if (_suppressEnsureUiSyncDepth <= 0)
                {
                    await TryEmitUiSyncSnapshotAsync(cancellationToken);
                }

                return;
            }
            if (state == AccountAccessState.LoggedOut)
            {
                Notify("[ensure-logged-in] session expired — attempting auto-relogin");
                try
                {
                    await LoginAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _lastEnsureLoggedInSucceeded = false;
                    throw new InvalidOperationException($"Auto-relogin failed: {ex.Message}", ex);
                }
                _lastEnsureLoggedInAt = DateTimeOffset.UtcNow;
                _lastEnsureLoggedInSucceeded = await IsLoggedInAsync();
                if (!_lastEnsureLoggedInSucceeded)
                {
                    throw new InvalidOperationException("Auto-relogin completed but session is still not logged in.");
                }
                Notify("[ensure-logged-in] auto-relogin succeeded");
                if (_suppressEnsureUiSyncDepth <= 0)
                {
                    await TryEmitUiSyncSnapshotAsync(cancellationToken);
                }
                return;
            }

            if (state == AccountAccessState.Unavailable)
            {
                throw new TransientNavigationException(
                    $"Travian page is unavailable while checking login state. Url='{_page.Url}'.");
            }

            throw new InvalidOperationException($"Not logged in. Current page state is '{FormatAccessState(state)}'.");
        }
        if (_suppressEnsureUiSyncDepth <= 0)
        {
            await TryEmitUiSyncSnapshotAsync(cancellationToken);
        }
    }

    private async Task<bool> IsLoggedInAsync()
    {
        return (await LoginStateAsync()) == AccountAccessState.LoggedIn;
    }

    private async Task<AccountAccessState> LoginStateAsync()
    {
        try
        {
            // Cheap logged-out check FIRST: on a logout redirect the page is on login.php and is
            // actively navigating. Running the continue-prompt scan (which reads element text) against
            // a navigating page can hang on the default action timeout, so confirm sign-out by URL
            // before touching any element.
            var currentUrl = _page.Url.ToLowerInvariant();
            if (currentUrl.StartsWith("chrome-error://", StringComparison.Ordinal)
                || currentUrl.StartsWith("about:neterror", StringComparison.Ordinal))
            {
                Notify($"[ensure-logged-in] browser network error page detected url='{_page.Url}'.");
                return AccountAccessState.Unavailable;
            }

            if (await _page.Locator("body.neterror, #main-frame-error, .error-code").CountAsync() > 0)
            {
                Notify($"[ensure-logged-in] browser network error DOM detected url='{_page.Url}'.");
                return AccountAccessState.Unavailable;
            }

            var explicitState = await ProbeExplicitAccountAccessStateAsync(currentUrl);
            if (explicitState is not null)
            {
                return explicitState.Value;
            }

            if (currentUrl.Contains("login.php", StringComparison.Ordinal))
            {
                return AccountAccessState.LoggedOut;
            }

            await TryDismissContinuePromptAsync();

            // The page can still be settling right after a navigation/reload (especially the
            // official React in-game pages). Probing the indicators too early yielded a false
            // 'unknown' → "Not logged in. Current page state is 'unknown'." task failures even
            // though we were on an authenticated page (e.g. dorf1.php). Retry the probe a few
            // times with a short wait before giving up. The happy path (indicator already present)
            // returns immediately on the first attempt with no delay.
            const int probeAttempts = 4;
            for (var attempt = 0; attempt < probeAttempts; attempt++)
            {
                foreach (var selector in Selectors.LoggedInIndicators)
                {
                    if (await _page.Locator(selector).CountAsync() > 0)
                    {
                        return AccountAccessState.LoggedIn;
                    }
                }

                foreach (var selector in Selectors.LoggedOutIndicators)
                {
                    if (await _page.Locator(selector).CountAsync() > 0)
                    {
                        return AccountAccessState.LoggedOut;
                    }
                }

                if (attempt < probeAttempts - 1)
                {
                    // Give the DOM a moment to render the topbar/village list, then re-probe.
                    await Task.Delay(Random.Shared.Next(300, 600)); // Random wait
                }
            }

            Notify("Login state is unknown");
            return AccountAccessState.Unknown;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify("Page navigated while checking login state. State is unknown.");
            return AccountAccessState.Unknown;
        }
    }

    private async Task<AccountAccessState?> ProbeExplicitAccountAccessStateAsync(string currentUrl)
    {
        var captchaInputPresent = await HasAnySelectorAsync(Selectors.AccountChallengeInputField);
        var pageSignal = await _page.EvaluateAsync<string>(
            """
            () => {
              const title = (document.title || '').toLowerCase();
              const text = (document.body?.innerText || '').slice(0, 12000).toLowerCase();
              return `${title}\n${text}`;
            }
            """);
        return AccountAccessClassifier.ClassifyExplicit(currentUrl, pageSignal, captchaInputPresent);
    }

    private async Task<bool> CanUseRecentLoginSuccessAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!IsRecentLoginCacheEligible(
                _lastEnsureLoggedInSucceeded,
                _lastEnsureLoggedInAt,
                now,
                _page.Url,
                ServerUrl))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var explicitState = await ProbeExplicitAccountAccessStateAsync(_page.Url.ToLowerInvariant());
            ThrowIfAccountAccessBlocked(explicitState ?? AccountAccessState.LoggedIn);
            return explicitState is null or AccountAccessState.LoggedIn;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    internal static bool IsRecentLoginCacheEligible(
        bool previousCheckSucceeded,
        DateTimeOffset previousCheckAt,
        DateTimeOffset now,
        string? currentUrl,
        string serverUrl)
    {
        var age = now - previousCheckAt;
        return previousCheckSucceeded
            && age >= TimeSpan.Zero
            && age < EnsureLoggedInMinInterval
            && IsConfiguredGameOrigin(currentUrl, serverUrl)
            && !(currentUrl?.Contains("login", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private async Task<AccountAccessState> VerifyUnknownAccessStateAsync(CancellationToken cancellationToken)
    {
        AccountAccessState state;
        try
        {
            await GotoAsync(Paths.Resources, cancellationToken);
            state = await LoginStateAsync();
        }
        catch (TransientNavigationException)
        {
            _session.ConsecutiveUnknownAccessStates = 0;
            return AccountAccessState.Unavailable;
        }

        var circuitState = AccountAccessClassifier.RegisterVerifiedState(
            _session.ConsecutiveUnknownAccessStates,
            state);
        _session.ConsecutiveUnknownAccessStates = circuitState.ConsecutiveUnknown;
        if (state != AccountAccessState.Unknown)
        {
            return state;
        }

        Notify($"[account-access] canonical page remained unknown ({_session.ConsecutiveUnknownAccessStates}/3).");
        if (circuitState.Stop)
        {
            throw new AccountAccessException(
                _account.Name,
                AccountAccessState.Unknown,
                "The canonical village page remained in an unknown access state after three verified checks.");
        }

        return state;
    }

    private void ThrowIfAccountAccessBlocked(AccountAccessState state)
    {
        if (state is not (AccountAccessState.Restricted or AccountAccessState.Challenge))
        {
            return;
        }

        var reason = state == AccountAccessState.Restricted
            ? "Travian displayed an explicit account restriction."
            : "Travian displayed a security challenge that requires manual review.";
        throw new AccountAccessException(_account.Name, state, reason);
    }

    private static string FormatAccessState(AccountAccessState state) => state.ToString().ToLowerInvariant();

    // Fills the login form the way a person would: focus and type each credential character by
    // character, with a pause between fields and before submitting. Shared by the lobby, dedicated
    // login page, and inline "form already on page" flows.
    private async Task FillLoginCredentialsWithPacingAsync(CancellationToken cancellationToken)
    {
        await FillFirstAvailableAsync(Selectors.LoginUsernameField, _account.Username, cancellationToken);
        await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait

        await FillFirstAvailableAsync(Selectors.LoginPasswordField, _account.Password, cancellationToken);
        await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
    }

    private async Task FillFirstAvailableAsync(IEnumerable<string> selectors, string value, CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            await RetryAsync($"type {selector}", async () =>
            {
                await TypeHumanlyAsync(locator, value, cancellationToken);
            }, cancellationToken: cancellationToken);
            return;
        }

        throw new InvalidOperationException($"Could not find input field for selectors: {string.Join(", ", selectors)}.");
    }

    private async Task<bool> HasAnySelectorAsync(IEnumerable<string> selectors)
    {
        foreach (var selector in selectors)
        {
            if (await _page.Locator(selector).CountAsync() > 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task ClickLoginButtonAsync(CancellationToken cancellationToken)
    {
        foreach (var selector in Selectors.LoginButton)
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            await RetryAsync($"click login selector {selector}", async () =>
            {
                await DelayBeforeClickAsync(cancellationToken, "lobby login submit");
                await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
            }, cancellationToken: cancellationToken);
            Notify("Clicked login button.");
            return;
        }

        // Fallback: no recognizable login button — submit the form by pressing Enter in the
        // password field. Many login forms submit on Enter even without a matched button selector.
        foreach (var selector in Selectors.LoginPasswordField)
        {
            var passwordField = _page.Locator(selector).First;
            if (await passwordField.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await DelayBeforeClickAsync(cancellationToken, "lobby login submit via Enter");
                await passwordField.PressAsync("Enter", new LocatorPressOptions { Timeout = _config.TimeoutMs });
                Notify("Login button not found; submitted the form via Enter.");
                return;
            }
            catch (PlaywrightException)
            {
                // Try next selector.
            }
            catch (TimeoutException)
            {
                // Try next selector.
            }
        }

        throw new InvalidOperationException("Could not find login button.");
    }

    private static bool IsLikelyGamePageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.Equals("/dorf1.php", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/dorf2.php", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/spieler.php", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/build.php", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/profile", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/hero", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/messages", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/report", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Polls for any of the given selectors until one appears or the timeout elapses. Used to
    /// tolerate a slow-loading login page before interacting with the form.
    /// </summary>
    private async Task<bool> WaitForAnySelectorAsync(IEnumerable<string> selectors, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await HasAnySelectorAsync(selectors))
                {
                    return true;
                }
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                // Page is mid-navigation; retry below.
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
        }
    }

    private static readonly string[] LoginErrorPhrases =
    {
        "wrong password",
        "password is wrong",
        "password is incorrect",
        "incorrect password",
        "invalid password",
        "name or password",
        "name or the password",
        "username or password",
        "invalid credentials",
        "does not exist",
        "doesn't exist",
        "unknown user",
        "user not found",
        "account not found",
        "too many login attempts",
    };

    /// <summary>
    /// While still on the login page, returns a visible error message (wrong credentials,
    /// unknown user, etc.) if Travian rendered one, so the caller can fail fast with a clear
    /// reason instead of waiting for the full login timeout. Returns null when no error is shown.
    /// </summary>
    private async Task<string?> TryReadVisibleLoginErrorAsync()
    {
        try
        {
            if (!_page.Url.Contains("login", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return await _page.EvaluateAsync<string?>(
                """
                (phrases) => {
                  const isVisible = (node) => {
                    if (!node || !(node instanceof Element)) return false;
                    const style = window.getComputedStyle(node);
                    if (!style || style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity || '1') === 0) return false;
                    const rect = node.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0;
                  };
                  const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();

                  const errorNodes = document.querySelectorAll('.error, p.error, .errorMessage, .error_message, [class*="error" i], [class*="warning" i]');
                  for (const node of errorNodes) {
                    if (!isVisible(node)) continue;
                    const text = clean(node.innerText || node.textContent);
                    if (text && text.length > 0 && text.length <= 200) return text;
                  }

                  const bodyText = clean(document.body && document.body.innerText).toLowerCase();
                  for (const phrase of phrases) {
                    if (bodyText.includes(phrase)) return phrase;
                  }

                  return null;
                }
                """,
                LoginErrorPhrases);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
    }

    private async Task<bool> TryClickFirstVisibleEnabledAsync(
        string selector,
        CancellationToken cancellationToken,
        string? requiredText = null,
        bool requireExactText = false,
        string? reason = null,
        int? timeoutMs = null)
    {
        var candidates = _page.Locator(selector);
        var count = await candidates.CountAsync();
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates.Nth(i);

            try
            {
                if (!await candidate.IsVisibleAsync())
                {
                    continue;
                }

                var disabled = await candidate.EvaluateAsync<bool>(
                    """
                    node => {
                      const className = (node.className || '').toString().toLowerCase();
                      return !!node.disabled
                        || node.getAttribute('disabled') !== null
                        || node.getAttribute('aria-disabled') === 'true'
                        || className.includes('disabled');
                    }
                    """);
                if (disabled)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(requiredText))
                {
                    var candidateText = await candidate.EvaluateAsync<string>(
                        """
                        node => {
                          const clean = value => (value || '').replace(/\s+/g, ' ').trim();
                          const primary = clean(node.textContent || node.getAttribute('value') || '');
                          if (primary) return primary;
                          return clean(`${node.getAttribute('title') || ''} ${node.getAttribute('aria-label') || ''}`);
                        }
                        """);
                    var expected = requiredText.Trim();
                    var matches = requireExactText
                        ? string.Equals(candidateText, expected, StringComparison.OrdinalIgnoreCase)
                        : candidateText.Contains(expected, StringComparison.OrdinalIgnoreCase);
                    if (!matches)
                    {
                        continue;
                    }
                }

                await candidate.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions
                {
                    Timeout = Math.Min(_config.TimeoutMs, 3000),
                });
                await DelayBeforeClickAsync(cancellationToken, reason);
                await candidate.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs ?? _config.TimeoutMs });
                return true;
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                throw;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                Notify($"[browser-click] Playwright click skipped candidate {i + 1}/{count} for '{selector}': {ex.Message}");
            }
        }

        return false;
    }

    private async Task<bool> WaitUntilLoggedInAsync(CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Max(10, _config.ManualLoginTimeoutSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var pollCount = 0;
        Notify($"[login:verbose] waiting for login confirmation (timeout={timeoutSeconds}s)");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pollCount++;
            if (DateTime.UtcNow >= deadline)
            {
                Notify($"[login] timeout: login not confirmed after {timeoutSeconds}s (polls={pollCount})");
                throw new InvalidOperationException("Login was not confirmed before timeout.");
            }

            try
            {
                await TryDismissContinuePromptAsync(cancellationToken);

                if (await IsLoggedInAsync())
                {
                    Notify($"[login:verbose] login confirmed after {pollCount} poll(s)");
                    return true;
                }

                ThrowIfAccountAccessBlocked(await LoginStateAsync());

                // Fail fast on an explicit credential/account error instead of waiting the full
                // login timeout (which can be minutes).
                var loginError = await TryReadVisibleLoginErrorAsync();
                if (loginError is not null)
                {
                    Notify($"[login] credential/account error visible on page: {loginError}");
                    throw new InvalidOperationException($"Login failed: {loginError}");
                }

                await Task.Delay(Random.Shared.Next(400, 600), cancellationToken); // Random wait
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                Notify("Page navigated while checking login state. Retrying...");
                await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
            }
        }
    }

}
