using Microsoft.Playwright;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    // Ad videos usually run 15-30s; 60s covers the playthrough plus the reload/return round-trip and
    // acts as the fail-safe when no ad ever loads.
    private const int AdventureVideoTimeoutSeconds = 60;
    private const int AdventureVideoPollIntervalMs = 3000;
    internal const int AdventureVideoMinimumAttemptSeconds = 45;

    // CSS class on the videoFeatureBonusBox for each bonus on the hero adventures page.
    private const string AdventureDifficultyBoxClass = "adventureDifficulty";
    private const string AdventureDurationBoxClass = "adventureDuration";

    /// <summary>
    /// Activates the "Increased adventure danger to hard" bonus video (second box on the adventures
    /// page). Official Travian (T4.6) only.
    /// </summary>
    public Task<string> IncreaseAdventuresToHardAsync(CancellationToken cancellationToken = default)
        => RunAdventureVideoBonusWithChanceAsync(
            AdventureDifficultyBoxClass,
            "Increased adventure danger (hard)",
            cancellationToken);

    /// <summary>
    /// Activates the "Reduce adventure duration by 25%" bonus video (top box on the adventures page).
    /// Official Travian (T4.6) only.
    /// </summary>
    public Task<string> ReduceAdventuresTimeAsync(CancellationToken cancellationToken = default)
        => RunAdventureVideoBonusWithChanceAsync(
            AdventureDurationBoxClass,
            "Reduced adventure duration (-25%)",
            cancellationToken);

    private async Task<string> RunAdventureVideoBonusWithChanceAsync(
        string boxClass,
        string label,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = AdventureVideoChanceDecision.Evaluate(
            _config.HeroAdventureVideoChancePercent,
            Random.Shared.Next(0, 100));
        Notify($"[adventure-video] chance gate — {label}: {decision.Reason}; decision={(decision.RunVideo ? "run" : "skip")}.");
        if (!decision.RunVideo)
        {
            return $"{label}: skipped by {decision.ChancePercent}% chance gate (roll {decision.Roll}).";
        }

        return await RunAdventureVideoBonusAsync(boxClass, label, cancellationToken);
    }

    /// <summary>
    /// Shared driver for the hero-adventures bonus videos. Opens adventures, clicks the box's
    /// "Watch video", confirms "Watch video" in the info dialog, starts the ad video and waits for it
    /// to play through, then confirms the box shows its "Active for next ... adventure" state. The two
    /// bonuses (difficulty / duration) share identical markup and differ only by the box CSS class.
    /// </summary>
    private async Task<string> RunAdventureVideoBonusAsync(string boxClass, string label, CancellationToken cancellationToken)
    {
        Notify($"[adventure-video] starting — {label} via bonus video");

        // The bonus video is best-effort: it must NEVER throw into the hero flow, or a video error (browser
        // launch failure, navigation timeout, login needed in the isolated browser, ...) would abort
        // the much more important hero adventure dispatch. Swallow everything except cancellation and return a
        // status string; failure details go to the verbose log (hidden in Clean) so problems stay discoverable.
        try
        {
            await EnsureLoggedInAsync();

            if (_runInIsolatedBonusVideoBrowserAsync is not null)
            {
                var earlyResult = await TryReadAdventureVideoEarlyResultAsync(boxClass, label, cancellationToken);
                if (earlyResult is not null)
                {
                    return earlyResult;
                }

                try
                {
                    return await _runInIsolatedBonusVideoBrowserAsync(
                        async (videoPage, videoCancellationToken) =>
                        {
                            var videoClient = CreateIsolatedBonusVideoClient(videoPage);
                            return await videoClient.RunAdventureVideoBonusInCurrentBrowserAsync(
                                boxClass,
                                label,
                                videoCancellationToken,
                                isIsolated: true);
                        },
                        cancellationToken);
                }
                finally
                {
                    // Always return the main browser to dorf1, even when the isolated video threw, so the rest
                    // of the hero flow never continues sitting on the adventures page (videoFeature box).
                    await ReturnMainPageAfterIsolatedBonusVideoAsync();
                }
            }

            // Fallback for tests/non-session callers. Normal Official runs use the isolated video browser
            // above so the main Travian context never loads consentmanager/oadts/adscale.
            _setConsentDomainsAllowed?.Invoke(true);
            try
            {
                return await RunAdventureVideoBonusInCurrentBrowserAsync(boxClass, label, cancellationToken);
            }
            finally
            {
                // Re-block the ad/consent domains, then quarantine the current tab to about:blank before
                // any DOM reads or state saves can retrigger the resident consentmanager/oadts/adscale
                // timers. Only after that cleanup do we navigate to dorf1.
                _setConsentDomainsAllowed?.Invoke(false);
                await FlushResidentAdProvidersAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // A stop/cancel must abort cleanly — let it propagate so the hero task actually stops.
            throw;
        }
        catch (Exception ex)
        {
            Notify($"[adventure-video:verbose] {label}: bonus video failed and was skipped so the hero is still dispatched: {ex.GetType().Name}: {ex.Message}");
            return $"{label}: bonus video could not run and was skipped ({ex.Message}).";
        }
    }

    private async Task<string> RunAdventureVideoBonusInCurrentBrowserAsync(
        string boxClass,
        string label,
        CancellationToken cancellationToken,
        bool isIsolated = false)
    {
        if (isIsolated)
        {
            // The isolated video browser is seeded with the main session's cookies. If those are no longer
            // valid we must NOT enter credentials here: a second login would invalidate the main browser's
            // session (Travian allows a single active session) and log the bot out. Navigate, then verify
            // read-only; skip the video instead of logging in so the main session is never disturbed.
            await OpenHeroAdventuresPageAsync(cancellationToken);
            if (!await IsLoggedInAsync())
            {
                Notify($"[adventure-video:verbose] {label}: isolated video browser is not logged in (stale cookies); skipping so the main session is not disturbed.");
                return $"{label}: skipped — the bonus-video browser was not logged in.";
            }

            return await RunAdventureVideoBonusCoreAsync(boxClass, label, cancellationToken);
        }

        await EnsureLoggedInAsync();
        return await RunAdventureVideoBonusCoreAsync(boxClass, label, cancellationToken);
    }

    private async Task<string?> TryReadAdventureVideoEarlyResultAsync(
        string boxClass,
        string label,
        CancellationToken cancellationToken)
    {
        await OpenHeroAdventuresPageAsync(cancellationToken);

        var state = await ReadAdventureVideoStateAsync(boxClass, cancellationToken);
        Notify($"[adventure-video] {label}: box state before isolated browser: {state}");
        if (state == "active")
        {
            return $"{label} is already active for the next adventure.";
        }

        if (state == "missing")
        {
            return $"{label}: the bonus video feature was not found on the adventures page.";
        }

        Notify($"[adventure-video] {label}: video appears available; opening isolated video browser.");
        return null;
    }

    private TravianClient CreateIsolatedBonusVideoClient(IPage page)
    {
        return new TravianClient(
            page,
            _config,
            _account,
            interactive: _interactive,
            browserVisible: true,
            projectRoot: _projectRoot,
            statusCallback: _statusCallback,
            sessionCache: _session);
    }

    private async Task ReturnMainPageAfterIsolatedBonusVideoAsync()
    {
        using var navigateCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await GotoAsync(Paths.Resources, navigateCts.Token);
            Notify("[adventure-video] main browser returned to dorf1 after isolated video browser.");
        }
        catch (Exception ex)
        {
            Notify($"[adventure-video] main browser could not return to dorf1 after isolated video browser: {ex.Message}");
        }
    }

    /// <summary>
    /// ROOT FIX for the stray ad tabs: the bonus video makes consentmanager write first-party consent
    /// (__cmp* cookies + localStorage on travian.com). If that persists, Travian's own JS sees stored
    /// consent on every page load and runs the ad stack, which spawns window.open tabs (network blocking
    /// can't stop a window.open-created tab). So after the video we delete the consent here, then
    /// navigate to dorf1 (no videoFeature box) to unload the resident ad JS. Consent is re-established
    /// transiently by the next video. Best-effort; failures are logged but not fatal.
    /// </summary>
    private async Task FlushResidentAdProvidersAsync()
    {
        using (var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            try
            {
                if (_cleanupAfterBonusVideoAsync is not null)
                {
                    await _cleanupAfterBonusVideoAsync(_page, cleanupCts.Token);
                    Notify("[adventure-video] browser session ad/consent cleanup completed.");
                }
                else
                {
                    await ClearCurrentPageConsentStorageFallbackAsync().WaitAsync(cleanupCts.Token);
                }
            }
            catch (Exception ex)
            {
                Notify($"[adventure-video] browser session ad/consent cleanup failed: {ex.Message}");
            }
        }

        using var navigateCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await GotoAsync(Paths.Resources, navigateCts.Token);
            Notify("[adventure-video] navigated to dorf1 to unload resident ad/consent providers.");
        }
        catch (Exception ex)
        {
            Notify($"[adventure-video] could not navigate to flush ad providers: {ex.Message}");
        }
    }

    private async Task ClearCurrentPageConsentStorageFallbackAsync()
    {
        await _page.EvaluateAsync(
            """
            () => {
              const isConsentOrAd = (key) => {
                const n = String(key || '').trim().toLowerCase();
                return n.startsWith('__cmp')
                  || n.startsWith('cmp')
                  || n.includes('consent')
                  || n.startsWith('euconsent')
                  || n.startsWith('usprivacy')
                  || n.includes('iab')
                  || n.includes('tcf')
                  || n.includes('gdpr')
                  || n.startsWith('gpp')
                  || n.includes('addtl_consent')
                  || n.startsWith('__gads')
                  || n.startsWith('__gpi')
                  || n.startsWith('_gac')
                  || n.startsWith('_gcl');
              };
              try {
                for (const key of Object.keys(localStorage)) {
                  if (isConsentOrAd(key)) localStorage.removeItem(key);
                }
              } catch (_) {}
              try {
                const host = location.hostname;
                const base = host.split('.').slice(-2).join('.');
                const domains = ['', host, '.' + host, base, '.' + base];
                for (const cookie of document.cookie.split(';')) {
                  const name = cookie.split('=')[0].trim();
                  if (!isConsentOrAd(name)) continue;
                  for (const domain of domains) {
                    document.cookie = name + '=;expires=Thu, 01 Jan 1970 00:00:00 GMT;path=/'
                      + (domain ? ';domain=' + domain : '');
                  }
                }
              } catch (_) {}
              return true;
            }
            """);
        Notify("[adventure-video] cleared consent/ad storage on current page (fallback).");
    }

    private async Task<string> RunAdventureVideoBonusCoreAsync(string boxClass, string label, CancellationToken cancellationToken)
    {
        var maxAttempts = boxClass == AdventureDifficultyBoxClass ? 2 : 1;
        string? lastResult = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Notify($"[adventure-video] {label}: video attempt {attempt}/{maxAttempts}.");
            lastResult = await RunAdventureVideoBonusAttemptAsync(boxClass, label, cancellationToken);

            await OpenHeroAdventuresPageAsync(cancellationToken);
            var state = await ReadAdventureVideoStateAsync(boxClass, cancellationToken);
            Notify($"[adventure-video] {label}: state after attempt {attempt}/{maxAttempts}: {state}.");
            if (state == "active")
            {
                return $"{label} activated; the bonus is now active for the next adventure.";
            }

            if (attempt < maxAttempts)
            {
                var failureKind = BonusVideoFailureClassifier.Classify(lastResult);
                if (!BonusVideoFailureClassifier.ShouldRetryImmediately(failureKind))
                {
                    Notify(
                        $"[adventure-video] {label}: skipping immediate retry after {failureKind}; "
                        + "normal hero flow continues and video can retry after cooldown.");
                    break;
                }

                Notify($"[adventure-video] {label}: activation was not confirmed; retrying the complete video flow once.");
            }
        }

        Notify($"[adventure-video:verbose] {label}: activation was not confirmed after {maxAttempts} video attempt(s); continuing without the bonus.");
        return lastResult ?? $"{label}: bonus video could not run and was skipped.";
    }

    private async Task<string> RunAdventureVideoBonusAttemptAsync(string boxClass, string label, CancellationToken cancellationToken)
    {
        await OpenHeroAdventuresPageAsync(cancellationToken);

        // The isolated video browser is seeded without consent (FilterForeignSubdomainState strips it), so
        // in GDPR/consent regions the consentmanager dialog overlays the page on load. Accept it up front so
        // the box state reads correctly and the ad stack is allowed to initialize. No-op when absent.
        await AcceptConsentManagerIfPresentAsync(cancellationToken);

        var state = await ReadAdventureVideoStateAsync(boxClass, cancellationToken);
        Notify($"[adventure-video] {label}: box state before watching: {state}");
        if (state == "active")
        {
            return $"{label} is already active for the next adventure.";
        }

        if (state == "missing")
        {
            return $"{label}: the bonus video feature was not found on the adventures page.";
        }

        if (!await ClickAdventureVideoWatchAsync(boxClass, label, cancellationToken))
        {
            return $"{label}: could not click 'Watch video' on the bonus box.";
        }

        if (!await ConfirmAdventureVideoDialogAsync(label, cancellationToken))
        {
            return $"{label}: the video info dialog did not appear or its 'Watch video' button could not be clicked.";
        }

        if (!await StartAdventureVideoAsync(label, cancellationToken))
        {
            // Distinguish a missing-codec machine from a transient "no ad" so the user gets an actionable
            // message instead of retrying forever on a browser that can never decode the H.264/AAC ad.
            if (!await IsH264PlaybackSupportedAsync(cancellationToken))
            {
                return $"{label}: this browser cannot play the ad video (missing H.264/AAC codecs). Install "
                    + "Google Chrome on this machine so the bonus videos can run.";
            }

            return $"{label}: the bonus video player did not open (likely no ad available or blocked). Try again later.";
        }

        // The reward is granted once the video plays through; the box then shows its "Active for next
        // ... adventure" state. Poll for that, using the timeout as the fail-safe. We deliberately do
        // not try to detect the ad-blocker/"force reload" notice — it stays in the DOM behind the
        // playing video and produced false failures even on successful runs.
        var confirmed = await WaitForAdventureVideoActiveAsync(boxClass, label, cancellationToken);
        await LogAdventureVideoBoxHtmlAsync(boxClass, label, "after waiting for reward", cancellationToken);

        if (confirmed)
        {
            return $"{label} activated; the bonus is now active for the next adventure.";
        }

        // Not confirmed: a missing-codec browser can still render an error iframe (so StartVideo "succeeded")
        // yet never grant the reward. Surface the actionable codec hint here too instead of a generic timeout.
        if (!await IsH264PlaybackSupportedAsync(cancellationToken))
        {
            return $"{label}: this browser cannot play the ad video (missing H.264/AAC codecs). Install "
                + "Google Chrome on this machine so the bonus videos can run.";
        }

        return $"{label}: bonus video ran but activation was not confirmed within {AdventureVideoTimeoutSeconds}s.";
    }

    /// <summary>
    /// Reads the state of a bonus box: "active" when the reward is already applied, "watchReady" when a
    /// video can be watched, "missing" when the box is absent, "other" otherwise. Used both as an
    /// early-exit gate and as the completion signal.
    /// </summary>
    private async Task<string> ReadAdventureVideoStateAsync(string boxClass, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<string>(
                """
                (boxClass) => {
                  const box = document.querySelector('.videoFeatureBonusBox.' + boxClass);
                  if (!box) return 'missing';
                  const status = box.querySelector('.bonusStatus');
                  const statusClass = String(status?.className || '').toLowerCase();
                  const text = String(box.innerText || '').replace(/\s+/g, ' ').toLowerCase();
                  // Success markup (language-independent): .bonusStatus gains class "bonusReady" and
                  // shows a <span class="bonusReadyText">Active for next ... adventure.</span>.
                  if (statusClass.includes('bonusready') || box.querySelector('.bonusReadyText') || text.includes('active for next')) {
                    return 'active';
                  }
                  // Watchable: the watch button is present AND enabled (it stays in the DOM but disabled
                  // once the reward is active).
                  const watchBtn = box.querySelector('.watchVideo button');
                  const enabledWatch = !!watchBtn && !watchBtn.disabled;
                  if (statusClass.includes('watchready') || enabledWatch) return 'watchReady';
                  return 'other';
                }
                """,
                boxClass);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return "other";
        }
    }

    private async Task<bool> ClickAdventureVideoWatchAsync(string boxClass, string label, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        var clicked = await _page.EvaluateAsync<bool>(
            """
            (boxClass) => {
              const btn = document.querySelector('.videoFeatureBonusBox.' + boxClass + ' .watchVideo button');
              if (!btn) return false;
              btn.click();
              return true;
            }
            """,
            boxClass);
        if (clicked)
        {
            Notify($"[adventure-video] {label}: clicked 'Watch video' on the bonus box.");
        }

        return clicked;
    }

    /// <summary>
    /// Confirms the second "Watch video" button inside the <c>#videoFeature</c> info dialog that opens
    /// after clicking the box. The dialog is the same for both bonuses, so no box class is needed.
    /// </summary>
    private async Task<bool> ConfirmAdventureVideoDialogAsync(string label, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await _page.EvaluateAsync<string>(
                """
                () => {
                  const dlg = document.querySelector('#videoFeature');
                  const player = document.querySelector('#videoArea, #videoFeature iframe');
                  if ((dlg && String(dlg.className || '').includes('showVideo')) || player) return 'video';
                  if (!dlg) return 'none';
                  const ok = dlg.querySelector('.dialogButtonOk')
                    || Array.from(dlg.querySelectorAll('button')).find(b => /watch video/i.test(b.textContent || ''));
                  return ok ? 'ready' : 'pending';
                }
                """);
            if (status == "video")
            {
                Notify($"[adventure-video] {label}: info dialog skipped ('don't show it again' already set); video opened directly.");
                return true;
            }

            if (status != "ready")
            {
                await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
                continue;
            }

            await TickBonusVideoDontShowAgainAsync(cancellationToken, "[adventure-video]");
            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            var clicked = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const dlg = document.querySelector('#videoFeature');
                  if (!dlg) return false;
                  const ok = dlg.querySelector('.dialogButtonOk')
                    || Array.from(dlg.querySelectorAll('button')).find(b => /watch video/i.test(b.textContent || ''));
                  if (!ok) return false;
                  ok.click();
                  return true;
                }
                """);
            if (clicked)
            {
                Notify($"[adventure-video] {label}: confirmed 'Watch video' in the info dialog.");
                return true;
            }

            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
        }

        return false;
    }

    /// <summary>
    /// Waits for the ad video iframe (<c>#videoArea</c>) to appear, then clicks the centered play
    /// button with a real (trusted) mouse gesture. The button lives inside the cross-origin ad iframe
    /// and the browser autoplay policy ignores scripted element.click()/video.play(); only a genuine
    /// input event starts playback, and the button is centered in the iframe. Returns false when the
    /// player never appears (ad blocked / no inventory).
    /// </summary>
    private async Task<bool> StartAdventureVideoAsync(string label, CancellationToken cancellationToken)
        => await StartBonusVideoPlayerAsync(label, "[adventure-video:verbose]", cancellationToken);

    private async Task<bool> StartBonusVideoPlayerAsync(
        string label,
        string logPrefix,
        CancellationToken cancellationToken)
    {
        var playerReady = false;
        var playerLoadDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(AdventureVideoMinimumAttemptSeconds);
        while (DateTimeOffset.UtcNow < playerLoadDeadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Consent can appear late (when the ad is first requested), so keep accepting it while we wait
            // for the player — without consent the ad iframe never renders in consent regions.
            await AcceptConsentManagerIfPresentAsync(cancellationToken, logPrefix);
            playerReady = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const dlg = document.querySelector('#videoFeature');
                  const showing = dlg && String(dlg.className || '').includes('showVideo');
                  const iframe = document.querySelector('#videoArea, #videoFeature iframe');
                  return !!(showing && iframe);
                }
                """);
            if (playerReady)
            {
                break;
            }

            await Task.Delay(500, cancellationToken);
        }

        if (!playerReady)
        {
            Notify($"{logPrefix} {label}: video player iframe did not appear.");
            return false;
        }

        // Let the ad frame load its player (the centered play button) before clicking it.
        await Task.Delay(1500, cancellationToken);

        // Up to two trusted clicks. Prefer the actual ad-player play control in any frame; cross-origin
        // frames remain queryable through Playwright. The iframe-center click is retained as fallback.
        // The first click can be swallowed by a consentmanager overlay that pops with
        // the ad request; we only re-click when such an overlay was found and accepted between attempts, so
        // a click is never sent into an already-playing ad (which could toggle pause).
        for (var clickAttempt = 1; clickAttempt <= 2; clickAttempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var clickedPlayControl = false;
                for (var findAttempt = 1; findAttempt <= 8 && !clickedPlayControl; findAttempt++)
                {
                    foreach (var frame in _page.Frames)
                    {
                        var play = frame.Locator(".atg-gima-big-play-button-outer, .atg-gima-big-play-button").First;
                        if (await play.CountAsync() == 0 || !await play.IsVisibleAsync())
                        {
                            continue;
                        }

                        await play.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                        clickedPlayControl = true;
                        Notify($"{logPrefix} {label}: clicked the visible ad-player play button attempt={clickAttempt}.");
                        break;
                    }

                    if (!clickedPlayControl)
                    {
                        await Task.Delay(250, cancellationToken);
                    }
                }

                if (clickedPlayControl)
                {
                    await MuteBonusVideoAsync(label, logPrefix, cancellationToken);
                }
                else
                {
                    var visibleFailure = await TryReadVisibleBonusVideoFailureAsync(cancellationToken);
                    if (visibleFailure is not null)
                    {
                        Notify($"{logPrefix} {label}: {visibleFailure}");
                        return false;
                    }

                    var area = _page.Locator("#videoArea, #videoFeature iframe").First;
                    // Scroll the player into view first: a click at a bounding-box center that sits below the
                    // fold lands nowhere, which was one way the play button "wasn't clicked".
                    await area.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 3000 });
                    var box = await area.BoundingBoxAsync();
                    if (box is null)
                    {
                        Notify($"{logPrefix} {label}: could not locate the video area to click play.");
                        return false;
                    }

                    var x = box.X + box.Width / 2;
                    var y = box.Y + box.Height / 2;
                    await _page.Mouse.ClickAsync(x, y);
                    Notify($"{logPrefix} {label}: actual play control was not found; clicked the video-area center fallback ({x:0},{y:0}) attempt={clickAttempt}.");
                    await MuteBonusVideoAsync(label, logPrefix, cancellationToken);
                }
            }
            catch (PlaywrightException ex)
            {
                Notify($"{logPrefix} {label}: could not click the play button: {ex.Message}");
                return false;
            }

            // If a consent overlay intercepted the click, accept it and click once more; otherwise the click
            // reached the player and we are done (no blind re-click into a playing ad).
            await Task.Delay(1000, cancellationToken);
            if (!await AcceptConsentManagerIfPresentAsync(cancellationToken, logPrefix))
            {
                break;
            }

            Notify($"{logPrefix} {label}: consent dialog intercepted the click; accepted and retrying.");
        }

        return true;
    }

    private async Task<string?> TryReadVisibleBonusVideoFailureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var messages = new[]
        {
            "Are you using an ad blocker or declining third-party cookies?",
            "Does the video not load?",
        };

        foreach (var frame in _page.Frames)
        {
            foreach (var message in messages)
            {
                try
                {
                    var locator = frame.Locator($"text={message}").First;
                    if (await locator.CountAsync() > 0
                        && await locator.IsVisibleAsync()
                        && await locator.EvaluateAsync<bool>(
                            """
                            element => {
                              const rect = element.getBoundingClientRect();
                              if (rect.width <= 0 || rect.height <= 0) return false;
                              const x = Math.max(0, Math.min(innerWidth - 1, rect.left + rect.width / 2));
                              const y = Math.max(0, Math.min(innerHeight - 1, rect.top + rect.height / 2));
                              const top = document.elementFromPoint(x, y);
                              return !!top && (top === element || element.contains(top) || top.contains(element));
                            }
                            """))
                    {
                        return "ad provider visibly reported no ad, ad blocking, or rejected third-party cookies";
                    }
                }
                catch (PlaywrightException)
                {
                    // Diagnostic only. Frame can navigate/detach while ad player starts.
                }
            }
        }

        return null;
    }

    private async Task MuteBonusVideoAsync(string label, string logPrefix, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var frame in _page.Frames)
            {
                var disabled = frame.Locator(".atg-gima-audio-button-disabled:not(.atg-gima-hidden)").First;
                if (await disabled.CountAsync() > 0 && await disabled.IsVisibleAsync())
                {
                    Notify($"{logPrefix} {label}: video audio is muted.");
                    return;
                }

                var enabled = frame.Locator(".atg-gima-audio-button:has(.atg-gima-audio-button-enabled:not(.atg-gima-hidden))").First;
                if (await enabled.CountAsync() == 0 || !await enabled.IsVisibleAsync())
                {
                    continue;
                }

                await enabled.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                for (var verifyAttempt = 1; verifyAttempt <= 4; verifyAttempt++)
                {
                    await Task.Delay(250, cancellationToken);
                    if (await disabled.CountAsync() > 0 && await disabled.IsVisibleAsync())
                    {
                        Notify($"{logPrefix} {label}: clicked the audio control and confirmed the video is muted.");
                        return;
                    }
                }

                Notify($"{logPrefix} {label}: clicked the audio control, but muted state was not confirmed.");
                return;
            }

            await Task.Delay(300, cancellationToken);
        }

        Notify($"{logPrefix} {label}: audio control was not found; continuing the video.");
    }

    /// <summary>
    /// Accepts the consentmanager (CMP/TCF) consent dialog when present so the bonus-video ad stack
    /// (oadts/adscale/Google IMA) is allowed to initialize. Without consent the player never loads in
    /// consent regions (GDPR), which is why the play button could not be clicked for some users. The dialog
    /// renders either first-party on the Travian page (<c>#cmpbox</c>) or inside a consentmanager.net
    /// iframe; both are handled. The isolated video browser is discarded on close, so accepting leaks
    /// nothing into the main session. Returns true when an "Accept all" button was clicked.
    /// </summary>
    private async Task<bool> AcceptConsentManagerIfPresentAsync(
        CancellationToken cancellationToken,
        string logPrefix = "[adventure-video:verbose]")
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string acceptScript =
            """
            (root) => {
              const scope = root || document;
              const box = scope.querySelector('#cmpbox, .cmpbox, [class*="cmpbox" i]') || scope;
              if (box !== scope) {
                const style = window.getComputedStyle(box);
                if (style.display === 'none' || style.visibility === 'hidden') return false;
              }
              const acceptText = /accept all|accept|agree|alle akzeptieren|zustimmen|godk[äa]nn|acceptera|tout accepter|accetta/i;
              const btn = box.querySelector('.cmpboxbtnyes, button.cmpboxbtnyes, a.cmpboxbtnyes')
                || Array.from(box.querySelectorAll('a, button, .cmpboxbtn')).find(b =>
                     acceptText.test(((b.textContent || '') + ' ' + (b.getAttribute('aria-label') || '')).trim()));
              if (!btn) return false;
              btn.click();
              return true;
            }
            """;

        // First-party overlay on the Travian page.
        try
        {
            if (await _page.EvaluateAsync<bool>(acceptScript, null))
            {
                Notify($"{logPrefix} accepted consentmanager consent (first-party overlay).");
                return true;
            }
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            // Page mid-navigation; the next poll retries.
        }

        // consentmanager.net iframe fallback.
        foreach (var frame in _page.Frames)
        {
            if (string.IsNullOrEmpty(frame.Url)
                || !frame.Url.Contains("consentmanager", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (await frame.EvaluateAsync<bool>(acceptScript, null))
                {
                    Notify($"{logPrefix} accepted consentmanager consent (iframe).");
                    return true;
                }
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                // Frame detached/navigating; ignore and let the next poll retry.
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the current browser can decode the H.264/AAC the bonus ads use. Playwright's bundled
    /// Chromium cannot (only the system Chrome/Edge channel ships the proprietary codecs), so a false here
    /// means the machine has no codec-capable browser installed and the videos can never play.
    /// </summary>
    private async Task<bool> IsH264PlaybackSupportedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  try {
                    const v = document.createElement('video');
                    const mp4 = v.canPlayType('video/mp4; codecs="avc1.42E01E"');
                    const aac = v.canPlayType('audio/mp4; codecs="mp4a.40.2"');
                    return (mp4 === 'probably' || mp4 === 'maybe') && aac !== '';
                  } catch (_) { return false; }
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            // Unknown on a transient read — don't mislead with a codec error.
            return true;
        }
    }

    /// <summary>
    /// Polls until the bonus box reports "active" (reward applied) or the timeout elapses. The reward
    /// is granted server-side after the video completes; the box may update in place or only after a
    /// reload, so a reload is issued partway through. A visibly rendered provider error stops the
    /// wait early; hidden fallback text is ignored so a real playing ad is never false-failed.
    /// </summary>
    private async Task<bool> WaitForAdventureVideoActiveAsync(string boxClass, string label, CancellationToken cancellationToken)
    {
        var startUtc = DateTimeOffset.UtcNow;
        var deadlineUtc = startUtc.AddSeconds(AdventureVideoTimeoutSeconds);
        const int maxReloads = 2;
        var reloadCount = 0;
        var lastReloadUtc = startUtc;
        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(AdventureVideoPollIntervalMs, cancellationToken);

            var state = await ReadAdventureVideoStateAsync(boxClass, cancellationToken);
            if (state == "active")
            {
                Notify($"[adventure-video] {label}: reward confirmed — the bonus is now active.");
                return true;
            }

            if (AdventureVideoAttemptMayAbort((DateTimeOffset.UtcNow - startUtc).TotalSeconds)
                && await TryReadVisibleBonusVideoFailureAsync(cancellationToken) is { } visibleFailure)
            {
                Notify($"[adventure-video:verbose] {label}: {visibleFailure}; stopping video wait early.");
                return false;
            }

            // Once the player has closed, reload the adventures page so the box reflects the granted reward.
            // Reload up to twice, spaced out: with a single reload a reward that lands late (long ad, or the
            // dialog only closing near the end) was missed and falsely reported as "not confirmed".
            var now = DateTimeOffset.UtcNow;
            var elapsedSeconds = (now - startUtc).TotalSeconds;
            var sinceReloadSeconds = (now - lastReloadUtc).TotalSeconds;
            if (reloadCount < maxReloads
                && elapsedSeconds >= AdventureVideoTimeoutSeconds / 3.0
                && sinceReloadSeconds >= 15
                && !await IsAdventureVideoDialogOpenAsync(cancellationToken))
            {
                reloadCount++;
                lastReloadUtc = now;
                Notify($"[adventure-video:verbose] {label}: video dialog closed; reloading adventures page to read reward state (reload {reloadCount}/{maxReloads}).");
                await OpenHeroAdventuresPageAsync(cancellationToken);
            }
        }

        return false;
    }

    internal static bool AdventureVideoAttemptMayAbort(double elapsedSeconds)
        => elapsedSeconds >= AdventureVideoMinimumAttemptSeconds;

    private async Task<bool> IsAdventureVideoDialogOpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<bool>("() => !!document.querySelector('#videoFeature')");
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Diagnostic: logs the bonus box markup so the exact "active" success state can be inspected from
    /// a real run.
    /// </summary>
    private async Task LogAdventureVideoBoxHtmlAsync(string boxClass, string label, string when, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var html = await _page.EvaluateAsync<string?>(
                """
                (boxClass) => {
                  const box = document.querySelector('.videoFeatureBonusBox.' + boxClass);
                  if (!box) return null;
                  return String(box.outerHTML || '').replace(/\s+/g, ' ').slice(0, 800);
                }
                """,
                boxClass);
            Notify($"[adventure-video:diag] {label} box {when}: {(string.IsNullOrWhiteSpace(html) ? "(not found)" : html)}");
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            // Diagnostic only — ignore transient navigation.
        }
    }
}
