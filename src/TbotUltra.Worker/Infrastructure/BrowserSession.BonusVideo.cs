using System.Text.Json.Nodes;
using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Services;

namespace TbotUltra.Worker.Infrastructure;

public sealed partial class BrowserSession
{
    public async Task<T> RunInIsolatedBonusVideoBrowserAsync<T>(
        Func<IPage, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_playwright is null || _context is null)
        {
            throw new InvalidOperationException("Browser session is not open.");
        }

        var cooldownKey = BuildBonusVideoCooldownKey();
        if (BonusVideoCooldownByRoute.TryGetValue(cooldownKey, out var cooldown)
            && cooldown.UntilUtc > DateTimeOffset.UtcNow)
        {
            throw new BonusVideoCooldownException(cooldown.UntilUtc, cooldown.Kind);
        }

        BonusVideoCooldownByRoute.TryRemove(cooldownKey, out _);

        IBrowser? videoBrowser = null;
        IBrowserContext? videoContext = null;
        Task<T>? actionTask = null;
        BonusVideoNetworkDiagnostics? networkDiagnostics = null;
        var closeReason = "setup did not complete";
        CancellationTokenSource? phaseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phaseTimeout.CancelAfter(IsolatedBonusVideoSetupMaxDuration);
        try
        {
            ConsentDomainsAllowed = false;
            await ClearTransientExternalStorageOriginsAsync(force: true).WaitAsync(phaseTimeout.Token);
            var stateJson = FilterForeignSubdomainState(await _context.StorageStateAsync().WaitAsync(phaseTimeout.Token));

            videoBrowser = await _playwright.Chromium
                .LaunchAsync(CreateChromiumLaunchOptions(keepNativePopupBlocker: false))
                .WaitAsync(phaseTimeout.Token);
            videoContext = await videoBrowser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = _effectiveBaseUrl,
                ViewportSize = ViewportSize.NoViewport,
                StorageState = stateJson,
            }).WaitAsync(phaseTimeout.Token);
            _browserTrace.Event("PAGE_CONTEXT", "bonus-video-context-opened", detail: "isolatedBrowser=true");
            videoContext.SetDefaultTimeout(_config.TimeoutMs);
            networkDiagnostics = new BonusVideoNetworkDiagnostics(_log);
            videoContext.RequestFailed += networkDiagnostics.OnRequestFailed;
            videoContext.Response += networkDiagnostics.OnResponse;
            videoContext.Page += (_, page) =>
            {
                _browserTrace.AttachPage(page, "bonus-video-context");
                _log?.Invoke($"[browser-video] page event pages={videoContext.Pages.Count} initialUrl='{page.Url}'");
                page.Close += (_, _) =>
                {
                    _log?.Invoke($"[browser-video] page closed pages={videoContext.Pages.Count} url='{page.Url}'");
                };
            };

            var page = await videoContext.NewPageAsync().WaitAsync(phaseTimeout.Token);
            _browserTrace.AttachPage(page, "bonus-video-main");
            _log?.Invoke("[browser-video] isolated bonus-video browser opened.");

            phaseTimeout.Dispose();
            phaseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            phaseTimeout.CancelAfter(IsolatedBonusVideoActionMaxDuration);
            actionTask = action(page, phaseTimeout.Token);
            var result = await actionTask.WaitAsync(phaseTimeout.Token);
            var resultKind = result is string text
                ? BonusVideoFailureClassifier.Classify(text)
                : BonusVideoFailureKind.None;
            if (resultKind == BonusVideoFailureKind.None)
            {
                BonusVideoCooldownByRoute.TryRemove(cooldownKey, out _);
                _log?.Invoke("[browser-video] video route confirmed working for current account/proxy.");
            }
            else
            {
                if (resultKind == BonusVideoFailureKind.Unknown && networkDiagnostics.HasFailures)
                {
                    resultKind = BonusVideoFailureKind.Network;
                }

                SetBonusVideoCooldown(cooldownKey, resultKind);
            }

            closeReason = resultKind == BonusVideoFailureKind.None
                ? "action completed successfully"
                : $"action completed with {FormatBonusVideoFailureKind(resultKind)}";

            return result;
        }
        catch (OperationCanceledException) when (phaseTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var setupPhase = actionTask is null;
            var limit = setupPhase ? IsolatedBonusVideoSetupMaxDuration : IsolatedBonusVideoActionMaxDuration;
            closeReason = $"{(setupPhase ? "setup" : "action")} hard timeout after {limit.TotalSeconds:0}s";
            SetBonusVideoCooldown(cooldownKey, BonusVideoFailureKind.Timeout);
            _log?.Invoke($"[browser-video] video {(setupPhase ? "setup" : "action")} exceeded {limit.TotalSeconds:0}s hard cap — aborting.");
            throw new TimeoutException($"Bonus-video {(setupPhase ? "setup" : "action")} exceeded {limit.TotalSeconds:0}s and was aborted.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            closeReason = "cancel or program shutdown";
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var kind = BonusVideoFailureClassifier.Classify(ex.Message);
            if (kind == BonusVideoFailureKind.Unknown && networkDiagnostics?.HasFailures == true)
            {
                kind = BonusVideoFailureKind.Network;
            }

            SetBonusVideoCooldown(cooldownKey, kind);
            closeReason = BrowserFailureClassifier.IsTargetCrash(ex)
                ? "browser closed or crashed"
                : $"action failed with {FormatBonusVideoFailureKind(kind)}";
            throw;
        }
        finally
        {
            phaseTimeout.Dispose();
            networkDiagnostics?.LogSummary();
            if (videoBrowser is not null)
            {
                try
                {
                    // Close the disposable browser directly — its context, pages and process go with it.
                    // We deliberately skip the graceful videoContext.CloseAsync(): it tries to close pages
                    // cleanly and hangs on a wedged ad/video renderer (that logged a benign "context cleanup
                    // failed: timed out" and burned the close timeout), while the browser close tears the
                    // same thing down in ~1s. Nothing reads state back from this browser, so there is
                    // nothing to flush. The timeout stays as a safety net against a wedged browser close.
                    // CancellationToken.None on purpose: this finally-block cleanup must run to completion
                    // even when the surrounding operation was canceled; the timeout is the only bound.
                    await videoBrowser.CloseAsync().WaitAsync(IsolatedBonusVideoCloseTimeout, CancellationToken.None);
                    _browserTrace.Event("PAGE_CONTEXT", "bonus-video-context-closed", detail: $"reason={closeReason}");
                }
                catch (Exception ex)
                {
                    _browserTrace.Event("ERROR", "bonus-video-context-close", "failed", ex.Message);
                    _log?.Invoke($"[browser-video] browser cleanup failed: {ex.Message}");
                }
            }

            // If we abandoned the action on the hard cap it may still be running; closing the browser
            // above faults it with a target-closed error. Observe that so it is not an unobserved
            // exception, without blocking cleanup on the hung call.
            if (actionTask is not null)
            {
                _ = actionTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
            }

            _log?.Invoke($"[browser-video] isolated bonus-video browser closed reason='{closeReason}'.");
        }
    }

    private string BuildBonusVideoCooldownKey()
        => $"{_account.Name}|{(_account.ProxyEnabled ? _account.ProxyServer.Trim() : "direct")}";

    private void SetBonusVideoCooldown(string key, BonusVideoFailureKind kind)
    {
        var duration = BonusVideoFailureClassifier.Cooldown(kind);
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var state = new BonusVideoCooldownState(DateTimeOffset.UtcNow + duration, kind);
        BonusVideoCooldownByRoute[key] = state;
        _log?.Invoke(
            $"[browser-video] current account/proxy paused for video until {state.UntilUtc.ToLocalTime():HH:mm} "
            + $"after {FormatBonusVideoFailureKind(kind)}; normal automation continues.");
    }

    private static string FormatBonusVideoFailureKind(BonusVideoFailureKind kind)
        => BonusVideoFailureClassifier.Format(kind);

    public async Task<IPage> OpenIsolatedExternalPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_browser is null)
        {
            throw new InvalidOperationException("Browser session is not open.");
        }

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = ViewportSize.NoViewport,
        });
        context.SetDefaultTimeout(_config.TimeoutMs);

        lock (_isolatedExternalContextsGate)
        {
            _isolatedExternalContexts.Add(context);
        }

        try
        {
            var page = await context.NewPageAsync();
            _browserTrace.AttachPage(page, "isolated-external-context");
            _log?.Invoke($"[browser] isolated external page created url='{page.Url}'");
            return page;
        }
        catch
        {
            await CloseIsolatedExternalContextAsync(context);
            throw;
        }
    }

    public async Task CloseIsolatedExternalPageAsync(IPage? page)
    {
        if (page is null)
        {
            return;
        }

        await CloseIsolatedExternalContextAsync(page.Context);
    }

    private async Task CloseIsolatedExternalContextAsync(IBrowserContext context)
    {
        var shouldClose = false;
        lock (_isolatedExternalContextsGate)
        {
            shouldClose = _isolatedExternalContexts.Remove(context);
        }

        if (!shouldClose)
        {
            return;
        }

        try
        {
            await context.CloseAsync();
        }
        catch
        {
            // Best-effort cleanup only; the parent browser is still owned by this session.
        }
    }

    // Hosts of the bonus-video ad/consent stack. These load on Travian pages and their OOPIFs spawn
    // periodic sync tabs, so they are blocked except during an active bonus-video flow. The video player
    // chain is oadts -> adscale -> Google IMA, gated behind consentmanager's TCF consent.
    private static readonly string[] BonusVideoAdHosts =
    {
        "consentmanager.net",
        "oadts.com",
        "adscale.de",
        "imasdk.googleapis.com",
        "doubleclick.net",
        "googlesyndication.com",
        "googleadservices.com",
        "googletagservices.com",
        "googletagmanager.com",
    };

    private static readonly string[] TransientExternalStorageOrigins =
    {
        "https://consentmanager.net",
        "https://www.consentmanager.net",
        "https://cdn.consentmanager.net",
        "https://delivery.consentmanager.net",
        "https://cmp.consentmanager.net",
        "https://oadts.com",
        "https://www.oadts.com",
        "https://cdn.oadts.com",
        "https://media.oadts.com",
        "https://adscale.de",
        "https://www.adscale.de",
        "https://cdn.adscale.de",
        "https://ih.adscale.de",
        "https://imasdk.googleapis.com",
        "https://googleads.g.doubleclick.net",
        "https://securepubads.g.doubleclick.net",
        "https://static.doubleclick.net",
        "https://ad.doubleclick.net",
        "https://pagead2.googlesyndication.com",
        "https://tpc.googlesyndication.com",
        "https://adservice.google.com",
        "https://travcotools.com",
        "https://www.travcotools.com",
    };

    private void TrackTransientExternalOrigin(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return;
        }

        var origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        lock (_transientExternalOriginsGate)
        {
            _transientExternalOrigins.Add(origin);
        }
    }

    private static bool IsBonusVideoAdDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        foreach (var adHost in BonusVideoAdHosts)
        {
            if (host.Equals(adHost, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + adHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task CleanupAfterBonusVideoAsync(IPage? mainPage, CancellationToken cancellationToken = default)
    {
        ConsentDomainsAllowed = false;

        if (_context is null)
        {
            return;
        }

        await RunBonusVideoCleanupStepAsync(
            "quarantine bonus video page",
            () => NavigateMainPageToAboutBlankAsync(mainPage),
            cancellationToken);
        await RunBonusVideoCleanupStepAsync(
            "close transient popup tabs",
            () => CloseBlockedOrForeignPagesAsync(mainPage, "bonus-video-cleanup"),
            cancellationToken);
        await RunBonusVideoCleanupStepAsync(
            "clear Travian consent/ad cookies",
            () => ClearFirstPartyConsentCookiesAsync(mainPage),
            cancellationToken);
        await RunBonusVideoCleanupStepAsync(
            "clear external ad/consent origins",
            () => ClearTransientExternalStorageOriginsAsync(force: true),
            cancellationToken);
        await RunBonusVideoCleanupStepAsync(
            "close post-cleanup transient popup tabs",
            () => CloseBlockedOrForeignPagesAsync(mainPage, "bonus-video-post-cleanup"),
            cancellationToken);
    }

    private async Task RunBonusVideoCleanupStepAsync(
        string name,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BonusVideoCleanupStepTimeout);

        try
        {
            await action().WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _log?.Invoke($"[browser] bonus-video cleanup step timed out: {name}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[browser] bonus-video cleanup step failed ({name}): {ex.Message}");
        }
    }

    private async Task CloseBlockedOrForeignPagesAsync(IPage? mainPage, string reason)
    {
        if (_context is null)
        {
            return;
        }

        string? workingHost = null;
        try
        {
            workingHost = new Uri(_effectiveBaseUrl).Host;
        }
        catch
        {
            // BaseUrl not absolute — use blocked-url/opener checks only.
        }

        var closed = 0;
        foreach (var page in _context.Pages.ToArray())
        {
            if (page.IsClosed || ReferenceEquals(page, mainPage))
            {
                continue;
            }

            try
            {
                var url = page.Url ?? string.Empty;
                var blocked = IsBlockedPopupOrConsentUrl(url);
                var popupHost = Uri.TryCreate(url, UriKind.Absolute, out var popupUri)
                    ? popupUri.Host
                    : null;
                var foreignHost = workingHost is not null
                    && !string.IsNullOrEmpty(popupHost)
                    && !string.Equals(popupHost, workingHost, StringComparison.OrdinalIgnoreCase);
                var opener = await page.OpenerAsync();

                if (!blocked && !foreignHost && opener is null)
                {
                    continue;
                }

                TrackTransientExternalOrigin(url);
                await page.CloseAsync();
                closed++;
                _log?.Invoke($"[browser] closed bonus-video transient tab url='{url}' blocked={blocked} foreign={foreignHost} opener={(opener is null ? "false" : "true")} reason={reason}");
            }
            catch
            {
                // The popup may already have closed while being inspected.
            }
        }

        if (closed > 0)
        {
            _log?.Invoke($"[browser] bonus-video cleanup closed {closed} transient tab(s). pages={TryGetPageCount()}");
        }
    }

    private async Task NavigateMainPageToAboutBlankAsync(IPage? mainPage)
    {
        if (mainPage is null || mainPage.IsClosed)
        {
            return;
        }

        using var trace = _browserTrace.BeginOperation(
            "NAV",
            "goto-about-blank",
            $"reason=bonus-video-cleanup from={mainPage.Url}",
            "about:blank");
        try
        {
            await mainPage.GotoAsync("about:blank", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 3000,
            });
            trace.Complete("success", "loadState=DOMContentLoaded", mainPage.Url);
            _log?.Invoke($"[nav] GOTO done target='bonus-video-cleanup' current='{mainPage.Url}' pages={TryGetPageCount()}");
            _log?.Invoke("[browser] bonus-video cleanup navigated main page to about:blank.");
        }
        catch (Exception ex)
        {
            trace.Complete("failed", $"{ex.GetType().Name}: {ex.Message}", mainPage.Url);
            throw;
        }
    }

    private async Task ClearFirstPartyConsentCookiesAsync(IPage? mainPage)
    {
        if (_context is null)
        {
            return;
        }

        var page = mainPage is not null && !mainPage.IsClosed
            ? mainPage
            : _context.Pages.FirstOrDefault(candidate => !candidate.IsClosed);
        if (page is null)
        {
            return;
        }

        ICDPSession? cdp = null;
        try
        {
            cdp = await _context.NewCDPSessionAsync(page);
            var cookiesRemoved = await ClearFirstPartyConsentCookiesWithCdpAsync(cdp);
            _log?.Invoke($"[browser] bonus-video cleanup cleared Travian consent/ad cookies={cookiesRemoved}.");
        }
        finally
        {
            if (cdp is not null)
            {
                try
                {
                    await cdp.DetachAsync();
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    private async Task<int> ClearFirstPartyConsentCookiesWithCdpAsync(ICDPSession cdp)
    {
        if (_context is null)
        {
            return 0;
        }

        IReadOnlyList<BrowserContextCookiesResult> cookies;
        try
        {
            cookies = await _context.CookiesAsync(new[] { _effectiveBaseUrl });
        }
        catch
        {
            return 0;
        }

        var removed = 0;
        foreach (var cookie in cookies.Where(cookie => IsConsentStorageName(cookie.Name)))
        {
            try
            {
                await cdp.SendAsync("Network.deleteCookies", new Dictionary<string, object>
                {
                    ["name"] = cookie.Name,
                    ["domain"] = cookie.Domain,
                    ["path"] = cookie.Path,
                });
                removed++;
            }
            catch
            {
                // Keep login cookies safe: delete only exact consent/ad cookies and ignore failures.
            }
        }

        return removed;
    }
    private async Task ClearTransientExternalStorageOriginsAsync(bool force)
    {
        if (_context is null || (!force && ConsentDomainsAllowed))
        {
            if (_context is not null && ConsentDomainsAllowed)
            {
                _log?.Invoke("[browser] transient ad/consent storage cleanup skipped while bonus video allowance is active.");
            }

            return;
        }

        var page = _context.Pages.FirstOrDefault(candidate => !candidate.IsClosed);
        if (page is null)
        {
            return;
        }

        ICDPSession? cdp = null;
        try
        {
            string[] origins;
            int trackedOriginCount;
            lock (_transientExternalOriginsGate)
            {
                trackedOriginCount = _transientExternalOrigins.Count;
                origins = TransientExternalStorageOrigins
                    .Concat(_transientExternalOrigins)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                _transientExternalOrigins.Clear();
            }

            cdp = await _context.NewCDPSessionAsync(page);
            var cleared = 0;
            foreach (var origin in origins)
            {
                try
                {
                    await cdp.SendAsync("Storage.clearDataForOrigin", new Dictionary<string, object>
                    {
                        ["origin"] = origin,
                        ["storageTypes"] = "all",
                    });
                    cleared++;
                }
                catch
                {
                    // Some origins are browser/version dependent and may not exist in this context.
                }
            }

            if (force || trackedOriginCount > 0)
            {
                var cleanupLog = $"[browser] transient ad/consent storage cleanup cleared origins={cleared} tracked={trackedOriginCount} force={force}.";
                var now = DateTimeOffset.UtcNow;
                if (!string.Equals(cleanupLog, _lastTransientStorageCleanupLog, StringComparison.Ordinal)
                    || now - _lastTransientStorageCleanupLogAtUtc >= TimeSpan.FromMinutes(5))
                {
                    _lastTransientStorageCleanupLog = cleanupLog;
                    _lastTransientStorageCleanupLogAtUtc = now;
                    _log?.Invoke(cleanupLog);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[browser] transient ad/consent storage cleanup skipped: {ex.Message}");
        }
        finally
        {
            if (cdp is not null)
            {
                try
                {
                    await cdp.DetachAsync();
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    private sealed class BonusVideoNetworkDiagnostics
    {
        private readonly Action<string>? _log;
        private readonly object _signatureGate = new();
        private readonly HashSet<string> _loggedSignatures = new(StringComparer.OrdinalIgnoreCase);
        private int _successfulResponses;
        private int _noContentResponses;
        private int _httpErrors;
        private int _requestFailures;

        internal BonusVideoNetworkDiagnostics(Action<string>? log)
        {
            _log = log;
        }

        internal bool HasFailures => Volatile.Read(ref _requestFailures) > 0 || Volatile.Read(ref _httpErrors) > 0;

        internal void OnRequestFailed(object? sender, IRequest request)
        {
            if (!IsBonusVideoAdDomain(request.Url))
            {
                return;
            }

            Interlocked.Increment(ref _requestFailures);
            var failureReason = SafeVideoFailureReason(request.Failure);
            LogOnce(
                $"failed|{SafeVideoRequestLabel(request.Url)}|{failureReason}",
                $"[browser-video:network] request failed host='{SafeVideoRequestLabel(request.Url)}' reason='{failureReason}'.");
        }

        internal void OnResponse(object? sender, IResponse response)
        {
            if (!IsBonusVideoAdDomain(response.Url))
            {
                return;
            }

            if (response.Status == 204)
            {
                Interlocked.Increment(ref _noContentResponses);
                return;
            }

            if (response.Status >= 400)
            {
                Interlocked.Increment(ref _httpErrors);
                LogOnce(
                    $"http|{response.Status}|{SafeVideoRequestLabel(response.Url)}",
                    $"[browser-video:network] HTTP {response.Status} host='{SafeVideoRequestLabel(response.Url)}'.");
                return;
            }

            if (response.Status >= 200)
            {
                Interlocked.Increment(ref _successfulResponses);
            }
        }

        internal void LogSummary()
        {
            _log?.Invoke(
                $"[browser-video:network] summary ok={Volatile.Read(ref _successfulResponses)} "
                + $"no-content={Volatile.Read(ref _noContentResponses)} http-errors={Volatile.Read(ref _httpErrors)} "
                + $"request-failures={Volatile.Read(ref _requestFailures)}.");
        }

        private void LogOnce(string signature, string message)
        {
            lock (_signatureGate)
            {
                if (!_loggedSignatures.Add(signature))
                {
                    return;
                }
            }

            _log?.Invoke(message);
        }
    }

    internal static string SafeVideoRequestLabel(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "invalid-url";
        }

        return uri.Host;
    }

    internal static string SafeVideoFailureReason(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
        {
            return "request failed";
        }

        var marker = failure.IndexOf("net::", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return "request failed";
        }

        var end = marker;
        while (end < failure.Length
               && (char.IsLetterOrDigit(failure[end]) || failure[end] is ':' or '_'))
        {
            end++;
        }

        return failure[marker..end];
    }

}
