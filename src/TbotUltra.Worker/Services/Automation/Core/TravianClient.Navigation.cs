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
    private Task DelayBeforeClickAsync(
        CancellationToken cancellationToken,
        string? reason = null)
    {
        if (!_config.ActionPacingEnabled)
        {
            return Task.CompletedTask;
        }

        // Default reason so the click-pacing delay is always logged (e.g. [pacing] Click: waiting 2.3s),
        // while callers that pass their own reason keep it.
        return ActionPacer.FromOptions(_config, Notify).DelayAsync(
            _config.ActionPacingClickMinSeconds,
            _config.ActionPacingClickMaxSeconds,
            cancellationToken,
            string.IsNullOrWhiteSpace(reason) ? "Click" : $"Click: {reason}");
    }

    private Task DelayFarmListStepAsync(CancellationToken cancellationToken)
    {
        return new ActionPacer(enabled: true, Notify).DelayAsync(
            _config.FarmListStepDelayMinSeconds,
            _config.FarmListStepDelayMaxSeconds,
            cancellationToken,
            "Farm list: step");
    }

    // Types a value into an input the way a person would: focus (real mouse click), clear, then enter the
    // characters one at a time with a small randomized cadence between keystrokes. This produces genuine
    // per-character keydown/keyup/input events instead of an instant paste, which looks far more human while
    // only costing a few hundred ms for short values like coordinates or troop counts.
    private async Task TypeHumanlyAsync(ILocator input, string value, CancellationToken cancellationToken)
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
        await input.PressSequentiallyAsync(
            value,
            new LocatorPressSequentiallyOptions { Delay = Random.Shared.Next(45, 110) });
        cancellationToken.ThrowIfCancellationRequested();
    }

// Helper function for waiting on a page to fully load with retries, to mitigate transient timeouts on slow-loading pages.
    private async Task WaitForPageReadyAsync(CancellationToken cancellationToken = default)
    {
        const int attempts = 4;
        const int timeoutMs = 15000;

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await PauseForManualStepIfVisibleAsync("Manual verification appeared on the page.", cancellationToken);
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                {
                    Timeout = timeoutMs,
                }).WaitAsync(cancellationToken);

                await _page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions
                {
                    Timeout = timeoutMs,
                }).WaitAsync(cancellationToken);

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PlaywrightException ex)
            {
                lastFailure = ex;
                if (await CurrentPageHasUsableTravianShellAsync(cancellationToken))
                {
                    Notify($"[WaitForPageReadyAsync] load event timed out, but Travian DOM is usable. Url='{_page.Url}'.");
                    return;
                }
                if (attempt < attempts)
                    Notify($"[WaitForPageReadyAsync:verbose] Page did not load, retry {attempt + 1}/{attempts}. Timeout: {timeoutMs} ms. Url='{_page.Url}'. {ex.Message}");
            }
            catch (TimeoutException ex)
            {
                lastFailure = ex;
                if (await CurrentPageHasUsableTravianShellAsync(cancellationToken))
                {
                    Notify($"[WaitForPageReadyAsync] load event timed out, but Travian DOM is usable. Url='{_page.Url}'.");
                    return;
                }
                if (attempt < attempts)
                    Notify($"[WaitForPageReadyAsync:verbose] Page did not load, retry {attempt + 1}/{attempts}. Timeout: {timeoutMs} ms. Url='{_page.Url}'. {ex.Message}");
            }
        }

        // Don't return silently after exhausting the retries: callers (login, navigation, keep-alive)
        // would then act on a half-loaded page. Throw so the operation is deferred/retried instead.
        // lastFailure is kept as InnerException so a fatal disconnect is still seen by
        // BrowserFailureClassifier (it walks the inner-exception chain) and recreates the session.
        var url = _page.Url;
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
        Notify($"[keep-alive] refreshing current page to avoid a stale session. Url='{_page.Url}'");
        await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded })
            .WaitAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await EnsureLoggedInAsync();
    }

    private async Task GotoAsync(string pathOrUrl, CancellationToken cancellationToken)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl
            : $"{_config.BaseUrl.TrimEnd('/')}/{pathOrUrl.TrimStart('/')}";
        var beforeUrl = _page.Url;
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
                if (response is not null && response.Headers.TryGetValue("date", out var dateHeader))
                {
                    RecordServerTime(dateHeader);
                }
            }, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (_account.ProxyEnabled && ProxyParser.LooksLikeProxyError(ex.Message))
        {
            // Make a dead/misconfigured proxy unmistakable instead of looking like a Travian outage.
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
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        }
        catch (TimeoutException ex)
        {
            throw new TransientNavigationException($"Navigation to '{url}' did not reach a ready state.", ex);
        }
        Notify($"[nav] GOTO done target='{url}' current='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
        InvalidateActiveConstructionsCache();
        await ActionPacer.FromOptions(_config, Notify).DelayAsync(
            _config.ActionPacingPageLoadMinSeconds,
            _config.ActionPacingPageLoadMaxSeconds,
            cancellationToken,
            "after page load");
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after navigation.", cancellationToken);
        await TryDismissContinuePromptAsync(cancellationToken);
    }

    // Reloads in place when already on the target path, otherwise navigates to it.
    private async Task ReloadOrGotoAsync(string pathOrUrl, CancellationToken cancellationToken)
    {
        if (IsCurrentUrlForPath(pathOrUrl))
        {
            RecordConstructionNavigation("reload", pathOrUrl);
            Notify($"[nav] RELOAD start target='{pathOrUrl}' current='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
            await ReloadCurrentPageWithSlowNetworkRecoveryAsync(pathOrUrl, cancellationToken);
            Notify($"[nav] RELOAD done target='{pathOrUrl}' current='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
            // A reload replaces page content just like a navigation, so any page-derived cache must
            // be dropped here too (the GotoAsync branch already does this). Without this the longer
            // active-constructions TTL could serve pre-reload state at the top of an upgrade iteration.
            InvalidateActiveConstructionsCache();
            await ActionPacer.FromOptions(_config, Notify).DelayAsync(
                _config.ActionPacingPageLoadMinSeconds,
                _config.ActionPacingPageLoadMaxSeconds,
                cancellationToken,
                "after page reload");
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
                await _page.ReloadAsync(new PageReloadOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = timeouts[attempt],
                    })
                    .WaitAsync(cancellationToken);
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
                    Notify(
                        $"[nav] RELOAD transient timeout attempt={attempt + 1}/{timeouts.Length} " +
                        $"timeout={timeouts[attempt]}ms; retrying in {retryDelay.TotalSeconds:F0}s.");
                    await Task.Delay(retryDelay, cancellationToken);
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
        await ActionPacer.FromOptions(_config, Notify).DelayAsync(
            _config.ActionPacingTaskMinSeconds,
            _config.ActionPacingTaskMaxSeconds,
            cancellationToken,
            "between actions");
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
