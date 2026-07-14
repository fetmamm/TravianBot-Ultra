using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;

namespace TbotUltra.Worker.Infrastructure;

public sealed partial class BrowserSession : IAsyncDisposable
{
    private const string LocalPlaywrightBrowsersDirectoryName = "ms-playwright";
    private const string LocalPlaywrightDriverDirectoryName = ".playwright";
    private static readonly TimeSpan BonusVideoCleanupStepTimeout = TimeSpan.FromSeconds(5);
    // Hard cap for the WHOLE isolated bonus-video flow (setup + video + completion wait). A stuck
    // ad/video renderer can hang a Playwright call past its own timeout; this bound guarantees the
    // isolated browser is always torn down so it can never be left open with the task stalled. A legit
    // run is ~95s worst case (75s completion wait + ~20s setup), so this leaves comfortable margin.
    private static readonly TimeSpan IsolatedBonusVideoMaxDuration = TimeSpan.FromSeconds(120);
    // Upper bound on tearing down the isolated bonus-video browser, so a wedged CloseAsync cannot itself
    // re-stall the calling task. A leaked browser process is recoverable; an infinite stall is not.
    private static readonly TimeSpan IsolatedBonusVideoCloseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BonusVideoFailureCooldown = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> BonusVideoCooldownUntilByAccount = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim WarmupGate = new(1, 1);
    private static readonly SemaphoreSlim StorageStateGate = new(1, 1);
    private static bool _warmupCompleted;
    private readonly BotOptions _config;
    private readonly AccountOptions _account;
    private readonly string _projectRoot;
    private readonly Action<string>? _log;
    private readonly object _transientExternalOriginsGate = new();
    private readonly HashSet<string> _transientExternalOrigins = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _isolatedExternalContextsGate = new();
    private readonly HashSet<IBrowserContext> _isolatedExternalContexts = [];
    private DateTimeOffset _lastTransientStorageCleanupLogAtUtc = DateTimeOffset.MinValue;
    private string _lastTransientStorageCleanupLog = string.Empty;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public BrowserSession(
        BotOptions config,
        AccountOptions account,
        string projectRoot,
        Action<string>? log = null)
    {
        _config = config;
        _account = account;
        _projectRoot = projectRoot;
        _log = log;
    }

    /// <summary>When true, consentmanager.net requests are allowed through the route block. Kept false
    /// during normal operation (so its sync tabs never spawn) and flipped on only for the duration of a
    /// bonus-video flow, which needs the GDPR/TCF consent. Volatile because the route handler runs on
    /// the Playwright connection thread.</summary>
    public volatile bool ConsentDomainsAllowed;

    public string StorageStatePath =>
        AccountStoragePaths.BrowserStatePath(_projectRoot, _account.Name);

    public string PlaywrightBrowsersPath =>
        Path.Combine(_projectRoot, LocalPlaywrightBrowsersDirectoryName);

