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
    // Post-login the game shell is already confirmed (WaitUntilLoggedInAsync), so give the browser 'load'
    // event only a short best-effort window to settle CSS/images. Travian's login landing page pulls in
    // third-party ad/consent/video iframes that can stall 'load' indefinitely; blocking the full
    // _config.TimeoutMs (~20s) here just wasted time and false-alarmed on an already-loaded page.
    private const int PostLoginLoadSettleTimeoutMs = 5000;

    // Login function
    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        Notify("[LoginAsync started]");
        Notify($"[login] Account='{_account.Name}' server='{ServerUrl}' — starting");
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before login.", cancellationToken);
        var state = await LoginStateAsync();
        if (state == "logged_in")
        {
            Notify($"[login] already logged in as '{_account.Name}'");
            await ConfirmExpectedLanguageIfEnabledAndRefreshAccountSignalsAsync(cancellationToken);
            return;
        }

        if (state == "unknown" && IsLikelyGamePageUrl(_page.Url))
        {
            Notify("[login] state unknown on game page; rechecking dorf1 before opening login page.");
            await GotoAsync(Paths.Resources, cancellationToken);
            if (await IsLoggedInAsync())
            {
                Notify($"[login] already logged in as '{_account.Name}' after dorf1 recheck");
                await ConfirmExpectedLanguageIfEnabledAndRefreshAccountSignalsAsync(cancellationToken);
                return;
            }
        }


        var loggedInFromCurrentPage = await TryLoginUsingCurrentPageAsync(cancellationToken);
        if (loggedInFromCurrentPage)
        {
            Notify($"[login] success ({_account.Name}) — used existing page form");
            await RefreshAccountFeatureSignalsAsync(cancellationToken);
            return;
        }

        await GotoAsync(Paths.Login, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on the login page.", cancellationToken);
        if (await IsLoggedInAsync())
        {
            Notify($"[login] success ({_account.Name}) — already authenticated after opening login page");
            await ConfirmExpectedLanguageIfEnabledAndRefreshAccountSignalsAsync(cancellationToken);
            return;
        }

        // Tolerate a slow-loading login page: wait for the form to appear before filling it.
        if (!await WaitForAnySelectorAsync(Selectors.LoginUsernameField, TimeSpan.FromSeconds(15), cancellationToken))
        {
            // Maybe a redirect logged us in, or a captcha/manual step is blocking the form.
            if (await IsLoggedInAsync())
            {
                await ConfirmExpectedLanguageIfEnabledAndRefreshAccountSignalsAsync(cancellationToken);
                return;
            }

            if (!await CaptchaOrManualStepVisibleAsync())
            {
                throw new InvalidOperationException("Login form did not load (the page may be slow or unavailable).");
            }
        }

        await FillLoginCredentialsWithPacingAsync(cancellationToken);

        if (await CaptchaOrManualStepVisibleAsync())
        {
            await CaptureManualVerificationScreenshotAsync("login-page", cancellationToken);

            if (!_browserVisible)
            {
                throw new ManualVerificationRequiredException(
                    "Captcha/manual verification appeared while running headless.");
            }

            Notify($"[login] ALARM: captcha/manual step detected for {_account.Name}. Solve it manually in the browser — bot is paused.");
            await WaitForManualVerificationToClearAsync(cancellationToken);
        }
        else
        {
            await ClickLoginButtonAsync(cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        }

        var loggedIn = await WaitUntilLoggedInAsync(cancellationToken);
        if (!loggedIn)
        {
            throw new InvalidOperationException("Login did not complete successfully.");
        }
        await EnsureExpectedLanguageIfEnabledAsync(cancellationToken);

        // Settle the post-login landing page (dorf1) before any task navigates away. DOMContentLoaded
        // fires before stylesheets/scripts finish, which made the bot switch pages half-loaded and
        // produced transient 'unknown' login-state reads; wait for it first (fast and reliable), then
        // give the full 'load' event only a SHORT best-effort chance to settle CSS/images.
        try
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = _config.TimeoutMs,
            });

            // The game is already confirmed ready (DOM parsed above + logged-in shell via
            // WaitUntilLoggedInAsync), so a 'load' that never fires because of stalled ad/consent
            // resources is benign — log it verbose and proceed instead of blocking + false-alarming.
            try
            {
                await _page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions
                {
                    Timeout = PostLoginLoadSettleTimeoutMs,
                });
                Notify("[login] page successfully loaded.");
            }
            catch (PlaywrightException)
            {
                Notify("[login:verbose] full 'load' event did not fire within the settle window (third-party ad/consent resources still pending); DOM is ready, proceeding.");
            }

            await ActionPacer.FromOptions(_config, Notify).DelayAsync(
                _config.ActionPacingPageLoadMinSeconds,
                _config.ActionPacingPageLoadMaxSeconds,
                cancellationToken,
                "login: after page load");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Notify($"[login:verbose] post-login page settle did not complete: {ex.Message}");
        }
        Notify($"[login] success ({_account.Name}) — submitted credentials and confirmed");
        await RefreshAccountFeatureSignalsAsync(cancellationToken);
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

    private async Task<bool> TryLoginUsingCurrentPageAsync(CancellationToken cancellationToken)
    {
        var hasUsernameField = await HasAnySelectorAsync(Selectors.LoginUsernameField);
        var hasPasswordField = await HasAnySelectorAsync(Selectors.LoginPasswordField);

        if (!hasUsernameField || !hasPasswordField)
        {
            return false;
        }

        Notify($"[login] form already on current page — submitting inline for {_account.Name}");

        await FillLoginCredentialsWithPacingAsync(cancellationToken);

        if (await CaptchaOrManualStepVisibleAsync())
        {
            await CaptureManualVerificationScreenshotAsync("login-current-page", cancellationToken);

            if (!_browserVisible)
            {
                throw new ManualVerificationRequiredException(
                    "Captcha/manual verification appeared while running headless.");
            }
        }
        else
        {
            await ClickLoginButtonAsync(cancellationToken);
        }

        var loggedIn = await WaitUntilLoggedInAsync(cancellationToken);
        if (loggedIn)
        {
            await EnsureExpectedLanguageIfEnabledAsync(cancellationToken);
            Notify($"[login] success ({_account.Name}) — inline form submitted");
        }

        return loggedIn;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        Notify($"[logout] account='{_account.Name}' — starting");
        _sessionTribe = null;
        _cachedTribe = null;
        _cachedGoldClubEnabled = null;
        await GotoAsync(Paths.Resources, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before logout.", cancellationToken);
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
        if (currentUrl.Contains("login.php", StringComparison.Ordinal))
        {
            Notify("You are logged out");
            return true;
        }

        try
        {
            foreach (var selector in Selectors.LoggedOutIndicators)
            {
                if (await _page.Locator(selector).CountAsync() > 0)
                {
                    Notify("You are logged out");
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

    public async Task<bool> CheckLoggedInAsync(CancellationToken cancellationToken = default)
    {
        Notify("CheckLoggedInAsync started");
        cancellationToken.ThrowIfCancellationRequested();
        return await IsLoggedInAsync();
    }

    private async Task EnsureLoggedInAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force
            && _lastEnsureLoggedInSucceeded
            && (now - _lastEnsureLoggedInAt) < EnsureLoggedInMinInterval)
        {
            return;
        }

        Notify($"[ensure-logged-in] start force={force} url='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
        var loggedIn = await IsLoggedInAsync();
        _lastEnsureLoggedInAt = now;
        _lastEnsureLoggedInSucceeded = loggedIn;
        Notify($"[ensure-logged-in] state loggedIn={loggedIn} url='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
        if (!loggedIn)
        {
            // Session may have expired during a long idle wait (Travian logs the user out and
            // redirects to login.php). Re-use the existing LoginAsync flow rather than throwing —
            // BotTaskRunner already calls LoginAsync at the start of every feature, so this just
            // covers the in-feature drop case (and the keep-alive idle path). Other non-logged-in
            // states (captcha, manual_step, unknown) still need human attention, so keep throwing.
            var state = await LoginStateAsync();
            if (state == "unknown")
            {
                Notify("[ensure-logged-in] state unknown; retrying login-state check before failing.");
                await Task.Delay(Random.Shared.Next(800, 1400), cancellationToken);
                state = await LoginStateAsync();
                Notify($"[ensure-logged-in] retry state={state} url='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
            }

            if (state == "logged_in")
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
            if (state == "logged_out")
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

            throw new InvalidOperationException($"Not logged in. Current page state is '{state}'.");
        }
        Notify($"[ensure-logged-in] confirmed url='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
        if (_suppressEnsureUiSyncDepth <= 0)
        {
            await TryEmitUiSyncSnapshotAsync(cancellationToken);
        }
    }

    private async Task<bool> IsLoggedInAsync()
    {
        return (await LoginStateAsync()) == "logged_in";
    }

    private async Task<string> LoginStateAsync()
    {
        try
        {
            // Cheap logged-out check FIRST: on a logout redirect the page is on login.php and is
            // actively navigating. Running the continue-prompt scan (which reads element text) against
            // a navigating page can hang on the default action timeout, so confirm sign-out by URL
            // before touching any element.
            var currentUrl = _page.Url.ToLowerInvariant();
            if (currentUrl.Contains("login.php", StringComparison.Ordinal))
            {
                Notify("You are logged out");
                return "logged_out";
            }

            await TryDismissContinuePromptAsync();

            if (await CaptchaOrManualStepVisibleAsync())
            {
                return "manual_step";
            }

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
                        return "logged_in";
                    }
                }

                foreach (var selector in Selectors.LoggedOutIndicators)
                {
                    if (await _page.Locator(selector).CountAsync() > 0)
                    {
                        Notify("You are logged out");
                        return "logged_out";
                    }
                }

                if (attempt < probeAttempts - 1)
                {
                    // Give the DOM a moment to render the topbar/village list, then re-probe.
                    await Task.Delay(Random.Shared.Next(300, 600)); // Random wait
                }
            }

            Notify("Login state is unknown");
            return "unknown";
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify("Page navigated while checking login state. State is unknown.");
            return "unknown";
        }
    }

    // Fills the login form the way a person would: type the username, pause briefly, type the
    // password, pause again before submitting. A short random 100-200ms wait between the steps is
    // enough to avoid the instant fill+submit look. Shared by the dedicated login page flow and the
    // inline "form already on page" flow.
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

            await RetryAsync($"fill {selector}", async () =>
            {
                await locator.FillAsync(value, new LocatorFillOptions { Timeout = _config.TimeoutMs });
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
                await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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

    private async Task<bool> TryClickFirstAsync(IEnumerable<string> selectors, CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await RetryAsync($"click selector {selector}", async () =>
                {
                    await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                    await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                }, cancellationToken: cancellationToken);
                await PauseForManualStepIfVisibleAsync("Manual verification appeared after click.", cancellationToken);
                return true;
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

        return false;
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
                await PauseForManualStepIfVisibleAsync("Manual verification appeared after click.", cancellationToken);
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
        // Interactive waits extend the deadline in 10s steps so the user can finish a manual
        // login/captcha — but never past this cap: the wait holds the worker session gate, so a
        // forgotten login window must not block the queue and account switching forever.
        var hardDeadline = DateTime.UtcNow.Add(ManualInteractiveWaitMaxDuration);
        var manualMessageShown = false;
        var pollCount = 0;
        Notify($"[login:verbose] waiting for login confirmation (timeout={timeoutSeconds}s, interactive={_interactive}, browserVisible={_browserVisible})");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pollCount++;
            if (DateTime.UtcNow >= deadline)
            {
                if (!_interactive || !_browserVisible)
                {
                    Notify($"[login] timeout: login not confirmed after {timeoutSeconds}s (polls={pollCount}, headless/non-interactive)");
                    throw new InvalidOperationException("Login was not confirmed before timeout.");
                }

                if (DateTime.UtcNow >= hardDeadline)
                {
                    Notify($"[login] login/captcha was not completed within {ManualInteractiveWaitMaxDuration.TotalMinutes:F0} minutes — giving up so the bot is not blocked forever.");
                    throw new InvalidOperationException($"Login was not confirmed within {ManualInteractiveWaitMaxDuration.TotalMinutes:F0} minutes.");
                }

                if (!manualMessageShown)
                {
                    Notify("Login is not confirmed yet. Finish login/captcha in the browser if needed.");
                    manualMessageShown = true;
                }

                deadline = DateTime.UtcNow.AddSeconds(10);
            }

            try
            {
                await TryDismissContinuePromptAsync(cancellationToken);

                if (await IsLoggedInAsync())
                {
                    Notify($"[login:verbose] login confirmed after {pollCount} poll(s)");
                    return true;
                }

                // Fail fast on an explicit credential/account error instead of waiting the full
                // login timeout (which can be minutes).
                var loginError = await TryReadVisibleLoginErrorAsync();
                if (loginError is not null)
                {
                    Notify($"[login] credential/account error visible on page: {loginError}");
                    throw new InvalidOperationException($"Login failed: {loginError}");
                }

                if (await CaptchaOrManualStepVisibleAsync() && !manualMessageShown)
                {
                    await CaptureManualVerificationScreenshotAsync("login-wait", cancellationToken);
                    Notify("Captcha/manual step detected. Solve it in the browser window, then wait here.");
                    if (!_browserVisible)
                    {
                        throw new ManualVerificationRequiredException(
                            "Captcha/manual verification appeared while running headless.");
                    }

                    manualMessageShown = true;
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
