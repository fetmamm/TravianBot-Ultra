using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Infrastructure;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private async Task ClickLocatorAsync(
        ILocator locator,
        string actionName,
        CancellationToken cancellationToken,
        int? timeoutMs = null)
    {
        await EnsureAccountAccessAllowedAsync(cancellationToken);
        var field = locator.ToString() ?? "unknown-locator";
        using var trace = _browserTrace.BeginOperation("CLICK", actionName, $"field={field}");
        try
        {
            await locator.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs ?? _config.TimeoutMs })
                .WaitAsync(cancellationToken);
            trace.Complete("success");
        }
        catch (OperationCanceledException)
        {
            trace.Complete("canceled");
            throw;
        }
        catch (Exception ex)
        {
            trace.Complete("failed", $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task PressKeyAsync(
        string key,
        string actionName,
        CancellationToken cancellationToken)
    {
        await EnsureAccountAccessAllowedAsync(cancellationToken);
        using var trace = _browserTrace.BeginOperation("KEY", actionName, $"key={key}");
        try
        {
            await _page.Keyboard.PressAsync(key).WaitAsync(cancellationToken);
            trace.Complete("success");
        }
        catch (OperationCanceledException)
        {
            trace.Complete("canceled");
            throw;
        }
        catch (Exception ex)
        {
            trace.Complete("failed", $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

// Helper för action pacing klick
    private async Task DelayBeforeClickAsync(
        CancellationToken cancellationToken,
        string? reason = null)
    {
        await EnsureAccountAccessAllowedAsync(cancellationToken);
        await ApplyPacingDelayAsync(
            _config.ActionPacingClickMinSeconds,
            _config.ActionPacingClickMaxSeconds,
            "click-pacing",
            string.IsNullOrWhiteSpace(reason) ? "Click" : $"Click: {reason}",
            cancellationToken);
    }

    // Types a value into an input the way a person would: focus (real mouse click), clear, then enter the
    // characters one at a time with a small randomized cadence between keystrokes. This produces genuine
    // per-character keydown/keyup/input events instead of an instant paste, which looks far more human while
    // only costing a few hundred ms for short values like coordinates or troop counts.
    private async Task TypeHumanlyAsync(ILocator input, string value, CancellationToken cancellationToken)
    {
        await EnsureAccountAccessAllowedAsync(cancellationToken);
        var field = input.ToString() ?? "unknown-input";
        using var trace = _browserTrace.BeginOperation(
            "INPUT",
            "type-humanly",
            $"field={field} {BrowserTraceSanitizer.FormatInputValue(field, value)}");
        try
        {
            await ClickLocatorAsync(input, "focus-input", cancellationToken);
            await input.FillAsync(string.Empty, new LocatorFillOptions { Timeout = _config.TimeoutMs });
        // Small settle so the clear commits before typing (a too-fast type races the field's reset). Then
        // select any residual the field re-populated with — some Travian inputs reset an emptied field to
        // "0" — so the first keystroke REPLACES it instead of landing in front of it (which produced e.g.
        // "098" when re-typing into a reused Add-target form).
            await Task.Delay(Random.Shared.Next(20, 45), cancellationToken);
            await input.PressAsync("Control+A");
        // One randomized delay-per-keystroke per field, so different fields are typed at a slightly
        // different speed (e.g. ~45-110 ms/char) rather than a constant machine-like rhythm.
            var keyDelay = Random.Shared.Next(45, 110);
            await input.PressSequentiallyAsync(
                value,
                new LocatorPressSequentiallyOptions { Delay = keyDelay });
            cancellationToken.ThrowIfCancellationRequested();
            trace.Complete("success", $"keyDelayMs={keyDelay}");
        }
        catch (OperationCanceledException)
        {
            trace.Complete("canceled");
            throw;
        }
        catch (Exception ex)
        {
            trace.Complete("failed", $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task<bool> TryTypeHumanlyIntoFirstMatchingInputAsync(
        IReadOnlyList<string> selectors,
        string value,
        CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            var input = _page.Locator(selector).First;
            if (await input.CountAsync() == 0 || !await input.IsVisibleAsync())
            {
                continue;
            }

            await TypeHumanlyAsync(input, value, cancellationToken);
            return true;
        }

        return false;
    }

// Helper function for waiting on a page to fully load with retries, to mitigate transient timeouts on slow-loading pages.
    private async Task WaitForPageReadyAsync(CancellationToken cancellationToken = default)
    {
        const int attempts = 4;
        const int timeoutMs = 15000;

        using var trace = _browserTrace.BeginOperation("WAIT", "page-ready", $"attempts={attempts} timeoutMs={timeoutMs}");
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                {
                    Timeout = timeoutMs,
                }).WaitAsync(cancellationToken);

                await _page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions
                {
                    Timeout = timeoutMs,
                }).WaitAsync(cancellationToken);

                await EnsureAccountAccessAllowedAsync(cancellationToken);
                trace.Complete("success", $"attempt={attempt}");
                return;
            }
            catch (OperationCanceledException)
            {
                trace.Complete("canceled", $"attempt={attempt}");
                throw;
            }
            catch (PlaywrightException ex)
            {
                lastFailure = ex;
                if (await CurrentPageHasUsableTravianShellAsync(cancellationToken))
                {
                    Notify($"[WaitForPageReadyAsync] load event timed out, but Travian DOM is usable. Url='{_page.Url}'.");
                    trace.Complete("recovered", $"attempt={attempt} reason=usable Travian DOM");
                    return;
                }

                if (attempt < attempts)
                {
                    _browserTrace.Event("RETRY", "page-ready", "retry", $"attempt={attempt}/{attempts} cause={ex.Message}");
                    Notify($"[WaitForPageReadyAsync:verbose] Page did not load, retry {attempt + 1}/{attempts}. Timeout: {timeoutMs} ms. Url='{_page.Url}'. {ex.Message}");
                }
            }
            catch (TimeoutException ex)
            {
                lastFailure = ex;
                if (await CurrentPageHasUsableTravianShellAsync(cancellationToken))
                {
                    Notify($"[WaitForPageReadyAsync] load event timed out, but Travian DOM is usable. Url='{_page.Url}'.");
                    trace.Complete("recovered", $"attempt={attempt} reason=usable Travian DOM");
                    return;
                }

                if (attempt < attempts)
                {
                    _browserTrace.Event("RETRY", "page-ready", "retry", $"attempt={attempt}/{attempts} cause={ex.Message}");
                    Notify($"[WaitForPageReadyAsync:verbose] Page did not load, retry {attempt + 1}/{attempts}. Timeout: {timeoutMs} ms. Url='{_page.Url}'. {ex.Message}");
                }
            }
        }

        var url = _page.Url;
        trace.Complete("failed", $"attempts={attempts} lastError={lastFailure?.Message}", url);
        Notify($"[WaitForPageReadyAsync] Page did not load after {attempts} attempts. Url='{url}'.");
        throw new TimeoutException(
            $"Page did not reach a ready state after {attempts} attempts (timeout {timeoutMs} ms each). Url='{url}'.",
            lastFailure);
    }

    private async Task<bool> CurrentPageHasUsableTravianShellAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = _page.Url;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("chrome-error://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var documentUsable = await _page.EvaluateAsync<bool>(
                "() => document.readyState !== 'loading' && !!document.body " +
                "&& !document.body.classList.contains('neterror') " +
                "&& !document.querySelector('#main-frame-error, .error-code')");
            if (!documentUsable)
            {
                return false;
            }

            var explicitAccessState = await ProbeExplicitAccountAccessStateAsync(_page.Url.ToLowerInvariant());
            if (explicitAccessState is AccountAccessState.Banned or AccountAccessState.Restricted or AccountAccessState.Challenge)
            {
                return true;
            }

            foreach (var selector in Selectors.LoggedInIndicators.Concat(Selectors.LoggedOutIndicators))
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

        return false;
    }

    // Reloads whatever page the browser is currently on to keep a long-idle session fresh. This avoids
    // Travian's own "auto-reload failed" stale state (a countdown that expired without reloading), which
    // makes the page show wrong/old values. Re-verifies login after the reload.
    public async Task RefreshCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        using var trace = _browserTrace.BeginOperation("REFRESH", "keep-alive-current-page", "reason=avoid stale session", _page.Url);
        try
        {
            Notify($"[keep-alive] refreshing current page to avoid a stale session. Url='{_page.Url}'");
            await ReloadPageTracedAsync(
                _page,
                "keep-alive current page",
                new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded },
                cancellationToken);
            await WaitForPageReadyAsync(cancellationToken);
            await TryDismissOneTimeGoldShopOfferAsync(cancellationToken);
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);
            trace.Complete("success", url: _page.Url);
        }
        catch (OperationCanceledException)
        {
            trace.Complete("canceled", url: _page.Url);
            throw;
        }
        catch (Exception ex)
        {
            trace.Complete("failed", $"{ex.GetType().Name}: {ex.Message}", _page.Url);
            throw;
        }
    }

    private async Task ReloadPageTracedAsync(
        IPage page,
        string reason,
        PageReloadOptions options,
        CancellationToken cancellationToken)
    {
        using var trace = _browserTrace.BeginOperation("NAV", "reload", $"reason={reason}", page.Url);
        try
        {
            Notify($"[nav] RELOAD start target='{reason}' current='{page.Url}' pages={TryGetPageCountForDiagnostics()}");
            var response = await page.ReloadAsync(options).WaitAsync(cancellationToken);
            Notify($"[nav] RELOAD done target='{reason}' current='{page.Url}' pages={TryGetPageCountForDiagnostics()}");
            if (ReferenceEquals(page, _page))
            {
                await EnsureAccountAccessAllowedAsync(cancellationToken);
            }
            trace.Complete("success", $"httpStatus={response?.Status.ToString() ?? "-"}", page.Url);
        }
        catch (OperationCanceledException)
        {
            trace.Complete("canceled", url: page.Url);
            throw;
        }
        catch (Exception ex)
        {
            trace.Complete("failed", $"{ex.GetType().Name}: {ex.Message}", page.Url);
            throw;
        }
    }

    private async Task GotoAsync(string pathOrUrl, CancellationToken cancellationToken)
    {
        var url = TravianUrls.ToAbsoluteUrl(ServerUrl, pathOrUrl);
        var beforeUrl = _page.Url;
        if (UrlMatchesPath(beforeUrl, pathOrUrl))
        {
            Notify($"[nav-audit] same-target GOTO requested; target='{url}' current='{beforeUrl}'. Review whether a live reload or page reuse was intended.");
        }
        using var trace = _browserTrace.BeginOperation(
            "NAV",
            "goto",
            $"from={BrowserTraceSanitizer.SanitizeUrl(beforeUrl)} target={BrowserTraceSanitizer.SanitizeUrl(url)}",
            url);
        int? httpStatus = null;
        try
        {
            RecordConstructionNavigation("goto", url);
            Notify($"[nav] GOTO start target='{url}' from='{beforeUrl}' pages={TryGetPageCountForDiagnostics()}");
            try
            {
                await RetryAsync($"navigate to {pathOrUrl}", async () =>
                {
                    IResponse? response = null;
                    try
                    {
                        response = await _page.GotoAsync(url, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = _config.TimeoutMs,
                        })
                        .WaitAsync(cancellationToken);
                    }
                    catch (Exception ex) when (IsTimeoutError(ex))
                    {
                        if (!await DidTimedOutNavigationReachUsablePageAsync(pathOrUrl, cancellationToken))
                        {
                            throw;
                        }

                        Notify(
                            $"[nav] GOTO timeout recovered: expected page is usable despite missing navigation event "
                            + $"current='{_page.Url}'.");
                    }

                    httpStatus = response?.Status;
                    if (response is not null && response.Headers.TryGetValue("date", out var dateHeader))
                    {
                        RecordServerTime(dateHeader);
                    }
                }, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (_account.ProxyEnabled && ProxyParser.LooksLikeProxyError(ex.Message))
            {
                Notify($"[proxy] Navigation failed through the proxy for account '{_account.Name}' "
                    + $"(server '{ProxyParser.MaskForLog(_account.ProxyServer)}'). Check the proxy in Manage account. {ex.Message}");
                throw new TransientNavigationException(
                    $"Navigation to '{url}' failed because the configured proxy is unavailable.",
                    ex);
            }
            catch (Exception ex) when (IsTimeoutError(ex))
            {
                if (await DidTimedOutNavigationReachUsablePageAsync(pathOrUrl, cancellationToken))
                {
                    Notify(
                        $"[nav] GOTO timeout recovered: expected page is usable after safe retries "
                        + $"current='{_page.Url}'.");
                }
                else
                {
                    throw new TransientNavigationException(
                        $"Navigation to '{url}' timed out after safe retries.",
                        ex);
                }
            }

            try
            {
                await WaitForPageReadyAsync(cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new TransientNavigationException($"Navigation to '{url}' did not reach a ready state.", ex);
            }

            Notify($"[nav] GOTO done target='{url}' current='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
            InvalidateActiveConstructionsCache();
            await ApplyPacingDelayAsync(
                _config.ActionPacingPageLoadMinSeconds,
                _config.ActionPacingPageLoadMaxSeconds,
                "page-load-pacing",
                "after page load",
                cancellationToken);
            await TryDismissOneTimeGoldShopOfferAsync(cancellationToken);
            await TryDismissContinuePromptAsync(cancellationToken);
            trace.Complete("success", $"httpStatus={httpStatus?.ToString() ?? "-"} current={BrowserTraceSanitizer.SanitizeUrl(_page.Url)}", _page.Url);
        }
        catch (OperationCanceledException)
        {
            trace.Complete("canceled", url: _page.Url);
            throw;
        }
        catch (Exception ex)
        {
            trace.Complete("failed", $"{ex.GetType().Name}: {ex.Message}", _page.Url);
            throw;
        }
    }

    // Prefer the overview's visible click target. Only a confirmed slot on the correct overview
    // may use the diagnostic URL fallback when Official exposes no actionable click target.
    private async Task OpenSlotFromOverviewAsync(int slotId, CancellationToken cancellationToken)
    {
        if (TravianUrls.IsBuildPageForSlot(_page.Url, slotId) && !await IsPageMarkedStaleAsync())
        {
            return;
        }

        var isResourceField = slotId is >= 1 and <= 18;
        var overviewPath = isResourceField ? Paths.Resources : Paths.Buildings;
        await OpenVillageOverviewAsync(isResourceField, cancellationToken);

        var slotLink = isResourceField
            ? $"#resourceFieldContainer a[data-aid='{slotId}'][href*='build.php?id={slotId}']"
            : $".buildingSlot[data-aid='{slotId}'] a[href*='build.php?id={slotId}']";
        if (isResourceField)
        {
            // Dorf1 exposes the exact field as a separate SVG path when its overlay anchor
            // cannot receive a real click.
            slotLink += $", #resourceFieldContainer svg path.buildingSlot{slotId}[onclick*='build.php?id={slotId}']";
        }
        else
        {
            // Empty building slots and City walls can have an invisible anchor; the visible SVG
            // path inside that exact slot carries the Official click handler.
            slotLink += $", .buildingSlot[data-aid='{slotId}'] svg path[onclick*='build.php?id={slotId}']";
        }
        var visibleSlot = isResourceField
            ? $"#resourceFieldContainer a[data-aid='{slotId}']:visible, "
              + $"#resourceFieldContainer svg path.buildingSlot{slotId}[onclick*='build.php?id={slotId}']:visible"
            : $".buildingSlot[data-aid='{slotId}']:visible";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (!IsCurrentUrlForPath(overviewPath))
            {
                if (TravianUrls.IsBuildPageForSlot(_page.Url, slotId))
                {
                    break;
                }

                throw new InvalidOperationException(
                    $"Cannot open slot {slotId}: overview changed to '{_page.Url}' before the exact slot click. No further click or URL fallback was attempted.");
            }

            try
            {
                await _page.Locator(visibleSlot).First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 4000,
                }).WaitAsync(cancellationToken);
            }
            catch (PlaywrightException ex) when (attempt == 1 &&
                (_page.Url.Contains("reload=auto", StringComparison.OrdinalIgnoreCase)
                    || IsTransientExecutionContextError(ex)))
            {
                Notify($"[slot-nav] slot {slotId} was hidden during an overview reload; waiting for the settled page once.");
                await WaitForPageReadyAsync(cancellationToken);
                await Task.Delay(300, cancellationToken);
                continue;
            }
            catch (PlaywrightException)
            {
                throw new InvalidOperationException(
                    $"Cannot open slot {slotId}: the slot is not visible on {(isResourceField ? "Dorf1" : "Dorf2")}; URL fallback was not attempted. Current page: '{_page.Url}'.");
            }

            if (await TryClickExactOverviewSlotAsync(slotLink, overviewPath, slotId, cancellationToken))
            {
                break;
            }

            if (!IsCurrentUrlForPath(overviewPath))
            {
                if (TravianUrls.IsBuildPageForSlot(_page.Url, slotId))
                {
                    break;
                }

                throw new InvalidOperationException(
                    $"Cannot open slot {slotId}: browser left the overview for '{_page.Url}'. No further click or URL fallback was attempted.");
            }

            if (attempt == 1 && _page.Url.Contains("reload=auto", StringComparison.OrdinalIgnoreCase))
            {
                Notify($"[slot-nav] slot {slotId} was not actionable during auto-reload; retrying the settled overview once.");
                await WaitForPageReadyAsync(cancellationToken);
                await Task.Delay(300, cancellationToken);
                continue;
            }

            if (await _page.Locator(visibleSlot).CountAsync() == 0)
            {
                throw new InvalidOperationException(
                    $"Cannot open slot {slotId}: the slot is not visible on {(isResourceField ? "Dorf1" : "Dorf2")}; URL fallback was not attempted. Current page: '{_page.Url}'.");
            }

            if (_page.Url.Contains("reload=auto", StringComparison.OrdinalIgnoreCase))
            {
                throw new TransientNavigationException(
                    $"Cannot open slot {slotId}: the overview is still auto-reloading after one recovery. URL fallback was not attempted.");
            }

            if (slotId == 40)
            {
                await CaptureFailureArtifactsAsync($"slot-{slotId}-click-fallback", cancellationToken);
            }

            Notify($"ALARM: [slot-nav] No clickable target for visible slot {slotId} on {(isResourceField ? "Dorf1" : "Dorf2")}; using direct build-page URL fallback. Review this slot's live click markup.");
            await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
            if (!TravianUrls.IsBuildPageForSlot(_page.Url, slotId))
            {
                throw new InvalidOperationException(
                    $"Cannot open slot {slotId}: URL fallback did not reach its exact build page. Current page: '{_page.Url}'.");
            }

            Notify($"[slot-nav] opened slot {slotId} via verified URL fallback from {(isResourceField ? "Dorf1" : "Dorf2")}.");
            return;
        }

        await WaitForPageReadyAsync(cancellationToken);
        if (!TravianUrls.IsBuildPageForSlot(_page.Url, slotId))
        {
            throw new InvalidOperationException(
                $"Cannot open slot {slotId}: slot click did not reach its build page. Current page: '{_page.Url}'.");
        }

        Notify($"[slot-nav] opened slot {slotId} via {(isResourceField ? "Dorf1" : "Dorf2")} link.");
        InvalidateActiveConstructionsCache();
        await ApplyPacingDelayAsync(
            _config.ActionPacingPageLoadMinSeconds,
            _config.ActionPacingPageLoadMaxSeconds,
            "page-load-pacing",
            "after village slot click",
            cancellationToken);
        await TryDismissContinuePromptAsync(cancellationToken);
    }

    private async Task<bool> TryClickExactOverviewSlotAsync(
        string selector,
        string overviewPath,
        int slotId,
        CancellationToken cancellationToken)
    {
        var candidates = _page.Locator(selector);
        var count = await candidates.CountAsync();
        if (count == 0)
        {
            return false;
        }

        await DelayBeforeClickAsync(cancellationToken, $"open slot {slotId} from overview");
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentUrlForPath(overviewPath))
            {
                return TravianUrls.IsBuildPageForSlot(_page.Url, slotId);
            }

            var candidate = candidates.Nth(i);
            try
            {
                if (!await candidate.IsVisibleAsync())
                {
                    continue;
                }

                await candidate.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions
                {
                    Timeout = Math.Min(_config.TimeoutMs, 3000),
                }).WaitAsync(cancellationToken);

                // A wall can expose several visible SVG layers. Do not click a layer whose
                // center belongs to another slot or to the village-content overlay.
                var ownsClickPoint = await candidate.EvaluateAsync<bool>(
                    """
                    node => {
                      const rect = node.getBoundingClientRect();
                      if (rect.width <= 0 || rect.height <= 0) return false;
                      const hit = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
                      if (!hit) return false;
                      if (hit === node || node.contains(hit)) return true;
                      const slot = node.closest('[data-aid]');
                      const hitSlot = hit.closest('[data-aid]');
                      return !!slot && !!hitSlot
                        && hitSlot.getAttribute('data-aid') === slot.getAttribute('data-aid');
                    }
                    """);
                if (!ownsClickPoint)
                {
                    Notify($"[slot-nav] slot {slotId} candidate {i + 1}/{count} does not own its click point; skipped.");
                    continue;
                }

                await ClickLocatorAsync(
                    candidate,
                    $"open slot {slotId} from overview (candidate {i + 1}/{count})",
                    cancellationToken,
                    Math.Min(_config.TimeoutMs, 3000));
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                Notify($"[slot-nav] slot {slotId} candidate {i + 1}/{count} was not actionable: {ex.Message}");
                await Task.Delay(300, cancellationToken);
                if (!IsCurrentUrlForPath(overviewPath))
                {
                    return TravianUrls.IsBuildPageForSlot(_page.Url, slotId);
                }
            }
        }

        return false;
    }

    private async Task OpenVillageOverviewAsync(bool resourceFields, CancellationToken cancellationToken)
    {
        var overviewPath = resourceFields ? Paths.Resources : Paths.Buildings;
        if (IsCurrentUrlForPath(overviewPath) && !await IsPageMarkedStaleAsync())
        {
            return;
        }

        var overviewLink = resourceFields
            ? "a.village.resourceView[href*='dorf1.php']"
            : "a.village.buildingView[href*='dorf2.php']";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (IsCurrentUrlForPath(overviewPath) && !await IsPageMarkedStaleAsync())
            {
                return;
            }

            try
            {
                await _page.Locator(overviewLink).First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 4000,
                }).WaitAsync(cancellationToken);
                if (await TryClickFirstVisibleEnabledAsync(
                        overviewLink,
                        cancellationToken,
                        reason: $"open {(resourceFields ? "resource" : "building")} overview",
                        timeoutMs: 4000))
                {
                    await WaitForPageReadyAsync(cancellationToken);
                    if (IsCurrentUrlForPath(overviewPath))
                    {
                        Notify($"[slot-nav] opened {(resourceFields ? "Dorf1" : "Dorf2")} via overview link.");
                        InvalidateActiveConstructionsCache();
                        await ApplyPacingDelayAsync(
                            _config.ActionPacingPageLoadMinSeconds,
                            _config.ActionPacingPageLoadMaxSeconds,
                            "page-load-pacing",
                            "after village overview click",
                            cancellationToken);
                        return;
                    }
                }
            }
            catch (PlaywrightException ex) when (attempt == 1 && IsTransientExecutionContextError(ex))
            {
                Notify($"[slot-nav] overview navigation interrupted by page reload; retrying once: {ex.Message}");
            }
            catch (PlaywrightException) when (attempt == 1 && _page.Url.Contains("reload=auto", StringComparison.OrdinalIgnoreCase))
            {
                Notify("[slot-nav] overview link hidden during auto-reload; retrying once.");
            }
            catch (PlaywrightException ex)
            {
                Notify($"[slot-nav] overview link unavailable on attempt {attempt}/2: {ex.Message}");
            }

            if (attempt == 1)
            {
                Notify($"[slot-nav] {(resourceFields ? "Dorf1" : "Dorf2")} overview did not settle; waiting once before retry. Current page: '{_page.Url}'.");
                await WaitForPageReadyAsync(cancellationToken);
                await Task.Delay(300, cancellationToken);
                continue;
            }

            throw new InvalidOperationException(
                $"Cannot open {(resourceFields ? "Dorf1" : "Dorf2")}: visible overview link was unavailable or its click did not reach {overviewPath} after one recovery. Current page: '{_page.Url}'.");
        }
    }

    private async Task OpenSlotTabAsync(int slotId, int tab, CancellationToken cancellationToken)
    {
        var tabPath = Paths.BuildBySlotTab(slotId, tab);
        if (IsCurrentUrlForPath(tabPath) && !await IsPageMarkedStaleAsync())
        {
            return;
        }

        await OpenSlotFromOverviewAsync(slotId, cancellationToken);
        var tabLink = $".contentNavi.subNavi a.tabItem[href*='id={slotId}'][href$='t={tab}']";
        if (!await TryClickFirstVisibleEnabledAsync(tabLink, cancellationToken, reason: $"open slot {slotId} tab {tab}"))
        {
            throw new InvalidOperationException(
                $"Cannot open slot {slotId} tab {tab}: no visible tab link on '{_page.Url}'.");
        }

        await WaitForPageReadyAsync(cancellationToken);
        if (!IsCurrentUrlForPath(tabPath))
        {
            throw new InvalidOperationException(
                $"Slot {slotId} tab {tab} click did not reach {tabPath}. Current page: '{_page.Url}'.");
        }

        Notify($"[slot-nav] opened slot {slotId} tab {tab} via tab click.");
        InvalidateActiveConstructionsCache();
        await ApplyPacingDelayAsync(
            _config.ActionPacingPageLoadMinSeconds,
            _config.ActionPacingPageLoadMaxSeconds,
            "page-load-pacing",
            "after building tab click",
            cancellationToken);
    }

    // Reuses an already-open page only when its URL contract matches and Travian has not marked a timer
    // stale. Callers still perform their normal live DOM read; this only removes a redundant navigation.
    private async Task EnsurePageForReadAsync(
        string pathOrUrl,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(pathOrUrl))
        {
            await GotoAsync(pathOrUrl, cancellationToken);
            return;
        }

        if (await IsPageMarkedStaleAsync())
        {
            Notify($"[nav] current page is stale; reloading for {purpose}.");
            await ReloadOrGotoAsync(pathOrUrl, cancellationToken);
            return;
        }

        Notify($"[nav] reusing current page for {purpose}; url='{_page.Url}'.");
        await WaitForPageReadyAsync(cancellationToken);
    }

    // Reloads in place when already on the target path, otherwise navigates to it.
    private async Task ReloadOrGotoAsync(string pathOrUrl, CancellationToken cancellationToken)
    {
        if (IsCurrentUrlForPath(pathOrUrl))
        {
            RecordConstructionNavigation("reload", pathOrUrl);
            await ReloadCurrentPageWithSlowNetworkRecoveryAsync(pathOrUrl, cancellationToken);
            // A reload replaces page content just like a navigation, so any page-derived cache must
            // be dropped here too (the GotoAsync branch already does this). Without this the longer
            // active-constructions TTL could serve pre-reload state at the top of an upgrade iteration.
            InvalidateActiveConstructionsCache();
            await ApplyPacingDelayAsync(
                _config.ActionPacingPageLoadMinSeconds,
                _config.ActionPacingPageLoadMaxSeconds,
                "page-load-pacing",
                "after page reload",
                cancellationToken);
            await TryDismissOneTimeGoldShopOfferAsync(cancellationToken);
        }
        else
        {
            await GotoAsync(pathOrUrl, cancellationToken);
        }
    }

    // The disposable video browser can occasionally reach the right URL before Travian finishes
    // rendering its page contract. Recover before any video action, so a malformed first load does
    // not immediately discard a browser that a safe reload can repair.
    private async Task<bool> LoadIsolatedBonusVideoPageAsync(
        string path,
        string label,
        Func<CancellationToken, Task<bool>> isPageReady,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= BonusVideoPlaybackPolicy.IsolatedPageLoadAttempts; attempt++)
        {
            try
            {
                if (attempt == 1)
                {
                    await GotoAsync(path, cancellationToken);
                }
                else
                {
                    await ReloadOrGotoAsync(path, cancellationToken);
                }

                await WaitForPageReadyAsync(cancellationToken);
                if (await isPageReady(cancellationToken))
                {
                    return true;
                }

                Notify($"[bonus-video] {label}: Travian page did not finish rendering (load {attempt}/{BonusVideoPlaybackPolicy.IsolatedPageLoadAttempts}).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTimeoutError(ex) || IsTransientExecutionContextException(ex))
            {
                Notify($"[bonus-video] {label}: transient page-load failure (load {attempt}/{BonusVideoPlaybackPolicy.IsolatedPageLoadAttempts}): {ex.Message}");
            }

            if (BonusVideoPlaybackPolicy.ShouldRetryIsolatedPageLoad(attempt))
            {
                Notify($"[bonus-video] {label}: reloading the isolated browser page before giving up.");
            }
        }

        return false;
    }

    private async Task ReloadCurrentPageWithSlowNetworkRecoveryAsync(
        string expectedPath,
        CancellationToken cancellationToken)
    {
        var timeouts = new[]
        {
            _config.TimeoutMs,
            Math.Max(_config.TimeoutMs + 10_000, 30_000),
            Math.Max(_config.TimeoutMs + 25_000, 45_000),
        };
        Exception? lastFailure = null;

        for (var attempt = 0; attempt < timeouts.Length; attempt++)
        {
            try
            {
                await ReloadPageTracedAsync(
                    _page,
                    $"slow-network recovery attempt {attempt + 1}/{timeouts.Length}",
                    new PageReloadOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = timeouts[attempt],
                    },
                    cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTimeoutError(ex))
            {
                lastFailure = ex;
                if (await DidTimedOutNavigationReachUsablePageAsync(expectedPath, cancellationToken))
                {
                    Notify(
                        $"[nav] RELOAD timeout recovered: expected page is usable despite missing navigation event " +
                        $"attempt={attempt + 1}/{timeouts.Length} current='{_page.Url}'.");
                    return;
                }

                if (attempt + 1 < timeouts.Length)
                {
                    var retryDelay = TimeSpan.FromSeconds(2 + attempt * 3);
                    _browserTrace.Event(
                        "RETRY",
                        "reload",
                        "retry",
                        $"attempt={attempt + 1}/{timeouts.Length} timeoutMs={timeouts[attempt]} backoffMs={retryDelay.TotalMilliseconds:0}");
                    Notify(
                        $"[nav] RELOAD transient timeout attempt={attempt + 1}/{timeouts.Length} " +
                        $"timeout={timeouts[attempt]}ms; retrying in {retryDelay.TotalSeconds:F0}s.");
                    await DelayForRetryAsync((int)retryDelay.TotalMilliseconds, "reload", cancellationToken);
                }
            }
        }

        throw new TransientNavigationException(
            $"Reload of '{expectedPath}' timed out after {timeouts.Length} safe attempts.",
            lastFailure);
    }

    private async Task<bool> DidTimedOutNavigationReachUsablePageAsync(
        string expectedPath,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(expectedPath))
        {
            return false;
        }

        try
        {
            await _page.WaitForFunctionAsync(
                "() => document.readyState !== 'loading'",
                null,
                new PageWaitForFunctionOptions { Timeout = 5_000 })
                .WaitAsync(cancellationToken);
            return await _page.EvaluateAsync<bool>(
                "() => !document.body?.classList.contains('neterror') && !document.querySelector('#main-frame-error, .error-code')")
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return false;
        }
    }

    private int TryGetPageCountForDiagnostics()
    {
        try
        {
            return _page.Context.Pages.Count;
        }
        catch
        {
            return -1;
        }
    }

    // True when Travian shows its own "auto-reload failed" UI on any timer: a <span class="timer no-reload">
    // wrapping a refresh icon (img/refresh.png). When this appears the page is stuck — the countdown has
    // expired but the expected reload never fired — and any value/level we read from it is stale. Detection
    // is a separate signal from "duration == 0" because the timer's value may be negative ("counting=down
    // value=-60") rather than zero. Callers that depend on fresh state should force a reload when this is true.
    private async Task<bool> IsPageMarkedStaleAsync()
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => !!document.querySelector('span.timer.no-reload, .timer.no-reload')
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private bool IsCurrentUrlForPath(string pathOrUrl)
        => UrlMatchesPath(_page.Url, pathOrUrl);

    internal static bool UrlMatchesPath(string? currentUrl, string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(currentUrl) || string.IsNullOrWhiteSpace(pathOrUrl))
        {
            return false;
        }

        try
        {
            if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri))
            {
                return false;
            }

            Uri expectedUri;
            var absoluteExpected = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            if (pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(pathOrUrl, UriKind.Absolute, out expectedUri!))
                {
                    return false;
                }
            }
            else
            {
                var expectedRelative = pathOrUrl.StartsWith('/')
                    ? pathOrUrl
                    : "/" + pathOrUrl;
                expectedUri = new Uri(new Uri("https://path.local/"), expectedRelative);
            }

            if (absoluteExpected
                && !string.Equals(currentUri.Authority, expectedUri.Authority, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(currentUri.AbsolutePath, expectedUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Expected query parameters identify the requested entity (for example build slot id).
            // Current URLs may contain extra server-added parameters such as gid; those do not make
            // the page a different target.
            var expectedQuery = ParseQueryParameters(expectedUri.Query);
            var currentQuery = ParseQueryParameters(currentUri.Query);
            return expectedQuery.All(pair =>
                currentQuery.TryGetValue(pair.Key, out var currentValue)
                && string.Equals(currentValue, pair.Value, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> ParseQueryParameters(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = Uri.UnescapeDataString(separator >= 0 ? part[..separator] : part);
            var value = Uri.UnescapeDataString(separator >= 0 ? part[(separator + 1)..] : string.Empty);
            result[key] = value;
        }

        return result;
    }

    private async Task ApplyActionDelayAsync(CancellationToken cancellationToken)
    {
        await ApplyPacingDelayAsync(
            _config.ActionPacingTaskMinSeconds,
            _config.ActionPacingTaskMaxSeconds,
            "action-pacing",
            "between actions",
            cancellationToken);
    }

    private async Task ApplyPacingDelayAsync(
        double minimumSeconds,
        double maximumSeconds,
        string action,
        string reason,
        CancellationToken cancellationToken)
    {
        using var trace = _browserTrace.BeginOperation(
            "WAIT",
            action,
            $"reason={reason} plannedRangeSeconds={minimumSeconds:0.###}-{maximumSeconds:0.###}");
        try
        {
            await ActionPacer.FromOptions(_config, Notify).DelayAsync(
                minimumSeconds,
                maximumSeconds,
                cancellationToken,
                reason);
            trace.Complete("success");
        }
        catch (OperationCanceledException)
        {
            trace.Complete("canceled");
            throw;
        }
    }

    private string? ResolveUrl(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(new Uri(ServerUrl.TrimEnd('/') + "/"), href, out var combined))
        {
            return combined.ToString();
        }

        return href;
    }

    private void RecordServerTime(string? dateHeader)
    {
        if (string.IsNullOrWhiteSpace(dateHeader))
        {
            return;
        }

        if (!DateTimeOffset.TryParse(
                dateHeader,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return;
        }

        _serverTimeUtc = parsed.ToUniversalTime();
        _serverTimeObservedAtUtc = DateTimeOffset.UtcNow;
    }

    private DateTimeOffset CurrentTravianServerTimeUtc()
    {
        if (_serverTimeUtc is not { } serverTime || _serverTimeObservedAtUtc is not { } observedAt)
        {
            return DateTimeOffset.UtcNow;
        }
        return serverTime + (DateTimeOffset.UtcNow - observedAt);
    }

}