    public async Task<IPage> OpenPageAsync(CancellationToken cancellationToken = default)
    {
        var authDirectory = Path.GetDirectoryName(StorageStatePath);
        if (string.IsNullOrWhiteSpace(authDirectory))
        {
            throw new InvalidOperationException("Storage state path is invalid.");
        }

        Directory.CreateDirectory(authDirectory);
        ConfigureLocalPlaywrightEnvironment(_projectRoot);

        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(CreateChromiumLaunchOptions(keepNativePopupBlocker: true));

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
            // The bonus-video ad/consent stack (consentmanager, oadts, adscale, Google IMA) loads on
            // Travian pages and its cross-origin (out-of-process) iframes periodically spawn visible
            // sync tabs that we cannot neutralise (initScript does not reach the OOPIFs). Block the whole
            // stack by default so nothing runs during idle/loop operation. The bonus videos DO need it,
            // so the video flow temporarily flips ConsentDomainsAllowed for its duration only.
            var isAdDomain = IsBonusVideoAdDomain(route.Request.Url);
            if (isAdDomain)
            {
                TrackTransientExternalOrigin(route.Request.Url);
            }

            if (!ConsentDomainsAllowed && isAdDomain)
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

              // Strip target='_blank' in the capture phase, before the default action opens a tab.
              // This catches synthetic dispatchEvent('click') opens (used by the consent/ad SDKs)
              // that bypass the .click() override above and race the MutationObserver below.
              document.addEventListener('click', function (event) {
                const node = event.target;
                const anchor = node && node.closest ? node.closest('a[target], area[target]') : null;
                if (anchor) {
                  anchor.removeAttribute('target');
                }
              }, true);

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

        // Close stray tabs that navigate to other hosts, plus any real popup (non-null Opener).
        // The bot's own extra pages (catapult waves via NewPageAsync) live on the working
        // server's host with no opener. External tools such as Travco run in isolated contexts,
        // never in this Travian context.
        _context.Page += (_, popup) =>
        {
            if (ReferenceEquals(popup, page))
            {
                return;
            }

            _log?.Invoke($"[browser] page event pages={TryGetPageCount()} initialUrl='{popup.Url}'");
            popup.Close += (_, _) =>
            {
                _log?.Invoke($"[browser] page closed pages={TryGetPageCount()} url='{popup.Url}'");
            };

            // Cross-domain consent/ad sync tabs (consentmanager, oadts, any foreign host) must be closed
            // the moment they navigate, before they flash visibly. The bonus videos run in an in-page
            // iframe (not a tab), so closing foreign tabs never affects them. The bot's own extra pages
            // (catapult waves via NewPageAsync) start as about:blank (empty host) and navigate to the
            // working host, so they are never treated as foreign.
            var closeHandled = 0;
            async Task TryCloseStrayTabAsync(string reason)
            {
                if (ConsentDomainsAllowed)
                {
                    TrackTransientExternalOrigin(popup.Url);
                    _log?.Invoke($"[browser] leaving popup open during bonus video url='{popup.Url}' reason={reason}");
                    return;
                }

                if (Interlocked.Exchange(ref closeHandled, 1) == 1)
                {
                    return;
                }

                try
                {
                    if (await CloseBlockedPopupAsync(popup, reason))
                    {
                        return;
                    }

                    var popupHost = Uri.TryCreate(popup.Url ?? string.Empty, UriKind.Absolute, out var popupUri)
                        ? popupUri.Host
                        : null;
                    var foreignHost = workingHost is not null
                        && !string.IsNullOrEmpty(popupHost)
                        && !string.Equals(popupHost, workingHost, StringComparison.OrdinalIgnoreCase);
                    var opener = await popup.OpenerAsync();
                    if (foreignHost || opener is not null)
                    {
                        await popup.CloseAsync();
                        _log?.Invoke($"[browser] closed stray tab url='{popup.Url}' foreign={foreignHost} opener={(opener is null ? "false" : "true")} reason={reason}");
                    }
                    else
                    {
                        // Not foreign yet (e.g. still about:blank) — allow a later navigation to re-evaluate.
                        Interlocked.Exchange(ref closeHandled, 0);
                    }
                }
                catch
                {
                    // Popup may already be navigating/closing — ignore.
                }
            }

            popup.FrameNavigated += async (_, frame) =>
            {
                if (ReferenceEquals(frame, popup.MainFrame))
                {
                    await TryCloseStrayTabAsync("frame-navigated");
                }
            };

            // Immediate attempt in case the URL is already resolved when the page event fires.
            _ = TryCloseStrayTabAsync("page-initial");
        };

            return page;
        }
        catch
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanupEx)
            {
                _log?.Invoke($"[browser] cleanup after failed initialization also failed: {cleanupEx.Message}");
            }

            throw;
        }
    }

    private BrowserTypeLaunchOptions CreateChromiumLaunchOptions(bool keepNativePopupBlocker)
    {
        // The live session must always run with a visible window. Headless is forced off here so a
        // stale config value (or a missing browser window) can never start the bot headless.
        var launchOptions = new BrowserTypeLaunchOptions
        {
            Headless = false,
        };

        if (_account.NeverUseOwnIp
            && (!_account.ProxyEnabled || !ProxyParser.TryBuild(_account.ProxyServer, out _, out _)))
        {
            throw new InvalidOperationException(
                $"Account '{_account.Name}' has 'Never use own IP address' enabled, but no valid proxy is configured. Browser startup blocked.");
        }

        // Per-account proxy. Set on launch so every context of this browser (main, bonus-video,
        // isolated external) routes through it — traffic cannot leak past the proxy. OFF by default.
        if (_account.ProxyEnabled && ProxyParser.TryBuild(_account.ProxyServer, out var proxy, out var proxyWarning))
        {
            launchOptions.Proxy = proxy;
            _log?.Invoke($"[browser] using proxy '{ProxyParser.MaskForLog(_account.ProxyServer)}' for account '{_account.Name}'.");
            if (proxyWarning is not null)
            {
                _log?.Invoke($"[browser] proxy warning for account '{_account.Name}': {proxyWarning}");
            }
        }
        else if (_account.ProxyEnabled)
        {
            _log?.Invoke($"[browser] proxy is enabled for account '{_account.Name}' but the server string is empty/invalid; running without a proxy.");
        }

        if (keepNativePopupBlocker)
        {
            // Playwright disables Chromium's popup blocker by default. Keep the native blocker on for
            // the main Travian browser; bot-owned tabs use NewPageAsync, while ad/consent OOPIFs use
            // script window.open. The isolated bonus-video browser intentionally leaves this default
            // alone so the ad player can open what it needs, then the whole browser is closed.
            launchOptions.IgnoreDefaultArgs = new[] { "--disable-popup-blocking" };
        }

        // Bonus videos require third-party cookies. Disable Chromium's third-party-cookie
        // phaseout so the ad/consent flow can run.
        launchOptions.Args = new[] { "--disable-features=TrackingProtection3pcd" };

        // The bonus ad videos are H.264/AAC, which Playwright's bundled open-source Chromium
        // cannot decode ("format is not supported"). Use the system Google Chrome build, which
        // ships the proprietary codecs. If Chrome is not installed we fall back to bundled
        // Chromium (everything except the bonus videos still works).
        var chromeChannel = ResolveInstalledChromeChannel();
        if (chromeChannel is not null)
        {
            launchOptions.Channel = chromeChannel;
            _log?.Invoke($"[browser] using system browser channel '{chromeChannel}' for codec support.");
        }
        else
        {
            _log?.Invoke("[browser] no system Chrome/Edge found; bonus videos may fail (missing H.264/AAC codecs).");
        }

        return launchOptions;
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

            TrackTransientExternalOrigin(url);
            if (ConsentDomainsAllowed)
            {
                _log?.Invoke($"[browser] allowed bonus-video popup temporarily source={source} url='{url}' pages={TryGetPageCount()}");
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

    // Returns the Playwright browser channel to use when proprietary codecs are needed (bonus videos),
    // preferring Google Chrome, then Edge. Returns null when neither is installed at a standard path.
    private static string? ResolveInstalledChromeChannel()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var chromePaths = new[]
        {
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
        };
        if (chromePaths.Any(File.Exists))
        {
            return "chrome";
        }

        var edgePaths = new[]
        {
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        if (edgePaths.Any(File.Exists))
        {
            return "msedge";
        }

        return null;
    }

    private static bool IsBlockedPopupOrConsentUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IsBonusVideoAdDomain(url);
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

    public async ValueTask DisposeAsync()
    {
        var context = _context;
        var browser = _browser;
        var playwright = _playwright;
        IBrowserContext[] isolatedContexts;
        lock (_isolatedExternalContextsGate)
        {
            isolatedContexts = _isolatedExternalContexts.ToArray();
            _isolatedExternalContexts.Clear();
        }

        _context = null;
        _browser = null;
        _playwright = null;

        Exception? cleanupFailure = null;
        foreach (var isolatedContext in isolatedContexts)
        {
            try
            {
                await isolatedContext.CloseAsync();
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
            }
        }

        if (context is not null)
        {
            try
            {
                await context.CloseAsync();
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }
        }

        if (browser is not null)
        {
            try
            {
                await browser.CloseAsync();
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
            }
        }

        if (playwright is not null)
        {
            try
            {
                playwright.Dispose();
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
            }
        }

        if (cleanupFailure is not null)
        {
            throw new InvalidOperationException("Browser session cleanup did not complete cleanly.", cleanupFailure);
        }
    }

    // Pin Playwright to the driver and browsers shipped inside the app folder. PLAYWRIGHT_DRIVER_PATH
    // is required for the single-file build: the bundled node.exe driver is not auto-discovered from the
    // exe location (Playwright otherwise reports "Driver not found"). Browsers live under ms-playwright.
    private static void ConfigureLocalPlaywrightEnvironment(string projectRoot)
    {
        var driverPath = Path.Combine(projectRoot, LocalPlaywrightDriverDirectoryName);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_PATH", driverPath);

        var browsersPath = Path.Combine(projectRoot, LocalPlaywrightBrowsersDirectoryName);
        Directory.CreateDirectory(browsersPath);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);
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
