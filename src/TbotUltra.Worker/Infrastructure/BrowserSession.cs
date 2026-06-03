using System.Text.Json.Nodes;
using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;

namespace TbotUltra.Worker.Infrastructure;

public sealed class BrowserSession : IAsyncDisposable
{
    private const string LocalPlaywrightBrowsersDirectoryName = "ms-playwright";
    private static readonly SemaphoreSlim WarmupGate = new(1, 1);
    private static readonly SemaphoreSlim StorageStateGate = new(1, 1);
    private static bool _warmupCompleted;
    private readonly BotOptions _config;
    private readonly AccountOptions _account;
    private readonly bool? _headlessOverride;
    private readonly string _projectRoot;
    private readonly Action<string>? _log;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public BrowserSession(
        BotOptions config,
        AccountOptions account,
        string projectRoot,
        bool? headlessOverride = null,
        Action<string>? log = null)
    {
        _config = config;
        _account = account;
        _projectRoot = projectRoot;
        _headlessOverride = headlessOverride;
        _log = log;
    }

    public string StorageStatePath =>
        AccountStoragePaths.BrowserStatePath(_projectRoot, _account.Name);

    public string PlaywrightBrowsersPath =>
        Path.Combine(_projectRoot, LocalPlaywrightBrowsersDirectoryName);

    public static async Task<bool> WarmupAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        if (_warmupCompleted || !ChromiumAlreadyInstalled(projectRoot))
        {
            return false;
        }

