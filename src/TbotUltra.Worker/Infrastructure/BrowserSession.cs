using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;

namespace TbotUltra.Worker.Infrastructure;

public sealed partial class BrowserSession : IAsyncDisposable
{
    internal const string IsolatedBonusVideoPopupSuppressionScript =
        """
        (() => {
          if (window.__tbotBonusVideoPopupSuppressionInstalled) return;
          window.__tbotBonusVideoPopupSuppressionInstalled = true;

          const blockedOpen = () => null;
          try {
            Object.defineProperty(window, 'open', {
              configurable: false,
              enumerable: true,
              writable: false,
              value: blockedOpen
            });
          } catch {
            window.open = blockedOpen;
          }

          document.addEventListener('click', (event) => {
            const target = event.target instanceof Element ? event.target.closest('a[href]') : null;
            if (!target) return;

            const href = target.getAttribute('href') || '';
            const opensAnotherWindow = (target.getAttribute('target') || '').toLowerCase() === '_blank';
            const usesExternalProtocol = /^[a-z][a-z0-9+.-]*:/i.test(href)
              && !/^(?:https?|about|javascript):/i.test(href);
            if (!opensAnotherWindow && !usesExternalProtocol) return;

            event.preventDefault();
            event.stopImmediatePropagation();
          }, true);
        })();
        """;

    internal const string MainContextConsentUiSuppressionScript =
        """
        (() => {
          if (window.__tbotMainConsentUiSuppressionInstalled) return;
          window.__tbotMainConsentUiSuppressionInstalled = true;

          const selectors = [
            '#cmpbox',
            '#cmpwrapper',
            '.cmpbox',
            '[class*="cmpbox" i]',
            'iframe[src*="consentmanager" i]',
            '[id*="consent" i][role="dialog"]',
            '[class*="consent" i][role="dialog"]'
          ].join(',');
          const styleId = '__tbot_main_consent_ui_suppression';
          const reported = new WeakSet();

          const installStyle = () => {
            if (!document.documentElement || document.getElementById(styleId)) return;
            const style = document.createElement('style');
            style.id = styleId;
            style.textContent = `${selectors} { display: none !important; visibility: hidden !important; pointer-events: none !important; }`;
            document.documentElement.appendChild(style);
          };

          const inspect = (node) => {
            if (!(node instanceof Element)) return;
            const candidates = [];
            if (node.matches(selectors)) candidates.push(node);
            for (const descendant of node.querySelectorAll(selectors)) candidates.push(descendant);
            for (const candidate of candidates) {
              if (reported.has(candidate)) continue;
              reported.add(candidate);
              if (window.__tbotDetailedBrowserLoggingEnabled === true) {
                console.debug('__TBOT_BROWSER_TRACE__' + JSON.stringify({
                  event: 'consent-ui-suppressed',
                  target: candidate.id ? `#${candidate.id}` : candidate.tagName.toLowerCase(),
                  field: '-',
                  valueLength: 0,
                  trusted: false
                }));
              }
            }
          };

          installStyle();
          inspect(document.documentElement);
          new MutationObserver((mutations) => {
            installStyle();
            for (const mutation of mutations) {
              if (mutation.type === 'attributes') inspect(mutation.target);
              for (const node of mutation.addedNodes) inspect(node);
            }
          }).observe(document, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['id', 'class', 'role', 'src']
          });
        })();
        """;

