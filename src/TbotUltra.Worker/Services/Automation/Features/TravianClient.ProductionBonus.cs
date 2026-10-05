using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    // Poll once per second so the "video finished" state (ad overlay closed + bonus box active) is noticed
    // within ~1s instead of up to 2s. The DOM read is cheap; the dominant former delay was the post-play
    // minute, which the completion check above now skips once the overlay has closed.
    private const int ProductionBonusVideoPollIntervalMs = 1000;
    private const int AdvantagesRenderAttempts = 60;
    private const int AdvantagesRenderPollIntervalMs = 500;
    private const int AdvantagesOpenAttempts = 2;
    private const int ProductionBonusVideoMaxAttemptsPerResource = 2;

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
            // The running bonus renders as "+N% active for:" next to its .timerReact countdown, inside
            // .bonusInfo — NOT in .bonusDuration (that only holds the auto-prolong checkbox / "whole game
            // round" text). Read the percent AND the timer from the countdown's own label so 25% vs 15% is
            // classified correctly and the remaining time is never dropped.
            const timerEl = box.querySelector('.bonusInfo .timerReact') || box.querySelector('.timerReact');
            if (timerEl) {
              timer = strip(timerEl.textContent).trim();
              const label = timerEl.closest('.bonusInfo') || timerEl.parentElement || box;
              const m = strip(label.textContent).match(/(\d+)\s*%/);
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
    /// reads back the resulting per-resource state (25%/15%/none + remaining timers) into a typed outcome.
    /// Account-wide; Official Travian only.
    ///
    /// Each resource is watched in its OWN isolated bonus-video browser. All resources found activatable at
    /// the start are attempted contiguously in the same automation task; a failure for one resource is logged
    /// but does not hand control back to other automation before the remaining resources are tried. Never
    /// spends gold and never clicks the gold Activate/Extend/Upgrade buttons. Best-effort — only cancellation propagates.
    /// </summary>
    public async Task<ProductionBonusOutcome> ActivateProductionBonusVideosAsync(CancellationToken cancellationToken = default)
    {
        Notify("[production-bonus] starting — activating free +15% production videos.");
        try
        {
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);
            return await CreateProductionBonusOperation().RunAsync(
                ProductionBonusRunIntent.Activate,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            Notify($"[production-bonus] WARNING: inspection unavailable: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            Notify($"[production-bonus:verbose] feature failed and was skipped: {ex.GetType().Name}: {ex.Message}");
            return ProductionBonusOutcome.Failed(
                $"Production bonus: could not run and was skipped ({ex.Message}).");
        }
    }

    /// <summary>
    /// Read-only: opens the Advantages tab, reads the current per-resource bonus state (25%/15%/none +
    /// timers) and returns a typed observation. Watches no video and clicks nothing.
    /// Used by the manual "Scan timers" button. Best-effort — only cancellation propagates.
    /// </summary>
    public async Task<ProductionBonusOutcome> ScanProductionBonusTimersAsync(CancellationToken cancellationToken = default)
    {
        Notify("[production-bonus] scanning Advantages timers.");
        try
        {
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);
            return await CreateProductionBonusOperation().RunAsync(
                ProductionBonusRunIntent.Inspect,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            Notify($"[production-bonus] WARNING: inspection unavailable: {ex.Message}");
            return ProductionBonusOutcome.Failed(
                $"Production bonus: scan could not complete ({ex.Message}).");
        }
        catch (Exception ex)
        {
            Notify($"[production-bonus:verbose] scan failed and was skipped: {ex.GetType().Name}: {ex.Message}");
            return ProductionBonusOutcome.Failed(
                $"Production bonus: scan could not run and was skipped ({ex.Message}).");
        }
    }

    private ProductionBonusOperation CreateProductionBonusOperation()
        => new(new ProductionBonusBrowserAdapter(this), Notify);

    private sealed class ProductionBonusBrowserAdapter : IProductionBonusBrowser
    {
        private readonly TravianClient _client;

        internal ProductionBonusBrowserAdapter(TravianClient client)
        {
            _client = client;
        }

        public async Task<ProductionBonusObservation> InspectAsync(
            bool afterActivationAttempt,
            bool refreshPage,
            CancellationToken cancellationToken)
        {
            var pageState = await _client.ReadProductionBonusPageStateInMainBrowserAsync(
                refreshPage,
                cancellationToken);
            return pageState.AccountDeletionPending
                ? new ProductionBonusObservation([], null, AccountDeletionPending: true)
                : new ProductionBonusObservation(
                    ProductionBonusDomParser.Classify(pageState.Boxes, afterActivationAttempt),
                    pageState.ServerUtcOffset);
        }

        public IProductionBonusActivationBatch BeginActivationBatch()
            => new ActivationBatchAdapter(_client._isolatedBonusVideoRunner.BeginOperation());

        public async Task ActivateInCurrentBrowserAsync(
            IReadOnlyList<string> resources,
            CancellationToken cancellationToken)
            => _ = await _client.RunProductionBonusVideosInCurrentBrowserAsync(resources, cancellationToken);

        private sealed class ActivationBatchAdapter : IProductionBonusActivationBatch
        {
            private readonly IIsolatedBonusVideoOperation _operation;

            internal ActivationBatchAdapter(IIsolatedBonusVideoOperation operation)
            {
                _operation = operation;
            }

            public async Task<ProductionBonusActivationResult> ActivateAsync(
                string resource,
                CancellationToken cancellationToken)
            {
                var result = await _operation.RunAsync(
                    new ProductionBonusVideoRequest(resource),
                    cancellationToken);
                var status = result.Status switch
                {
                    IsolatedBonusVideoRunStatus.Completed => ProductionBonusActivationStatus.Completed,
                    IsolatedBonusVideoRunStatus.CooldownActive => ProductionBonusActivationStatus.CooldownActive,
                    IsolatedBonusVideoRunStatus.Unavailable => ProductionBonusActivationStatus.Unavailable,
                    _ => ProductionBonusActivationStatus.Failed,
                };
                return new ProductionBonusActivationResult(
                    status,
                    result.Message,
                    result.FailureKind is BonusVideoFailureKind.None or BonusVideoFailureKind.Unknown,
                    result.RetryAtUtc);
            }
        }
    }

    // Opens the Advantages tab in the main browser, reads the boxes, then returns to dorf1 so no ad/video
    // iframe is left loaded in the main context.
    private sealed record ProductionBonusPageState(
        IReadOnlyList<ProductionBonusDomParser.ProductionBonusBox> Boxes,
        TimeSpan? ServerUtcOffset,
        bool AccountDeletionPending = false);

    private async Task<ProductionBonusPageState> ReadProductionBonusPageStateInMainBrowserAsync(
        bool refreshPage,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var openAttempt = 1; openAttempt <= AdvantagesOpenAttempts; openAttempt++)
            {
                // Reuse a healthy dorf1 page for the initial read. A post-activation read must refresh once
                // to observe server state written by the isolated browser. Later attempts are true recovery.
                if (openAttempt == 1 && !refreshPage)
                {
                    await EnsurePageForReadAsync(
                        Paths.Resources,
                        "production bonus inspection",
                        cancellationToken);
                }
                else
                {
                    await ReloadOrGotoAsync(Paths.Resources, cancellationToken);
                }

                var pageHtml = await _page.ContentAsync();
                if (AccountDeletionDomParser.IsPending(pageHtml))
                {
                    return new ProductionBonusPageState([], null, AccountDeletionPending: true);
                }

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
            // The payment wizard contains no running ad at inspection time. Dismiss it locally instead of
            // reloading dorf1, so a successful read does not create a second page load.
            await TryDismissAdvantagesTabAsync(cancellationToken);
        }
    }

    private async Task TryDismissAdvantagesTabAsync(CancellationToken cancellationToken)
    {
        const string wizardGoneScript =
            """
            () => {
              const nodes = document.querySelectorAll('#paymentWizardContent, #paymentWizard, .paymentWizard, .advantagesBonusBox');
              return !Array.from(nodes).some(node => node.getClientRects().length > 0);
            }
            """;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _page.EvaluateAsync<bool>(wizardGoneScript))
            {
                return;
            }

            await PressKeyAsync("Escape", "dismiss-production-bonus-wizard", cancellationToken);
            await _page.WaitForFunctionAsync(
                wizardGoneScript,
                null,
                new PageWaitForFunctionOptions { Timeout = 3000 });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            Notify($"[production-bonus:verbose] Advantages wizard could not be dismissed without navigation: {ex.Message}");
        }
    }

    // Isolated browser: activate exactly one resource's +15% video.
    internal async Task<string> RunSingleProductionBonusVideoIsolatedAsync(string resource, CancellationToken cancellationToken)
    {
        var pageReady = await LoadIsolatedBonusVideoPageAsync(
            Paths.Resources,
            $"{resource} production bonus",
            pageLoadCancellationToken => IsLoggedInAsync(pageLoadCancellationToken),
            cancellationToken);
        if (!pageReady)
        {
            // Do not log in here: Travian allows a single active session, so a second login would log the
            // main browser out. Skip instead (same reasoning as the adventure bonus video).
            Notify($"[production-bonus:verbose] {resource}: isolated browser did not load a ready Travian page after two reloads; skipping so the main session is not disturbed.");
            return $"{resource}: skipped — the bonus-video browser could not load Travian.";
        }

        await AcceptConsentManagerIfPresentAsync(
            cancellationToken,
            "[production-bonus:verbose]",
            observeLateOverlay: true);

        if (!await OpenAdvantagesTabAsync(cancellationToken))
        {
            return $"{resource}: could not open the Advantages tab.";
        }

        var result = await RunProductionBonusVideoCoreAsync(resource, cancellationToken);
        var resultKind = BonusVideoFailureClassifier.Classify(result);
        Notify($"[production-bonus{(resultKind == BonusVideoFailureKind.None ? string.Empty : ":verbose")}] {result}");
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
            for (var attempt = 1; attempt <= ProductionBonusVideoMaxAttemptsPerResource; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await OpenAdvantagesTabAsync(cancellationToken))
                {
                    break;
                }

                var result = await RunProductionBonusVideoCoreAsync(resource, cancellationToken);
                var resultKind = BonusVideoFailureClassifier.Classify(result);
                Notify($"[production-bonus{(resultKind == BonusVideoFailureKind.None ? string.Empty : ":verbose")}] {result}");
                var boxes = ProductionBonusDomParser.ParseBoxesJson(await ReadProductionBonusBoxesRawAsync(cancellationToken));
                if (ProductionBonusDomParser.FindUnconfirmedActivations(new[] { resource }, boxes).Count == 0)
                {
                    activated++;
                    break;
                }

                await GotoAsync(Paths.Resources, cancellationToken);
                if (attempt >= ProductionBonusVideoMaxAttemptsPerResource
                    || resultKind is not (BonusVideoFailureKind.None or BonusVideoFailureKind.Unknown))
                {
                    break;
                }
            }
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

        // The info dialog, ad player and completion detection are the shared Travian bonus-video overlay
        // (#videoFeature / #videoArea), identical to construct-faster — reuse it.
        if (!await ConfirmConstructFasterVideoDialogAsync(cancellationToken))
        {
            return $"{resource}: the video info dialog did not confirm.";
        }

        var playback = await RunProductionBonusVideoPlaybackAsync(resource, cancellationToken);
        if (playback.Status == BonusVideoPlaybackStatus.StartUnavailable)
        {
            if (!await IsH264PlaybackSupportedAsync(cancellationToken))
            {
                return $"{resource}: this browser cannot play the ad video (missing H.264/AAC codecs). Install "
                    + "Google Chrome on this machine so the bonus videos can run.";
            }

            return $"{resource}: the bonus video player did not open (likely no ad available or blocked).";
        }

        if (playback.Status == BonusVideoPlaybackStatus.ProviderFailed
            && !string.IsNullOrWhiteSpace(playback.ProviderFailure))
        {
            return $"{resource}: {playback.ProviderFailure}.";
        }

        return playback.Status == BonusVideoPlaybackStatus.Completed
            ? $"{resource}: +15% production video completed."
            : $"{resource}: bonus video ran but completion was not confirmed.";
    }

    // Waits for the +15% video to actually play through. Unlike construct-faster we must NOT treat a
    // dorf1/dorf2 URL as completion: the payment wizard is a React overlay ON dorf1, so the URL is already
    // dorf1 and that check fires instantly (the browser then closes before the ad even starts). Instead we
    // succeed only when the resource box turns active (+15% timer appears), after the shared protected
    // post-play minute has elapsed.
    private async Task<BonusVideoPlaybackResult> RunProductionBonusVideoPlaybackAsync(
        string resource,
        CancellationToken cancellationToken)
    {
        var playback = CreateBonusVideoPlayback();
        return await playback.RunAsync(
            new BonusVideoPlaybackRequest(
                "production bonus",
                "[production-bonus:verbose]",
                TimeSpan.FromMilliseconds(ProductionBonusVideoPollIntervalMs)),
            async (_, pollCancellationToken) =>
            {
                var boxes = ProductionBonusDomParser.ParseBoxesJson(
                    await ReadProductionBonusBoxesRawAsync(pollCancellationToken));
                var boxActive = ProductionBonusDomParser
                    .FindUnconfirmedActivations(new[] { resource }, boxes)
                    .Count == 0;
                return new BonusVideoFeatureObservation(
                    boxActive
                        ? BonusVideoFeatureSignal.RewardConfirmed
                        : BonusVideoFeatureSignal.None);
            },
            cancellationToken);
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

}
