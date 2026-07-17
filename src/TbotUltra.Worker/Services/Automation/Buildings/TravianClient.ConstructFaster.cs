using Microsoft.Playwright;
using System.Globalization;
using System.Text.Json;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const string ConstructFasterButtonSelector = ".upgradeButtonsContainer .section2 button.videoFeatureButton";
    private const int ConstructFasterVideoPollIntervalMs = 2000;
    private const int ConstructFasterMaxVideoAttempts = 2;

    private sealed record ConstructFasterButtonState(
        bool Present,
        bool Disabled,
        string Text,
        string Classes);

    private sealed record ConstructFasterAttemptResult(
        bool ActionRegistered,
        bool BonusConfirmed,
        string Evidence)
    {
        internal static ConstructFasterAttemptResult Skipped(string evidence)
            => new(false, false, evidence);
    }

    private async Task<ConstructFasterAttemptResult> TryUseConstructFasterForBuildAsync(
        int slotId,
        int? gid,
        string buildingName,
        int previousLevel,
        int targetLevel,
        IReadOnlyList<BuildQueueItem> buildQueueBefore,
        int durationSeconds,
        string? restorePath,
        CancellationToken cancellationToken)
        => await TryUseConstructFasterAsync(
            slotId,
            gid,
            buildingName,
            durationSeconds,
            "building",
            verifyResultAsync: token => VerifyConstructFasterResultOnDorf2Async(
                slotId,
                previousLevel,
                buildingName,
                targetLevel,
                buildQueueBefore,
                gid,
                token),
            restorePageAsync: token => RestoreBuildPageAfterConstructFasterFallbackAsync(slotId, restorePath, token),
            cancellationToken);

    private async Task<ConstructFasterAttemptResult> TryUseConstructFasterForResourceAsync(
        int slotId,
        string resourceName,
        int previousLevel,
        int targetLevel,
        IReadOnlyList<BuildQueueItem> buildQueueBefore,
        int durationSeconds,
        CancellationToken cancellationToken)
        => await TryUseConstructFasterAsync(
            slotId,
            gid: null,
            resourceName,
            durationSeconds,
            "resource",
            verifyResultAsync: token => VerifyConstructFasterResultOnDorf1Async(
                slotId,
                resourceName,
                previousLevel,
                targetLevel,
                buildQueueBefore,
                durationSeconds,
                token),
            restorePageAsync: token => RestoreBuildPageAfterConstructFasterFallbackAsync(slotId, restorePath: null, token),
            cancellationToken);

    private async Task<ConstructFasterAttemptResult> TryUseConstructFasterAsync(
        int slotId,
        int? gid,
        string targetName,
        int durationSeconds,
        string constructionKind,
        Func<CancellationToken, Task<(bool Success, string Evidence)>> verifyResultAsync,
        Func<CancellationToken, Task> restorePageAsync,
        CancellationToken cancellationToken)
    {
        var state = await ReadConstructFasterButtonStateAsync(cancellationToken);
        var randomRoll = _config.ConstructFasterRandomEnabled ? Random.Shared.Next(0, 100) : (int?)null;
        var decision = ConstructFasterDecision.Evaluate(
            _config,
            durationSeconds,
            state.Present,
            state.Disabled,
            randomRoll);

        if (!decision.UseVideo)
        {
            if (_config.ConstructFasterEnabled)
            {
                Notify($"[construct-faster] skipped — {decision.Reason}.");
            }

            return ConstructFasterAttemptResult.Skipped(decision.Reason);
        }

        string? lastVideoResult = null;
        string? lastEvidence = null;
        var navigatedForVerification = false;
        for (var attempt = 1; attempt <= ConstructFasterMaxVideoAttempts; attempt++)
        {
            var videoCompleted = false;
            try
            {
                Notify($"[construct-faster] starting — kind={constructionKind}, slot={slotId}, gid={gid?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, duration={durationSeconds}s, attempt={attempt}/{ConstructFasterMaxVideoAttempts}, reason={decision.Reason}.");
                lastVideoResult = await RunConstructFasterVideoAsync(slotId, gid, targetName, cancellationToken);
                videoCompleted = true;
                Notify($"[construct-faster] video flow completed: {lastVideoResult}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BonusVideoCooldownException ex)
            {
                Notify(
                    "[construct-faster] skipped video — shared account/proxy cooldown active after "
                    + $"{BonusVideoFailureClassifier.Format(ex.Kind)}; building normally.");
                return ConstructFasterAttemptResult.Skipped($"video cooldown after {ex.Kind}");
            }
            catch (Exception ex)
            {
                lastVideoResult = ex.Message;
                Notify($"[construct-faster] video attempt {attempt}/{ConstructFasterMaxVideoAttempts} ended before normal completion: {ex.Message}. Verifying on fresh dorf2 before fallback.");
            }

            navigatedForVerification = true;
            var verification = await verifyResultAsync(cancellationToken);
            lastEvidence = verification.Evidence;
            if (verification.Success)
            {
                var outcome = ConstructFasterDecision.ResolveVerifiedOutcome(
                    videoCompleted,
                    targetConstructionVerified: verification.Success);
                if (videoCompleted)
                {
                    Notify($"[construct-faster] success — slot={slotId}, evidence={verification.Evidence}.");
                }
                else
                {
                    Notify(
                        $"[construct-faster] construction registered but 25% bonus was not confirmed — "
                        + $"slot={slotId}, evidence={verification.Evidence}.");
                }

                return new ConstructFasterAttemptResult(
                    outcome.ActionRegistered,
                    outcome.BonusConfirmed,
                    verification.Evidence);
            }

            if (attempt < ConstructFasterMaxVideoAttempts)
            {
                var failureKind = BonusVideoFailureClassifier.Classify(lastVideoResult);
                if (!BonusVideoFailureClassifier.ShouldRetryImmediately(failureKind))
                {
                    Notify(
                        $"[construct-faster] skipping immediate video retry after {failureKind}; "
                        + "building normally without changing route.");
                    break;
                }

                Notify($"[construct-faster] no queue/progress evidence after attempt {attempt}/{ConstructFasterMaxVideoAttempts}: {verification.Evidence}. Retrying video once.");
                continue;
            }
        }

        Notify($"[construct-faster] WARNING: video unavailable after {ConstructFasterMaxVideoAttempts} attempts (last video: {lastVideoResult ?? "no result"}, evidence: {lastEvidence ?? "none"}) — building normally.");
        if (navigatedForVerification)
        {
            await restorePageAsync(cancellationToken);
        }

        return ConstructFasterAttemptResult.Skipped(lastEvidence ?? lastVideoResult ?? "no result");
    }

    private async Task<(bool Success, string Evidence)> VerifyConstructFasterResultOnDorf1Async(
        int slotId,
        string resourceName,
        int previousLevel,
        int targetLevel,
        IReadOnlyList<BuildQueueItem> buildQueueBefore,
        int expectedWaitSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            Notify($"[construct-faster] verifying resource result on fresh dorf1 — slot={slotId}.");
            await GotoAsync(ResolveConstructFasterVerificationPath(ConstructionKind.Resource), cancellationToken);
            var progress = await WaitForResourceLevelAdvanceAsync(
                slotId,
                previousLevel,
                resourceName,
                targetLevel,
                buildQueueBefore,
                expectedWaitSeconds,
                cancellationToken);
            if (progress.Advanced || progress.QueuedOrInProgress)
            {
                return (true, progress.Evidence);
            }

            return (false, $"target level {targetLevel} not queued or reached: {progress.Evidence}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ShouldRethrowConstructFasterVerificationFailure(ex))
        {
            return (false, $"resource verification failed: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Evidence)> VerifyConstructFasterResultOnDorf2Async(
        int slotId,
        int previousLevel,
        string buildingName,
        int targetLevel,
        IReadOnlyList<BuildQueueItem> buildQueueBefore,
        int? gid,
        CancellationToken cancellationToken)
    {
        try
        {
            Notify($"[construct-faster] verifying result on fresh dorf2 — slot={slotId}.");
            await GotoAsync(ResolveConstructFasterVerificationPath(ConstructionKind.Building), cancellationToken);
            var progress = await WaitForBuildingLevelAdvanceAsync(
                slotId,
                previousLevel,
                buildingName,
                buildQueueBefore,
                gid,
                targetLevel,
                cancellationToken);
            if (progress.Advanced || progress.QueuedOrInProgress)
            {
                return (true, progress.Evidence);
            }

            var dorf2Level = await ProbeSlotLevelOnDorf2Async(slotId, cancellationToken);
            if (dorf2Level is int confirmedLevel && confirmedLevel > previousLevel)
            {
                return (true, $"dorf2 level {confirmedLevel}");
            }

            return (false, progress.Evidence);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ShouldRethrowConstructFasterVerificationFailure(ex))
        {
            return (false, $"verification failed: {ex.Message}");
        }
    }

    private static bool ShouldRethrowConstructFasterVerificationFailure(Exception ex)
    {
        if (BrowserFailureClassifier.IsTargetCrash(ex) || IsTransientExecutionContextException(ex))
        {
            return true;
        }

        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or PlaywrightException)
            {
                return true;
            }
        }

        return false;
    }

    private async Task RestoreBuildPageAfterConstructFasterFallbackAsync(
        int slotId,
        string? restorePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(restorePath) ? Paths.BuildBySlot(slotId) : restorePath;
            await GotoAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify($"[construct-faster] build-page restore after fallback failed: {ex.Message}");
        }
    }

    private async Task<string> RunConstructFasterVideoAsync(
        int slotId,
        int? gid,
        string buildingName,
        CancellationToken cancellationToken)
    {
        if (_runInIsolatedBonusVideoBrowserAsync is null)
        {
            throw new InvalidOperationException("isolated bonus-video browser is unavailable");
        }

        return await _runInIsolatedBonusVideoBrowserAsync(
            async (videoPage, videoCancellationToken) =>
            {
                var videoClient = CreateIsolatedBonusVideoClient(videoPage);
                return await videoClient.RunConstructFasterVideoInCurrentBrowserAsync(
                    slotId,
                    gid,
                    buildingName,
                    videoCancellationToken);
            },
            cancellationToken);
    }

    private async Task<string> RunConstructFasterVideoInCurrentBrowserAsync(
        int slotId,
        int? gid,
        string buildingName,
        CancellationToken cancellationToken)
    {
        await GotoAsync(BuildConstructFasterPath(slotId, gid), cancellationToken);
        if (!await IsLoggedInAsync())
        {
            throw new InvalidOperationException("isolated bonus-video browser is not logged in");
        }

        await AcceptConsentManagerIfPresentAsync(cancellationToken, "[construct-faster:verbose]");

        var state = await ReadConstructFasterButtonStateAsync(cancellationToken);
        Notify($"[construct-faster] button state before watching: present={state.Present}, disabled={state.Disabled}, text='{state.Text}'.");
        if (!state.Present)
        {
            throw new InvalidOperationException("video feature button missing");
        }

        if (state.Disabled)
        {
            throw new InvalidOperationException("video feature button disabled");
        }

        if (!await ClickConstructFasterWatchButtonAsync(cancellationToken))
        {
            throw new InvalidOperationException("could not click video feature button");
        }

        if (!await ConfirmConstructFasterVideoDialogAsync(cancellationToken))
        {
            throw new InvalidOperationException("video info dialog did not confirm");
        }

        var playClickedAtUtc = await StartConstructFasterVideoAsync(cancellationToken);
        if (playClickedAtUtc is null)
        {
            if (!await IsH264PlaybackSupportedAsync(cancellationToken))
            {
                throw new InvalidOperationException("browser cannot play H.264/AAC ad video");
            }

            throw new InvalidOperationException("video player did not open");
        }

        var completed = await WaitForConstructFasterVideoCompletionAsync(
            playClickedAtUtc.Value,
            cancellationToken);
        if (!completed)
        {
            if (!await IsH264PlaybackSupportedAsync(cancellationToken))
            {
                throw new InvalidOperationException("browser cannot play H.264/AAC ad video");
            }

            throw new InvalidOperationException(
                $"video completion not confirmed within {BonusVideoPlaybackPolicy.PostPlayTimeoutSeconds}s after play");
        }

        return $"{buildingName}: construct-faster video completed.";
    }

    internal static string BuildConstructFasterPath(int slotId, int? gid)
    {
        var path = Paths.BuildBySlot(slotId);
        if (gid is int value && value > 0)
        {
            path += $"&gid={value.ToString(CultureInfo.InvariantCulture)}";
        }

        return path;
    }

    internal static string ResolveConstructFasterVerificationPath(ConstructionKind constructionKind)
        => constructionKind == ConstructionKind.Resource ? Paths.Resources : Paths.Buildings;

    private async Task<ConstructFasterButtonState> ReadConstructFasterButtonStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var rawJson = await _page.EvaluateAsync<string>(
                """
                (selector) => {
                  const btn = document.querySelector(selector);
                  if (!btn) return JSON.stringify({ present: false, disabled: false, text: '', classes: '' });
                  const classes = String(btn.className || '');
                  const disabled =
                    !!btn.disabled
                    || classes.toLowerCase().includes('disabled')
                    || btn.getAttribute('aria-disabled') === 'true'
                    || !!btn.closest('.disabled');
                  return JSON.stringify({
                    present: true,
                    disabled,
                    text: String(btn.textContent || btn.value || '').replace(/\s+/g, ' ').trim(),
                    classes
                  });
                }
                """,
                ConstructFasterButtonSelector);

            using var doc = JsonDocument.Parse(rawJson ?? "{}");
            var root = doc.RootElement;
            return new ConstructFasterButtonState(
                GetBoolean(root, "present"),
                GetBoolean(root, "disabled"),
                GetString(root, "text"),
                GetString(root, "classes"));
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return new ConstructFasterButtonState(false, false, string.Empty, string.Empty);
        }
        catch (JsonException)
        {
            return new ConstructFasterButtonState(false, false, string.Empty, string.Empty);
        }
    }

    private async Task<bool> ClickConstructFasterWatchButtonAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DelayBeforeClickAsync(cancellationToken);
        var locator = _page.Locator(ConstructFasterButtonSelector).First;
        if (await locator.CountAsync() <= 0)
        {
            return false;
        }

        try
        {
            await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
            Notify("[construct-faster] clicked video feature button.");
            return true;
        }
        catch (PlaywrightException ex)
        {
            Notify($"[construct-faster:verbose] click video feature button failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ConfirmConstructFasterVideoDialogAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await _page.EvaluateAsync<string>(
                """
                () => {
                  const dlg = document.querySelector('#videoFeature');
                  const player = document.querySelector('#videoArea, #videoFeature iframe');
                  // 'Don't show it again' persisted on a previous run: Travian skips the info
                  // screen and opens the player directly, so there is nothing to confirm.
                  if ((dlg && String(dlg.className || '').includes('showVideo')) || player) return 'video';
                  if (!dlg) return 'none';
                  const ok = dlg.querySelector('.dialogButtonOk')
                    || Array.from(dlg.querySelectorAll('button')).find(b => /watch video/i.test(b.textContent || ''));
                  return ok ? 'ready' : 'pending';
                }
                """);

            if (status == "video")
            {
                Notify("[construct-faster] info dialog skipped ('don't show it again' already set); video opened directly.");
                return true;
            }

            if (status != "ready")
            {
                await Task.Delay(Random.Shared.Next(150, 350), cancellationToken);
                continue;
            }

            // Tick 'Don't show it again' so future runs skip this info screen (see 'video' branch above).
            await TickBonusVideoDontShowAgainAsync(cancellationToken, "[construct-faster]");
            await DelayBeforeClickAsync(cancellationToken);
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
                Notify("[construct-faster] confirmed 'Watch video' in info dialog.");
                return true;
            }

            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken);
        }

        return false;
    }

    // Checks the info dialog's 'Don't show it again' box so Travian skips the popup on later
    // runs and jumps straight to the video. Best-effort: a failure here must not abort the flow.
    private async Task TickBonusVideoDontShowAgainAsync(CancellationToken cancellationToken, string logPrefix)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await _page.EvaluateAsync<string>(
                """
                () => {
                  const dlg = document.querySelector('#videoFeature');
                  if (!dlg) return 'no-dialog';
                  const cb = dlg.querySelector('input[type="checkbox"][name="preference"]')
                    || dlg.querySelector('label.checkbox input[type="checkbox"]');
                  if (!cb) return 'no-checkbox';
                  if (cb.checked) return 'already-set';
                  cb.click(); // native click fires React onChange so the preference persists
                  return cb.checked ? 'set' : 'unchanged';
                }
                """);
            Notify($"{logPrefix} 'don't show it again' checkbox -> {result}.");
        }
        catch (PlaywrightException ex)
        {
            Notify($"{logPrefix}:verbose could not tick 'don't show it again' checkbox: {ex.Message}");
        }
    }

    private async Task<DateTimeOffset?> StartConstructFasterVideoAsync(CancellationToken cancellationToken)
        => await StartBonusVideoPlayerAsync("construct-faster", "[construct-faster:verbose]", cancellationToken);

    private async Task<bool> WaitForConstructFasterVideoCompletionAsync(
        DateTimeOffset playClickedAtUtc,
        CancellationToken cancellationToken)
    {
        var deadlineUtc = playClickedAtUtc.AddSeconds(BonusVideoPlaybackPolicy.PostPlayTimeoutSeconds);
        var consecutiveProviderFailures = 0;
        var earlyCompletionLogged = false;
        var ignoredProviderLogged = false;
        var videoWasActive = false;
        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(ConstructFasterVideoPollIntervalMs, cancellationToken);

            string rawJson;
            try
            {
                rawJson = await _page.EvaluateAsync<string>(
                    """
                    () => {
                      const url = window.location.href;
                      const dialog = document.querySelector('#videoFeature');
                      const dialogOpen = !!dialog && !String(dialog.className || '').includes('hide');
                      const hasPlayer = !!document.querySelector('#videoArea, #videoFeature iframe');
                      const onVillage = /\/dorf[12]\.php/i.test(url);
                      return JSON.stringify({ url, dialogOpen, hasPlayer, onVillage });
                    }
                    """);
            }
            catch (PlaywrightException ex) when (IsBonusVideoNavigationTransition(ex))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(rawJson ?? "{}");
            var root = doc.RootElement;
            var elapsedSeconds = (DateTimeOffset.UtcNow - playClickedAtUtc).TotalSeconds;
            var onVillage = GetBoolean(root, "onVillage");
            var dialogOpen = GetBoolean(root, "dialogOpen");
            var hasPlayer = GetBoolean(root, "hasPlayer");
            if (hasPlayer || !onVillage)
            {
                // The player loaded, or we navigated to the ad/video URL: the video genuinely started. Only
                // after this do we trust a later redirect back to the village as a real completion, so a
                // dialog that is merely opening (player not loaded yet) is never mistaken for a redirect.
                videoWasActive = true;
            }

            // A redirect back to the village with the player gone is Travian's own navigation AFTER the reward
            // flow finished, so accept it immediately once the video was actually active — no need to wait out
            // the protected minute. The weaker "dialog + player both gone" signal (no redirect) can also be an
            // ad no-fill that granted nothing, so it stays gated by the protected post-play minute.
            var villageRedirectComplete = onVillage && !hasPlayer && videoWasActive;
            var weakComplete = !dialogOpen && !hasPlayer;
            if (villageRedirectComplete)
            {
                Notify(
                    $"[construct-faster] video completion accepted via village redirect after {elapsedSeconds:F1}s " +
                    "post-play (player gone, back on village).");
                return true;
            }

            if (weakComplete && BonusVideoPlaybackPolicy.MayComplete(elapsedSeconds))
            {
                Notify(
                    $"[construct-faster] video completion signal accepted after {elapsedSeconds:F1}s post-play " +
                    $"villageRedirect={onVillage} dialogOpen={dialogOpen} playerPresent={hasPlayer}.");
                return true;
            }

            if (weakComplete && !earlyCompletionLogged)
            {
                earlyCompletionLogged = true;
                Notify(
                    $"[construct-faster:verbose] completion signal ignored after {elapsedSeconds:F1}s; " +
                    $"protected post-play minute has {BonusVideoPlaybackPolicy.RemainingGraceSeconds(elapsedSeconds)}s remaining.");
            }

            var visibleFailure = await TryReadVisibleBonusVideoFailureAsync(cancellationToken);
            if (visibleFailure is not null)
            {
                consecutiveProviderFailures++;
                if (BonusVideoPlaybackPolicy.MayAcceptProviderFailure(
                        elapsedSeconds,
                        consecutiveProviderFailures,
                        hasPlayer))
                {
                    Notify(
                        $"[construct-faster:verbose] provider failure confirmed after {elapsedSeconds:F1}s " +
                        $"post-play confirmations={consecutiveProviderFailures} playerPresent={hasPlayer}.");
                    throw new InvalidOperationException(visibleFailure);
                }

                if (!BonusVideoPlaybackPolicy.MayComplete(elapsedSeconds) && !ignoredProviderLogged)
                {
                    ignoredProviderLogged = true;
                    Notify(
                        $"[construct-faster:verbose] ignored provider text during protected post-play minute " +
                        $"({BonusVideoPlaybackPolicy.RemainingGraceSeconds(elapsedSeconds)}s remaining).");
                }
            }
            else
            {
                consecutiveProviderFailures = 0;
            }
        }

        return false;
    }

    private static bool GetBoolean(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            && property.GetBoolean();
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
    }
}
