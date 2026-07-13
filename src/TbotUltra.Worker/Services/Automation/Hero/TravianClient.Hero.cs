using Microsoft.Playwright;
using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Hero surface of the TravianClient facade. The interface list is declared on
// this partial to co-locate the contract with the domain it covers.
public sealed partial class TravianClient : IHeroClient
{
    private const int HeroLowHpRetrySeconds = 60;
    private const int HeroLowHpMaxDeferSeconds = 30 * 60;
    // Fallback defer when the hero looks home/alive but the adventure button is disabled (e.g. a
    // just-revived cooldown). Prevents the loop from completing-and-instantly-re-queueing hero_manage.
    private const int HeroAdventureBlockedRetrySeconds = 5 * 60;
    private HeroOintmentRetryKey? _lastHeroOintmentMissKey;
    private bool? _lastHeroAutoUseOintmentsEnabled;

    public async Task<HeroAdventureDispatchResult> SendHeroOnAdventureAsync(CancellationToken cancellationToken = default)
    {
        Notify("[hero] adventure check starting");

        var quick = await ReadHeroQuickStatusAsync(allowDorf1Fallback: true, forceDorf1Reload: false, cancellationToken);
        var statusText = quick.Sidebar.StatusText;
        var inHomeVillage = quick.IsInVillage;
        var isDead = quick.Status.IsDead || HeroStatusDecision.IsDeadStatusText(statusText);
        var isOnTheWay = (quick.Status.SecondsUntilReturn is > 0) || HeroStatusDecision.IsAwayStatusText(statusText);
        var adventures = ResolveAdventureCount(quick);

        Notify($"[hero] status — '{statusText ?? "(unknown)"}', inHomeVillage={inHomeVillage}, dead={isDead}, onTheWay={isOnTheWay}, hp={quick.Status.HpPercent?.ToString() ?? "?"}%, adventures={adventures}");

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
            var etaText = etaSeconds is int e ? TravianParsing.FormatDuration(e) : "(unknown)";
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

        // Before dispatching, optionally raise the next adventure's danger to hard via the bonus video.
        // IncreaseAdventuresToHardAsync navigates to adventures, skips if already active, and never
        // throws, so the dispatch below continues regardless of its outcome.
        if (_config.IncreaseAdventuresToHard)
        {
            var hardResult = await IncreaseAdventuresToHardForSelectedAdventureAsync("top", cancellationToken);
            Notify($"[hero] increase-adventures-to-hard: {hardResult}");
        }

        if (_config.ReduceAdventureTime)
        {
            var reduceResult = await ReduceAdventuresTimeAsync(cancellationToken);
            Notify($"[hero] reduce-adventure-time: {reduceResult}");
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

        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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

        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after dispatching hero.", cancellationToken);

        // Land back on dorf1 after dispatch so the next status read happens on a fresh page.
        await EnsureFreshDorf1ForHeroAsync(forceReload: false, cancellationToken);

        var returnText = returnSeconds is int rs ? TravianParsing.FormatDuration(rs) : "(unknown)";
        Notify($"[hero] adventure dispatched — return in {returnText}");

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

        if (_session.LogValueChanged("adv-page", sidebar.AdventureCount.ToString()))
        {
            Notify($"Adventures on current page: {sidebar.AdventureCount}.");
        }
        // We're on dorf1 here; cheaply read the hero home village + state from the hero widget. If a
        // dead/reviving icon no longer exposes the home-village link, fall back to attributes once so the
        // dashboard can still mark the correct village.
        await NotifyHeroHomeFromDorf1Async(cancellationToken);
        return sidebar.AdventureCount;
    }

    // Cheap, no-navigation probe used by the periodic desktop refresh. Official Travian shows this
    // icon on ordinary pages when the hero has unspent attribute points.
    public async Task<bool> HasHeroLevelUpIndicatorOnCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => !!document.querySelector('i.levelUp.show')
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    // Reads the hero home village + away/dead state from the dorf1 hero widget (the rally-point link in the
    // hero box points to the hero's HOME village; the icon class shows the state). Emits a [herohome] log
    // line the desktop parses. Dead/reviving Official widgets can have an empty href, so those states fall
    // back to /hero/attributes to resolve the village name.
    private async Task NotifyHeroHomeFromDorf1Async(CancellationToken cancellationToken)
    {
        try
        {
            var raw = await _page.EvaluateAsync<string?>(
                """
                () => {
                  const clean = (v) => (v || '')
                    .replace(/[‪-‮⁦-⁩‎‏]/g, '')
                    .replace(/−/g, '-')
                    .replace(/\s+/g, ' ')
                    .replace(/\s*\(?-?\d+\s*[|｜]\s*-?\d+\)?\s*$/, '')
                    .trim();
                  const widget = document.querySelector('.heroStatus a[href*="build.php"][href*="id=39"]')
                              || document.querySelector('a[href*="build.php"][href*="id=39"]')
                              || document.querySelector('.heroStatus a')
                              || document.querySelector('.heroStatus span')
                              || document.querySelector('.heroStatus');
                  const icon = widget?.querySelector?.('i') || document.querySelector('.heroStatus i');
                  const cls = icon ? String(icon.className || '').toLowerCase() : '';
                  const m = (widget?.getAttribute?.('href') || '').match(/newdid=(\d+)/);
                  const did = m ? m[1] : null;
                  // Canonical state signals from the top-bar/sidebar hero status. A travelling hero
                  // (adventure/attack/returning) shows a heroRunning/statusRunning icon or an arrival
                  // countdown (.timerReact); a hero standing in its home village shows a heroHome icon.
                  // These are more reliable than the single widget-icon class, which on official Travian
                  // can keep a heroHome-like class even while the hero is away — which left the dashboard
                  // icon stuck on green (home) during adventures.
                  const runningSignal = !!document.querySelector('i.heroRunning, [class*="heroRunning"], [class*="statusRunning"]')
                    || !!document.querySelector('.heroStatus .timerReact, .heroState .timerReact');
                  const homeSignal = !!document.querySelector('i.heroHome, [class*="heroHome"]');
                  // Reviving (<i class="heroReviving">) is its own state (orange), distinct from dead (red).
                  const reviving = /reviv/.test(cls) || !!document.querySelector('i.heroReviving, [class*="heroReviving"]');
                  const dead = !reviving && (/dead|status101/.test(cls) || !!document.querySelector('i.heroDead, [class*="heroDead"]'));
                  let away = runningSignal || /running|away|onadventure|status5/.test(cls);
                  // Only trust the "home" signal when there is no active travel signal.
                  if (homeSignal && !runningSignal) away = false;
                  let name = null;
                  if (did) {
                    const entry = document.querySelector('.listEntry.village[data-did="' + did + '"]')
                               || document.querySelector('[data-did="' + did + '"]');
                    if (entry) {
                      const nameEl = entry.querySelector('.villageName .name, .name, .villageName');
                      if (nameEl) name = clean(nameEl.textContent);
                    }
                  }
                  if (!name && !(dead || reviving)) return null;
                  return JSON.stringify({ name: name, away: away, dead: dead, reviving: reviving });
                }
                """);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var away = root.TryGetProperty("away", out var a) && a.GetBoolean();
            var dead = root.TryGetProperty("dead", out var d) && d.GetBoolean();
            var reviving = root.TryGetProperty("reviving", out var r) && r.GetBoolean();

            if (string.IsNullOrWhiteSpace(name))
            {
                if (!dead && !reviving)
                {
                    return;
                }

                Notify("[herohome] home village missing for dead/reviving hero; reading hero attributes.");
                await GotoAsync(HeroAttributesPath, cancellationToken);
                await WaitForPageReadyAsync(cancellationToken);
                await PauseForManualStepIfVisibleAsync("Manual verification appeared while resolving hero home village.", cancellationToken);
                await EnsureLoggedInAsync(cancellationToken: cancellationToken);

                var heroHome = await ReadHeroHomeVillageInfoAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(heroHome.Name))
                {
                    Notify("[herohome] hero attributes did not expose a home village name.");
                    return;
                }

                name = heroHome.Name;
                away = heroHome.Away;
            }

            Notify($"[herohome] away={(away ? "true" : "false")} dead={(dead ? "true" : "false")} reviving={(reviving ? "true" : "false")} name={name.Trim()}");
        }
        catch
        {
            // Best-effort: the dashboard keeps the last-known home village.
        }
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
        return HeroStatusDecision.ResolveAdventureCount(
            quick.Sidebar.AdventureFound,
            quick.Sidebar.AdventureCount,
            quick.Status.AdventuresAvailable);
    }