    private const string LocalPlaywrightBrowsersDirectoryName = "ms-playwright";
    private const string LocalPlaywrightDriverDirectoryName = ".playwright";
    private static readonly TimeSpan BonusVideoCleanupStepTimeout = TimeSpan.FromSeconds(5);
    // Setup and playback have separate caps. Slow proxies no longer consume the playback budget while
    // Chrome starts/navigates, but either phase still has a firm bound so video can never stall automation.
    private static readonly TimeSpan IsolatedBonusVideoSetupMaxDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IsolatedBonusVideoActionMaxDuration =
        TimeSpan.FromSeconds(BonusVideoPlaybackPolicy.IsolatedActionTimeoutSeconds);
    // Upper bound on tearing down the isolated bonus-video browser, so a wedged CloseAsync cannot itself
    // re-stall the calling task. A leaked browser process is recoverable; an infinite stall is not.
    private static readonly TimeSpan IsolatedBonusVideoCloseTimeout = TimeSpan.FromSeconds(10);
    private sealed record BonusVideoCooldownState(DateTimeOffset UntilUtc, BonusVideoFailureKind Kind);
    private static readonly ConcurrentDictionary<string, BonusVideoCooldownState> BonusVideoCooldownByRoute = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim StorageStateGate = new(1, 1);
    private readonly BotOptions _config;
    private readonly AccountOptions _account;
    private readonly string _projectRoot;
    private readonly Action<string>? _log;
    private readonly BrowserTraceLogger _browserTrace;
    private readonly object _transientExternalOriginsGate = new();
    private readonly HashSet<string> _transientExternalOrigins = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _isolatedExternalContextsGate = new();
    private readonly HashSet<IBrowserContext> _isolatedExternalContexts = [];
    private string _effectiveBaseUrl;
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
        _effectiveBaseUrl = config.BaseUrl.TrimEnd('/');
        _browserTrace = new BrowserTraceLogger(config.DetailedBrowserLoggingEnabled, log);
    }

    public BrowserTraceLogger BrowserTrace => _browserTrace;

    public async Task SetDetailedBrowserLoggingAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (_browserTrace.Enabled == enabled)
        {
            return;
        }

        var context = _context;
        if (context is null)
        {
            _browserTrace.SetEnabled(enabled);
            return;
        }

        var browserValue = enabled ? "true" : "false";
        await context.AddInitScriptAsync($"window.__tbotDetailedBrowserLoggingEnabled = {browserValue};");
        foreach (var page in context.Pages.ToArray())
        {
            if (page.IsClosed)
            {
                continue;
            }

            try
            {
                await page.EvaluateAsync("enabled => { window.__tbotDetailedBrowserLoggingEnabled = enabled; }", enabled)
                    .WaitAsync(cancellationToken);
            }
            catch (PlaywrightException) when (page.IsClosed)
            {
                // A transient popup may close while the setting is propagated.
            }
        }

        _browserTrace.SetEnabled(enabled);
    }

    /// <summary>When true, consentmanager.net requests are allowed through the route block. Kept false
    /// during normal operation (so its sync tabs never spawn) and flipped on only for the duration of a
    /// bonus-video flow, which needs the GDPR/TCF consent. Volatile because the route handler runs on
    /// the Playwright connection thread.</summary>
    public volatile bool ConsentDomainsAllowed;

    /// <summary>Allows only user-initiated Travian lobby authentication popups while the host is
    /// displaying the blocking manual-login confirmation. This is separate from bonus-video consent
    /// so enabling manual login never opens the wider ad stack.</summary>
    public volatile bool ManualAuthenticationPopupsAllowed;

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
        var driverPath = ConfigureLocalPlaywrightEnvironment(_projectRoot);

        try
        {
            try
            {
                _playwright = await Playwright.CreateAsync();
            }
            catch (PlaywrightException ex)
            {
                _log?.Invoke(
                    $"[browser] Playwright driver failed to start from '{driverPath}'. " +
                    $"nodeExists={File.Exists(Path.Combine(driverPath, "node", "win32_x64", "node.exe"))} " +
                    $"cliExists={File.Exists(Path.Combine(driverPath, "package", "cli.js"))}. {ex.Message}");
                throw;
            }
            var launchOptions = CreateChromiumLaunchOptions(
                keepNativePopupBlocker: ShouldKeepNativePopupBlocker(_account.ManualLogin));
            // Record the process this launch creates so a crashed run's browser window can be closed on the
            // next start. The session runs the user's system Chrome, so its processes are indistinguishable
            // from the user's own by name or path — only the recorded identity makes cleanup safe.
            _browser = await BrowserLaunchRetry.RunAsync(
                () => LaunchedBrowserRegistry.TrackAsync(
                    _projectRoot,
                    launchOptions.Channel,
                    () => _playwright.Chromium.LaunchAsync(launchOptions),
                    _log),
                _log,
                cancellationToken: cancellationToken);
            return await OpenMainContextPageAsync(cancellationToken);
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

    private async Task<IPage> OpenMainContextPageAsync(CancellationToken cancellationToken)
    {
        var browser = _browser
            ?? throw new InvalidOperationException("Chromium is not open.");
        cancellationToken.ThrowIfCancellationRequested();

        var contextOptions = new BrowserNewContextOptions
        {
            BaseURL = _effectiveBaseUrl,
            Proxy = ResolveContextProxy(),
            // Let headed Chrome use the real maximized window area instead of emulating a fixed
            // viewport that may be larger than the user's monitor.
            ViewportSize = ViewportSize.NoViewport,
        };

        LegacyBrowserStorageAdapter.MigrateIfNeeded(
            AccountStoragePaths.LegacyBrowserStatePath(_projectRoot, _account.Name),
            StorageStatePath);

        if (File.Exists(StorageStatePath))
        {
            contextOptions.StorageStatePath = StorageStatePath;
        }

        _context = await browser.NewContextAsync(contextOptions);
        _browserTrace.Event("PAGE_CONTEXT", "main-context-opened", detail: $"storageStateLoaded={File.Exists(StorageStatePath)}");
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

            if (ShouldBlockMainContextRequest(
                    isAdDomain,
                    ConsentDomainsAllowed,
                    ManualAuthenticationPopupsAllowed))
            {
                await route.AbortAsync();
                return;
            }

            await route.ContinueAsync();
        });

        // Manual authentication can leave the game shell primed to recreate its CMP overlay later,
        // including during a read-only status pass. Install this at document start so the in-page
        // overlay is hidden before first paint. Bonus videos use a separate browser context.
        await _context.AddInitScriptAsync(MainContextConsentUiSuppressionScript);

        // Some Travian pages spawn short-lived tabs via window.open or target=_blank links. Neutralise
        // those in every document so they are never created. The bot navigates with GotoAsync and
        // creates its own pages via NewPageAsync, so it does not rely on page script popups.
        var allowManualPopupSources = ShouldAllowPopupSourcesInNewContext(
            _account.ManualLogin,
            ManualAuthenticationPopupsAllowed);
        await _context.AddInitScriptAsync(
            $"window.__tbotManualLoginPopupSourcesAllowed = {(allowManualPopupSources ? "true" : "false")};");
        await _context.AddInitScriptAsync(
            """
            (() => {
              // Clear the WebDriver automation flag in every document. The --disable-blink-features
              // launch arg already suppresses it, but the real Chrome/Edge channel can still expose it;
              // redefining it as undefined here guarantees navigator.webdriver never reads true.
              try {
                Object.defineProperty(navigator, 'webdriver', {
                  get: () => undefined,
                  configurable: true
                });
              } catch (_) {
                try { delete navigator.webdriver; } catch (_) { /* ignore */ }
              }

              const isTravianAuthenticationPage = () => {
                const host = String(location.hostname || '').toLowerCase();
                return host === 'www.travian.com'
                    || host === 'lobby.legends.travian.com'
                    || host === 'auth.travian.com'
                    || host === 'login.travian.com'
                    || host === 'accounts.travian.com';
              };
              const originalWindowOpen = window.open.bind(window);
              const blockedOpen = function (...args) {
                return window.__tbotManualLoginPopupSourcesAllowed === true || isTravianAuthenticationPage()
                    ? originalWindowOpen(...args)
                    : null;
              };
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
                if (window.__tbotManualLoginPopupSourcesAllowed === true || isTravianAuthenticationPage()) return;
                for (const element of document.querySelectorAll('a[target], form[target]')) {
                  element.removeAttribute('target');
                }
              };

              const originalAnchorClick = HTMLAnchorElement.prototype.click;
              HTMLAnchorElement.prototype.click = function () {
                if (window.__tbotManualLoginPopupSourcesAllowed !== true && !isTravianAuthenticationPage()) this.removeAttribute('target');
                return originalAnchorClick.call(this);
              };

              // Strip target='_blank' in the capture phase, before the default action opens a tab.
              // This catches synthetic dispatchEvent('click') opens (used by the consent/ad SDKs)
              // that bypass the .click() override above and race the MutationObserver below.
              document.addEventListener('click', function (event) {
                const node = event.target;
                const anchor = node && node.closest ? node.closest('a[target], area[target]') : null;
                if (anchor && window.__tbotManualLoginPopupSourcesAllowed !== true && !isTravianAuthenticationPage()) {
                  anchor.removeAttribute('target');
                }
              }, true);

              const originalFormSubmit = HTMLFormElement.prototype.submit;
              HTMLFormElement.prototype.submit = function () {
                if (window.__tbotManualLoginPopupSourcesAllowed !== true && !isTravianAuthenticationPage()) this.removeAttribute('target');
                return originalFormSubmit.call(this);
              };

              const startTargetObserver = () => {
                const observerTarget = document.documentElement;
                if (!observerTarget) return;
                neutralizeTargets();
                new MutationObserver(neutralizeTargets).observe(observerTarget, {
                  childList: true,
                  subtree: true,
                  attributes: true,
                  attributeFilter: ['target']
                });
              };

              if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', startTargetObserver, { once: true });
              } else {
                startTargetObserver();
              }
            })();
            """);

        await _context.AddInitScriptAsync(
            $"window.__tbotDetailedBrowserLoggingEnabled = {(_browserTrace.Enabled ? "true" : "false")};");

        // Capture successful, user-visible DOM actions from both real Playwright interactions and
        // verified JavaScript/React fallbacks. No field values are emitted and the trace sink drops
        // these console messages immediately while detailed browser logging is disabled.
        await _context.AddInitScriptAsync(
            """
            (() => {
              if (window.__tbotBrowserTraceInstalled) return;
              window.__tbotBrowserTraceInstalled = true;
              const prefix = '__TBOT_BROWSER_TRACE__';
              const describe = (node) => {
                if (!(node instanceof Element)) return '-';
                const id = node.id ? `#${node.id}` : '';
                const name = node.getAttribute('name');
                const role = node.getAttribute('role');
                return `${node.tagName.toLowerCase()}${id}${name ? `[name=${name}]` : ''}${role ? `[role=${role}]` : ''}`;
              };
              const emit = (event) => {
                if (window.__tbotDetailedBrowserLoggingEnabled !== true) return;
                const node = event.target instanceof Element ? event.target : null;
                const field = node?.getAttribute('name') || node?.id || node?.getAttribute('aria-label') || '-';
                const rawValue = node && 'value' in node ? String(node.value ?? '') : '';
                console.debug(prefix + JSON.stringify({
                  event: event.type,
                  target: describe(node),
                  field,
                  valueLength: rawValue.length,
                  trusted: event.isTrusted === true
                }));
              };
              document.addEventListener('click', emit, true);
              document.addEventListener('change', emit, true);
              document.addEventListener('submit', emit, true);
            })();
            """);

        var page = await _context.NewPageAsync();
        _browserTrace.AttachPage(page, "main-context");
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
            workingHost = new Uri(_effectiveBaseUrl).Host;
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
            _browserTrace.AttachPage(popup, "main-context-page-event");
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

                if (ShouldKeepExtraPageOpen(ManualAuthenticationPopupsAllowed, popup.Url))
                {
                    TrackTransientExternalOrigin(popup.Url);
                    _log?.Invoke($"[browser] leaving user-opened manual-login popup/tab open url='{popup.Url}' reason={reason}");
                    Interlocked.Exchange(ref closeHandled, 0);
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

    public async Task<IPage> RotateMainContextFromSavedStateAsync(
        string effectiveBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var previousContext = _context
            ?? throw new InvalidOperationException("The main browser context is not open.");

        if (!Uri.TryCreate(effectiveBaseUrl?.Trim(), UriKind.Absolute, out var effectiveUri)
            || effectiveUri.Scheme != Uri.UriSchemeHttps
            || !effectiveUri.Host.EndsWith(".travian.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The resolved game world is not a valid Official Travian server.");
        }

        // A manual lobby choice can correct a stale configured server. Use the origin that Play now
        // actually reached when filtering and restoring state, otherwise the new world's SSO cookies
        // are mistaken for foreign sibling-server state and removed during the consent cleanup.
        _effectiveBaseUrl = effectiveUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');

        // The lobby context is about to be destroyed, so do not spend time clearing its live external
        // origins. The serialized state is still filtered below; skipping live cleanup closes the SSO
        // landing context before its first-party consent bootstrap can become visible.
        await SaveStateAsync(clearTransientOrigins: false);
        cancellationToken.ThrowIfCancellationRequested();

        _context = null;
        ConsentDomainsAllowed = false;
        ManualAuthenticationPopupsAllowed = false;
        lock (_transientExternalOriginsGate)
        {
            _transientExternalOrigins.Clear();
        }

        IPage cleanPage;
        try
        {
            // Create and load the replacement before closing the lobby context. Closing the old page
            // while the replacement is still about:blank leaves a visible white/empty browser for
            // several seconds and looks like a consent popup flashing after login.
            cleanPage = await OpenMainContextPageAsync(cancellationToken);
            var gamePageUrl = new Uri(effectiveUri, "/dorf1.php").AbsoluteUri;
            _log?.Invoke("[browser] preloading the clean post-login game page before closing the lobby context.");
            await cleanPage.GotoAsync(gamePageUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _config.TimeoutMs,
            }).WaitAsync(cancellationToken);
            _log?.Invoke("[browser] clean post-login game page loaded; closing the lobby context.");
        }
        catch
        {
            var failedReplacementContext = _context;
            _context = previousContext;
            if (failedReplacementContext is not null
                && !ReferenceEquals(failedReplacementContext, previousContext))
            {
                try
                {
                    await failedReplacementContext.CloseAsync();
                }
                catch
                {
                    // The original context remains usable; surface the context-creation failure.
                }
            }

            throw;
        }

        await previousContext.CloseAsync();
        _browserTrace.Event("PAGE_CONTEXT", "lobby-context-closed", detail: "reason=clean-game-page-loaded");
        _log?.Invoke("[browser] lobby context closed after the clean game page loaded; Chromium stayed running.");
        return cleanPage;
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

        // Per-account proxy. Set on both launch and each context: Chromium uses the context
        // credentials to answer authenticated-proxy challenges without showing its native dialog.
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
        // --disable-blink-features=AutomationControlled removes the `navigator.webdriver` automation
        // flag at the source (the single most common bot tell); an init-script below also clears it as
        // a belt-and-suspenders fallback for the real Chrome/Edge channel.
        launchOptions.Args = new[]
        {
            "--disable-features=TrackingProtection3pcd",
            "--disable-blink-features=AutomationControlled",
            "--start-maximized",
        };

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

    private Proxy? ResolveContextProxy()
        => _account.ProxyEnabled
           && ProxyParser.TryBuild(_account.ProxyServer, out var contextProxy, out _)
            ? contextProxy
            : null;

    internal static bool ShouldKeepNativePopupBlocker(bool manualLoginAccount)
        // Manual identity providers may create their authentication tab asynchronously after the
        // user's click, so Chrome's native blocker would require the user to approve it manually.
        // The separate request route continues blocking the CMP/ad stack during manual login.
        => !manualLoginAccount;

    internal static bool ShouldAllowPopupSourcesInNewContext(
        bool manualLoginAccount,
        bool manualLoginWaitActive)
        => manualLoginAccount && manualLoginWaitActive;

    internal static bool ShouldBlockMainContextRequest(
        bool isAdDomain,
        bool bonusVideoConsentAllowed,
        bool manualLoginWaitActive)
    {
        // Manual login permits user-created authentication tabs, not Travian's unrelated CMP/ad
        // network stack. Letting the manual flag bypass this block allows that stack to create
        // short-lived Chrome targets long after the login dialog has closed.
        _ = manualLoginWaitActive;
        return isAdDomain && !bonusVideoConsentAllowed;
    }

    internal static bool ShouldKeepExtraPageOpen(bool manualLoginWaitActive, string? pageUrl)
        => manualLoginWaitActive;

    private async Task<bool> CloseBlockedPopupAsync(IPage popup, string source)
    {
        try
        {
            var url = popup.Url ?? string.Empty;
            if (ShouldKeepExtraPageOpen(ManualAuthenticationPopupsAllowed, url))
            {
                TrackTransientExternalOrigin(url);
                _log?.Invoke($"[browser] allowed user-opened manual-login popup/tab source={source} url='{url}' pages={TryGetPageCount()}");
                return false;
            }

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
        _browserTrace.Event("PAGE_CONTEXT", "browser-session-closing", detail: "reason=dispose");
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
        Exception? browserCloseFailure = null;
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
                browserCloseFailure = ex;
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

        // Verify every browser recorded for this Playwright lifetime is gone before clearing ownership.
        // This also removes a wedged isolated bonus-video process that CloseAsync reported as closed.
        var processCleanup = LaunchedBrowserRegistry.CleanupTrackedBrowsers(_projectRoot, _log);
        if (processCleanup.ClosedCount > 0)
        {
            _log?.Invoke($"[browser] force-closed {processCleanup.ClosedCount} verified leftover browser process(es).");
        }

        var browserCloseWasRecovered = browserCloseFailure is not null
            && processCleanup.RecordedCount > 0
            && processCleanup.Completed;
        if (processCleanup.RemainingCount > 0
            || (processCleanup.Skipped && browserCloseFailure is not null)
            || (browserCloseFailure is not null && !browserCloseWasRecovered))
        {
            var failure = cleanupFailure ?? new InvalidOperationException(
                $"{processCleanup.RemainingCount} owned browser process(es) remain after cleanup.");
            _browserTrace.Event("ERROR", "browser-session-close", "failed", failure.Message);
            throw new InvalidOperationException("Browser session cleanup did not close every owned browser process.", failure);
        }

        if (cleanupFailure is not null)
        {
            _log?.Invoke($"[browser] graceful cleanup reported an error, but browser-process closure was verified: {cleanupFailure.Message}");
        }

        _browserTrace.Event("PAGE_CONTEXT", "browser-session-closed", detail: "result=success");
    }

    // Pin Playwright to the driver and browsers shipped inside the app folder. PLAYWRIGHT_DRIVER_PATH
    // is required for the single-file build: the bundled node.exe driver is not auto-discovered from the
    // exe location (Playwright otherwise reports "Driver not found"). Browsers live under ms-playwright.
    public static string ConfigureLocalPlaywrightEnvironment(string projectRoot)
    {
        var driverPath = ResolvePlaywrightDriverPath(projectRoot, AppContext.BaseDirectory);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_PATH", driverPath);

        var browsersPath = Path.Combine(projectRoot, LocalPlaywrightBrowsersDirectoryName);
        Directory.CreateDirectory(browsersPath);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);
        return driverPath;
    }

    internal static string ResolvePlaywrightDriverPath(string projectRoot, string appBaseDirectory)
    {
        var bundledDriverPath = Path.Combine(appBaseDirectory, LocalPlaywrightDriverDirectoryName);
        if (File.Exists(Path.Combine(bundledDriverPath, "node", "win32_x64", "node.exe")) &&
            File.Exists(Path.Combine(bundledDriverPath, "package", "cli.js")))
        {
            return bundledDriverPath;
        }

        return Path.Combine(projectRoot, LocalPlaywrightDriverDirectoryName);
    }

}
