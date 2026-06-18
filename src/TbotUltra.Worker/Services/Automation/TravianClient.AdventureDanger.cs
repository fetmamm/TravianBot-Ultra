using Microsoft.Playwright;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    // Ad videos usually run 15-30s; 60s covers the playthrough plus the reload/return round-trip and
    // acts as the fail-safe when no ad ever loads.
    private const int AdventureDangerVideoTimeoutSeconds = 60;
    private const int AdventureDangerPollIntervalMs = 3000;

    /// <summary>
    /// Activates the "Increased adventure danger to hard" video bonus on the hero adventures page:
    /// opens adventures, clicks the box's "Watch video", confirms "Watch video" in the info dialog,
    /// starts the ad video and waits for it to play through, then confirms the box shows
    /// "Active for next normal adventure". Official Travian (T4.6) only — the feature relies on the
    /// videoFeatureBonusBox markup that legacy/private servers do not expose.
    /// </summary>
    public async Task<string> IncreaseAdventuresToHardAsync(CancellationToken cancellationToken = default)
    {
        Notify("[adventure-danger] starting — activate increased adventure danger (hard) via bonus video");
        await EnsureLoggedInAsync();
        await OpenHeroAdventuresPageAsync(cancellationToken);

        var state = await ReadAdventureDangerStateAsync(cancellationToken);
        Notify($"[adventure-danger] difficulty box state before watching: {state}");
        if (state == "active")
        {
            return "Adventure danger is already active for the next normal adventure.";
        }

        if (state == "missing")
        {
            return "Adventure danger video feature was not found on the adventures page.";
        }

        if (!await ClickAdventureDangerWatchVideoAsync(cancellationToken))
        {
            return "Could not click 'Watch video' on the adventure danger box.";
        }

        if (!await ConfirmAdventureDangerVideoDialogAsync(cancellationToken))
        {
            return "The video info dialog did not appear or its 'Watch video' button could not be clicked.";
        }

        if (!await StartAdventureDangerVideoAsync(cancellationToken))
        {
            return "The bonus video player did not open (likely no ad available or blocked). Try again later.";
        }

        // The reward is granted once the video plays through; the box then shows "Active for next
        // normal adventure". Poll for that, using the timeout as the fail-safe. We deliberately do not
        // try to detect the ad-blocker/"force reload" notice — it stays in the DOM behind the playing
        // video and produced false failures even on successful runs.
        var confirmed = await WaitForAdventureDangerActiveAsync(cancellationToken);
        await LogAdventureDangerBoxHtmlAsync("after waiting for reward", cancellationToken);

        return confirmed
            ? "Adventure danger increased to hard. 'Active for next normal adventure' is now shown."
            : $"Bonus video flow ran but 'Active for next normal adventure' was not confirmed within {AdventureDangerVideoTimeoutSeconds}s.";
    }

    /// <summary>
    /// Reads the state of the "Increased adventure danger" box: "active" when the reward is already
    /// applied, "watchReady" when a video can be watched, "missing" when the box is absent, "other"
    /// otherwise. Used both as an early-exit gate and as the completion signal.
    /// </summary>
    private async Task<string> ReadAdventureDangerStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<string>(
                """
                () => {
                  const box = document.querySelector('.videoFeatureBonusBox.adventureDifficulty');
                  if (!box) return 'missing';
                  const status = box.querySelector('.bonusStatus');
                  const statusClass = String(status?.className || '').toLowerCase();
                  const text = String(box.innerText || '').replace(/\s+/g, ' ').toLowerCase();
                  // Success markup (language-independent): .bonusStatus gains class "bonusReady" and
                  // shows a <span class="bonusReadyText">Active for next normal adventure.</span>.
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
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return "other";
        }
    }

    private async Task<bool> ClickAdventureDangerWatchVideoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clicked = await _page.EvaluateAsync<bool>(
            """
            () => {
              const btn = document.querySelector('.videoFeatureBonusBox.adventureDifficulty .watchVideo button');
              if (!btn) return false;
              btn.click();
              return true;
            }
            """);
        if (clicked)
        {
            Notify("[adventure-danger] clicked 'Watch video' on the difficulty box.");
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the adventure danger video.", cancellationToken);
        return clicked;
    }

    /// <summary>
    /// Confirms the second "Watch video" button inside the <c>#videoFeature</c> info dialog that
    /// opens after clicking the box. Polls because the dialog animates in.
    /// </summary>
    private async Task<bool> ConfirmAdventureDangerVideoDialogAsync(CancellationToken cancellationToken)
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
                Notify("[adventure-danger] confirmed 'Watch video' in the info dialog.");
                await PauseForManualStepIfVisibleAsync("Manual verification appeared after confirming the adventure danger video.", cancellationToken);
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
    private async Task<bool> StartAdventureDangerVideoAsync(CancellationToken cancellationToken)
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
            Notify("[adventure-danger] video player iframe did not appear.");
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
                Notify("[adventure-danger] could not locate the video area to click play.");
                return false;
            }

            var x = box.X + box.Width / 2;
            var y = box.Y + box.Height / 2;
            await _page.Mouse.ClickAsync(x, y);
            Notify($"[adventure-danger] clicked play (trusted) at video area center ({x:0},{y:0}); waiting for playthrough.");
        }
        catch (PlaywrightException ex)
        {
            Notify($"[adventure-danger] could not click the play button: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Polls until the difficulty box reports "active" (reward applied) or the timeout elapses. The
    /// reward is granted server-side after the video completes; the box may update in place or only
    /// after a reload, so a reload is issued partway through.
    /// </summary>
    private async Task<bool> WaitForAdventureDangerActiveAsync(CancellationToken cancellationToken)
    {
        var startUtc = DateTimeOffset.UtcNow;
        var deadlineUtc = startUtc.AddSeconds(AdventureDangerVideoTimeoutSeconds);
        var reloaded = false;
        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(AdventureDangerPollIntervalMs, cancellationToken);

            var state = await ReadAdventureDangerStateAsync(cancellationToken);
            if (state == "active")
            {
                Notify("[adventure-danger] reward confirmed — 'Active for next normal adventure' is shown.");
                return true;
            }

            // Once we're past roughly the video length and the player has closed, reload the
            // adventures page once so the box reflects the granted reward.
            var elapsedSeconds = (DateTimeOffset.UtcNow - startUtc).TotalSeconds;
            if (!reloaded && elapsedSeconds >= AdventureDangerVideoTimeoutSeconds / 2.0
                && !await IsAdventureDangerVideoDialogOpenAsync(cancellationToken))
            {
                reloaded = true;
                Notify("[adventure-danger] video dialog closed; reloading adventures page to read reward state.");
                await OpenHeroAdventuresPageAsync(cancellationToken);
            }
        }

        return false;
    }

    private async Task<bool> IsAdventureDangerVideoDialogOpenAsync(CancellationToken cancellationToken)
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
    /// Diagnostic: logs the difficulty box markup so the exact "active" success state can be locked
    /// down from a real run (the automated browser blocks the ad, so it cannot be observed offline).
    /// </summary>
    private async Task LogAdventureDangerBoxHtmlAsync(string when, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var html = await _page.EvaluateAsync<string?>(
                """
                () => {
                  const box = document.querySelector('.videoFeatureBonusBox.adventureDifficulty');
                  if (!box) return null;
                  return String(box.outerHTML || '').replace(/\s+/g, ' ').slice(0, 800);
                }
                """);
            Notify($"[adventure-danger:diag] difficulty box {when}: {(string.IsNullOrWhiteSpace(html) ? "(not found)" : html)}");
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            // Diagnostic only — ignore transient navigation.
        }
    }

}
