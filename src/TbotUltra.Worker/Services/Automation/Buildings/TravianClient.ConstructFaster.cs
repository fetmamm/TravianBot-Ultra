using Microsoft.Playwright;
using System.Globalization;
using System.Text.Json;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const string ConstructFasterButtonSelector = ".upgradeButtonsContainer .section2 button.videoFeatureButton";
    private const int ConstructFasterVideoTimeoutSeconds = 75;
    private const int ConstructFasterVideoPollIntervalMs = 2000;
    private const int ConstructFasterMaxVideoAttempts = 2;

    private sealed record ConstructFasterButtonState(
        bool Present,
        bool Disabled,
        string Text,
        string Classes);

    private async Task<bool> TryUseConstructFasterForBuildAsync(
        int slotId,
        int? gid,
        string buildingName,
        int previousLevel,
        string queueFingerprintBefore,
        int durationSeconds,
        string? restorePath,
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

            return false;
        }

        string? lastVideoResult = null;
        string? lastEvidence = null;
        var navigatedForVerification = false;
        for (var attempt = 1; attempt <= ConstructFasterMaxVideoAttempts; attempt++)
        {
            try
            {
                Notify($"[construct-faster] starting — slot={slotId}, gid={gid?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, duration={durationSeconds}s, attempt={attempt}/{ConstructFasterMaxVideoAttempts}, reason={decision.Reason}.");
                lastVideoResult = await RunConstructFasterVideoAsync(slotId, gid, buildingName, cancellationToken);
                Notify($"[construct-faster] video flow completed: {lastVideoResult}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastVideoResult = ex.Message;
                Notify($"[construct-faster] video attempt {attempt}/{ConstructFasterMaxVideoAttempts} ended before normal completion: {ex.Message}. Verifying on fresh dorf2 before fallback.");
            }

            navigatedForVerification = true;
            var verification = await VerifyConstructFasterResultOnDorf2Async(
                slotId,
                previousLevel,
                buildingName,
                queueFingerprintBefore,
                cancellationToken);
            lastEvidence = verification.Evidence;
            if (verification.Success)
            {
                Notify($"[construct-faster] success — slot={slotId}, evidence={verification.Evidence}.");
                return true;
            }

            if (attempt < ConstructFasterMaxVideoAttempts)
            {
                Notify($"[construct-faster] no queue/progress evidence after attempt {attempt}/{ConstructFasterMaxVideoAttempts}: {verification.Evidence}. Retrying video once.");
                continue;
            }
        }

        Notify($"ALARM: construct-faster video failed after {ConstructFasterMaxVideoAttempts} attempts (last video: {lastVideoResult ?? "no result"}, evidence: {lastEvidence ?? "none"}) — building normally.");
        if (navigatedForVerification)
        {
            await RestoreBuildPageAfterConstructFasterFallbackAsync(slotId, restorePath, cancellationToken);
        }

        return false;
    }

    private async Task<(bool Success, string Evidence)> VerifyConstructFasterResultOnDorf2Async(
        int slotId,
        int previousLevel,
        string buildingName,
        string queueFingerprintBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            Notify($"[construct-faster] verifying result on fresh dorf2 — slot={slotId}.");
            await GotoAsync(Paths.Buildings, cancellationToken);
            var progress = await WaitForBuildingLevelAdvanceAsync(
                slotId,
                previousLevel,
                buildingName,
                queueFingerprintBefore,
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
        catch (Exception ex)
        {
            return (false, $"verification failed: {ex.Message}");
        }
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

        if (!await StartConstructFasterVideoAsync(cancellationToken))
        {
            if (!await IsH264PlaybackSupportedAsync(cancellationToken))
            {
                throw new InvalidOperationException("browser cannot play H.264/AAC ad video");
            }

            throw new InvalidOperationException("video player did not open");
        }

        var completed = await WaitForConstructFasterVideoCompletionAsync(cancellationToken);
        if (!completed)
        {
            if (!await IsH264PlaybackSupportedAsync(cancellationToken))
            {
                throw new InvalidOperationException("browser cannot play H.264/AAC ad video");
            }

            throw new InvalidOperationException($"video completion not confirmed within {ConstructFasterVideoTimeoutSeconds}s");
        }

        return $"{buildingName}: construct-faster video completed.";
    }

    private static string BuildConstructFasterPath(int slotId, int? gid)
    {
        var path = Paths.BuildBySlot(slotId);
        if (gid is int value && value > 0)
        {
            path += $"&gid={value.ToString(CultureInfo.InvariantCulture)}";
        }

        return path;
    }

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
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening construct-faster video.", cancellationToken);
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
            var ready = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const dlg = document.querySelector('#videoFeature');
                  if (!dlg) return false;
                  return !!(dlg.querySelector('.dialogButtonOk')
                    || Array.from(dlg.querySelectorAll('button')).find(b => /watch video/i.test(b.textContent || '')));
                }
                """);
            if (!ready)
            {
                await Task.Delay(Random.Shared.Next(150, 350), cancellationToken);
                continue;
            }

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
                await PauseForManualStepIfVisibleAsync("Manual verification appeared after confirming construct-faster video.", cancellationToken);
                return true;
            }

            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken);
        }

        return false;
    }

    private async Task<bool> StartConstructFasterVideoAsync(CancellationToken cancellationToken)
    {
        var playerReady = false;
        for (var attempt = 1; attempt <= 16; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AcceptConsentManagerIfPresentAsync(cancellationToken, "[construct-faster:verbose]");
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
            Notify("[construct-faster:verbose] video player iframe did not appear.");
            return false;
        }

        await Task.Delay(1500, cancellationToken);

        for (var clickAttempt = 1; clickAttempt <= 2; clickAttempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var area = _page.Locator("#videoArea, #videoFeature iframe").First;
                await area.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 3000 });
                var box = await area.BoundingBoxAsync();
                if (box is null)
                {
                    Notify("[construct-faster:verbose] could not locate video area to click play.");
                    return false;
                }

                var x = box.X + box.Width / 2;
                var y = box.Y + box.Height / 2;
                await _page.Mouse.ClickAsync(x, y);
                Notify($"[construct-faster:verbose] clicked play at video area center ({x:0},{y:0}) attempt={clickAttempt}; waiting for playthrough.");
            }
            catch (PlaywrightException ex)
            {
                Notify($"[construct-faster:verbose] could not click play: {ex.Message}");
                return false;
            }

            await Task.Delay(1000, cancellationToken);
            if (!await AcceptConsentManagerIfPresentAsync(cancellationToken, "[construct-faster:verbose]"))
            {
                break;
            }

            Notify("[construct-faster:verbose] consent dialog intercepted play click; accepted and retrying.");
        }

        return true;
    }

    private async Task<bool> WaitForConstructFasterVideoCompletionAsync(CancellationToken cancellationToken)
    {
        var startUtc = DateTimeOffset.UtcNow;
        var deadlineUtc = startUtc.AddSeconds(ConstructFasterVideoTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(ConstructFasterVideoPollIntervalMs, cancellationToken);

            var rawJson = await _page.EvaluateAsync<string>(
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

            using var doc = JsonDocument.Parse(rawJson ?? "{}");
            var root = doc.RootElement;
            if (GetBoolean(root, "onVillage"))
            {
                Notify("[construct-faster] video completion confirmed by village redirect.");
                return true;
            }

            var elapsedSeconds = (DateTimeOffset.UtcNow - startUtc).TotalSeconds;
            var dialogOpen = GetBoolean(root, "dialogOpen");
            var hasPlayer = GetBoolean(root, "hasPlayer");
            if (elapsedSeconds >= 20 && !dialogOpen && !hasPlayer)
            {
                Notify("[construct-faster] video completion inferred from closed video dialog.");
                return true;
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
