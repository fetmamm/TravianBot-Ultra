using System.Text.Json.Nodes;
using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;

namespace TbotUltra.Worker.Infrastructure;

public sealed class BrowserSession : IAsyncDisposable
{
    private const string LocalPlaywrightBrowsersDirectoryName = "ms-playwright";
    private const string LocalPlaywrightDriverDirectoryName = ".playwright";
    private static readonly SemaphoreSlim WarmupGate = new(1, 1);
    private static readonly SemaphoreSlim StorageStateGate = new(1, 1);
    private static bool _warmupCompleted;
    private readonly BotOptions _config;
    private readonly AccountOptions _account;
    private readonly bool? _headlessOverride;
    private readonly string _projectRoot;
    private readonly Action<string>? _log;
    private readonly object _transientExternalOriginsGate = new();
    private readonly HashSet<string> _transientExternalOrigins = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _isolatedExternalContextsGate = new();
    private readonly HashSet<IBrowserContext> _isolatedExternalContexts = [];

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

    /// <summary>When true, consentmanager.net requests are allowed through the route block. Kept false
    /// during normal operation (so its sync tabs never spawn) and flipped on only for the duration of a
    /// bonus-video flow, which needs the GDPR/TCF consent. Volatile because the route handler runs on
    /// the Playwright connection thread.</summary>
    public volatile bool ConsentDomainsAllowed;

    public string StorageStatePath =>
        AccountStoragePaths.BrowserStatePath(_projectRoot, _account.Name);

    public string PlaywrightBrowsersPath =>
        Path.Combine(_projectRoot, LocalPlaywrightBrowsersDirectoryName);

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

            ConfigureLocalPlaywrightEnvironment(projectRoot);

            using var playwright = await Playwright.CreateAsync();
            IBrowser? browser = null;
            try
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                });

                await browser.CloseAsync();
                browser = null;
                _warmupCompleted = true;
                return true;
            }
            finally
            {
                if (browser is not null)
                {
                    try
                    {
                        await browser.CloseAsync();
                    }
                    catch
                    {
                        // Best-effort cleanup if warmup was cancelled or launch partially failed.
                    }
                }
            }
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
        ConfigureLocalPlaywrightEnvironment(_projectRoot);

        try
        {
            _playwright = await Playwright.CreateAsync();
            // The live session must always run with a visible window. Headless is forced off here so a
            // stale config value (or a missing browser window) can never start the bot headless.
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = false,
                // Playwright disables Chromium's popup blocker by default. Keep the native blocker on:
                // bot-owned tabs use NewPageAsync, while ad/consent OOPIFs use script window.open.
                IgnoreDefaultArgs = new[] { "--disable-popup-blocking" },
            };

            // Official Travian's bonus videos (e.g. "increased adventure danger") require third-party
            // cookies. Disable Chromium's third-party-cookie phaseout so the ad/consent flow can run.
            // Private servers (SS-Travi) keep the default so their cross-promo behaviour is unchanged.
            if (!_config.IsPrivateServer)
            {
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
            }

            _browser = await _playwright.Chromium.LaunchAsync(launchOptions);

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

        // Close stray tabs the SS-Travi site spawns to OTHER hosts (cross-server promos like
        // mga.ss-travi.com), plus any real popup (non-null Opener). The bot's own extra pages
        // (catapult waves via NewPageAsync) live on the working server's host with no opener. External
        // tools such as Travco run in isolated contexts, never in this Travian context.
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
        "https://adscale.de",
        "https://www.adscale.de",
        "https://cdn.adscale.de",
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
            await ClearTransientExternalStorageOriginsAsync();
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

    private async Task ClearTransientExternalStorageOriginsAsync()
    {
        if (_context is null || ConsentDomainsAllowed)
        {
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
            lock (_transientExternalOriginsGate)
            {
                origins = TransientExternalStorageOrigins
                    .Concat(_transientExternalOrigins)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                _transientExternalOrigins.Clear();
            }

            cdp = await _context.NewCDPSessionAsync(page);
            foreach (var origin in origins)
            {
                try
                {
                    await cdp.SendAsync("Storage.clearDataForOrigin", new Dictionary<string, object>
                    {
                        ["origin"] = origin,
                        ["storageTypes"] = "all",
                    });
                }
                catch
                {
                    // Some origins are browser/version dependent and may not exist in this context.
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
                // Also drop the consentmanager (CMP) consent cookies (__cmp*, euconsent, etc.). If they
                // persist, Travian's first-party JS sees stored consent on every page load and runs the
                // bonus-video ad stack, which spawns window.open tabs (network blocking can't stop a
                // window.open-created tab). Consent is re-established transiently during a video.
                var kept = cookies.OfType<JsonObject>()
                    .Where(c => KeepHostForAccount(c["domain"]?.GetValue<string>() ?? string.Empty, accountHost)
                        && !IsConsentStorageName(c["name"]?.GetValue<string>() ?? string.Empty))
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
                // Strip consent entries from each origin's localStorage for the same reason as cookies.
                foreach (var origin in kept.OfType<JsonObject>())
                {
                    if (origin["localStorage"] is JsonArray ls)
                    {
                        var keptLs = ls.OfType<JsonObject>()
                            .Where(e => !IsConsentStorageName(e["name"]?.GetValue<string>() ?? string.Empty))
                            .Select(e => e.DeepClone())
                            .ToArray();
                        origin["localStorage"] = new JsonArray(keptLs);
                    }
                }

                root["origins"] = new JsonArray(kept);
            }

            return root.ToJsonString();
        }
        catch
        {
            return stateJson; // On any parse/shape error, keep the original state.
        }
    }

    // Cookie/localStorage names written by the consentmanager CMP (and IAB TCF). Stripped from saved
    // state so stored consent does not make Travian run the bonus-video ad stack on every page.
    internal static bool IsConsentStorageName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var n = name.Trim().ToLowerInvariant();
        return n.StartsWith("__cmp", StringComparison.Ordinal)
            || n.StartsWith("cmp", StringComparison.Ordinal)
            || n.Contains("consent", StringComparison.Ordinal)
            || n.StartsWith("euconsent", StringComparison.Ordinal)
            || n.StartsWith("usprivacy", StringComparison.Ordinal);
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
