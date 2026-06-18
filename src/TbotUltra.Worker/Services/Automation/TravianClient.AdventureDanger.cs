using Microsoft.Playwright;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    // Ad videos usually run 15-30s; 60s covers the playthrough plus the reload/return round-trip and
    // acts as the fail-safe when no ad ever loads.
    private const int AdventureVideoTimeoutSeconds = 60;
    private const int AdventureVideoPollIntervalMs = 3000;

    // CSS class on the videoFeatureBonusBox for each bonus on the hero adventures page.
    private const string AdventureDifficultyBoxClass = "adventureDifficulty";
    private const string AdventureDurationBoxClass = "adventureDuration";

    /// <summary>
    /// Activates the "Increased adventure danger to hard" bonus video (second box on the adventures
    /// page). Official Travian (T4.6) only.
    /// </summary>
    public Task<string> IncreaseAdventuresToHardAsync(CancellationToken cancellationToken = default)
        => RunAdventureVideoBonusAsync(AdventureDifficultyBoxClass, "Increased adventure danger (hard)", cancellationToken);

    /// <summary>
    /// Activates the "Reduce adventure duration by 25%" bonus video (top box on the adventures page).
    /// Official Travian (T4.6) only.
    /// </summary>
    public Task<string> ReduceAdventuresTimeAsync(CancellationToken cancellationToken = default)
        => RunAdventureVideoBonusAsync(AdventureDurationBoxClass, "Reduced adventure duration (-25%)", cancellationToken);

    /// <summary>
    /// Shared driver for the hero-adventures bonus videos. Opens adventures, clicks the box's
    /// "Watch video", confirms "Watch video" in the info dialog, starts the ad video and waits for it
    /// to play through, then confirms the box shows its "Active for next ... adventure" state. The two
    /// bonuses (difficulty / duration) share identical markup and differ only by the box CSS class.
    /// </summary>
    private async Task<string> RunAdventureVideoBonusAsync(string boxClass, string label, CancellationToken cancellationToken)
    {
        Notify($"[adventure-video] starting — {label} via bonus video");
        await EnsureLoggedInAsync();
        await OpenHeroAdventuresPageAsync(cancellationToken);

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
            return $"{label}: the bonus video player did not open (likely no ad available or blocked). Try again later.";
        }

        // The reward is granted once the video plays through; the box then shows its "Active for next
        // ... adventure" state. Poll for that, using the timeout as the fail-safe. We deliberately do
        // not try to detect the ad-blocker/"force reload" notice — it stays in the DOM behind the
        // playing video and produced false failures even on successful runs.
        var confirmed = await WaitForAdventureVideoActiveAsync(boxClass, label, cancellationToken);
        await LogAdventureVideoBoxHtmlAsync(boxClass, label, "after waiting for reward", cancellationToken);

        return confirmed
            ? $"{label} activated; the bonus is now active for the next adventure."
            : $"{label}: bonus video ran but activation was not confirmed within {AdventureVideoTimeoutSeconds}s.";
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

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the adventure bonus video.", cancellationToken);
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
                await PauseForManualStepIfVisibleAsync("Manual verification appeared after confirming the adventure bonus video.", cancellationToken);
                return true;
            }

            await Task.Delay(250, cancellationToken);
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
    {
        var playerReady = false;
        for (var attempt = 1; attempt <= 16; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            Notify($"[adventure-video] {label}: video player iframe did not appear.");
            return false;
        }

        // Let the ad frame load its player (the centered play button) before clicking it, then click
        // once. A single trusted click reliably starts the ad; re-clicking risks toggling pause.
        await Task.Delay(1500, cancellationToken);
        try
        {
            var box = await _page.Locator("#videoArea, #videoFeature iframe").First.BoundingBoxAsync();
            if (box is null)
            {
                Notify($"[adventure-video] {label}: could not locate the video area to click play.");
                return false;
            }

            var x = box.X + box.Width / 2;
            var y = box.Y + box.Height / 2;
            await _page.Mouse.ClickAsync(x, y);
            Notify($"[adventure-video] {label}: clicked play (trusted) at video area center ({x:0},{y:0}); waiting for playthrough.");
        }
        catch (PlaywrightException ex)
        {
            Notify($"[adventure-video] {label}: could not click the play button: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Polls until the bonus box reports "active" (reward applied) or the timeout elapses. The reward
    /// is granted server-side after the video completes; the box may update in place or only after a
    /// reload, so a reload is issued partway through.
    /// </summary>
    private async Task<bool> WaitForAdventureVideoActiveAsync(string boxClass, string label, CancellationToken cancellationToken)
    {
        var startUtc = DateTimeOffset.UtcNow;
        var deadlineUtc = startUtc.AddSeconds(AdventureVideoTimeoutSeconds);
        var reloaded = false;
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

            // Once we're past roughly the video length and the player has closed, reload the
            // adventures page once so the box reflects the granted reward.
            var elapsedSeconds = (DateTimeOffset.UtcNow - startUtc).TotalSeconds;
            if (!reloaded && elapsedSeconds >= AdventureVideoTimeoutSeconds / 2.0
                && !await IsAdventureVideoDialogOpenAsync(cancellationToken))
            {
                reloaded = true;
                Notify($"[adventure-video] {label}: video dialog closed; reloading adventures page to read reward state.");
                await OpenHeroAdventuresPageAsync(cancellationToken);
            }
        }

        return false;
    }

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
