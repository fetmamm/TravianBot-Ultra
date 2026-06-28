using System.Text.Json.Nodes;
using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;

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

        ConsentDomainsAllowed = false;
        await ClearTransientExternalStorageOriginsAsync(force: true);
        var stateJson = FilterForeignSubdomainState(await _context.StorageStateAsync());

        IBrowser? videoBrowser = null;
        IBrowserContext? videoContext = null;
        try
        {
            videoBrowser = await _playwright.Chromium.LaunchAsync(CreateChromiumLaunchOptions(keepNativePopupBlocker: false));
            videoContext = await videoBrowser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = _config.BaseUrl,
                ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
                StorageState = stateJson,
            });
            videoContext.SetDefaultTimeout(_config.TimeoutMs);
            videoContext.Page += (_, page) =>
            {
                _log?.Invoke($"[browser-video] page event pages={videoContext.Pages.Count} initialUrl='{page.Url}'");
                page.Close += (_, _) =>
                {
                    _log?.Invoke($"[browser-video] page closed pages={videoContext.Pages.Count} url='{page.Url}'");
                };
            };

            var page = await videoContext.NewPageAsync();
            _log?.Invoke("[browser-video] isolated bonus-video browser opened.");
            return await action(page, cancellationToken);
        }
        finally
        {
            if (videoContext is not null)
            {
                try
                {
                    await videoContext.CloseAsync();
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[browser-video] context cleanup failed: {ex.Message}");
                }
            }

            if (videoBrowser is not null)
            {
                try
                {
                    await videoBrowser.CloseAsync();
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[browser-video] browser cleanup failed: {ex.Message}");
                }
            }

            _log?.Invoke("[browser-video] isolated bonus-video browser closed.");
        }
    }

    public async Task<IPage> OpenIsolatedExternalPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_browser is null)
        {
            throw new InvalidOperationException("Browser session is not open.");
        }

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
        });
        context.SetDefaultTimeout(_config.TimeoutMs);

        lock (_isolatedExternalContextsGate)
        {
            _isolatedExternalContexts.Add(context);
        }

        try
        {
            var page = await context.NewPageAsync();
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
            workingHost = new Uri(_config.BaseUrl).Host;
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

        await mainPage.GotoAsync("about:blank", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 3000,
        });
        _log?.Invoke("[browser] bonus-video cleanup navigated main page to about:blank.");
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
            cookies = await _context.CookiesAsync(new[] { _config.BaseUrl });
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
                _log?.Invoke($"[browser] transient ad/consent storage cleanup cleared origins={cleared} tracked={trackedOriginCount} force={force}.");
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

}