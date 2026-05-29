using Microsoft.Playwright;
using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int HeroLowHpRetrySeconds = 60;
    private HeroOintmentRetryKey? _lastHeroOintmentMissKey;
    private bool? _lastHeroAutoUseOintmentsEnabled;

    public async Task<HeroAdventureDispatchResult> SendHeroOnAdventureAsync(CancellationToken cancellationToken = default)
    {
        Notify("SendHeroOnAdventureAsync started");

        var quick = await ReadHeroQuickStatusAsync(allowDorf1Fallback: true, forceDorf1Reload: false, cancellationToken);
        var statusText = quick.Sidebar.StatusText;
        var inHomeVillage = quick.IsInVillage;
        var isDead = quick.Status.IsDead || IsHeroStatusTextDead(statusText);
        var isOnTheWay = (quick.Status.SecondsUntilReturn is > 0) || IsHeroStatusTextAway(statusText);
        var adventures = ResolveAdventureCount(quick);

        Notify($"Hero quick status: '{statusText ?? "(unknown)"}', adventures available: {adventures}.");

        if (isDead)
        {
            var revived = await ReviveHeroOnInventoryAsync(cancellationToken);
            return new HeroAdventureDispatchResult(
                IsInHomeVillage: false,
                StatusText: statusText,
                AdventureCount: adventures,
                Dispatched: false,
                SecondsUntilReturn: null,
                Message: revived
                    ? "Hero was dead. Clicked Revive — retry dispatch in a moment."
                    : "Hero is dead but Revive button could not be located on inventory page.",
                WasRevived: revived);
        }

        if (isOnTheWay)
        {
            var etaSeconds = quick.Status.SecondsUntilReturn ?? await ReadHeroReturnFromRallyPointAsync(cancellationToken);
            var etaText = etaSeconds is int e ? FormatDuration(e) : "(unknown)";
            return new HeroAdventureDispatchResult(
                IsInHomeVillage: false,
                StatusText: statusText,
                AdventureCount: adventures,
                Dispatched: false,
                SecondsUntilReturn: etaSeconds,
                Message: $"Hero is on the way home. ETA: {etaText}.",
                IsOnTheWayHome: true);
        }

        if (!inHomeVillage)
        {
            return new HeroAdventureDispatchResult(
                IsInHomeVillage: false,
                StatusText: statusText,
                AdventureCount: adventures,
                Dispatched: false,
                SecondsUntilReturn: null,
                Message: $"Hero is not in home village (status: '{statusText ?? "unknown"}'). Skipping dispatch.");
        }

        if (adventures <= 0)
        {
            return new HeroAdventureDispatchResult(
                IsInHomeVillage: true,
                StatusText: statusText,
                AdventureCount: 0,
                Dispatched: false,
                SecondsUntilReturn: null,
                Message: "No adventures available. Nothing to dispatch.");
        }

        await OpenAdventureListWithFallbackAsync(cancellationToken);

        var openedDetail = await ClickFirstAdventureEntryAsync(cancellationToken);
        if (!openedDetail)
        {
            return new HeroAdventureDispatchResult(
                IsInHomeVillage: true,
                StatusText: statusText,
                AdventureCount: adventures,
                Dispatched: false,
                SecondsUntilReturn: null,
                Message: "Could not find a 'To the adventure' entry on the adventures list.");
        }

        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on adventure detail page.", cancellationToken);

        var returnSeconds = await ReadHeroReturnSecondsAsync(cancellationToken);

        var submitted = await ClickAdventureSubmitButtonAsync(cancellationToken);
        if (!submitted)
        {
            return new HeroAdventureDispatchResult(
                IsInHomeVillage: true,
                StatusText: statusText,
                AdventureCount: adventures,
                Dispatched: false,
                SecondsUntilReturn: returnSeconds,
                Message: "Could not click the final 'To adventure' submit button on the adventure detail page.");
        }

        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after dispatching hero.", cancellationToken);

        // Land back on dorf1 after dispatch so the next status read happens on a fresh page.
        await EnsureFreshDorf1ForHeroAsync(forceReload: false, cancellationToken);

        var returnText = returnSeconds is int rs ? FormatDuration(rs) : "(unknown)";
        Notify($"Hero dispatched. Return in: {returnText}.");

        return new HeroAdventureDispatchResult(
            IsInHomeVillage: true,
            StatusText: statusText,
            AdventureCount: adventures,
            Dispatched: true,
            SecondsUntilReturn: returnSeconds,
            Message: $"Hero dispatched. Adventures left after dispatch: {Math.Max(0, adventures - 1)}. Return in {returnText}.");
    }

    public async Task<int?> RefreshAdventureCountAsync(bool forceReload = true, CancellationToken cancellationToken = default)
    {
        Notify("RefreshAdventureCountAsync started");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var sidebar = await ReadHeroSidebarStatusAsync(cancellationToken);
        if (!sidebar.AdventureFound)
        {
            Notify("Adventure indicator not found on current page. Trying dorf1 without reload.");
            await EnsureFreshDorf1ForHeroAsync(forceReload: false, cancellationToken);
            sidebar = await ReadHeroSidebarStatusAsync(cancellationToken);
        }

        if (!sidebar.AdventureFound && forceReload)
        {
            Notify("Adventure indicator still not found. Retrying with dorf1 reload.");
            await EnsureFreshDorf1ForHeroAsync(forceReload: true, cancellationToken);
            sidebar = await ReadHeroSidebarStatusAsync(cancellationToken);
        }

        if (!sidebar.AdventureFound)
        {
            Notify("Adventure indicator not found on current page.");
            return null;
        }

        Notify($"Adventures on current page: {sidebar.AdventureCount}.");
        return sidebar.AdventureCount;
    }

    private async Task<HeroQuickStatus> ReadHeroQuickStatusAsync(
        bool allowDorf1Fallback,
        bool forceDorf1Reload,
        CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var status = await ReadHeroStatusAsync(cancellationToken);
        var sidebar = await ReadHeroSidebarStatusAsync(cancellationToken);
        var heroHpFromSidebar = await ReadHeroHpFromSidebarAsync(cancellationToken);
        var inVillage = await IsHeroInActiveVillageAsync(cancellationToken);
        var hasUnassignedPointsSignal = status.UnassignedPoints > 0 || await HasHeroLevelUpIndicatorAsync(cancellationToken);

        var hasUsefulSignals = status.Exists
            || sidebar.AdventureFound
            || hasUnassignedPointsSignal
            || IsCurrentUrlForPath(Paths.Resources);

        if (!hasUsefulSignals && allowDorf1Fallback)
        {
            await EnsureFreshDorf1ForHeroAsync(forceDorf1Reload, cancellationToken);
            status = await ReadHeroStatusAsync(cancellationToken);
            sidebar = await ReadHeroSidebarStatusAsync(cancellationToken);
            heroHpFromSidebar = await ReadHeroHpFromSidebarAsync(cancellationToken);
            inVillage = await IsHeroInActiveVillageAsync(cancellationToken);
            hasUnassignedPointsSignal = status.UnassignedPoints > 0 || await HasHeroLevelUpIndicatorAsync(cancellationToken);
        }

        return new HeroQuickStatus(status, sidebar, inVillage, heroHpFromSidebar, hasUnassignedPointsSignal);
    }

    private static int ResolveAdventureCount(HeroQuickStatus quick)
    {
        if (quick.Sidebar.AdventureFound)
        {
            return Math.Max(0, quick.Sidebar.AdventureCount);
        }

        return Math.Max(0, quick.Status.AdventuresAvailable);
    }

    private static int? TryResolveAdventureCount(HeroQuickStatus quick)
    {
        if (quick.Sidebar.AdventureFound)
        {
            return Math.Max(0, quick.Sidebar.AdventureCount);
        }

        if (quick.Status.Exists)
        {
            return Math.Max(0, quick.Status.AdventuresAvailable);
        }

        return null;
    }

    private static bool IsHeroStatusTextDead(string? statusText)
    {
        var text = (statusText ?? string.Empty).ToLowerInvariant();
        return text.Contains("dead", StringComparison.Ordinal)
            || text.Contains("deceased", StringComparison.Ordinal);
    }

    private static bool IsHeroStatusTextAway(string? statusText)
    {
        var text = (statusText ?? string.Empty).ToLowerInvariant();
        return text.Contains("on the way", StringComparison.Ordinal)
            || text.Contains("back from", StringComparison.Ordinal)
            || text.Contains("returning", StringComparison.Ordinal);
    }

    private async Task EnsureFreshDorf1ForHeroAsync(bool forceReload, CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening dorf1 for hero check.", cancellationToken);
        await EnsureLoggedInAsync();
        if (!forceReload)
        {
            return;
        }

        await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while refreshing dorf1 for hero check.", cancellationToken);
    }

    private async Task<HeroSidebarStatusJs> ReadHeroSidebarStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var rawJson = await _page.EvaluateAsync<string>(
                """
                () => {
                  const clean = (v) => (v || '').replace(/\s+/g, ' ').trim();
                  const statusEl = document.querySelector('.heroStatusMessage');
                  const statusText = statusEl ? clean(statusEl.textContent || '') : null;

                  let found = false;
                  let count = 0;

                  const button = document.querySelector('button.adventureWhite, button.layoutButton.adventureWhite');
                  if (button) {
                    found = true;
                    const bubble = button.querySelector('.speechBubbleContent');
                    if (bubble) {
                      const parsed = parseInt(clean(bubble.textContent || ''), 10);
                      if (Number.isFinite(parsed)) count = parsed;
                    }
                  } else {
                    const heroArea = document.querySelector('#sidebarBoxHero, #heroV2, .heroPosition, .heroSidebar');
                    const bubble = (heroArea || document).querySelector('.adventureWhite .speechBubbleContent');
                    if (bubble) {
                      found = true;
                      const parsed = parseInt(clean(bubble.textContent || ''), 10);
                      if (Number.isFinite(parsed)) count = parsed;
                    }
                  }

                  return JSON.stringify({ statusText, adventureFound: found, adventureCount: count });
                }
                """);

            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return new HeroSidebarStatusJs();
            }

            return JsonSerializer.Deserialize<HeroSidebarStatusJs>(rawJson) ?? new HeroSidebarStatusJs();
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return new HeroSidebarStatusJs();
        }
    }

    private async Task<bool> ClickAdventureButtonOnDorf1Async(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const candidate =
                document.querySelector('button.adventureWhite, button.layoutButton.adventureWhite')
                || Array.from(document.querySelectorAll('button')).find(b => /adventure/i.test(b.className || ''));
              if (!candidate) return false;
              if (candidate.hasAttribute('disabled')) return false;
              candidate.click();
              return true;
            }
            """);
    }

    private async Task OpenAdventureListWithFallbackAsync(CancellationToken cancellationToken)
    {
        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening adventures list.", cancellationToken);

        if (await HasAdventureEntryOnPageAsync(cancellationToken))
        {
            return;
        }

        await GotoAsync(Paths.HeroAdventures, cancellationToken);
        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on hero adventures page.", cancellationToken);
        if (await HasAdventureEntryOnPageAsync(cancellationToken))
        {
            return;
        }

        await GotoAsync(Paths.HeroAdventureLegacy, cancellationToken);
        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on legacy hero adventures page.", cancellationToken);
    }

    private async Task OpenHeroAdventuresPageAsync(CancellationToken cancellationToken)
    {
        if (await IsHeroAdventuresPageAsync(cancellationToken))
        {
            return;
        }

        try
        {
            await GotoAsync(Paths.HeroAdventures, cancellationToken);
            await WaitForNavigationSettledAsync(cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared on hero adventures page.", cancellationToken);
            if (await IsHeroAdventuresPageAsync(cancellationToken))
            {
                return;
            }
        }
        catch (Exception ex) when (IsTransientExecutionContextException(ex))
        {
            Notify($"Hero adventures modern page hit transient navigation issue. Falling back to {Paths.HeroAdventureLegacy}.");
        }

        await GotoAsync(Paths.HeroAdventureLegacy, cancellationToken);
        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on legacy hero adventures page.", cancellationToken);
    }

    private async Task<bool> IsHeroAdventuresPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const url = (window.location.href || '').toLowerCase();
                  if (url.includes('/hero_adventure.php') || url.includes('/hero.php?t=3')) {
                    return true;
                  }

                  if (document.querySelector('a.gotoAdventure[href*="start_adventure.php"]')) {
                    return true;
                  }

                  const text = (document.body?.innerText || '').toLowerCase();
                  return text.includes('adventure')
                    && (text.includes('to the adventure') || text.includes('arrival in') || text.includes('back in'));
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private async Task<bool> HasAdventureEntryOnPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const nodes = Array.from(document.querySelectorAll('a, button, input[type="submit"]'));
              return nodes.some(node => {
                const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                const href = (node.getAttribute('href') || '').toLowerCase();
                return text.includes('to the adventure')
                    || text.includes('to adventure')
                    || href.includes('hero.php?t=3&kid=');
              });
            }
            """);
    }

    private async Task<bool> ClickAdventureSubmitButtonAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const isDisabled = (node) =>
                !node || (node.hasAttribute && node.hasAttribute('disabled'))
                || (node.className || '').toString().toLowerCase().includes('disabled');

              const direct = document.querySelector('button#start, button[name="s1"]');
              if (direct && !isDisabled(direct)) {
                direct.click();
                return true;
              }

              const submit = Array.from(document.querySelectorAll('button[type="submit"], input[type="submit"]'))
                .find(node => {
                  if (isDisabled(node)) return false;
                  const text = ((node.value || '') + ' ' + (node.textContent || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                  return text.includes('to adventure') || text.includes('to the adventure');
                });
              if (!submit) return false;
              submit.click();
              return true;
            }
            """);
    }

    private async Task<bool> ClickFirstAdventureEntryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const isDisabled = (node) => {
                if (!node) return true;
                if (node.hasAttribute && node.hasAttribute('disabled')) return true;
                const cls = (node.className || '').toString().toLowerCase();
                return cls.includes('disabled') || cls.includes('inactive');
              };

              // Strategy 1: direct match on adventure detail link (Travian's standard URL pattern).
              const direct = document.querySelector(
                'a[href*="hero.php?t=3&kid="], a[href*="hero.php?t=3&amp;kid="], #adventureListForm a[href*="kid="]'
              );
              if (direct && !isDisabled(direct)) {
                direct.click();
                return true;
              }

              // Strategy 2: text or attribute match on any clickable element.
              const candidates = Array.from(document.querySelectorAll('a, button, input[type="submit"]'));
              const target = candidates.find(node => {
                if (isDisabled(node)) return false;
                const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                const href = (node.getAttribute('href') || '').toLowerCase();
                return text.includes('to the adventure')
                    || text.includes('to adventure')
                    || text.includes('start adventure')
                    || href.includes('hero.php?t=3&kid=')
                    || href.includes('action=start');
              });
              if (!target) return false;
              target.click();
              return true;
            }
            """);
    }

    public async Task<bool> CheckAndReviveDeadHeroOnCurrentPageAsync(bool autoRevive, CancellationToken cancellationToken = default)
    {
        // Lightweight check from whatever page we are currently on. The dead hero indicator
        // (<div class="bigSpeechBubble dead">) is rendered in the hero sidebar on most pages.
        var isDead = await _page.EvaluateAsync<bool>(
            "() => !!document.querySelector('.bigSpeechBubble.dead')");
        if (!isDead)
        {
            return false;
        }

        Notify("Hero appears dead (bigSpeechBubble.dead detected on current page).");
        if (!autoRevive)
        {
            Notify("Auto revive is disabled. Skipping revive.");
            return false;
        }

        var revived = await ReviveHeroOnInventoryAsync(cancellationToken);
        Notify(revived
            ? "Auto revive: clicked Revive on hero inventory."
            : "Auto revive: hero is dead but Revive button could not be located.");
        return revived;
    }

    private async Task<bool> ReviveHeroOnInventoryAsync(CancellationToken cancellationToken)
    {
        Notify("ReviveHeroOnInventoryAsync started");
        await GotoAsync(Paths.HeroInventory, cancellationToken);
        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero inventory.", cancellationToken);

        // Make sure the Attributes tab is active. The Revive button is rendered there.
        var onAttributesTab = await _page.EvaluateAsync<bool>(
            """
            () => {
              const url = window.location.href.toLowerCase();
              const active = document.querySelector('a.tabItem.active, a.active.tabItem');
              if (active && /attribute/i.test(active.textContent || '')) return true;
              return !!document.querySelector('button#save.startTraining, button#save.green');
            }
            """);

        if (!onAttributesTab)
        {
            var clicked = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const link = Array.from(document.querySelectorAll('a.tabItem, a.tab, a'))
                    .find(a => /attribute/i.test(a.textContent || '') && /hero_inventory\.php/i.test(a.getAttribute('href') || ''));
                  if (!link) return false;
                  link.click();
                  return true;
                }
                """);
            if (clicked)
            {
                await WaitForNavigationSettledAsync(cancellationToken);
            }
        }

        // Read the revive duration shown above the button (example: 00:00:03) before clicking revive.
        var reviveDurationRaw = await _page.EvaluateAsync<string?>(
            """
            () => {
              const wrapper = document.querySelector('.lineWrapper');
              if (!wrapper) return null;
              const value = wrapper.querySelector('.inlineIcon.duration .value, .duration .value');
              return value ? (value.textContent || '').trim() : null;
            }
            """);
        // Reuse shared parser so we support HH:MM:SS and other duration formats used elsewhere.
        var reviveDurationSeconds = ParseDurationToSeconds(reviveDurationRaw);

        // Click the revive button using direct selector first, then a text-based fallback.
        var clickedRevive = await _page.EvaluateAsync<bool>(
            """
            () => {
              const isDisabled = (node) =>
                !node || (node.hasAttribute && node.hasAttribute('disabled'))
                || (node.className || '').toString().toLowerCase().includes('disabled');

              const direct = document.querySelector('button#save.green, button#save.startTraining, button[name="save"][value="Revive" i]');
              if (direct && !isDisabled(direct)) { direct.click(); return true; }

              const candidate = Array.from(document.querySelectorAll('button, input[type="submit"]'))
                .find(node => {
                  if (isDisabled(node)) return false;
                  const text = ((node.value || '') + ' ' + (node.textContent || '')).toLowerCase();
                  return text.includes('revive') || text.includes('resurrect');
                });
              if (!candidate) return false;
              candidate.click();
              return true;
            }
            """);

        if (clickedRevive)
        {
            // Wait revive duration + 1 second to avoid continuing before the server-side revive is completed.
            var reviveWaitSeconds = Math.Max(1, (reviveDurationSeconds ?? 0) + 1);
            Notify($"Revive duration detected: {FormatDuration(reviveWaitSeconds)}. Starting countdown.");
            for (var remaining = reviveWaitSeconds; remaining > 0; remaining--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Notify($"Revive countdown: {remaining}s remaining.");
                await Task.Delay(1000, cancellationToken);
            }

            await WaitForNavigationSettledAsync(cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared after clicking Revive.", cancellationToken);
            Notify("Revive button clicked.");
        }
        else
        {
            Notify("Revive button could not be found on hero inventory.");
        }

        return clickedRevive;
    }

    private async Task<int?> ReadHeroReturnFromRallyPointAsync(CancellationToken cancellationToken)
    {
        Notify("ReadHeroReturnFromRallyPointAsync started");
        await GotoAsync(Paths.RallyPointTroops, cancellationToken);
        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening rally point.", cancellationToken);

        var raw = await _page.EvaluateAsync<string?>(
            """
            () => {
              const clean = (v) => (v || '').replace(/\s+/g, ' ').trim();

              const rows = Array.from(document.querySelectorAll('table tr'));
              for (const row of rows) {
                const heroImg = row.querySelector('img.unit.uhero, img.uhero');
                if (!heroImg) continue;

                const cells = Array.from(row.querySelectorAll('td'));
                const hasOneCount = cells.some(td => clean(td.textContent || '') === '1');
                if (!hasOneCount) continue;

                // Prefer a live timer if present.
                const timerEl = row.querySelector('.timer[value], span.timer[value]');
                if (timerEl) {
                  const v = timerEl.getAttribute('value');
                  const n = parseInt(v || '', 10);
                  if (Number.isFinite(n) && n >= 0) return String(n);
                }

                // Fall back to "Today at HH:MM:SS" or any HH:MM:SS in the row.
                const text = clean(row.textContent || '');
                const match = text.match(/(\d{1,2}):(\d{2}):(\d{2})/);
                if (match) {
                  const target = new Date();
                  target.setHours(parseInt(match[1], 10), parseInt(match[2], 10), parseInt(match[3], 10), 0);
                  if (target.getTime() < Date.now()) target.setDate(target.getDate() + 1);
                  const seconds = Math.round((target.getTime() - Date.now()) / 1000);
                  return String(Math.max(0, seconds));
                }
              }

              return null;
            }
            """);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, out var seconds) ? Math.Max(0, seconds) : null;
    }

    private async Task WaitForNavigationSettledAsync(CancellationToken cancellationToken)
    {
        // Wait for DOMContentLoaded + Load so Mootools/PropertySetter init has run. Skip
        // NetworkIdle: Travian keeps long-poll XHR open in the background, so that wait
        // almost always burns its full timeout (~4s) for no benefit. The downstream
        // WaitForAttributesTableAsync already waits on the actual element we need.
        try
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = _config.TimeoutMs,
            }).WaitAsync(cancellationToken);
        }
        catch (PlaywrightException) { }
        catch (TimeoutException) { }

        try
        {
            await _page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions
            {
                Timeout = _config.TimeoutMs,
            }).WaitAsync(cancellationToken);
        }
        catch (PlaywrightException) { }
        catch (TimeoutException) { }
    }

    private async Task<int?> ReadHeroReturnSecondsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await _page.EvaluateAsync<string?>(
            """
            () => {
              const clean = (v) => (v || '').replace(/\s+/g, ' ').trim();
              const text = clean(document.body?.innerText || '');
              const match = text.match(/back\s*in[^\d]*(\d{1,2}:\d{2}(?::\d{2})?)/i);
              if (match) return match[1];
              const timer = document.querySelector('.heroReturn .timer, [class*="return" i] [class*="timer" i]');
              if (timer) return clean(timer.textContent || '');
              return null;
            }
            """);

        return ParseDurationToSeconds(raw);
    }

    public async Task<string> ManageHeroAsync(
        int minHpForAdventure,
        bool autoRevive,
        bool autoAssignPoints,
        bool autoUseOintments,
        string statPriority,
        string adventurePickOrder = "shortest",
        string hideMode = "hide",
        CancellationToken cancellationToken = default)
    {
        Notify("ManageHeroAsync started");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        UpdateHeroOintmentAutoUseState(autoUseOintments);

        // Force a fresh dorf1 reload before reading hero status. When the continuous loop only runs
        // the hero function it sits idle until the hero-return timer expires; without this reload the
        // page can be stale and falsely report the hero as still away.
        await EnsureFreshDorf1ForHeroAsync(forceReload: true, cancellationToken);

        var quick = await ReadHeroQuickStatusAsync(allowDorf1Fallback: true, forceDorf1Reload: false, cancellationToken);
        var status = quick.Status;
        var inVillage = quick.IsInVillage;
        var hpPercent = status.HpPercent ?? quick.HeroHpFromSidebar;
        var adventureHintCount = ResolveAdventureCount(quick);
        if (!status.Exists && adventureHintCount == 0 && !quick.HasUnassignedPointsSignal)
        {
            return "Hero page is unavailable for this account.";
        }

        var actions = new List<string>();

        if ((status.IsDead || IsHeroStatusTextDead(quick.Sidebar.StatusText)) && autoRevive)
        {
            var revived = await TryReviveHeroAsync(cancellationToken);
            actions.Add(revived ? "revive_started" : "revive_not_available");

            quick = await ReadHeroQuickStatusAsync(allowDorf1Fallback: true, forceDorf1Reload: false, cancellationToken);
            status = quick.Status;
            inVillage = quick.IsInVillage;
            hpPercent = status.HpPercent ?? quick.HeroHpFromSidebar;
            adventureHintCount = ResolveAdventureCount(quick);
        }

        if (quick.HasUnassignedPointsSignal)
        {
            Notify("Hero level up / unassigned points signal detected.");
        }

        if (autoAssignPoints && quick.HasUnassignedPointsSignal)
        {
            var allocated = await TryAllocateHeroPointsAsync(statPriority, cancellationToken);
            if (allocated > 0)
            {
                actions.Add($"points_allocated={allocated}");
            }
        }

        var heroReturnWaitSeconds = status.SecondsUntilReturn;
        var adventureCount = adventureHintCount;
        if (adventureHintCount > 0)
        {
            await OpenHeroAdventuresPageAsync(cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared on adventures page.", cancellationToken);
            adventureCount = await CountAdventureRowsAsync(cancellationToken);
            status = await ReadHeroStatusAsync(cancellationToken);
            hpPercent ??= status.HpPercent;
            heroReturnWaitSeconds = status.SecondsUntilReturn;
        }

        var minHpThreshold = Math.Clamp(minHpForAdventure, 1, 100);
        if (hpPercent is >= 0 && hpPercent >= minHpThreshold)
        {
            ClearHeroOintmentMiss();
        }

        if (autoUseOintments
            && adventureCount > 0
            && inVillage
            && !status.IsDead
            && hpPercent is >= 0
            && hpPercent < minHpThreshold)
        {
            var ointmentResult = await TryUseHeroOintmentsForAdventureAsync(
                hpPercent.Value,
                minHpThreshold,
                adventureCount,
                cancellationToken);

            if (ointmentResult.SkippedBySuppression)
            {
                actions.Add("ointment_check_skipped_cached_miss");
            }
            else if (ointmentResult.UsedCount > 0)
            {
                actions.Add($"ointments_used={ointmentResult.UsedCount}");
                quick = await ReadHeroQuickStatusAsync(allowDorf1Fallback: true, forceDorf1Reload: false, cancellationToken);
                status = quick.Status;
                inVillage = quick.IsInVillage;
                hpPercent = status.HpPercent ?? quick.HeroHpFromSidebar;
                var refreshedAdventureCount = ResolveAdventureCount(quick);
                if (refreshedAdventureCount > 0)
                {
                    adventureCount = refreshedAdventureCount;
                }
            }
            else if (ointmentResult.LookupAttempted)
            {
                actions.Add("ointments_unavailable");
            }
        }

        var canSendByHp = !status.IsDead && (hpPercent ?? 0) >= minHpThreshold;

        if (adventureCount > 0 && canSendByHp && inVillage)
        {
            var (sent, durationSeconds, returnSeconds) = await TrySendHeroToAdventureAsync(adventurePickOrder, cancellationToken);
            heroReturnWaitSeconds = returnSeconds > 0 ? returnSeconds : durationSeconds > 0 ? durationSeconds * 2 : null;
            if (sent)
            {
                actions.Add($"adventure_sent({adventurePickOrder},duration={durationSeconds}s,return_eta={heroReturnWaitSeconds ?? 0}s)");
            }
            else
            {
                actions.Add("adventure_not_clickable");
            }
        }
        else if (adventureCount > 0 && !inVillage)
        {
            actions.Add("adventure_skipped_hero_away");
        }
        else if (adventureCount > 0 && !canSendByHp)
        {
            actions.Add($"adventure_skipped_hp_too_low(hp={hpPercent?.ToString() ?? "?"}%)");
        }

        var pointsText = quick.HasUnassignedPointsSignal
            ? (status.UnassignedPoints > 0 ? status.UnassignedPoints.ToString() : "signal")
            : status.UnassignedPoints.ToString();
        var summary = $"Hero status: dead={status.IsDead}, hp={hpPercent?.ToString() ?? "?"}%, adventures={adventureCount}, points={pointsText}, in_village={inVillage}";
        if (heroReturnWaitSeconds is > 0 && actions.Count == 0)
        {
            return $"{summary}. Hero is away. queue_wait_seconds={heroReturnWaitSeconds.Value}";
        }

        if (actions.Count == 0)
        {
            return $"{summary}. No hero action was needed.";
        }

        if (heroReturnWaitSeconds is > 0)
        {
            return $"{summary}. Actions: {string.Join(", ", actions)}. queue_wait_seconds={heroReturnWaitSeconds.Value}";
        }

        if (actions.Any(action => action.StartsWith("adventure_skipped_hp_too_low", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{summary}. Actions: {string.Join(", ", actions)}. queue_wait_seconds={HeroLowHpRetrySeconds}";
        }

        return $"{summary}. Actions: {string.Join(", ", actions)}.";

        // Step 1: dorf1 — quick hero-in-village check + sidebar HP read.
        #if false
        await GotoAsync(Paths.Resources, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening dorf1 for hero check.", cancellationToken);
        await EnsureLoggedInAsync();
        var inVillage = await IsHeroInActiveVillageAsync(cancellationToken);
        var heroHpFromSidebar = await ReadHeroHpFromSidebarAsync(cancellationToken);

        // Step 2: /hero_adventure.php — authoritative adventure count + selection target.
        await OpenHeroAdventuresPageAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on adventures page.", cancellationToken);
        var adventureCount = await CountAdventureRowsAsync(cancellationToken);

        var status = await ReadHeroStatusAsync(cancellationToken);
        if (!status.Exists && adventureCount == 0)
        {
            return "Hero page is unavailable for this account.";
        }

        // Use the most reliable readings: adventure count from list, HP from sidebar fallback if status read failed.
        var hpPercent = status.HpPercent ?? heroHpFromSidebar;
        var actions = new List<string>();

        if (status.IsDead && autoRevive)
        {
            var revived = await TryReviveHeroAsync(cancellationToken);
            actions.Add(revived ? "revive_started" : "revive_not_available");
            // Re-read after revive attempt.
            await GotoAsync(Paths.HeroAdventures, cancellationToken);
            status = await ReadHeroStatusAsync(cancellationToken);
            adventureCount = await CountAdventureRowsAsync(cancellationToken);
            hpPercent = status.HpPercent ?? await ReadHeroHpFromSidebarAsync(cancellationToken);
        }

        var heroLeveledUp = await HasHeroLevelUpIndicatorAsync(cancellationToken);
        if (heroLeveledUp) Notify("Hero level up detected.");

        // Step 3: when auto-allocate is on, always check the attributes tab. The level-up speech
        // bubble disappears once the user has visited /hero_inventory, and the /hero_adventure.php
        // status read can't see #availablePoints — neither is reliable as a gate, so we let the
        // allocator itself decide via the authoritative attributes-tab snapshot.
        if (autoAssignPoints)
        {
            var allocated = await TryAllocateHeroPointsAsync(statPriority, cancellationToken);
            if (allocated > 0)
            {
                actions.Add($"points_allocated={allocated}");
            }
            // Return to adventure page for dispatch.
            await OpenHeroAdventuresPageAsync(cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared after allocating points.", cancellationToken);
        }

        // Step 4: dispatch adventure if hero is in village, has HP and adventures exist.
        var canSendByHp = !status.IsDead && (hpPercent ?? 0) >= Math.Clamp(minHpForAdventure, 1, 100);
        var heroReturnWaitSeconds = status.SecondsUntilReturn;
        if (adventureCount > 0 && canSendByHp && inVillage)
        {
            var (sent, durationSeconds, returnSeconds) = await TrySendHeroToAdventureAsync(adventurePickOrder, cancellationToken);
            heroReturnWaitSeconds = returnSeconds > 0 ? returnSeconds : durationSeconds > 0 ? durationSeconds * 2 : null;
            if (sent)
            {
                actions.Add($"adventure_sent({adventurePickOrder},duration={durationSeconds}s,return_eta={heroReturnWaitSeconds ?? 0}s)");
            }
            else
            {
                actions.Add("adventure_not_clickable");
            }
        }
        else if (adventureCount > 0 && !inVillage)
        {
            actions.Add("adventure_skipped_hero_away");
        }
        else if (adventureCount > 0 && !canSendByHp)
        {
            actions.Add($"adventure_skipped_hp_too_low(hp={hpPercent?.ToString() ?? "?"}%)");
        }

        // Step 5: end on /hero_inventory.php (Attributes) — that's where the user wants to land.
        await GotoAsync(Paths.HeroInventory, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while returning to hero inventory.", cancellationToken);
        await ExpandAttributesPanelIfClosedAsync(cancellationToken);

        // Step 6: apply Hide hero / stay-with-troops preference if it differs from current.
        var hideApplied = await ApplyHeroHideModeAsync(hideMode, cancellationToken);
        if (hideApplied) actions.Add($"hide_mode_set={(string.Equals(hideMode, "fight", StringComparison.OrdinalIgnoreCase) ? "fight" : "hide")}");

        var summary = $"Hero status: dead={status.IsDead}, hp={hpPercent?.ToString() ?? "?"}%, adventures={adventureCount}, points={status.UnassignedPoints}, in_village={inVillage}";
        if (heroReturnWaitSeconds is > 0 && actions.Count == 0)
        {
            return $"{summary}. Hero is away. queue_wait_seconds={heroReturnWaitSeconds.Value}";
        }

        if (actions.Count == 0)
        {
            return $"{summary}. No hero action was needed.";
        }

        if (heroReturnWaitSeconds is > 0)
        {
            return $"{summary}. Actions: {string.Join(", ", actions)}. queue_wait_seconds={heroReturnWaitSeconds.Value}";
        }

        return $"{summary}. Actions: {string.Join(", ", actions)}.";
        #endif
    }

    private async Task<int> CountAdventureRowsAsync(CancellationToken cancellationToken)
    {
        return await _page.EvaluateAsync<int>(
            """
            () => document.querySelectorAll('a.gotoAdventure[href*="start_adventure.php"]').length
            """);
    }

    private async Task<int?> ReadHeroHpFromSidebarAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<int?>(
            """
            () => {
              // HP is encoded in the inline width of the bar inside .heroHealthBarBox.
              const bar = document.querySelector('#sidebarBoxHero .heroHealthBarBox .bar, .heroHealthBarBox .bar');
              if (bar) {
                const style = (bar.getAttribute('style') || '').toLowerCase();
                const m = style.match(/width\s*:\s*(\d{1,3})(?:\.\d+)?\s*%/);
                if (m) return Number(m[1]);
              }
              // Fallback: any element with a "health" text label.
              const fallback = document.querySelector('#heroStatus .health, .heroStatus .health, [class*="health" i]');
              const txt = (fallback?.textContent || '').match(/(\d{1,3})/);
              return txt ? Number(txt[1]) : null;
            }
            """);
    }

    public async Task<HeroAttributeSnapshot> ReadHeroAttributeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Notify("ReadHeroAttributeSnapshotAsync started");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var quick = await ReadHeroQuickStatusAsync(
            allowDorf1Fallback: false,
            forceDorf1Reload: false,
            cancellationToken);
        var adventureCount = TryResolveAdventureCount(quick);
        var cachedSnapshot = TryGetCachedHeroAttributeSnapshot();
        if (cachedSnapshot is not null && !quick.HasUnassignedPointsSignal)
        {
            Notify("Hero attribute snapshot served from cache.");
            return cachedSnapshot with { AdventureCount = adventureCount };
        }

        await EnsureHeroInventoryAttributesTabAsync(cancellationToken);
        if (adventureCount is null)
        {
            var sidebar = await ReadHeroSidebarStatusAsync(cancellationToken);
            if (sidebar.AdventureFound)
            {
                adventureCount = Math.Max(0, sidebar.AdventureCount);
            }
        }

        var snapshot = await ReadHeroInventorySnapshotAsync(cancellationToken);
        snapshot = snapshot with { AdventureCount = adventureCount };
        SaveCachedHeroAttributeSnapshot(snapshot);
        Notify(
            $"Hero inventory snapshot: free points={snapshot.FreePoints}, fighting strength={snapshot.FightingStrength}, offence bonus={snapshot.OffenceBonus}, defence bonus={snapshot.DefenceBonus}, resources={snapshot.Resources}, adventures={(snapshot.AdventureCount?.ToString() ?? "?")}.");
        return snapshot;
    }

    private async Task<HeroStatus> ReadHeroStatusAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading hero status.", cancellationToken);
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const parseNumber = (value) => {
                const text = (value || '').replace(/\s+/g, ' ').trim();
                if (!text) return null;
                const match = text.match(/(\d[\d\s.,']*)/);
                if (!match) return null;
                const digits = match[1].replace(/[^\d]/g, '');
                if (!digits) return null;
                const parsed = Number(digits);
                return Number.isFinite(parsed) ? parsed : null;
              };

              const parseTimer = (value) => {
                const text = (value || '').trim();
                if (!text) return null;
                const parts = text.split(':').map(v => Number(v));
                if (parts.some(v => !Number.isFinite(v))) return null;
                if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2];
                if (parts.length === 2) return parts[0] * 60 + parts[1];
                return null;
              };

              const parseInlineTimer = (value, patterns) => {
                const text = (value || '').replace(/\s+/g, ' ').trim();
                if (!text) return null;
                for (const pattern of patterns) {
                  const match = text.match(pattern);
                  if (!match || !match[1]) continue;
                  const parsed = parseTimer(match[1]);
                  if (parsed !== null) return parsed;
                }

                return null;
              };

              const text = (document.body?.innerText || '').toLowerCase();
              const statusMessage = document.querySelector('.heroStatusMessage');
              const statusText = (statusMessage?.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const reviving = /being\s+revived|remaining\s+time|reviv/i.test(statusText)
                && !!statusMessage?.querySelector('.timer, [counting="down"], .heroStatus101Regenerate');
              const dead = /\bdead\b|\btot\b|\bdeceased\b|\bdöd\b/.test(text);

              const effectiveDead = !reviving && (dead || /hero\s+is\s+dead/i.test(statusText));
              const reviveTimerNode = statusMessage?.querySelector('.timer[value], [counting="down"][value], .timer');
              const reviveTimer = reviveTimerNode?.getAttribute('value')
                ? Number(reviveTimerNode.getAttribute('value'))
                : parseTimer(reviveTimerNode?.textContent || '');
              const state = reviving ? 'Reviving' : effectiveDead ? 'Dead' : 'Alive';

              const hp =
                parseNumber(document.querySelector('#health')?.textContent || '')
                ?? parseNumber(document.querySelector('[class*="health" i]')?.textContent || '')
                ?? parseNumber(document.querySelector('[id*="health" i]')?.textContent || '');

              const adventures =
                // Modern Travian: speech-bubble badge on the adventure menu icon (works from any page).
                parseNumber(document.querySelector('a[href*="hero_adventure.php"] .speechBubbleContent')?.textContent || '')
                ?? parseNumber(document.querySelector('a[href*="hero_adventure.php"] .speechBubble')?.textContent || '')
                ?? parseNumber(document.querySelector('a[href*="hero.php?t=3"] .speechBubbleContent')?.textContent || '')
                ?? parseNumber(document.querySelector('a[href*="hero.php?t=3"] .speechBubble')?.textContent || '')
                // Adventures list page: count rows directly.
                ?? (document.querySelectorAll('#adventureListForm tbody tr, table.adventureList tbody tr').length || null)
                ?? parseNumber(document.querySelector('#adventureCount')?.textContent || '')
                ?? parseNumber(document.querySelector('[class*="adventure" i] .badge')?.textContent || '')
                ?? parseNumber(document.querySelector('[id*="adventure" i] .badge')?.textContent || '')
                ?? 0;

              const points =
                parseNumber(document.querySelector('#points')?.textContent || '')
                ?? parseNumber(document.querySelector('[class*="attribute" i] [class*="free" i]')?.textContent || '')
                ?? 0;

              const adventureTimer =
                parseTimer(document.querySelector('.adventure .timer')?.textContent || '')
                ?? parseTimer(document.querySelector('[class*="adventure" i] [class*="timer" i]')?.textContent || '');

              const returnTimer =
                parseTimer(document.querySelector('[class*="return" i] [class*="timer" i]')?.textContent || '')
                ?? parseTimer(document.querySelector('.heroReturn .timer')?.textContent || '')
                ?? parseInlineTimer(document.body?.innerText || '', [
                  /back\s+in\s*:?\s*(\d{1,3}:\d{2}:\d{2})/i,
                  /return\s+in\s*:?\s*(\d{1,3}:\d{2}:\d{2})/i,
                  /arrival\s+in\s*:?\s*\d{1,3}:\d{2}:\d{2}(?:\s*hour)?\s*\|\s*back\s+in\s*:?\s*(\d{1,3}:\d{2}:\d{2})/i
                ]);

              const exists = !!document.querySelector('#heroImage, #heroStatus, [class*="hero" i]');
              return JSON.stringify({
                exists,
                isDead: effectiveDead,
                state,
                hpPercent: hp,
                adventuresAvailable: adventures || 0,
                secondsUntilAdventureReady: adventureTimer,
                secondsUntilReturn: returnTimer,
                reviveRemainingSeconds: Number.isFinite(reviveTimer) ? Math.max(0, Math.trunc(reviveTimer)) : null,
                unassignedPoints: points || 0
              });
            }
            """);

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new HeroStatus();
        }

        var parsed = JsonSerializer.Deserialize<HeroStatusJs>(rawJson);
        if (parsed is null)
        {
            return new HeroStatus();
        }

        return new HeroStatus(
            Exists: parsed.Exists,
            IsDead: parsed.IsDead,
            State: string.IsNullOrWhiteSpace(parsed.State) ? "Unknown" : parsed.State,
            HpPercent: parsed.HpPercent,
            AdventuresAvailable: parsed.AdventuresAvailable ?? 0,
            SecondsUntilAdventureReady: parsed.SecondsUntilAdventureReady,
            SecondsUntilReturn: parsed.SecondsUntilReturn,
            ReviveRemainingSeconds: parsed.ReviveRemainingSeconds,
            UnassignedPoints: parsed.UnassignedPoints ?? 0);
    }

    private async Task<bool> TryReviveHeroAsync(CancellationToken cancellationToken)
    {
        // Revive UI is on the inventory/attributes page on this Travian version. /hero.php opens Appearance.
        await GotoAsync(Paths.HeroInventory, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while trying to revive hero.", cancellationToken);
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const buttons = Array.from(document.querySelectorAll('button, input[type="submit"], a'));
              const candidate = buttons.find(node => {
                const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '')).toLowerCase();
                const cls = (node.className || '').toLowerCase();
                const isRevive = text.includes('revive') || text.includes('resurrect') || text.includes('återuppliva');
                const isGold = text.includes('gold') || text.includes('instant') || cls.includes('gold');
                const disabled = node.hasAttribute('disabled') || cls.includes('disabled');
                return isRevive && !isGold && !disabled;
              });
              if (!candidate) return false;
              candidate.click();
              return true;
            }
            """);
    }

    private void UpdateHeroOintmentAutoUseState(bool enabled)
    {
        if (_lastHeroAutoUseOintmentsEnabled != enabled)
        {
            _lastHeroOintmentMissKey = null;
            _lastHeroAutoUseOintmentsEnabled = enabled;
        }

        if (!enabled)
        {
            _lastHeroOintmentMissKey = null;
        }
    }

    private void ClearHeroOintmentMiss()
    {
        _lastHeroOintmentMissKey = null;
    }

    internal static int CalculateOintmentsToUse(int? currentHpPercent, int minHpForAdventure, int availableOintments)
    {
        if (currentHpPercent is null || availableOintments <= 0)
        {
            return 0;
        }

        var targetHp = Math.Clamp(minHpForAdventure, 1, 100);
        var currentHp = Math.Clamp(currentHpPercent.Value, 0, 100);
        if (currentHp >= targetHp)
        {
            return 0;
        }

        return Math.Min(targetHp - currentHp, availableOintments);
    }

    private async Task<HeroOintmentUseResult> TryUseHeroOintmentsForAdventureAsync(
        int currentHpPercent,
        int minHpForAdventure,
        int adventureCount,
        CancellationToken cancellationToken)
    {
        var retryKey = new HeroOintmentRetryKey(adventureCount, currentHpPercent, minHpForAdventure);
        if (_lastHeroOintmentMissKey == retryKey)
        {
            Notify($"Hero ointment lookup skipped for unchanged state: hp={currentHpPercent}%, adventures={adventureCount}.");
            return HeroOintmentUseResult.Suppressed;
        }

        await GotoAsync(Paths.HeroInventory, cancellationToken);
        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero inventory for ointments.", cancellationToken);

        var info = await ReadHeroOintmentInventoryInfoAsync(cancellationToken);
        if (!info.Found || info.Count <= 0)
        {
            _lastHeroOintmentMissKey = retryKey;
            Notify("Hero ointments not found in inventory. Suppressing repeat inventory checks until hero state changes.");
            return HeroOintmentUseResult.AttemptedWithoutUse;
        }

        var useCount = CalculateOintmentsToUse(currentHpPercent, minHpForAdventure, info.Count);
        if (useCount <= 0)
        {
            ClearHeroOintmentMiss();
            return HeroOintmentUseResult.AttemptedWithoutUse;
        }

        Notify($"Hero ointments found: {info.Count}. Trying to use {useCount}.");
        var clicked = await ClickHeroOintmentItemAsync(info.ItemIndex, cancellationToken);
        if (!clicked)
        {
            _lastHeroOintmentMissKey = retryKey;
            Notify("Hero ointment item was detected but could not be clicked. Suppressing repeat checks for this state.");
            return HeroOintmentUseResult.AttemptedWithoutUse;
        }

        await Task.Delay(500, cancellationToken);
        var confirmed = await ConfirmHeroOintmentUseAsync(useCount, cancellationToken);
        await WaitForNavigationSettledAsync(cancellationToken);
        await Task.Delay(500, cancellationToken);

        var refreshedHp = await ReadHeroHpFromSidebarAsync(cancellationToken);
        if (confirmed || refreshedHp > currentHpPercent)
        {
            ClearHeroOintmentMiss();
            var observedUsed = refreshedHp > currentHpPercent
                ? Math.Min(useCount, refreshedHp.Value - currentHpPercent)
                : useCount;
            Notify($"Hero ointment use completed. HP before={currentHpPercent}%, after={refreshedHp?.ToString() ?? "?"}%.");
            return new HeroOintmentUseResult(observedUsed, LookupAttempted: true, SkippedBySuppression: false);
        }

        _lastHeroOintmentMissKey = retryKey;
        Notify("Hero ointment use could not be confirmed. Suppressing repeat checks for this state.");
        return HeroOintmentUseResult.AttemptedWithoutUse;
    }

    private async Task<HeroOintmentInventoryInfo> ReadHeroOintmentInventoryInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const isVisible = (node) => {
                if (!node) return false;
                const style = window.getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden') return false;
                const rect = node.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
              };
              const readText = (node) => {
                const parts = [
                  node.textContent,
                  node.getAttribute('title'),
                  node.getAttribute('aria-label'),
                  node.getAttribute('data-title'),
                  node.getAttribute('data-tooltip'),
                  node.getAttribute('data-tip'),
                  node.getAttribute('alt'),
                  node.className
                ];
                for (const child of Array.from(node.querySelectorAll('img, [title], [aria-label], [data-title], [data-tooltip], [data-tip], [alt]'))) {
                  parts.push(child.getAttribute('title'));
                  parts.push(child.getAttribute('aria-label'));
                  parts.push(child.getAttribute('data-title'));
                  parts.push(child.getAttribute('data-tooltip'));
                  parts.push(child.getAttribute('data-tip'));
                  parts.push(child.getAttribute('alt'));
                  parts.push(child.className);
                }
                return clean(parts.filter(Boolean).join(' '));
              };
              const matchesOintment = (text) => /(^|[^a-z])(ointment|ointments|salve|salves|salva|salvor|salbe|salben)([^a-z]|$)/i.test(text);
              const parseCount = (node) => {
                const countNode = node.querySelector('.amount, .itemAmount, .count, .number, [class*="amount" i], [class*="count" i]');
                const countText = clean(countNode?.textContent || '');
                let match = countText.match(/(\d+)/);
                if (match) return Number(match[1]) || 0;
                match = clean(node.textContent || '').match(/[x\u00d7]\s*(\d+)|(\d+)\s*[x\u00d7]/i);
                if (match) return Number(match[1] || match[2]) || 1;
                return 1;
              };
              const selectors = [
                '#inventory .item',
                '#items .item',
                '.inventory .item',
                '.heroInventory .item',
                '.hero_inventory .item',
                '[class*="inventory" i] [class*="item" i]',
                '[data-title], [data-tooltip], [data-tip], img[title], img[alt]'
              ];
              const nodes = Array.from(new Set(selectors.flatMap(selector => Array.from(document.querySelectorAll(selector)))));
              const candidates = nodes
                .filter(isVisible)
                .map((node) => ({ node, text: readText(node) }))
                .filter(item => matchesOintment(item.text));
              if (candidates.length === 0) {
                return JSON.stringify({ found: false, count: 0, itemIndex: -1 });
              }
              const picked = candidates[0];
              return JSON.stringify({
                found: true,
                count: parseCount(picked.node),
                itemIndex: candidates.indexOf(picked)
              });
            }
            """);

        return JsonSerializer.Deserialize<HeroOintmentInventoryInfo>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new HeroOintmentInventoryInfo();
    }

    private async Task<bool> ClickHeroOintmentItemAsync(int ointmentIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            """
            (ointmentIndex) => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const isVisible = (node) => {
                if (!node) return false;
                const style = window.getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden') return false;
                const rect = node.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
              };
              const readText = (node) => {
                const parts = [
                  node.textContent,
                  node.getAttribute('title'),
                  node.getAttribute('aria-label'),
                  node.getAttribute('data-title'),
                  node.getAttribute('data-tooltip'),
                  node.getAttribute('data-tip'),
                  node.getAttribute('alt'),
                  node.className
                ];
                for (const child of Array.from(node.querySelectorAll('img, [title], [aria-label], [data-title], [data-tooltip], [data-tip], [alt]'))) {
                  parts.push(child.getAttribute('title'));
                  parts.push(child.getAttribute('aria-label'));
                  parts.push(child.getAttribute('data-title'));
                  parts.push(child.getAttribute('data-tooltip'));
                  parts.push(child.getAttribute('data-tip'));
                  parts.push(child.getAttribute('alt'));
                  parts.push(child.className);
                }
                return clean(parts.filter(Boolean).join(' '));
              };
              const matchesOintment = (text) => /(^|[^a-z])(ointment|ointments|salve|salves|salva|salvor|salbe|salben)([^a-z]|$)/i.test(text);
              const selectors = [
                '#inventory .item',
                '#items .item',
                '.inventory .item',
                '.heroInventory .item',
                '.hero_inventory .item',
                '[class*="inventory" i] [class*="item" i]',
                '[data-title], [data-tooltip], [data-tip], img[title], img[alt]'
              ];
              const nodes = Array.from(new Set(selectors.flatMap(selector => Array.from(document.querySelectorAll(selector)))));
              const candidates = nodes
                .filter(isVisible)
                .filter(node => matchesOintment(readText(node)));
              const item = candidates[ointmentIndex];
              if (!item) return false;
              const target = item.querySelector('button:not([disabled]), a, [role="button"], img') || item;
              target.click();
              return true;
            }
            """,
            ointmentIndex);
    }

    private async Task<bool> ConfirmHeroOintmentUseAsync(int useCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            """
            (useCount) => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const isVisible = (node) => {
                if (!node) return false;
                const style = window.getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden') return false;
                const rect = node.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
              };
              const dialog = Array.from(document.querySelectorAll('.dialog, .modal, .popup, .overlay, [role="dialog"]'))
                .filter(isVisible)
                .reverse()
                .find(node => /ointment|ointments|salve|salves|salva|salvor|salbe|salben/i.test(clean(node.textContent || '')));
              if (!dialog) return false;
              const input = Array.from(dialog.querySelectorAll('input[type="number"], input[type="text"]'))
                .find(node => isVisible(node) && !node.disabled && !node.readOnly);
              if (input) {
                input.value = String(Math.max(1, useCount));
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
              }
              const buttons = Array.from(dialog.querySelectorAll('button, input[type="submit"], input[type="button"], a, div.addHoverClick'))
                .filter(node => isVisible(node) && !node.disabled && !(node.className || '').toString().toLowerCase().includes('disabled'));
              const button = buttons.find(node => {
                const text = clean((node.value || '') + ' ' + (node.textContent || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                return /\b(use|apply|confirm|ok|yes|anwenden|benutzen)\b/.test(text);
              }) || buttons[0];
              if (!button) return false;
              button.click();
              return true;
            }
            """,
            useCount);
    }

    private async Task<bool> HasHeroLevelUpIndicatorAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => !!document.querySelector('.bigSpeechBubble.levelUp')
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private async Task<int> TryAllocateHeroPointsAsync(string priority, CancellationToken cancellationToken)
    {
        Notify("[allocate-v2] TryAllocateHeroPointsAsync entered");
        await EnsureHeroInventoryAttributesTabAsync(cancellationToken);
        // The plus buttons live inside the collapsible panel; expand it so Travian's click
        // handler accepts the input. Read-only flows skip this on purpose (it's a slow toggle).
        await ExpandAttributesPanelIfClosedAsync(cancellationToken);

        // Diagnostic: dump what Travian's DOM exposes for free points so we can see why a "4/4" page reads 0.
        var diag = await _page.EvaluateAsync<string>(
            """
            () => {
              const ap = document.querySelector('#availablePoints');
              const apClass = document.querySelector('.availablePoints');
              const pointsValue = document.querySelector('th.pointsValue');
              const tableExists = !!document.querySelector('#attributesOfHero');
              return JSON.stringify({
                url: location.href,
                tableExists,
                apId: ap ? (ap.textContent || '') : null,
                apClassText: apClass ? (apClass.textContent || '') : null,
                pointsValueText: pointsValue ? (pointsValue.textContent || '').replace(/\s+/g,' ').trim() : null
              });
            }
            """);
        Notify($"[allocate-v2] DOM diag: {diag}");

        var snapshot = await ReadHeroInventorySnapshotAsync(cancellationToken);
        Notify($"Hero free points found: {snapshot.FreePoints}.");
        if (snapshot.FreePoints <= 0)
        {
            return 0;
        }

        var priorities = ParseHeroStatPriority(priority);
        var allocated = 0;
        foreach (var stat in priorities)
        {
            if (allocated >= snapshot.FreePoints)
            {
                break;
            }

            Notify($"Hero attribute prioritized: {GetHeroAttributeDisplayName(stat)}.");
            // Click + until either freePoints exhausted or the add button refuses (becomes .disabled),
            // which is the only reliable "maxed" signal — the row's textContent contains numbers like
            // base Fighting Strength (100) that are NOT the points-used value.
            while (allocated < snapshot.FreePoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clicked = await ClickHeroAttributePlusAsync(stat, cancellationToken);
                if (!clicked)
                {
                    Notify($"Hero attribute maxed or unavailable: {GetHeroAttributeDisplayName(stat)}.");
                    break;
                }

                allocated += 1;
            }
        }

        if (allocated <= 0)
        {
            return 0;
        }

        var saved = await ClickHeroSaveChangesAsync(cancellationToken);
        if (saved)
        {
            InvalidateCachedHeroAttributeSnapshot();
            Notify("Hero points saved.");
        }

        return saved ? allocated : 0;
    }

    internal static IReadOnlyList<string> ParseHeroStatPriority(string value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fighting_strength"] = "fighting_strength",
            ["fighting strength"] = "fighting_strength",
            ["fight"] = "fighting_strength",
            ["strength"] = "fighting_strength",
            ["offence_bonus"] = "offence_bonus",
            ["offence bonus"] = "offence_bonus",
            ["offense_bonus"] = "offence_bonus",
            ["offense bonus"] = "offence_bonus",
            ["offence"] = "offence_bonus",
            ["offense"] = "offence_bonus",
            ["off"] = "offence_bonus",
            ["attack"] = "offence_bonus",
            ["defence_bonus"] = "defence_bonus",
            ["defence bonus"] = "defence_bonus",
            ["defense_bonus"] = "defence_bonus",
            ["defense bonus"] = "defence_bonus",
            ["defence"] = "defence_bonus",
            ["defense"] = "defence_bonus",
            ["def"] = "defence_bonus",
            ["resources"] = "resources",
            ["resource"] = "resources",
            ["production"] = "resources",
        };

        var parsed = (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => map.GetValueOrDefault(item, string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var fallback in new[] { "fighting_strength", "offence_bonus", "defence_bonus", "resources" })
        {
            if (!parsed.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                parsed.Add(fallback);
            }
        }

        return parsed;
    }

    private async Task EnsureHeroInventoryAttributesTabAsync(CancellationToken cancellationToken)
    {
        await GotoAsync(Paths.HeroInventory, cancellationToken);
        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero inventory.", cancellationToken);
        await EnsureLoggedInAsync();

        if (!IsCurrentUrlForPath(Paths.HeroInventory))
        {
            await GotoAsync(Paths.HeroInventory, cancellationToken);
            await WaitForNavigationSettledAsync(cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared after re-opening hero inventory.", cancellationToken);
        }

        var onAttributesTab = await _page.EvaluateAsync<bool>(
            """
            () => {
              const url = window.location.href.toLowerCase();
              const active = document.querySelector('a.tabItem.active, a.active.tabItem');
              if (active && /attribute/i.test(active.textContent || '')) return true;
              return !!document.querySelector('a.setPoint, td.pointsValueSetter.add');
            }
            """);

        if (onAttributesTab)
        {
            return;
        }

        var clicked = await _page.EvaluateAsync<bool>(
            """
            () => {
              const link = Array.from(document.querySelectorAll('a.tabItem, a.tab, a'))
                .find(a => /attribute/i.test(a.textContent || '') && /hero_inventory\.php/i.test(a.getAttribute('href') || ''));
              if (!link) return false;
              link.click();
              return true;
            }
            """);

        if (clicked)
        {
            await WaitForNavigationSettledAsync(cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero attributes tab.", cancellationToken);
        }

        // The attributes table is in DOM regardless of whether the collapsible panel is expanded —
        // we don't block on expansion here. Callers that need to CLICK (e.g. assign points) must
        // call ExpandAttributesPanelIfClosedAsync themselves; pure reads don't.
        var tableReady = await WaitForAttributesTableAsync(cancellationToken, timeoutMs: 4000);
        if (!tableReady)
        {
            Notify($"Hero attributes table missing after tab click — reloading {Paths.HeroInventory}.");
            await GotoAsync(Paths.HeroInventory, cancellationToken);
            await WaitForNavigationSettledAsync(cancellationToken);
            tableReady = await WaitForAttributesTableAsync(cancellationToken, timeoutMs: 6000);
            if (!tableReady)
            {
                Notify($"Hero attributes table still missing after reload. url='{_page.Url}'.");
            }
        }

        // Fire-and-forget: open the panel so the user lands on a visually-expanded panel if they
        // look at the browser. We do NOT wait for confirmation — the read path doesn't need it.
        await TryClickExpandPanelFireAndForgetAsync();
    }

    private async Task TryClickExpandPanelFireAndForgetAsync()
    {
        try
        {
            await _page.EvaluateAsync(
                """
                () => {
                  const sw = document.querySelector('.hero_inventory #attributes img.openedClosedSwitch')
                          || document.querySelector('img.openedClosedSwitch');
                  if (!sw || !sw.classList.contains('switchClosed')) return;
                  const bar = sw.closest('.openCloseSwitchBar') || sw;
                  bar.click();
                }
                """);
        }
        catch (PlaywrightException) { }
        catch (TimeoutException) { }
    }

    private async Task<bool> WaitForAttributesTableAsync(CancellationToken cancellationToken, int timeoutMs)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Travian renders #availablePoints ONLY when free points exist. With 0 free points
            // the span is absent — waiting on it would always burn the full timeout. Accept any
            // of: populated #availablePoints, a row's td.points text, or an attribute input.
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const table = document.querySelector('#attributesOfHero');
                  if (!table) return false;
                  const ap = document.querySelector('#availablePoints');
                  if (ap && (ap.textContent || '').trim().length > 0) return true;
                  if (table.querySelector('td.points')) return true;
                  if (table.querySelector('input[name^="attribute"]')) return true;
                  return false;
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = timeoutMs });
            return true;
        }
        catch (PlaywrightException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task ExpandAttributesPanelIfClosedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The single source of truth for "panel is collapsed" is the `switchClosed` class on
        // `img.openedClosedSwitch`. Travian's toggle script swaps that class on click, so layout
        // and computed-style checks can race the XHR / animation and produce false negatives.
        // We try up to twice — if the first click accidentally closed an already-open panel
        // (rare, but possible if the class hadn't been updated yet), the second click re-opens it.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var clicked = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const sw = document.querySelector('.hero_inventory #attributes img.openedClosedSwitch')
                          || document.querySelector('img.openedClosedSwitch');
                  if (!sw || !sw.classList.contains('switchClosed')) return false;
                  const bar = sw.closest('.openCloseSwitchBar') || sw;
                  bar.click();
                  return true;
                }
                """);

            if (!clicked)
            {
                return;
            }

            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    () => {
                      const sw = document.querySelector('.hero_inventory #attributes img.openedClosedSwitch')
                              || document.querySelector('img.openedClosedSwitch');
                      return !!sw && !sw.classList.contains('switchClosed');
                    }
                    """,
                    null,
                    new PageWaitForFunctionOptions { Timeout = 3000 });
                return;
            }
            catch (PlaywrightException) { }
            catch (TimeoutException) { }
        }

        Notify("Hero attributes panel did not visibly expand after 2 toggle attempts — proceeding anyway.");
    }

    private async Task<HeroAttributeSnapshot> ReadHeroInventorySnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The reader uses querySelector + textContent, which work on collapsed elements too —
        // we deliberately do NOT expand the panel here. Expansion is a multi-second click+animation
        // wait and only matters for writes (clicking +/- point buttons), not reads.
        var ready = await WaitForAttributesTableAsync(cancellationToken, timeoutMs: 5000);
        if (!ready)
        {
            var url = _page.Url;
            var apText = await _page.EvaluateAsync<string?>(
                "() => { const a = document.querySelector('#availablePoints'); return a ? (a.textContent || '') : null; }");
            Notify($"Hero attributes table did not appear in time. url='{url}', availablePointsText='{apText ?? "<null>"}'.");
        }

        string rawJson;
        try
        {
            rawJson = await _page.EvaluateAsync<string>(
                """
                () => {
                  try {
                    const readDigit = (el) => {
                      if (!el) return 0;
                      const m = (el.textContent || '').match(/(\d+)/);
                      return m ? Number(m[1]) || 0 : 0;
                    };
                    // Each attribute row has a fixed id ("attributepower" etc.). Travian shows the
                    // points either as <input value="N"> (when free points exist and are editable)
                    // OR as plain text in <td class="points">N</td> (when no free points are
                    // available). Read whichever is present, in that order.
                    const rowPoints = (rowId) => {
                      const row = document.getElementById(rowId);
                      if (!row) return 0;
                      const input = row.querySelector('input[type="text"][name^="attribute"]');
                      if (input) return Number(input.value) || 0;
                      const td = row.querySelector('td.points');
                      return td ? readDigit(td) : 0;
                    };
                    const parseTimer = (value) => {
                      const text = (value || '').replace(/\s+/g, ' ').trim();
                      if (!text) return null;
                      const parts = text.split(':').map(v => Number(v));
                      if (parts.some(v => !Number.isFinite(v))) return null;
                      if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2];
                      if (parts.length === 2) return parts[0] * 60 + parts[1];
                      return null;
                    };
                    const statusMessage = document.querySelector('.heroStatusMessage');
                    const statusText = (statusMessage?.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                    const reviving = /being\s+revived|remaining\s+time|reviv/i.test(statusText)
                      && !!statusMessage?.querySelector('.timer, [counting="down"], .heroStatus101Regenerate');
                    const dead = !reviving && /hero\s+is\s+dead|\bdead\b|\btot\b|\bdeceased\b/i.test(statusText);
                    const reviveTimerNode = statusMessage?.querySelector('.timer[value], [counting="down"][value], .timer')
                      || document.querySelector('.lineWrapper .inlineIcon.duration .value, .lineWrapper .duration .value');
                    const reviveTimer = reviveTimerNode?.getAttribute?.('value')
                      ? Number(reviveTimerNode.getAttribute('value'))
                      : parseTimer(reviveTimerNode?.textContent || '');

                    return JSON.stringify({
                      ok: true,
                      levelUpAvailable: !!document.querySelector('.bigSpeechBubble.levelUp'),
                      freePoints: readDigit(document.querySelector('#availablePoints')),
                      fightingStrength: rowPoints('attributepower'),
                      offenceBonus: rowPoints('attributeoffBonus'),
                      defenceBonus: rowPoints('attributedefBonus'),
                      resources: rowPoints('attributeproductionPoints'),
                      heroState: reviving ? 'Reviving' : dead ? 'Dead' : 'Alive',
                      reviveRemainingSeconds: Number.isFinite(reviveTimer) ? Math.max(0, Math.trunc(reviveTimer)) : null
                    });
                  } catch (e) {
                    return JSON.stringify({ ok: false, error: String(e && e.message || e) });
                  }
                }
                """);
        }
        catch (Exception ex)
        {
            Notify($"[snapshot] EvaluateAsync threw: {ex.GetType().Name}: {ex.Message}");
            return new HeroAttributeSnapshot();
        }
        Notify($"[snapshot] raw JSON: {rawJson}");

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new HeroAttributeSnapshot();
        }

        // PropertyNameCaseInsensitive is required: JS emits camelCase ("freePoints"), the record is PascalCase ("FreePoints").
        // Without this, every field silently deserializes to its default (0/false).
        return JsonSerializer.Deserialize<HeroAttributeSnapshot>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new HeroAttributeSnapshot();
    }

    private async Task<bool> ClickHeroAttributePlusAsync(string stat, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            """
            (name) => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const labelsByName = {
                fighting_strength: ['fighting strength'],
                offence_bonus: ['offence bonus', 'offense bonus'],
                defence_bonus: ['defence bonus', 'defense bonus'],
                resources: ['resources']
              };

              const row = Array.from(document.querySelectorAll('tr, .attribute, .heroAttribute, .row'))
                .find(item => {
                  const text = clean(item.textContent || '');
                  return (labelsByName[name] || []).some(label => text.includes(label));
                });
              if (!row) return false;

              // Travian renders a sub (-) and add (+) cell per row. The first matching `a.setPoint`
              // in DOM order is the SUB button, which is .disabled when value is 0 — never click that.
              const button = row.querySelector('td.pointsValueSetter.add a.setPoint')
                || Array.from(row.querySelectorAll('a.setPoint')).pop();
              if (!button) return false;
              const cls = clean(button.className || '');
              if (cls.includes('disabled')) return false;
              button.click();
              return true;
            }
            """,
            stat);
    }

    public async Task<bool> SetHeroHideModeOnlyAsync(string hideMode, CancellationToken cancellationToken = default)
    {
        Notify($"SetHeroHideModeOnlyAsync started: requested='{hideMode}'.");
        return await ApplyHeroHideModeAsync(hideMode, cancellationToken);
    }

    private async Task<bool> ApplyHeroHideModeAsync(string hideMode, CancellationToken cancellationToken)
    {
        var desired = string.Equals(hideMode, "fight", StringComparison.OrdinalIgnoreCase) ? "fight" : "hide";
        await EnsureHeroInventoryAttributesTabAsync(cancellationToken);

        var changed = await _page.EvaluateAsync<bool>(
            """
            (desired) => {
              const radios = Array.from(document.querySelectorAll('input[type="radio"][name="attackBehaviour"]'));
              if (radios.length === 0) return false;
              const target = radios.find(r => (r.getAttribute('value') || '').toLowerCase() === desired);
              if (!target) return false;
              if (target.checked) return false;
              target.checked = true;
              target.click();
              target.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            }
            """,
            desired);

        if (!changed)
        {
            return false;
        }

        await ClickHeroSaveChangesAsync(cancellationToken);
        Notify($"Hero hide mode set to '{desired}'.");
        return true;
    }

    private async Task<bool> ClickHeroSaveChangesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var saved = await _page.EvaluateAsync<bool>(
            """
            () => {
              const candidates = Array.from(document.querySelectorAll('button, input[type="submit"], a, .button-container'));
              const target = candidates.find(node => {
                const text = ((node.textContent || '') + ' ' + (node.getAttribute?.('value') || '')).toLowerCase();
                const cls = (node.className || '').toString().toLowerCase();
                const disabled = cls.includes('disabled') || !!node.getAttribute?.('disabled');
                return text.includes('save changes') && !disabled;
              });
              if (!target) return false;
              target.click();
              return true;
            }
            """);

        if (!saved)
        {
            return false;
        }

        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after saving hero points.", cancellationToken);
        return true;
    }

    private string BuildHeroAttributeSnapshotCacheKey()
    {
        return $"{_account.Name}|{_config.BaseUrl.TrimEnd('/')}";
    }

    private HeroAttributeSnapshot? TryGetCachedHeroAttributeSnapshot()
    {
        var key = BuildHeroAttributeSnapshotCacheKey();
        lock (HeroAttributeSnapshotCacheSync)
        {
            return CachedHeroAttributeSnapshotsByKey.TryGetValue(key, out var snapshot)
                ? snapshot
                : null;
        }
    }

    private void SaveCachedHeroAttributeSnapshot(HeroAttributeSnapshot snapshot)
    {
        var key = BuildHeroAttributeSnapshotCacheKey();
        lock (HeroAttributeSnapshotCacheSync)
        {
            CachedHeroAttributeSnapshotsByKey[key] = snapshot with { };
        }
    }

    private void InvalidateCachedHeroAttributeSnapshot()
    {
        var key = BuildHeroAttributeSnapshotCacheKey();
        lock (HeroAttributeSnapshotCacheSync)
        {
            CachedHeroAttributeSnapshotsByKey.Remove(key);
        }
    }

    private static string GetHeroAttributeDisplayName(string stat) => stat switch
    {
        "fighting_strength" => "Fighting strength",
        "offence_bonus" => "Offence bonus",
        "defence_bonus" => "Defence bonus",
        "resources" => "Resources",
        _ => stat,
    };

    private static int GetHeroAttributeValue(HeroAttributeSnapshot snapshot, string stat) => stat switch
    {
        "fighting_strength" => snapshot.FightingStrength,
        "offence_bonus" => snapshot.OffenceBonus,
        "defence_bonus" => snapshot.DefenceBonus,
        "resources" => snapshot.Resources,
        _ => 0,
    };

    private static HeroAttributeSnapshot SetHeroAttributeValue(HeroAttributeSnapshot snapshot, string stat, int value) => stat switch
    {
        "fighting_strength" => snapshot with { FightingStrength = value },
        "offence_bonus" => snapshot with { OffenceBonus = value },
        "defence_bonus" => snapshot with { DefenceBonus = value },
        "resources" => snapshot with { Resources = value },
        _ => snapshot,
    };

    private async Task<(bool Sent, int DurationSeconds, int ReturnSeconds)> TrySendHeroToAdventureAsync(string pickOrder, CancellationToken cancellationToken)
    {
        await OpenHeroAdventuresPageAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening adventures.", cancellationToken);

        // Step 1: pick a row (top or shortest), open the adventure detail page, and report its duration.
        var pickedJson = await _page.EvaluateAsync<string>(
            $$"""
            () => {
              const order = '{{(pickOrder?.ToLowerInvariant() == "top" ? "top" : "shortest")}}';
              const parseDuration = (text) => {
                const m = (text || '').match(/(\d{1,3}):(\d{2}):(\d{2})/);
                if (!m) return Number.MAX_SAFE_INTEGER;
                return Number(m[1]) * 3600 + Number(m[2]) * 60 + Number(m[3]);
              };

              const links = Array.from(document.querySelectorAll('a.gotoAdventure[href*="start_adventure.php"]'));
              if (links.length === 0) return JSON.stringify({ ok: false });

              const entries = links.map(link => {
                const row = link.closest('tr');
                const moveCell = row?.querySelector('td.moveTime');
                const duration = parseDuration(moveCell?.textContent || row?.textContent || '');
                return { link, duration };
              });

              if (order === 'shortest') entries.sort((a, b) => a.duration - b.duration);
              const chosen = entries[0];
              chosen.link.click();
              return JSON.stringify({ ok: true, durationSeconds: chosen.duration, returnSeconds: 0 });
            }
            """);

        var picked = string.IsNullOrWhiteSpace(pickedJson)
            ? null
            : JsonSerializer.Deserialize<AdventurePickJs>(pickedJson);

        if (picked is null || !picked.Ok)
        {
            return (false, 0, 0);
        }

        var duration = picked.DurationSeconds ?? 0;
        var fallbackReturnSeconds = duration > 0 ? duration * 2 : 0;

        // Step 2: confirm on the start_adventure.php page by clicking #start (button[name="s1"]).
        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on adventure detail page.", cancellationToken);
        var fallbackReturnFromDetail = await ReadAdventureReturnSecondsAsync(cancellationToken) ?? fallbackReturnSeconds;
        Notify($"[adventure] picked {pickOrder} adventure, duration={duration}s, hero return ETA={fallbackReturnFromDetail}s");

        var confirmed = await _page.EvaluateAsync<bool>(
            """
            () => {
              const btn = document.querySelector('button#start[name="s1"], button[name="s1"], button#start');
              if (btn && !btn.hasAttribute('disabled')) { btn.click(); return true; }
              const fallback = Array.from(document.querySelectorAll('button[type="submit"], input[type="submit"]'))
                .find(n => !n.hasAttribute('disabled') && /to\s+adventure|start\s+adventure/i.test((n.value || '') + ' ' + (n.textContent || '')));
              if (fallback) { fallback.click(); return true; }
              return false;
            }
            """);

        if (!confirmed)
        {
            return (false, duration, fallbackReturnFromDetail);
        }

        await WaitForNavigationSettledAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after starting hero adventure.", cancellationToken);
        var dispatched = await IsHeroAdventureActivePageAsync(cancellationToken);
        var returnSeconds = await ReadAdventureReturnSecondsAsync(cancellationToken) ?? fallbackReturnFromDetail;
        Notify($"[adventure] dispatch confirmed={dispatched}, hero return ETA={returnSeconds}s");

        // Navigate back to dorf1 after the "To adventure" submit so we don't leave the page on the
        // adventure result view (keeps the page fresh for the next hero status read).
        await EnsureFreshDorf1ForHeroAsync(forceReload: false, cancellationToken);

        return (dispatched, duration, returnSeconds);
    }

    private async Task<int?> ReadAdventureReturnSecondsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await _page.EvaluateAsync<string?>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const text = clean(document.body?.innerText || '');
              if (!text) return null;

              const patterns = [
                /back\s+in\s*:\s*(\d{1,3}:\d{2}:\d{2})/i,
                /back\s+in\s+(\d{1,3}:\d{2}:\d{2})/i,
                /return\s+in\s*:\s*(\d{1,3}:\d{2}:\d{2})/i,
              ];

              for (const pattern of patterns) {
                const match = text.match(pattern);
                if (match && match[1]) return match[1];
              }

              const labels = Array.from(document.querySelectorAll('td, div, span, p'));
              for (const label of labels) {
                const labelText = clean(label.textContent || '');
                if (!/back\s+in|return\s+in/i.test(labelText)) continue;
                const timer = label.querySelector?.('.timer')?.textContent || '';
                const timerText = clean(timer);
                if (/^\d{1,3}:\d{2}:\d{2}$/.test(timerText)) return timerText;
                const inline = labelText.match(/(\d{1,3}:\d{2}:\d{2})/);
                if (inline && inline[1]) return inline[1];
              }

              return null;
            }
            """);

        return ParseDurationToSeconds(raw);
    }

    private async Task<bool> IsHeroAdventureActivePageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const statusText = clean(document.querySelector('.heroStatusMessage')?.textContent || '');
                  if (statusText.includes('hero is on adventure') || statusText.includes('arrival in')) {
                    return true;
                  }

                  const bodyText = clean(document.body?.innerText || '');
                  return bodyText.includes('hero is on adventure')
                    || bodyText.includes('arrival in')
                    || bodyText.includes('back in');
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private sealed class AdventurePickJs
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("durationSeconds")]
        public int? DurationSeconds { get; init; }

        [JsonPropertyName("returnSeconds")]
        public int? ReturnSeconds { get; init; }
    }

    private async Task<bool> IsHeroInActiveVillageAsync(CancellationToken cancellationToken)
    {
        // Sidebar hero status icon on dorf1/dorf2 carries class names like heroStatus50 (in village),
        // heroStatus52/53 (on the way), heroStatus51 (dead). The status text (title/aria) is also localized.
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              // 1) Most reliable: the hero icon class.
              const icon = document.querySelector('.heroStatus, [class*="heroStatus"]');
              if (icon) {
                const cls = (icon.className || '').toString();
                // Treat heroStatus100/heroStatusHome/heroStatus50 as "in this village".
                if (/heroStatus(?:100|50|Home)\b/i.test(cls)) return true;
                // Anything else (52/53 = on the way, 51 = dead) => not in this village.
                if (/heroStatus\d+/i.test(cls)) return false;
              }
              // 2) Fallback: localized status text in the hero sidebar box.
              const box = document.querySelector('#sidebarBoxHero, .heroSidebar, .heroStatusMessage');
              const text = (box?.textContent || '').toLowerCase();
              if (!text) return true; // unknown — don't block
              if (/(home|in this village|in der heimat|på väg|on the way|adventure|äventyr|abenteuer|dead|tot|d[öo]d)/i.test(text)) {
                return /(home|in this village|in der heimat)/i.test(text);
              }
              return true;
            }
            """);
    }

    private sealed record HeroQuickStatus(
        HeroStatus Status,
        HeroSidebarStatusJs Sidebar,
        bool IsInVillage,
        int? HeroHpFromSidebar,
        bool HasUnassignedPointsSignal);

    private sealed record HeroOintmentRetryKey(int AdventureCount, int HpPercent, int MinHpForAdventure);

    private sealed record HeroOintmentUseResult(
        int UsedCount,
        bool LookupAttempted,
        bool SkippedBySuppression)
    {
        public static HeroOintmentUseResult Suppressed { get; } = new(0, LookupAttempted: false, SkippedBySuppression: true);
        public static HeroOintmentUseResult AttemptedWithoutUse { get; } = new(0, LookupAttempted: true, SkippedBySuppression: false);
    }

    private sealed class HeroOintmentInventoryInfo
    {
        [JsonPropertyName("found")]
        public bool Found { get; init; }

        [JsonPropertyName("count")]
        public int Count { get; init; }

        [JsonPropertyName("itemIndex")]
        public int ItemIndex { get; init; } = -1;
    }

    private sealed class HeroSidebarStatusJs
    {
        [JsonPropertyName("statusText")]
        public string? StatusText { get; init; }

        [JsonPropertyName("adventureCount")]
        public int AdventureCount { get; init; }

        [JsonPropertyName("adventureFound")]
        public bool AdventureFound { get; init; }
    }

    private sealed class HeroStatusJs
    {
        [JsonPropertyName("exists")]
        public bool Exists { get; init; }

        [JsonPropertyName("isDead")]
        public bool IsDead { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("hpPercent")]
        public int? HpPercent { get; init; }

        [JsonPropertyName("adventuresAvailable")]
        public int? AdventuresAvailable { get; init; }

        [JsonPropertyName("secondsUntilAdventureReady")]
        public int? SecondsUntilAdventureReady { get; init; }

        [JsonPropertyName("secondsUntilReturn")]
        public int? SecondsUntilReturn { get; init; }

        [JsonPropertyName("reviveRemainingSeconds")]
        public int? ReviveRemainingSeconds { get; init; }

        [JsonPropertyName("unassignedPoints")]
        public int? UnassignedPoints { get; init; }
    }
}