    private static int? TryResolveAdventureCount(HeroQuickStatus quick)
    {
        return HeroStatusDecision.TryResolveAdventureCount(
            quick.Sidebar.AdventureFound,
            quick.Sidebar.AdventureCount,
            quick.Status.Exists,
            quick.Status.AdventuresAvailable);
    }

    private async Task EnsureFreshDorf1ForHeroAsync(bool forceReload, CancellationToken cancellationToken)
    {
        var onDorf1 = IsCurrentUrlForPath(Paths.Resources);
        Notify($"[hero:verbose] EnsureFreshDorf1ForHero — onDorf1={onDorf1}, forceReload={forceReload}");
        if (!onDorf1)
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening dorf1 for hero check.", cancellationToken);
        await EnsureLoggedInAsync();
        if (!forceReload || !onDorf1)
        {
            return;
        }

        Notify("[hero:verbose] reloading dorf1 to refresh hero sidebar");
        await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded })
            .WaitAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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

                  if (!found) {
                    // Official Travian (T4.6): the adventure indicator is an anchor to
                    // /hero/adventures with the count in a .content child (e.g. "99+").
                    const adv = document.querySelector('a.adventure[href*="/hero/adventures"], #heroV2 a[href*="/hero/adventures"], a[href*="/hero/adventures"]');
                    if (adv) {
                      found = true;
                      const contentEl = adv.querySelector('.content') || adv;
                      const parsed = parseInt(clean(contentEl.textContent || '').replace(/[^\d]/g, ''), 10);
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
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const candidate =
                document.querySelector('button.adventureWhite, button.layoutButton.adventureWhite')
                || document.querySelector('a.adventure[href*="/hero/adventures"], a[href*="/hero/adventures"]')
                || Array.from(document.querySelectorAll('button')).find(b => /adventure/i.test(b.className || ''));
              if (!candidate) return false;
              if (candidate.hasAttribute('disabled') || /\bdisabled\b/.test(candidate.className || '')) return false;
              candidate.click();
              return true;
            }
            """);
    }

    private async Task OpenAdventureListWithFallbackAsync(CancellationToken cancellationToken)
    {
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening adventures list.", cancellationToken);

        if (await HasAdventureEntryOnPageAsync(cancellationToken))
        {
            return;
        }

        await GotoAsync(HeroAdventuresPath, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on hero adventures page.", cancellationToken);
        if (await HasAdventureEntryOnPageAsync(cancellationToken))
        {
            return;
        }

        Notify("[hero:verbose] no adventure rows found after opening hero adventures page.");
    }

    private async Task OpenHeroAdventuresPageAsync(CancellationToken cancellationToken)
    {
        if (await IsHeroAdventuresPageAsync(cancellationToken))
        {
            return;
        }

        try
        {
            await GotoAsync(HeroAdventuresPath, cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            await PauseForManualStepIfVisibleAsync("Manual verification appeared on hero adventures page.", cancellationToken);
            if (await IsHeroAdventuresPageAsync(cancellationToken))
            {
                return;
            }
        }
        catch (Exception ex) when (IsTransientExecutionContextException(ex))
        {
            Notify($"Hero adventures page hit transient navigation issue; retrying {HeroAdventuresPath}. {ex.Message}");
        }

        await GotoAsync(HeroAdventuresPath, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on hero adventures page.", cancellationToken);
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
                  if (url.includes('/hero/adventures')) {
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
                    || text.includes('explore');
              });
            }
            """);
    }

    private async Task<bool> ClickAdventureSubmitButtonAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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

              const submit = Array.from(document.querySelectorAll('button, input[type="submit"]'))
                .find(node => {
                  if (isDisabled(node)) return false;
                  const text = ((node.value || '') + ' ' + (node.textContent || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                  return text.includes('to adventure') || text.includes('to the adventure') || text.includes('continue');
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
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const isDisabled = (node) => {
                if (!node) return true;
                if (node.hasAttribute && node.hasAttribute('disabled')) return true;
                const cls = (node.className || '').toString().toLowerCase();
                return cls.includes('disabled') || cls.includes('inactive');
              };

              // Strategy 1: direct match on adventure detail link.
              const direct = document.querySelector(
                '#adventureListForm a[href*="kid="], a[href*="/hero/adventures"]'
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
                    || text.includes('explore')
                    || href.includes('action=start')
                    || href.includes('/hero/adventures');
              });
              if (!target) return false;
              target.click();
              return true;
            }
            """);
    }

    public async Task<bool> CheckAndReviveDeadHeroOnCurrentPageAsync(bool autoRevive, CancellationToken cancellationToken = default)
    {
        // Lightweight check from whatever page we are currently on. The dead hero is shown either by the
        // sidebar speech bubble (<div class="bigSpeechBubble dead">) or the top-bar hero status icon
        // (<div class="heroStatus">...<i class="heroDead">), depending on the page — accept either.
        var isDead = await _page.EvaluateAsync<bool>(
            "() => !!document.querySelector('.bigSpeechBubble.dead, .heroStatus i.heroDead, i.heroDead, [class*=\"heroDead\"]')");
        if (!isDead)
        {
            return false;
        }

        Notify("[hero] dead — bigSpeechBubble.dead detected on current page");
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

    // Lightweight current-page probe (no navigation): the top-bar hero status shows an
    // <i class="heroReviving"> icon on every page while the hero regenerates. The periodic refresh uses
    // this to release a hero_manage that was deferred for the full revive time when the user revives the
    // hero early (e.g. with a bucket), so adventures resume without waiting out the original countdown.
    public async Task<bool> IsHeroRevivingOnCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            "() => !!document.querySelector('.heroStatus i.heroReviving, i.heroReviving, [class*=\"heroReviving\"]')");
    }

    private async Task<bool> ReviveHeroOnInventoryAsync(CancellationToken cancellationToken)
    {
        Notify("[hero] revive flow starting (attributes page)");
        await GotoAsync(HeroAttributesPath, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero attributes.", cancellationToken);

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
        var reviveDurationSeconds = TravianParsing.ParseDurationToSeconds(reviveDurationRaw);

        // Click the revive button using direct selector first, then a text-based fallback.
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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
            Notify($"Revive duration detected: {TravianParsing.FormatDuration(reviveWaitSeconds)}. Starting countdown.");
            for (var remaining = reviveWaitSeconds; remaining > 0; remaining--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Notify($"Revive countdown: {remaining}s remaining.");
                await Task.Delay(1000, cancellationToken);
            }

            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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
        Notify("[hero:verbose] ReadHeroReturnFromRallyPoint starting");
        await GotoAsync(RallyPointTroopsPath, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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

    private async Task<int?> ReadHeroReturnSecondsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawJson = await _page.EvaluateAsync<string?>(
            """
            () => {
              const clean = (v) => (v || '').replace(/\s+/g, ' ').trim();
              const text = clean(document.body?.innerText || '');
              const heroState = document.querySelector('.heroState');
              const heroStateText = clean(heroState?.textContent || '');
              const awayText = heroStateText || text;
              const isOutboundAdventure = /on its way to an adventure/i.test(awayText);
              const isReturningHome = /on its way back to the village/i.test(awayText);
              const multiplier = isOutboundAdventure && !isReturningHome ? 2 : 1;
              const pack = (value) => value ? JSON.stringify({ value, multiplier }) : null;
              const findDuration = (value) => {
                const match = value.match(/back\s*in[^\d]*(\d{1,2}:\d{2}(?::\d{2})?)/i)
                  || value.match(/arrival\s*in[^\d]*(\d{1,2}:\d{2}(?::\d{2})?)/i);
                return match?.[1] || null;
              };
              const heroStateTimer = heroState?.querySelector('.timerReact, .timer');
              if (heroStateTimer) return pack(clean(heroStateTimer.textContent || ''));
              const heroStateDuration = findDuration(heroStateText);
              if (heroStateDuration) return pack(heroStateDuration);
              // Official Travian (T4.6): the hero return/arrival countdown is a span.timerReact.
              const timerReact = document.querySelector('span.timerReact, .timerReact');
              if (timerReact) return pack(clean(timerReact.textContent || ''));
              const timer = document.querySelector('.heroReturn .timer, [class*="return" i] [class*="timer" i]');
              if (timer) return pack(clean(timer.textContent || ''));
              const match = findDuration(text);
              if (match) return pack(match);
              return null;
            }
            """);

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var raw = root.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;
            var seconds = TravianParsing.ParseDurationToSeconds(raw);
            if (seconds is null)
            {
                return null;
            }

            var multiplier = root.TryGetProperty("multiplier", out var multiplierProp)
                && multiplierProp.TryGetInt32(out var parsedMultiplier)
                ? Math.Max(1, parsedMultiplier)
                : 1;
            return seconds.Value * multiplier;
        }
        catch (JsonException)
        {
            return TravianParsing.ParseDurationToSeconds(rawJson);
        }
    }

    /// <summary>
    /// When the hero is on an adventure, opens the adventures page and reads the return ETA so the
    /// loop can defer instead of re-querying every tick. Official Travian (T4.6) shows the countdown
    /// as "Arrival in <span class='timerReact'>00:04:20</span>" on /hero/adventures.
    /// </summary>
    private async Task<int?> ReadHeroReturnEtaWhenAwayAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrentUrlForPath(HeroAdventuresPath))
            {
                await GotoAsync(HeroAdventuresPath, cancellationToken);
                await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
                await EnsureLoggedInAsync();
            }

            // The away countdown (.heroState span.timerReact / "Arrival in 00:05:30") is
            // React-rendered and not in the DOM immediately after navigation. Wait for it to
            // appear, otherwise the read returns null and the caller falls back to a flat guess.
            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    () => !!document.querySelector('.heroState .timerReact, span.timerReact, .timerReact')
                       || /arrival\s+in\s+\d/i.test(document.body?.innerText || '')
                    """,
                    null,
                    new PageWaitForFunctionOptions { Timeout = 5000 });
            }
            catch (TimeoutException)
            {
            }

            return await ReadHeroReturnSecondsAsync(cancellationToken);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
    }

    // Official Travian (T4.6): the hero HP percent is shown on /hero/attributes as an integer
    // percent in a .value div (e.g. "96%"); attribute bonuses there are decimals like "0.0%".
    private async Task<int?> ReadHeroHpPercentOfficialAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GotoAsync("/hero/attributes", cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            await EnsureLoggedInAsync();

            await WaitForOfficialHeroHpRenderAsync(cancellationToken);

            var hp = await _page.EvaluateAsync<int?>(
                """
                () => {
                  const parsePercent = (value) => {
                    const text = (value || '')
                      .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                      .replace(/\s+/g, ' ')
                      .trim();
                    const match = text.match(/(?:^|[^\d.])(\d{1,3})(?:\s*%| percent\b| hp\b)/i);
                    if (!match) return null;
                    const parsed = Number(match[1]);
                    return Number.isFinite(parsed) && parsed >= 0 && parsed <= 100 ? parsed : null;
                  };

                  const readNode = (node) => {
                    if (!node) return null;
                    const parts = [
                      node.textContent || '',
                      node.getAttribute?.('title') || '',
                      node.getAttribute?.('aria-label') || '',
                      node.getAttribute?.('data-tooltip') || '',
                      node.getAttribute?.('data-tooltip-data') || '',
                      node.getAttribute?.('value') || ''
                    ];
                    for (const part of parts) {
                      const parsed = parsePercent(part);
                      if (parsed !== null) return parsed;
                    }

                    const style = node.getAttribute?.('style') || '';
                    const styleMatch = style.match(/(?:width|height)\s*:\s*(\d{1,3})(?:\.\d+)?\s*%/i);
                    if (styleMatch) {
                      const parsed = Number(styleMatch[1]);
                      if (Number.isFinite(parsed) && parsed >= 0 && parsed <= 100) return parsed;
                    }

                    return null;
                  };

                  const readFirst = (nodes) => {
                    for (const node of nodes) {
                      const parsed = readNode(node);
                      if (parsed !== null) return parsed;
                    }
                    return null;
                  };

                  const healthSelectors = [
                    '.heroHealthBarBox',
                    '.heroHealthBarBox .bar',
                    '.heroHealthBarBox .value',
                    '[class*="heroHealth" i]',
                    '[class*="attributeHealth" i]',
                    '[class*="health" i]',
                    '[id*="health" i]',
                    '[title*="health" i]',
                    '[aria-label*="health" i]',
                    '[data-tooltip*="health" i]'
                  ];

                  const direct = readFirst(Array.from(document.querySelectorAll(healthSelectors.join(','))));
                  if (direct !== null) return direct;

                  const healthIcon = document.querySelector('i[class*="attributeHealth" i], [class*="attributeHealth" i], [class*="heroHealth" i]');
                  const healthBox = healthIcon?.closest('.attributeBox')
                    || healthIcon?.closest('.stats')
                    || healthIcon?.closest('tr, .attribute, .attributeRow, li, section');
                  const boxed = healthBox
                    ? readFirst(Array.from(healthBox.querySelectorAll('.value, [title], [aria-label], [style], [data-tooltip], [data-tooltip-data]')))
                    : null;
                  if (boxed !== null) return boxed;

                  const healthName = Array.from(document.querySelectorAll('.name, [class*="name" i]'))
                    .find(node => /\b(health|hit points|hp)\b/i.test(node.textContent || ''));
                  const namedBox = healthName?.closest('.attributeBox')
                    || healthName?.closest('.stats')
                    || healthName?.closest('tr, .attribute, .attributeRow, li, section');
                  const named = namedBox
                    ? readFirst(Array.from(namedBox.querySelectorAll('.value, [title], [aria-label], [style], [data-tooltip], [data-tooltip-data]')))
                    : null;
                  if (named !== null) return named;

                  const labelledRows = Array.from(document.querySelectorAll('tr, li, .attributeBox, .attributeRow, .attribute, section, div'))
                    .filter(node => /\b(health|hit points|hp)\b/i.test(node.textContent || ''));
                  const labelled = readFirst(labelledRows);
                  if (labelled !== null) return labelled;

                  const filling = document.querySelector('.heroHealthBarBox .filling, .heroHealthBarBox .bar, [class*="health" i] .filling, [class*="health" i] .bar');
                  const fillingParsed = readNode(filling);
                  if (fillingParsed !== null) return fillingParsed;

                  return null;
                }
                """);
            if (hp is null)
            {
                await LogOfficialHeroHpDiagnosticsAsync(cancellationToken);
            }

            Notify($"[hero] official HP read: {(hp?.ToString() ?? "unknown")}%");
            return hp;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
    }

    private async Task WaitForOfficialHeroHpRenderAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const hasHealthSignal = !!document.querySelector(
                    '.attributeBox [class*="attributeHealth" i], [class*="heroHealth" i], .heroHealthBarBox, [class*="health" i], [id*="health" i], [title*="health" i], [aria-label*="health" i]'
                  );
                  const hasAttributeSignal = !!document.querySelector('#availablePoints, .attributeBox, .attributeRow, .value');
                  const body = document.body?.innerText || '';
                  const healthValueReady = Array.from(document.querySelectorAll('.attributeBox, .stats, tr, .attributeRow, .attribute'))
                    .some(node => /\b(health|hit points|hp)\b/i.test(node.textContent || '') && /\d{1,3}[\u200e\u200f\u202a-\u202e\u2066-\u2069\s]*%/.test(node.textContent || ''));
                  return healthValueReady || hasHealthSignal || (hasAttributeSignal && /\b(health|hit points|hp|attributes)\b/i.test(body));
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 8000 }).WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            Notify("[hero] official HP render wait timed out; trying read anyway.");
        }
    }

    private async Task LogOfficialHeroHpDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var diagnostics = await _page.EvaluateAsync<string>(
                """
                () => JSON.stringify({
                  url: window.location.href,
                  valueCount: document.querySelectorAll('.value').length,
                  healthCount: document.querySelectorAll('[class*="health" i], [id*="health" i], [title*="health" i], [aria-label*="health" i]').length,
                  availablePointsText: document.querySelector('#availablePoints')?.textContent || null,
                  bodyHasHealthText: /\b(health|hit points|hp)\b/i.test(document.body?.innerText || '')
                })
                """);
            Notify($"[hero] official HP diagnostics: {diagnostics}");
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"[hero] official HP diagnostics skipped: {ex.Message}");
        }
    }

    // Estimate how long until the hero regenerates from its current HP back up to the threshold,
    // but re-check periodically because manual hero actions can change HP/status outside the bot.
    private static int ComputeHeroHpWaitSeconds(int? hpPercent, int thresholdPercent, int regenPerDayPercent)
    {
        const int Fallback = 600;
        if (hpPercent is not int hp || regenPerDayPercent <= 0)
        {
            return Fallback;
        }

        var deficit = thresholdPercent - hp;
        if (deficit <= 0)
        {
            return Fallback;
        }

        var hours = deficit * 24.0 / regenPerDayPercent;
        var seconds = (int)Math.Ceiling(hours * 3600.0);
        return Math.Clamp(seconds, 60, HeroLowHpMaxDeferSeconds);
    }

    public async Task<string> ManageHeroAsync(
        int minHpForAdventure,
        bool autoRevive,
        bool autoAssignPoints,
        bool autoUseOintments,
        string statPriority,
        string adventurePickOrder = "shortest",
        int heroHpRegenPerDayPercent = 100,
        CancellationToken cancellationToken = default)
    {
        Notify("[hero] manage starting (revive + points + adventure)");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        UpdateHeroOintmentAutoUseState(autoUseOintments);

        // Hero/adventure/status signals live in the global sidebar on normal Travian pages, including
        // dorf2 and build pages. Read the current page first so a no-action result does not cause an
        // artificial dorf2 -> dorf1 navigation. ReadHeroQuickStatusAsync keeps dorf1 as a fallback when
        // the current page has no trustworthy hero signals (login/interstitial/incomplete page).
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

        if ((status.IsDead || HeroStatusDecision.IsDeadStatusText(quick.Sidebar.StatusText)) && autoRevive)
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
            Notify("[hero] level up — unassigned points signal detected");
        }

        if (autoAssignPoints && quick.HasUnassignedPointsSignal)
        {
            var allocated = await TryAllocateHeroPointsAsync(statPriority, cancellationToken);
            if (allocated > 0)
            {
                actions.Add($"points_allocated={allocated}");
            }
        }

        var minHpThreshold = Math.Clamp(minHpForAdventure, 1, 100);

        // The sidebar can miss HP, but /hero/attributes shows it.
        // Read HP before opening /hero/adventures so low-HP decisions do not require an extra
        // adventures -> attributes -> adventures round trip.
        if (adventureHintCount > 0 && inVillage && !status.IsDead && hpPercent is null)
        {
            hpPercent = await ReadHeroHpPercentOfficialAsync(cancellationToken);
        }

        var heroReturnWaitSeconds = status.SecondsUntilReturn;
        var adventureCount = adventureHintCount;
        if (adventureHintCount > 0 && !inVillage)
        {
            await OpenHeroAdventuresPageAsync(cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared on adventures page.", cancellationToken);
            adventureCount = await CountAdventureRowsAsync(cancellationToken);
            status = await ReadHeroStatusAsync(cancellationToken);
            hpPercent ??= status.HpPercent;
            heroReturnWaitSeconds = status.SecondsUntilReturn;
        }

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

        // The sidebar sometimes fails to expose HP immediately after the hero returns. If the sidebar
        // says the hero is home, do not defer 10 minutes on unknown HP; try to dispatch instead.
        // A reviving hero (orange revive timer) can read as "alive, home" on the attribute page while its
        // adventure button is disabled. Trying anyway only wastes a danger-video watch and then loops on
        // "adventure_not_clickable". Treat reviving like away: never attempt the adventure, defer instead.
        var isReviving = string.Equals(status.State, "Reviving", StringComparison.OrdinalIgnoreCase)
            || status.ReviveRemainingSeconds is > 0;
        var canSendByHp = !status.IsDead
            && !isReviving
            && (hpPercent.HasValue
                ? hpPercent.Value >= minHpThreshold
                : inVillage);
        var hpTooLow = false;

        if (isReviving)
        {
            actions.Add("adventure_skipped_hero_reviving");

            // If we only saw the reviving icon (e.g. status read on dorf1) without the countdown, open the
            // attributes page once — the "Remaining revival time" timer lives in the .heroRevive box there —
            // so we can defer by the hero's real revive ETA instead of blind-polling every few minutes.
            if (status.ReviveRemainingSeconds is not > 0)
            {
                await GotoAsync(HeroAttributesPath, cancellationToken);
                await WaitForPageReadyAsync(cancellationToken);
                status = await ReadHeroStatusAsync(cancellationToken);
            }

            heroReturnWaitSeconds ??= status.ReviveRemainingSeconds is > 0
                ? status.ReviveRemainingSeconds
                : HeroAdventureBlockedRetrySeconds;
            Notify("[hero] reviving — deferring until revive completes: "
                + (status.ReviveRemainingSeconds is > 0
                    ? TravianParsing.FormatDuration(status.ReviveRemainingSeconds.Value)
                    : "unknown (5m fallback)"));
        }
        else if (adventureCount > 0 && canSendByHp && inVillage)
        {
            // Optionally raise the next adventure's danger to hard (bonus video) before dispatching.
            // Self-skips if already active and never throws, so dispatch proceeds regardless.
            if (_config.IncreaseAdventuresToHard)
            {
                var hardResult = await IncreaseAdventuresToHardForSelectedAdventureAsync(adventurePickOrder, cancellationToken);
                Notify($"[hero] increase-adventures-to-hard: {hardResult}");
                actions.Add(hardResult.Contains("already hard", StringComparison.OrdinalIgnoreCase)
                    ? "increase_danger_to_hard_skipped_already_hard"
                    : "increase_danger_to_hard");
            }

            if (_config.ReduceAdventureTime)
            {
                var reduceResult = await ReduceAdventuresTimeAsync(cancellationToken);
                Notify($"[hero] reduce-adventure-time: {reduceResult}");
                actions.Add("reduce_adventure_time");
            }

            var (sent, durationSeconds, returnSeconds) = await TrySendHeroToAdventureAsync(adventurePickOrder, cancellationToken);
            heroReturnWaitSeconds = returnSeconds > 0 ? returnSeconds : durationSeconds > 0 ? durationSeconds * 2 : null;
            if (sent)
            {
                actions.Add($"adventure_sent({adventurePickOrder},duration={durationSeconds}s,return_eta={heroReturnWaitSeconds ?? 0}s)");
            }
            else
            {
                actions.Add("adventure_not_clickable");
                // Button disabled even though the hero looks home/alive (e.g. just-revived cooldown).
                // Defer instead of completing, so the loop does not instantly re-queue (and re-watch the
                // danger video) in a tight loop that never resolves.
                heroReturnWaitSeconds ??= HeroAdventureBlockedRetrySeconds;
            }
        }
        else if (!inVillage && !status.IsDead)
        {
            actions.Add("adventure_skipped_hero_away");
            // The hero is away (on an adventure/attack). Read the return ETA — regardless of the
            // adventure count — so the loop defers until the hero is home instead of re-queueing
            // hero_manage every tick, and the dashboard shows the return timer instead of "Ready".
            if (heroReturnWaitSeconds is not > 0)
            {
                heroReturnWaitSeconds = await ReadHeroReturnEtaWhenAwayAsync(cancellationToken);
            }
        }
        else if (adventureCount > 0 && !canSendByHp)
        {
            actions.Add($"adventure_skipped_hp_too_low(hp={hpPercent?.ToString() ?? "?"}%)");
            hpTooLow = true;
        }

        var pointsText = quick.HasUnassignedPointsSignal
            ? (status.UnassignedPoints > 0 ? status.UnassignedPoints.ToString() : "signal")
            : status.UnassignedPoints.ToString();
        var summary = $"Hero status: dead={status.IsDead}, hp={hpPercent?.ToString() ?? "?"}%, adventures={adventureCount}, points={pointsText}, in_village={inVillage}";
        // When the hero is away (not dead), always defer by the return ETA (or a sane fallback) so the
        // loop does not re-run hero_manage every second and the dashboard shows the return timer
        // instead of "Ready" — independent of the adventure count read while away.
        if (!inVillage && !status.IsDead)
        {
            var awaitSeconds = heroReturnWaitSeconds is > 0 ? heroReturnWaitSeconds.Value : 300;
            return $"{summary}. Hero is away. queue_wait_seconds={awaitSeconds}";
        }
        // Too little HP to send safely. Defer the hero group by the estimated time to regenerate
        // back to the threshold (from the configured regen %/day), falling back to 10 minutes.
        if (hpTooLow)
        {
            var waitSeconds = ComputeHeroHpWaitSeconds(hpPercent, minHpThreshold, heroHpRegenPerDayPercent);
            return $"{summary}. Hero HP too low to send. queue_wait_seconds={waitSeconds}";
        }
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

    }

    public async Task<string> SpendHeroAttributePointsAsync(
        string statPriority,
        CancellationToken cancellationToken = default)
    {
        Notify("[hero] spend attribute points starting");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var allocated = await TryAllocateHeroPointsAsync(statPriority, cancellationToken);
        return allocated > 0
            ? $"Hero attribute points spent: {allocated}."
            : "Hero attribute points: no points available.";
    }

    private async Task<int> CountAdventureRowsAsync(CancellationToken cancellationToken)
    {
        return await _page.EvaluateAsync<int>(
            """
            () => {
              const official = document.querySelectorAll('table.adventureList tbody tr, #adventureListForm tbody tr').length;
              if (official > 0) return official;
              // Last resort: the sidebar adventure badge count (a.adventure -> .content, e.g. "3").
              const badge = document.querySelector('a.adventure[href*="/hero/adventures"] .content, a[href*="/hero/adventures"] .content');
              if (badge) {
                const n = parseInt((badge.textContent || '').replace(/[^\d]/g, ''), 10);
                if (Number.isFinite(n)) return n;
              }
              return 0;
            }
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
        Notify("[hero:verbose] ReadHeroAttributeSnapshotAsync started");
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

        await GotoAsync(HeroAttributesPath, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero attributes.", cancellationToken);
        await EnsureLoggedInAsync();

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

        // The attributes page names the hero's village ("Hero is currently in village X" when home, or
        // "Home village is village X" when away). Capture name + away-state so the dashboard can show the
        // green (home) vs yellow (away) hero icon. Best-effort: name null when on an adventure (no anchor).
        var heroHome = await ReadHeroHomeVillageInfoAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(heroHome.Name))
        {
            snapshot = snapshot with { HomeVillageName = heroHome.Name, HomeVillageHeroAway = heroHome.Away };
        }

        SaveCachedHeroAttributeSnapshot(snapshot);
        Notify(
            $"Hero inventory snapshot: free points={snapshot.FreePoints}, fighting strength={snapshot.FightingStrength}, offence bonus={snapshot.OffenceBonus}, defence bonus={snapshot.DefenceBonus}, resources={snapshot.Resources}, adventures={(snapshot.AdventureCount?.ToString() ?? "?")}.");
        return snapshot;
    }

    // Reads the hero's home village name + whether the hero is away, from the attributes page hero-state
    // box ("Hero is currently in village X" when home; "Home village is village X" when away on a raid).
    // Both phrasings link the village via karte.php. Returns (null, false) when no village anchor is present
    // (e.g. on an adventure), so callers keep the previously known value.
    private async Task<(string? Name, bool Away)> ReadHeroHomeVillageInfoAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            var raw = await _page.EvaluateAsync<string?>(
                """
                () => {
                  const clean = (v) => (v || '').replace(/\s+/g, ' ').trim();
                  const boxes = Array.from(document.querySelectorAll('.heroState, .attributeBox .heroState, .attributeBox'));
                  for (const box of boxes) {
                    const link = box.querySelector('a[href*="karte.php"], a[href*="position_details"]');
                    if (!link) continue;
                    const name = clean(link.textContent);
                    if (!name) continue;
                    const txt = clean(box.textContent).toLowerCase();
                    // "currently in village" or a statusHome icon => hero is standing in the village (home).
                    const home = txt.includes('currently in village')
                      || !!box.querySelector('i[class*="statusHome" i], i[class*="heroHome" i]');
                    return JSON.stringify({ name: name, away: !home });
                  }
                  return null;
                }
                """);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return (null, false);
            }

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var away = root.TryGetProperty("away", out var a) && a.GetBoolean();
            return (string.IsNullOrWhiteSpace(name) ? null : name!.Trim(), away);
        }
        catch
        {
            return (null, false);
        }
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

              const bodyInnerText = document.body?.innerText || '';
              const text = bodyInnerText.toLowerCase();
              const statusMessage = document.querySelector('.heroStatusMessage');
              const statusText = (statusMessage?.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const heroStateText = (document.querySelector('.heroState')?.textContent || '').replace(/\s+/g, ' ').trim();
              const awayText = heroStateText || bodyInnerText;
              const isOutboundAdventure = /on its way to an adventure/i.test(awayText);
              const isReturningHome = /on its way back to the village/i.test(awayText);
              // The top-bar/sidebar hero status shows a heroReviving icon while reviving
              // (<div class="heroStatus">...<i class="heroReviving">) — treat it as reviving even when no
              // status text/timer is present on the page.
              const revivingIcon = !!document.querySelector('.heroStatus i.heroReviving, i.heroReviving, [class*="heroReviving"]');
              const reviving = revivingIcon
                || (/being\s+revived|remaining\s+time|reviv/i.test(statusText)
                  && !!statusMessage?.querySelector('.timer, [counting="down"], .heroStatus101Regenerate'));
              // The top-bar/sidebar hero status shows a heroDead icon when the hero is dead
              // (<div class="heroStatus">...<i class="heroDead"></i>). This is more reliable than the
              // localized body text, so treat it as a dead signal too.
              const deadIcon = !!document.querySelector('.heroStatus i.heroDead, i.heroDead, [class*="heroDead"]');
              const dead = deadIcon || /\bdead\b|\btot\b|\bdeceased\b|\bdöd\b/.test(text);

              const effectiveDead = !reviving && (dead || /hero\s+is\s+dead/i.test(statusText));
              const reviveTimerNode = statusMessage?.querySelector('.timer[value], [counting="down"][value], .timer')
                // Official Travian (T4.6) /hero/attributes: the revive countdown is a span.timerReact inside
                // the .heroRevive box ("Remaining revival time: 04:33:53"), not in .heroStatusMessage.
                || document.querySelector('.heroRevive .timerReact, .heroRevive .details .timerReact, .heroRevive .timer');
              const reviveTimer = reviveTimerNode?.getAttribute('value')
                ? Number(reviveTimerNode.getAttribute('value'))
                : parseTimer(reviveTimerNode?.textContent || '');
              const state = reviving ? 'Reviving' : effectiveDead ? 'Dead' : 'Alive';

              const hp =
                parseNumber(document.querySelector('#health')?.textContent || '')
                ?? parseNumber(document.querySelector('[class*="health" i]')?.textContent || '')
                ?? parseNumber(document.querySelector('[id*="health" i]')?.textContent || '');

              const adventures =
                parseNumber(document.querySelector('a.adventure[href*="/hero/adventures"] .content')?.textContent || '')
                ?? parseNumber(document.querySelector('a[href*="/hero/adventures"] .content')?.textContent || '')
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

              const rawReturnTimer =
                parseTimer(document.querySelector('[class*="return" i] [class*="timer" i]')?.textContent || '')
                ?? parseTimer(document.querySelector('.heroReturn .timer')?.textContent || '')
                ?? parseInlineTimer(bodyInnerText, [
                  /back\s+in\s*:?\s*(\d{1,3}:\d{2}:\d{2})/i,
                  /return\s+in\s*:?\s*(\d{1,3}:\d{2}:\d{2})/i,
                  /arrival\s+in\s*:?\s*\d{1,3}:\d{2}:\d{2}(?:\s*hour)?\s*\|\s*back\s+in\s*:?\s*(\d{1,3}:\d{2}:\d{2})/i,
                  // Official Travian (T4.6): "Arrival in 00:03:20 at 17:53".
                  /arrival\s+in\s*:?\s*(\d{1,2}:\d{2}(?::\d{2})?)/i
                ])
                // Official countdown span, only trusted when the hero is moving.
                ?? ((isOutboundAdventure || isReturningHome)
                      ? parseTimer(document.querySelector('span.timerReact, .timerReact')?.textContent || '')
                      : null);
              const returnTimer = rawReturnTimer !== null && isOutboundAdventure && !isReturningHome
                ? rawReturnTimer * 2
                : rawReturnTimer;

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
            UnassignedPoints: parsed.UnassignedPoints ?? 0,
            AdventureReadyFinish: parsed.SecondsUntilAdventureReady is > 0
                ? TimerSnapshot.FromRemaining(parsed.SecondsUntilAdventureReady.Value)
                : null,
            ReturnFinish: parsed.SecondsUntilReturn is > 0
                ? TimerSnapshot.FromRemaining(parsed.SecondsUntilReturn.Value)
                : null,
            ReviveFinish: parsed.ReviveRemainingSeconds is > 0
                ? TimerSnapshot.FromRemaining(parsed.ReviveRemainingSeconds.Value)
                : null);
    }

    private async Task<bool> TryReviveHeroAsync(CancellationToken cancellationToken)
    {
        // Revive UI is on the inventory/attributes page on this Travian version. /hero.php opens Appearance.
        await GotoAsync(HeroInventoryPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while trying to revive hero.", cancellationToken);
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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

        await GotoAsync(HeroInventoryPath, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero inventory for ointments.", cancellationToken);

        var info = await ReadHeroOintmentInventoryInfoAsync(cancellationToken);
        if (!info.Found || info.Count <= 0)
        {
            _lastHeroOintmentMissKey = retryKey;
            Notify("Hero ointments not found in inventory. Suppressing repeat inventory checks until hero state changes.");
            return HeroOintmentUseResult.AttemptedWithoutUse;
        }

        var useCount = HeroCalc.CalculateOintmentsToUse(currentHpPercent, minHpForAdventure, info.Count);
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
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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

    private async Task<bool> IsHeroInActiveVillageAsync(CancellationToken cancellationToken)
    {
        // Sidebar hero status icon on dorf1/dorf2 carries class names like heroStatus50 (in village),
        // heroStatus52/53 (on the way), heroStatus51 (dead). The status text (title/aria) is also localized.
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              // 1) Most reliable: explicit hero home/running icons in the top bar/sidebar.
              if (document.querySelector('i.heroHome, [class*="heroHome"]')) return true;
              if (document.querySelector('i.heroRunning, [class*="heroRunning"]')) return false;

              // 2) Legacy hero status class.
              const icon = document.querySelector('.heroStatus, [class*="heroStatus"]');
              if (icon) {
                const cls = (icon.className || '').toString();
                // Treat heroStatus100/heroStatusHome/heroStatus50 as "in this village".
                if (/heroStatus(?:100|50|Home)\b/i.test(cls)) return true;
                // Anything else (52/53 = on the way, 51 = dead) => not in this village.
                if (/heroStatus\d+/i.test(cls)) return false;
              }
              // 3) Official Travian (T4.6): a hero on its way is shown by a heroRunning icon in the
              // top-bar sidebar, and on the adventures page by a statusRunning icon + .heroState /
              // "on its way to an adventure" / "Arrival in <timerReact>". There is no heroStatus
              // class. Any of these means the hero is NOT in the village.
              if (document.querySelector('[class*="statusRunning"], .heroState, .timerReact')) return false;
              const officialBox = document.querySelector('#topBarHero, #heroV2, .heroV2');
              const officialText = (officialBox?.textContent || '').toLowerCase();
              if (/on its way|on the way to|arrival in/.test(officialText)) return false;

              // 4) Fallback: localized status text in the hero sidebar box.
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
