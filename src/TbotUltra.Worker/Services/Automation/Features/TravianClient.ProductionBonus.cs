using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int ProductionBonusVideoTimeoutSeconds = 75;
    private const int ProductionBonusVideoPollIntervalMs = 2000;
    // Minimum playthrough before a closed player counts as "done", so an ad that has not started yet is
    // never mistaken for a completed one.
    private const int ProductionBonusVideoMinPlaySeconds = 20;
    private const int AdvantagesRenderAttempts = 60;
    private const int AdvantagesRenderPollIntervalMs = 500;
    private const int AdvantagesOpenAttempts = 2;

    // Opens Travian's payment wizard on the Advantages tab and returns which tab/state is visible.
    private const string OpenAdvantagesWizardScript =
        """
        () => {
          try {
            if (window.Travian && Travian.React && typeof Travian.React.openPaymentWizard === 'function') {
              Travian.React.openPaymentWizard({ activeTab: 'advantages' });
              return 'react';
            }
          } catch (e) { /* fall through to DOM triggers */ }
          const btn = document.querySelector('button.productionBoostButton');
          if (btn) { btn.click(); return 'button'; }
          const shop = document.querySelector('a.shop');
          if (shop) { shop.click(); return 'shop'; }
          return 'missing';
        }
        """;

    private const string AdvantagesWizardStatusScript =
        """
        () => {
          const classes = ['lumberProductionBonus', 'clayProductionBonus', 'ironProductionBonus', 'cropProductionBonus'];
          const rendered = classes.filter(cls => document.querySelector('.advantagesBonusBox.' + cls)).length;
          if (rendered === classes.length) return 'boxes';
          if (rendered > 0) return 'loading';
          if (document.querySelector('#paymentWizardContent, #paymentWizard, .paymentWizard')) return 'wizard';
          return 'none';
        }
        """;

    private const string ClickAdvantagesTabScript =
        """
        () => {
          const tabs = Array.from(document.querySelectorAll('a.tabItem'));
          const tab = tabs.find(t => /advantages/i.test((t.textContent || '').trim()));
          if (!tab) return false;
          tab.click();
          return true;
        }
        """;

    // Reads the four resource bonus boxes into a JSON array. Strips the bidi/isolate markers Travian wraps
    // around the numbers so the percent/timer parse cleanly.
    private const string ReadProductionBonusBoxesScript =
        """
        () => {
          const strip = (s) => String(s || '').replace(/[‪-‮⁦-⁩‎‏]/g, '');
          const map = { lumber: 'lumberProductionBonus', clay: 'clayProductionBonus', iron: 'ironProductionBonus', crop: 'cropProductionBonus' };
          const out = [];
          for (const res of Object.keys(map)) {
            const box = document.querySelector('.advantagesBonusBox.' + map[res]);
            if (!box) continue;
            const active = box.classList.contains('active');
            let percent = 0, timer = '';
            const dur = box.querySelector('.bonusDuration');
            if (dur) {
              const t = dur.querySelector('.timerReact');
              timer = t ? strip(t.textContent).trim() : '';
              const m = strip(dur.textContent).match(/(\d+)\s*%/);
              if (m) percent = parseInt(m[1], 10);
            }
            // Only the purple "Activate" button in .bonusVideo is the free +15% video. The gold
            // prosButton (Activate/Extend/Upgrade) costs gold and must never be clicked.
            const purple = box.querySelector('.bonusVideo button.textButtonV2.purple')
              || box.querySelector('.bonusVideo button.withText.purple');
            const purplePresent = !!purple;
            const purpleEnabled = purplePresent
              && !purple.disabled
              && !String(purple.className || '').toLowerCase().includes('disabled');
            out.push({ resource: res, active: active, percent: percent, timer: timer, purplePresent: purplePresent, purpleEnabled: purpleEnabled });
          }
          return JSON.stringify(out);
        }
        """;

    private const string ReadProductionBonusServerUtcOffsetScript =
        """
        () => {
          const raw = window.Travian && Travian.Game ? Travian.Game.timezoneOffsetToUTC : null;
          const secondsToUtc = Number(raw);
          if (!Number.isFinite(secondsToUtc)) return '';
          return String(-secondsToUtc);
        }
        """;

    private const string ClickProductionBonusVideoButtonScript =
        """
        (cls) => {
          const box = document.querySelector('.advantagesBonusBox.' + cls);
          if (!box) return false;
          const btn = box.querySelector('.bonusVideo button.textButtonV2.purple')
            || box.querySelector('.bonusVideo button.withText.purple');
          if (!btn || btn.disabled) return false;
          btn.click();
          return true;
        }
        """;

    /// <summary>
    /// Activates the free +15% production bonus video for every resource that currently offers it, then
    /// reads back the resulting per-resource state (25%/15%/none + remaining timers) into the result
    /// string. Account-wide; Official Travian only.
    ///
    /// Each resource is watched in its OWN isolated bonus-video browser: the isolated flow is hard-capped
    /// at 120s, so four videos can never share one browser. Never spends gold and never clicks the gold
    /// Activate/Extend/Upgrade buttons. Best-effort — only cancellation propagates.
    /// </summary>
    public async Task<string> ActivateProductionBonusVideosAsync(CancellationToken cancellationToken = default)
    {
        Notify("[production-bonus] starting — activating free +15% production videos.");
        try
        {
            await EnsureLoggedInAsync();

            var initialState = await ReadProductionBonusPageStateInMainBrowserAsync(cancellationToken);
            var activatable = initialState.Boxes
                .Where(box => box.PurplePresent && box.PurpleEnabled && !box.Active)
                .Select(box => box.Resource)
                .ToList();

            if (activatable.Count == 0)
            {
                var stateOnly = ProductionBonusDomParser.Classify(initialState.Boxes);
                Notify($"[production-bonus] nothing to activate — {FormatProductionBonusLog(stateOnly)}.");
                return $"Production bonus: nothing to activate. {ProductionBonusDomParser.BuildResultToken(stateOnly)}{FormatProductionBonusOffsetToken(initialState.ServerUtcOffset)}{FormatProductionBonusFreeAvailableToken(false)}";
            }

            Notify($"[production-bonus] activatable resources: {string.Join(", ", activatable)}.");

            if (_runInIsolatedBonusVideoBrowserAsync is null)
            {
                // Fallback for tests / non-session callers: watch in the current browser (no isolation).
                await RunProductionBonusVideosInCurrentBrowserAsync(activatable, cancellationToken);
            }
            else
            {
                foreach (var resource in activatable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var videoResult = await _runInIsolatedBonusVideoBrowserAsync(
                            async (videoPage, videoCancellationToken) =>
                            {
                                var videoClient = CreateIsolatedBonusVideoClient(videoPage);
                                return await videoClient.RunSingleProductionBonusVideoIsolatedAsync(resource, videoCancellationToken);
                            },
                            cancellationToken);
                        var failureKind = BonusVideoFailureClassifier.Classify(videoResult);
                        if (failureKind != BonusVideoFailureKind.None
                            && !BonusVideoFailureClassifier.ShouldRetryImmediately(failureKind))
                        {
                            Notify(
                                $"[production-bonus] stopping remaining video attempts after {failureKind}; "
                                + "normal automation continues and videos can retry after cooldown.");
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Notify($"[production-bonus:verbose] {resource}: isolated bonus video failed and was skipped: {ex.GetType().Name}: {ex.Message}");
                        var failureKind = BonusVideoFailureClassifier.Classify(ex.Message);
                        if (!BonusVideoFailureClassifier.ShouldRetryImmediately(failureKind))
                        {
                            Notify(
                                $"[production-bonus] stopping remaining video attempts after {failureKind}; "
                                + "normal automation continues without changing route.");
                            break;
                        }
                    }
                    finally
                    {
                        // Always bring the main browser back to dorf1 after each isolated video.
                        await ReturnMainPageAfterIsolatedBonusVideoAsync();
                    }
                }
            }

            // Verify and collect the resulting timers from the main browser.
            var finalPageState = await ReadProductionBonusPageStateInMainBrowserAsync(cancellationToken);
            var finalStates = ProductionBonusDomParser.Classify(finalPageState.Boxes);
            var token = ProductionBonusDomParser.BuildResultToken(finalStates);
            Notify($"[production-bonus] done — {FormatProductionBonusLog(finalStates)}.");
            return $"Production bonus: processed {activatable.Count} resource(s). {token}{FormatProductionBonusOffsetToken(finalPageState.ServerUtcOffset)}{FormatProductionBonusFreeAvailableToken(activatable.Count > 0)}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            Notify($"[production-bonus] ALARM: could not inspect +15% bonuses because {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            Notify($"[production-bonus:verbose] feature failed and was skipped: {ex.GetType().Name}: {ex.Message}");
            return $"Production bonus: could not run and was skipped ({ex.Message}).";
        }
    }

    /// <summary>
    /// Read-only: opens the Advantages tab, reads the current per-resource bonus state (25%/15%/none +
    /// timers) and returns it as the production_bonus=... token. Watches no video and clicks nothing.
    /// Used by the manual "Scan timers" button. Best-effort — only cancellation propagates.
    /// </summary>
    public async Task<string> ScanProductionBonusTimersAsync(CancellationToken cancellationToken = default)
    {
        Notify("[production-bonus] scanning Advantages timers.");
        try
        {
            await EnsureLoggedInAsync();
            var pageState = await ReadProductionBonusPageStateInMainBrowserAsync(cancellationToken);
            var states = ProductionBonusDomParser.Classify(pageState.Boxes, afterActivationAttempt: false);
            var freeAvailable = ProductionBonusDomParser.AnyActivatable(pageState.Boxes);
            Notify($"[production-bonus] scan done — {FormatProductionBonusLog(states)}.");
            return $"Production bonus: scanned. {ProductionBonusDomParser.BuildResultToken(states)}{FormatProductionBonusOffsetToken(pageState.ServerUtcOffset)}{FormatProductionBonusFreeAvailableToken(freeAvailable)}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            Notify($"[production-bonus] ALARM: could not inspect +15% bonuses because {ex.Message}");
            return $"Production bonus: scan could not complete ({ex.Message}).";
        }
        catch (Exception ex)
        {
            Notify($"[production-bonus:verbose] scan failed and was skipped: {ex.GetType().Name}: {ex.Message}");
            return $"Production bonus: scan could not run and was skipped ({ex.Message}).";
        }
    }

    // Opens the Advantages tab in the main browser, reads the boxes, then returns to dorf1 so no ad/video
    // iframe is left loaded in the main context.
    private sealed record ProductionBonusPageState(
        IReadOnlyList<ProductionBonusDomParser.ProductionBonusBox> Boxes,
        TimeSpan? ServerUtcOffset);

    private async Task<ProductionBonusPageState> ReadProductionBonusPageStateInMainBrowserAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            for (var openAttempt = 1; openAttempt <= AdvantagesOpenAttempts; openAttempt++)
            {
                // A fresh dorf1 load gives a slow or stalled React wizard one clean retry.
                await ReloadOrGotoAsync(Paths.Resources, cancellationToken);
                if (!await OpenAdvantagesTabAsync(cancellationToken))
                {
                    Notify($"[production-bonus:verbose] Advantages tab did not finish rendering (open attempt {openAttempt}/{AdvantagesOpenAttempts}).");
                    continue;
                }

                // The status script saw all four boxes. Read until the serialized state agrees, guarding
                // against a React re-render or transient execution-context replacement between calls.
                for (var readAttempt = 1; readAttempt <= 5; readAttempt++)
                {
                    var boxes = ProductionBonusDomParser.ParseBoxesJson(await ReadProductionBonusBoxesRawAsync(cancellationToken));
                    if (ProductionBonusDomParser.HasCompleteResourceSet(boxes))
                    {
                        var serverUtcOffset = await ReadProductionBonusServerUtcOffsetAsync(cancellationToken);
                        return new ProductionBonusPageState(boxes, serverUtcOffset);
                    }

                    await Task.Delay(400, cancellationToken);
                }

                Notify($"[production-bonus:verbose] Advantages tab returned an incomplete resource set (open attempt {openAttempt}/{AdvantagesOpenAttempts}).");
            }

            throw new TimeoutException(
                "Advantages did not finish loading all four production bonus boxes after two attempts.");
        }
        finally
        {
            // Close the wizard/iframes before normal automation continues, including after a failed read.
            await ReloadOrGotoAsync(Paths.Resources, cancellationToken);
        }
    }

    // Isolated browser: activate exactly one resource's +15% video.
    private async Task<string> RunSingleProductionBonusVideoIsolatedAsync(string resource, CancellationToken cancellationToken)
    {
        await GotoAsync(Paths.Resources, cancellationToken);
        if (!await IsLoggedInAsync())
        {
            // Do not log in here: Travian allows a single active session, so a second login would log the
            // main browser out. Skip instead (same reasoning as the adventure bonus video).
            Notify($"[production-bonus:verbose] {resource}: isolated browser is not logged in (stale cookies); skipping so the main session is not disturbed.");
            return $"{resource}: skipped — the bonus-video browser was not logged in.";
        }

        await AcceptConsentManagerIfPresentAsync(cancellationToken, "[production-bonus:verbose]");

        if (!await OpenAdvantagesTabAsync(cancellationToken))
        {
            return $"{resource}: could not open the Advantages tab.";
        }

        var result = await RunProductionBonusVideoCoreAsync(resource, cancellationToken);
        Notify($"[production-bonus] {result}");
        return result;
    }

    // Fallback path used when no isolated-browser factory is wired (tests / non-session callers).
    private async Task<string> RunProductionBonusVideosInCurrentBrowserAsync(
        IReadOnlyList<string> resources,
        CancellationToken cancellationToken)
    {
        var activated = 0;
        foreach (var resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await OpenAdvantagesTabAsync(cancellationToken))
            {
                continue;
            }

            var result = await RunProductionBonusVideoCoreAsync(resource, cancellationToken);
            Notify($"[production-bonus] {result}");
            if (result.Contains("video completed", StringComparison.OrdinalIgnoreCase))
            {
                activated++;
            }

            await GotoAsync(Paths.Resources, cancellationToken);
        }

        return $"Production bonus: activated {activated} resource(s).";
    }

    // Assumes the Advantages tab is already open. Confirms the resource is still activatable, clicks the
    // purple free-video button, then reuses the shared #videoFeature dialog / player / completion flow.
    private async Task<string> RunProductionBonusVideoCoreAsync(string resource, CancellationToken cancellationToken)
    {
        var cls = ResourceBonusBoxClass(resource);
        var box = ProductionBonusDomParser
            .ParseBoxesJson(await ReadProductionBonusBoxesRawAsync(cancellationToken))
            .FirstOrDefault(candidate => string.Equals(candidate.Resource, resource, StringComparison.OrdinalIgnoreCase));
        if (box is null)
        {
            return $"{resource}: bonus box not found on the Advantages tab.";
        }

        if (box.Active)
        {
            return $"{resource}: a production bonus is already active.";
        }

        if (!box.PurplePresent || !box.PurpleEnabled)
        {
            return $"{resource}: the free +15% video is not available.";
        }

        await DelayBeforeClickAsync(cancellationToken);
        var clicked = await _page.EvaluateAsync<bool>(ClickProductionBonusVideoButtonScript, cls);
        if (!clicked)
        {
            return $"{resource}: could not click the free +15% video button.";
        }

        Notify($"[production-bonus] {resource}: clicked the free +15% video button.");
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the production bonus video.", cancellationToken);

        // The info dialog, ad player and completion detection are the shared Travian bonus-video overlay
        // (#videoFeature / #videoArea), identical to construct-faster — reuse it.
        if (!await ConfirmConstructFasterVideoDialogAsync(cancellationToken))
        {
            return $"{resource}: the video info dialog did not confirm.";
        }

        if (!await StartConstructFasterVideoAsync(cancellationToken))
        {
            if (!await IsH264PlaybackSupportedAsync(cancellationToken))
            {
                return $"{resource}: this browser cannot play the ad video (missing H.264/AAC codecs). Install "
                    + "Google Chrome on this machine so the bonus videos can run.";
            }

            return $"{resource}: the bonus video player did not open (likely no ad available or blocked).";
        }

        var completed = await WaitForProductionBonusVideoCompletionAsync(cls, cancellationToken);
        return completed
            ? $"{resource}: +15% production video completed."
            : $"{resource}: bonus video ran but completion was not confirmed.";
    }

    // Waits for the +15% video to actually play through. Unlike construct-faster we must NOT treat a
    // dorf1/dorf2 URL as completion: the payment wizard is a React overlay ON dorf1, so the URL is already
    // dorf1 and that check fires instantly (the browser then closes before the ad even starts). Instead we
    // succeed when the resource box turns active (+15% timer appears) or the video player has closed after a
    // real minimum playthrough.
    private async Task<bool> WaitForProductionBonusVideoCompletionAsync(string cls, CancellationToken cancellationToken)
    {
        var startUtc = DateTimeOffset.UtcNow;
        var deadlineUtc = startUtc.AddSeconds(ProductionBonusVideoTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(ProductionBonusVideoPollIntervalMs, cancellationToken);

            string rawJson;
            try
            {
                rawJson = await _page.EvaluateAsync<string>(
                    """
                    (cls) => {
                      const strip = (s) => String(s || '').replace(/[‪-‮⁦-⁩‎‏]/g, '');
                      const box = document.querySelector('.advantagesBonusBox.' + cls);
                      let boxActive = false;
                      if (box && box.classList.contains('active')) {
                        const dur = box.querySelector('.bonusDuration');
                        boxActive = !!dur && /15\s*%/.test(strip(dur.textContent));
                      }
                      const dlg = document.querySelector('#videoFeature');
                      const dialogOpen = !!dlg && !String(dlg.className || '').includes('hide');
                      const hasPlayer = !!document.querySelector('#videoArea, #videoFeature iframe');
                      return JSON.stringify({ boxActive, dialogOpen, hasPlayer });
                    }
                    """,
                    cls);
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(rawJson ?? "{}");
            var root = doc.RootElement;
            if (GetBoolean(root, "boxActive"))
            {
                Notify("[production-bonus] video completion confirmed — box shows +15% active.");
                return true;
            }

            var elapsedSeconds = (DateTimeOffset.UtcNow - startUtc).TotalSeconds;
            if (elapsedSeconds >= 8 && await TryReadVisibleBonusVideoFailureAsync(cancellationToken) is { } visibleFailure)
            {
                Notify($"[production-bonus:verbose] {visibleFailure}; stopping video wait early.");
                return false;
            }

            if (elapsedSeconds >= ProductionBonusVideoMinPlaySeconds
                && !GetBoolean(root, "dialogOpen")
                && !GetBoolean(root, "hasPlayer"))
            {
                Notify("[production-bonus] video completion inferred — player closed after playthrough.");
                return true;
            }
        }

        return false;
    }

    // Opens the payment wizard on the Advantages tab and waits for the bonus boxes to render. When the
    // wizard opens on another tab it clicks the Advantages tab item.
    private async Task<bool> OpenAdvantagesTabAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DelayBeforeClickAsync(cancellationToken);

        // "Execution context was destroyed" is a harmless navigation race (see ENGINEERING_NOTES) that
        // can hit the trigger right after a GotoAsync settles — retry a few times instead of failing.
        var trigger = "missing";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                trigger = await _page.EvaluateAsync<string>(OpenAdvantagesWizardScript);
                break;
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                Notify($"[production-bonus:verbose] open Advantages trigger hit a navigation race (attempt {attempt}/3): {ex.Message}");
                await Task.Delay(400, cancellationToken);
            }
        }

        Notify($"[production-bonus:verbose] open Advantages tab trigger -> {trigger}.");
        if (trigger == "missing")
        {
            return false;
        }

        for (var attempt = 1; attempt <= AdvantagesRenderAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string status;
            try
            {
                status = await _page.EvaluateAsync<string>(AdvantagesWizardStatusScript);
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                await Task.Delay(AdvantagesRenderPollIntervalMs, cancellationToken);
                continue;
            }

            if (status == "boxes")
            {
                return true;
            }

            if (status == "wizard")
            {
                await _page.EvaluateAsync<bool>(ClickAdvantagesTabScript);
            }

            await Task.Delay(AdvantagesRenderPollIntervalMs, cancellationToken);
        }

        Notify("[production-bonus:verbose] Advantages did not render all four bonus boxes before the 30s deadline.");
        return false;
    }

    private async Task<string> ReadProductionBonusBoxesRawAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<string>(ReadProductionBonusBoxesScript);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return "[]";
        }
    }

    private async Task<TimeSpan?> ReadProductionBonusServerUtcOffsetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var raw = await _page.EvaluateAsync<string>(ReadProductionBonusServerUtcOffsetScript);
            return int.TryParse(raw, out var seconds) ? TimeSpan.FromSeconds(seconds) : null;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
    }

    private static string ResourceBonusBoxClass(string resource) => resource switch
    {
        "lumber" => "lumberProductionBonus",
        "clay" => "clayProductionBonus",
        "iron" => "ironProductionBonus",
        "crop" => "cropProductionBonus",
        _ => resource + "ProductionBonus",
    };

    private static string FormatProductionBonusLog(IReadOnlyList<ProductionBonusDomParser.ProductionBonusResourceState> states)
    {
        return string.Join(
            ", ",
            states.Select(state =>
            {
                var bonus = state.Bonus == 0 ? "none" : state.Bonus + "%";
                return $"{state.Resource}={bonus}({state.RemainingSeconds}s)";
            }));
    }

    private static string FormatProductionBonusOffsetToken(TimeSpan? serverUtcOffset)
        => serverUtcOffset.HasValue
            ? " " + ProductionBonusDomParser.BuildServerUtcOffsetToken(serverUtcOffset.Value)
            : string.Empty;

    private static string FormatProductionBonusFreeAvailableToken(bool available)
        => " " + ProductionBonusDomParser.BuildFreeVideoAvailableToken(available);
}