        await WarmupGate.WaitAsync(cancellationToken);
        try
        {
            if (_warmupCompleted)
            {
                return false;
            }

            var playwrightBrowsersPath = Path.Combine(projectRoot, LocalPlaywrightBrowsersDirectoryName);
            Directory.CreateDirectory(playwrightBrowsersPath);
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", playwrightBrowsersPath);

            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
            });

            await browser.CloseAsync();
            _warmupCompleted = true;
            return true;
        }
        finally
        {
            WarmupGate.Release();
        }
    }

    // Kills Chromium processes left over from a previous run that crashed or was force-stopped
    // (so the MainWindow_Closing cleanup never ran). Those orphaned browser windows linger on screen
    // — including stale cross-promo tabs — and look like the current session "flickering". Only runs
    // when this is the single app instance, so it never kills a concurrently-running instance's live
    // browser. Returns the number of processes terminated.
    public static int KillOrphanedChromium(string projectRoot)
    {
        var killed = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(projectRoot)
                || System.Diagnostics.Process.GetProcessesByName("TbotUltra.Desktop").Length > 1)
            {
                return 0;
            }

            var playwrightPath = Path.Combine(projectRoot, LocalPlaywrightBrowsersDirectoryName);
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("chrome"))
            {
                try
                {
                    var exePath = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath)
                        && exePath.StartsWith(playwrightPath, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(entireProcessTree: true);
                        killed++;
                    }
                }
                catch
                {
                    // Access denied / already exited / different bitness — skip.
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
            // Best-effort cleanup; never block startup.
        }

        return killed;
    }

    public async Task<IPage> OpenPageAsync(CancellationToken cancellationToken = default)
    {
        var authDirectory = Path.GetDirectoryName(StorageStatePath);
        if (string.IsNullOrWhiteSpace(authDirectory))
        {
            throw new InvalidOperationException("Storage state path is invalid.");
        }

        Directory.CreateDirectory(authDirectory);
        Directory.CreateDirectory(PlaywrightBrowsersPath);

        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", PlaywrightBrowsersPath);

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _headlessOverride ?? _config.Headless,
        });

        var contextOptions = new BrowserNewContextOptions
        {
            BaseURL = _config.BaseUrl,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
        };

        MigrateLegacyStorageStateIfNeeded();

        if (File.Exists(StorageStatePath))
        {
            contextOptions.StorageStatePath = StorageStatePath;
        }

        _context = await _browser.NewContextAsync(contextOptions);
        _context.SetDefaultTimeout(_config.TimeoutMs);
        await _context.RouteAsync("**/*", async route =>
        {
            if (IsBlockedPopupOrConsentUrl(route.Request.Url))
            {
                await route.AbortAsync();
                return;
            }

            await route.ContinueAsync();
        });

        // Some Travian pages spawn short-lived tabs via window.open or target=_blank links. Neutralise
        // those in every document so they are never created. The bot navigates with GotoAsync and
        // creates its own pages via NewPageAsync, so it does not rely on page script popups.
        await _context.AddInitScriptAsync(
            """
            (() => {
              const blockedOpen = function () { return null; };
              try {
                Object.defineProperty(window, 'open', {
                  value: blockedOpen,
                  writable: false,
                  configurable: false
                });
              } catch (_) {
                window.open = blockedOpen;
              }

              const neutralizeTargets = () => {
                for (const element of document.querySelectorAll('a[target], form[target]')) {
                  element.removeAttribute('target');
                }
              };

              const originalAnchorClick = HTMLAnchorElement.prototype.click;
              HTMLAnchorElement.prototype.click = function () {
                this.removeAttribute('target');
                return originalAnchorClick.call(this);
              };

              const originalFormSubmit = HTMLFormElement.prototype.submit;
              HTMLFormElement.prototype.submit = function () {
                this.removeAttribute('target');
                return originalFormSubmit.call(this);
              };

              if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', neutralizeTargets, { once: true });
              } else {
                neutralizeTargets();
              }

              new MutationObserver(neutralizeTargets).observe(document.documentElement, {
                childList: true,
                subtree: true,
                attributes: true,
                attributeFilter: ['target']
              });
            })();
            """);

        var page = await _context.NewPageAsync();
        _log?.Invoke($"[browser] main page created pages={_context.Pages.Count} url='{page.Url}'");
        page.Popup += async (_, popup) =>
        {
            await CloseBlockedPopupAsync(popup, "page-popup");
        };
        page.Close += (_, _) =>
        {
            _log?.Invoke($"[browser] main page closed pages={TryGetPageCount()}");
        };

        string? workingHost = null;
        try
        {
            workingHost = new Uri(_config.BaseUrl).Host;
        }
        catch
        {
            // BaseUrl not absolute — host check disabled, fall back to opener check only.
        }

        // Close stray tabs the SS-Travi site spawns to OTHER hosts (cross-server promos like
        // mga.ss-travi.com), plus any real popup (non-null Opener). The bot's own extra pages
        // (catapult waves via NewPageAsync) live on the working server's host with no opener, so
        // they're never closed.
        _context.Page += async (_, popup) =>
        {
            if (ReferenceEquals(popup, page))
            {
                return;
            }

            try
            {
                _log?.Invoke($"[browser] page event pages={TryGetPageCount()} initialUrl='{popup.Url}'");
                if (await CloseBlockedPopupAsync(popup, "context-page-initial"))
                {
                    return;
                }

                popup.Close += (_, _) =>
                {
                    _log?.Invoke($"[browser] page closed pages={TryGetPageCount()} url='{popup.Url}'");
                };

                try { await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 1500 }); }
                catch { /* about:blank or fast-closing popup */ }

                if (await CloseBlockedPopupAsync(popup, "context-page-loaded"))
                {
                    return;
                }

                var popupHost = Uri.TryCreate(popup.Url ?? string.Empty, UriKind.Absolute, out var popupUri)
                    ? popupUri.Host
                    : null;
                // Note: about:blank yields an EMPTY (non-null) host. The bot's own extra pages
                // (catapult waves via NewPageAsync) start as about:blank before they navigate, so we
                // must require a real, non-empty host here — otherwise those tabs would be treated as
                // "foreign" and closed before the catapult code can navigate them.
                var foreignHost = workingHost is not null
                    && !string.IsNullOrEmpty(popupHost)
                    && !string.Equals(popupHost, workingHost, StringComparison.OrdinalIgnoreCase);
                var opener = await popup.OpenerAsync();
                _log?.Invoke($"[browser] new page detected url='{popup.Url}' host='{popupHost ?? "-"}' opener={(opener is null ? "false" : "true")} foreign={foreignHost}");

                if (foreignHost || opener is not null)
                {
                    await popup.CloseAsync();
                    _log?.Invoke($"[browser] closed popup url='{popup.Url}' foreign={foreignHost} opener={(opener is null ? "false" : "true")}");
                }
            }
            catch
            {
                // Popup may already be navigating/closing — ignore.
            }
        };

        return page;
    }

    private async Task<bool> CloseBlockedPopupAsync(IPage popup, string source)
    {
        try
        {
            var url = popup.Url ?? string.Empty;
            if (!IsBlockedPopupOrConsentUrl(url))
            {
                return false;
            }

            await popup.CloseAsync();
            _log?.Invoke($"[browser] closed blocked popup source={source} url='{url}' pages={TryGetPageCount()}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBlockedPopupOrConsentUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("consentmanager.net", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".consentmanager.net", StringComparison.OrdinalIgnoreCase);
    }

    private int TryGetPageCount()
    {
        try
        {
            return _context?.Pages.Count ?? 0;
        }
        catch
        {
            return -1;
        }
    }

    public async Task SaveStateAsync()
    {
        if (_context is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(StorageStatePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Storage state path is invalid.");
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(StorageStatePath)}.{Guid.NewGuid():N}.tmp");

        await StorageStateGate.WaitAsync();
        try
        {
            // Strip cookies/localStorage that belong to a SISTER server (e.g. mga.ss-travi.com while
            // this account is on elt.ss-travi.com). All SS-Travi servers share the ss-travi.com domain,
            // so a stray sister-server session can otherwise persist in this account's saved state and
            // keep triggering cross-promo popups on every login. We keep the account's own host plus
            // shared parent-domain cookies (which login needs) and drop only foreign sibling subdomains.
            var stateJson = await _context.StorageStateAsync();
            stateJson = FilterForeignSubdomainState(stateJson);
            await File.WriteAllTextAsync(tempPath, stateJson);

            await ReplaceStorageStateWithRetryAsync(tempPath, StorageStatePath);
        }
        finally
        {
            StorageStateGate.Release();
            TryDeleteFile(tempPath);
        }

        DeleteLegacyStorageStateIfPresent();
    }

    // Removes cookies and localStorage origins that belong to a sister subdomain of the account's
    // server (e.g. mga.ss-travi.com when the account is on elt.ss-travi.com). Keeps the account's own
    // host, parent/shared domains (ss-travi.com — needed for login), and any sub-host of the account.
    private string FilterForeignSubdomainState(string stateJson)
    {
        string accountHost;
        try
        {
            accountHost = new Uri(_config.BaseUrl).Host.ToLowerInvariant();
        }
        catch
        {
            return stateJson; // BaseUrl not absolute — leave state untouched.
        }

        if (string.IsNullOrEmpty(accountHost))
        {
            return stateJson;
        }

        try
        {
            if (JsonNode.Parse(stateJson) is not JsonObject root)
            {
                return stateJson;
            }

            if (root["cookies"] is JsonArray cookies)
            {
                var kept = cookies.OfType<JsonObject>()
                    .Where(c => KeepHostForAccount(c["domain"]?.GetValue<string>() ?? string.Empty, accountHost))
                    .Select(c => c.DeepClone())
                    .ToArray();
                root["cookies"] = new JsonArray(kept);
            }

            if (root["origins"] is JsonArray origins)
            {
                var kept = origins.OfType<JsonObject>()
                    .Where(o =>
                    {
                        var origin = o["origin"]?.GetValue<string>() ?? string.Empty;
                        return !Uri.TryCreate(origin, UriKind.Absolute, out var u)
                            || KeepHostForAccount(u.Host, accountHost);
                    })
                    .Select(o => o.DeepClone())
                    .ToArray();
                root["origins"] = new JsonArray(kept);
            }

            return root.ToJsonString();
        }
        catch
        {
            return stateJson; // On any parse/shape error, keep the original state.
        }
    }

    private static bool KeepHostForAccount(string cookieDomainOrHost, string accountHost)
    {
        var d = cookieDomainOrHost.TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(d))
        {
            return true;
        }

        // Keep: exact host, a parent/shared domain of the account host, or a sub-host of it.
        // Drop: sibling subdomains (different server on the same shared base domain).
        return d == accountHost
            || accountHost.EndsWith("." + d, StringComparison.Ordinal)
            || d.EndsWith("." + accountHost, StringComparison.Ordinal);
    }

    private static async Task ReplaceStorageStateWithRetryAsync(string sourcePath, string targetPath)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (IOException ex) when (IsTransientStorageStateWriteError(ex) && attempt < 5)
            {
                lastError = ex;
                await Task.Delay(150 * attempt);
            }
            catch (UnauthorizedAccessException ex) when (attempt < 5)
            {
                lastError = ex;
                await Task.Delay(150 * attempt);
            }
        }

        throw new IOException($"Could not replace browser state after retries: {lastError?.Message}", lastError);
    }

    private static bool IsTransientStorageStateWriteError(IOException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("user-mapped section", StringComparison.OrdinalIgnoreCase)
            || message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot access the file", StringComparison.OrdinalIgnoreCase)
            || message.Contains("begärda åtgärden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("användarmappat", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.CloseAsync();
        }

        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
    }

    public static bool ChromiumAlreadyInstalled(string projectRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            var playwrightRoot = Path.Combine(projectRoot, LocalPlaywrightBrowsersDirectoryName);
            if (!Directory.Exists(playwrightRoot))
            {
                return false;
            }

            var executables = Directory.GetFiles(playwrightRoot, "chrome.exe", SearchOption.AllDirectories);
            return executables.Any(path =>
                path.Contains("chromium-", StringComparison.OrdinalIgnoreCase) &&
                path.Contains("chrome-win", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private void MigrateLegacyStorageStateIfNeeded()
    {
        var legacyPath = AccountStoragePaths.LegacyBrowserStatePath(_projectRoot, _account.Name);
        if (File.Exists(StorageStatePath) || !File.Exists(legacyPath))
        {
            return;
        }

        var targetDirectory = Path.GetDirectoryName(StorageStatePath);
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new InvalidOperationException("Storage state path is invalid.");
        }

        Directory.CreateDirectory(targetDirectory);
        File.Copy(legacyPath, StorageStatePath, overwrite: false);
    }

    private void DeleteLegacyStorageStateIfPresent()
    {
        var legacyPath = AccountStoragePaths.LegacyBrowserStatePath(_projectRoot, _account.Name);
        if (File.Exists(legacyPath))
        {
            File.Delete(legacyPath);
        }
    }
}
