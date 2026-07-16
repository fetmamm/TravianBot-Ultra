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
// Helper för action pacing klick
    private async Task DelayBeforeClickAsync(
        CancellationToken cancellationToken,
        string? reason = null)
    {
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
        var field = input.ToString() ?? "unknown-input";
        using var trace = _browserTrace.BeginOperation(
            "INPUT",
            "type-humanly",
            $"field={field} {BrowserTraceSanitizer.FormatInputValue(field, value)}");
        try
        {
            await input.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
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
            if (explicitAccessState is AccountAccessState.Restricted or AccountAccessState.Challenge)
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
            await EnsureLoggedInAsync();
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
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl
            : $"{_config.BaseUrl.TrimEnd('/')}/{pathOrUrl.TrimStart('/')}";
        var beforeUrl = _page.Url;
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
                    var response = await _page.GotoAsync(url, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = _config.TimeoutMs,
                        })
                        .WaitAsync(cancellationToken);
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
                throw new TransientNavigationException(
                    $"Navigation to '{url}' timed out after safe retries.",
                    ex);
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
        }
        else
        {
            await GotoAsync(pathOrUrl, cancellationToken);
        }
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
    {
        if (string.IsNullOrWhiteSpace(_page.Url))
        {
            return false;
        }

        try
        {
            if (!Uri.TryCreate(_page.Url, UriKind.Absolute, out var currentUri))
            {
                return false;
            }

            string expectedPath;
            if (pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var expectedUri))
                {
                    return false;
                }

                expectedPath = expectedUri.AbsolutePath;
            }
            else
            {
                expectedPath = pathOrUrl.StartsWith('/')
                    ? pathOrUrl
                    : "/" + pathOrUrl;
            }

            return string.Equals(currentUri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
        if (!_config.ActionPacingEnabled)
        {
            _browserTrace.Event("DECISION", action, "skipped", "reason=action pacing disabled");
            return;
        }

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

        if (Uri.TryCreate(new Uri(_config.BaseUrl.TrimEnd('/') + "/"), href, out var combined))
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
    }

}
