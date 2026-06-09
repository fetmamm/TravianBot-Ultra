using Microsoft.Playwright;
using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int HeroLowHpRetrySeconds = 60;
    private const int HeroLowHpMaxDeferSeconds = 30 * 60;
    private HeroOintmentRetryKey? _lastHeroOintmentMissKey;
    private bool? _lastHeroAutoUseOintmentsEnabled;

    public async Task<HeroAdventureDispatchResult> SendHeroOnAdventureAsync(CancellationToken cancellationToken = default)
    {
        Notify("[hero] adventure check starting");

        var quick = await ReadHeroQuickStatusAsync(allowDorf1Fallback: true, forceDorf1Reload: false, cancellationToken);
        var statusText = quick.Sidebar.StatusText;
        var inHomeVillage = quick.IsInVillage;
        var isDead = quick.Status.IsDead || IsHeroStatusTextDead(statusText);
        var isOnTheWay = (quick.Status.SecondsUntilReturn is > 0) || IsHeroStatusTextAway(statusText);
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

        var returnText = returnSeconds is int rs ? FormatDuration(rs) : "(unknown)";
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
        Notify("[hero:verbose] RefreshAdventureCountAsync started");
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
        // We're on dorf1 here; cheaply read the hero home village + state from the hero widget (no extra
        // navigation) and surface it so the dashboard hero icon updates during normal polling too.
        await NotifyHeroHomeFromDorf1Async(cancellationToken);
        return sidebar.AdventureCount;
    }

    // Reads the hero home village + away/dead state from the dorf1 hero widget (the rally-point link in the
    // hero box points to the hero's HOME village; the icon class shows the state). Emits a [herohome] log
    // line the desktop parses. Best-effort: silent when the widget/village name isn't present.
    private async Task NotifyHeroHomeFromDorf1Async(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
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
                              || document.querySelector('a[href*="build.php"][href*="id=39"]');
                  if (!widget) return null;
                  const icon = widget.querySelector('i') || document.querySelector('.heroStatus i');
                  const cls = icon ? (icon.className || '').toLowerCase() : '';
                  const m = (widget.getAttribute('href') || '').match(/newdid=(\d+)/);
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
                  if (!name) return null;
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
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var away = root.TryGetProperty("away", out var a) && a.GetBoolean();
            var dead = root.TryGetProperty("dead", out var d) && d.GetBoolean();
            var reviving = root.TryGetProperty("reviving", out var r) && r.GetBoolean();
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
            || text.Contains("on its way", StringComparison.Ordinal) // official Travian (T4.6)
            || text.Contains("arrival in", StringComparison.Ordinal)
            || text.Contains("back from", StringComparison.Ordinal)
            || text.Contains("returning", StringComparison.Ordinal);
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
        await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
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

        await GotoAsync(Paths.HeroAdventureLegacy, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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
            Notify($"Hero adventures modern page hit transient navigation issue. Falling back to {Paths.HeroAdventureLegacy}.");
        }

        await GotoAsync(Paths.HeroAdventureLegacy, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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
                  // SS/legacy: /hero_adventure.php or /hero.php?t=3. Official (T4.6): /hero/adventures.
                  if (url.includes('/hero_adventure.php') || url.includes('/hero.php?t=3') || url.includes('/hero/adventures')) {
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
                    || text.includes('explore')
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
                    || text.includes('explore')
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

    private async Task<bool> ReviveHeroOnInventoryAsync(CancellationToken cancellationToken)
    {
        Notify("[hero] revive flow starting (inventory page)");
        await GotoAsync(HeroInventoryPath, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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
                await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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
            var seconds = ParseDurationToSeconds(raw);
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
            return ParseDurationToSeconds(rawJson);
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
        bool hideModeEnabled = false,
        string hideMode = "hide",
        int heroHpRegenPerDayPercent = 100,
        CancellationToken cancellationToken = default)
    {
        Notify("[hero] manage starting (revive + points + adventure)");
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

        // Official Travian (T4.6) does not expose HP in the sidebar, but /hero/attributes shows it.
        // Read HP before opening /hero/adventures so low-HP decisions do not require an extra
        // adventures -> attributes -> adventures round trip.
        if (!_config.IsPrivateServer && adventureHintCount > 0 && inVillage && !status.IsDead && hpPercent is null)
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

        // Official sometimes fails to expose HP immediately after the hero returns. If the sidebar
        // says the hero is home, do not defer 10 minutes on unknown HP; try to dispatch instead.
        var canSendByHp = !status.IsDead
            && (hpPercent.HasValue
                ? hpPercent.Value >= minHpThreshold
                : !_config.IsPrivateServer && inVillage);
        var hpTooLow = false;

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
            // The hero is on an adventure. Read the return ETA so the loop defers until the hero
            // is home instead of re-queueing hero_manage every tick (which otherwise spams).
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
        // When the hero is away, always defer by the return ETA (or a sane fallback) so the loop
        // does not re-run hero_manage every second while there is nothing to do.
        if (!inVillage && adventureCount > 0)
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
            await GotoAsync(HeroAdventuresPath, cancellationToken);
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
        // Unknown HP (null) is sendable on official Travian only (SVG HP bar, no numeric value).
        var canSendByHp = !status.IsDead
            && (hpPercent.HasValue ? hpPercent.Value >= Math.Clamp(minHpForAdventure, 1, 100) : !_config.IsPrivateServer);
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
        await GotoAsync(HeroInventoryPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while returning to hero inventory.", cancellationToken);
        await ExpandAttributesPanelIfClosedAsync(cancellationToken);

        // Step 6: apply Hide hero / stay-with-troops preference if it differs from current.
        if (hideModeEnabled)
        {
            var hideApplied = await ApplyHeroHideModeAsync(hideMode, cancellationToken);
            if (hideApplied) actions.Add($"hide_mode_set={(string.Equals(hideMode, "fight", StringComparison.OrdinalIgnoreCase) ? "fight" : "hide")}");
        }

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
            () => {
              // SS/legacy: each adventure is a gotoAdventure link.
              const legacy = document.querySelectorAll('a.gotoAdventure[href*="start_adventure.php"]').length;
              if (legacy > 0) return legacy;
              // Official Travian (T4.6): adventures are rows in <table class="... adventureList">.
              const official = document.querySelectorAll('table.adventureList tbody tr').length;
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
        if (cachedSnapshot is not null && !quick.HasUnassignedPointsSignal && !string.IsNullOrWhiteSpace(cachedSnapshot.HideMode))
        {
            Notify("Hero attribute snapshot served from cache.");
            return cachedSnapshot with { AdventureCount = adventureCount };
        }

        if (_config.IsPrivateServer)
        {
            await EnsureHeroInventoryAttributesTabAsync(cancellationToken);
        }
        else
        {
            await GotoAsync(HeroAttributesPath, cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero attributes.", cancellationToken);
            await EnsureLoggedInAsync();
        }

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
            $"Hero inventory snapshot: free points={snapshot.FreePoints}, fighting strength={snapshot.FightingStrength}, offence bonus={snapshot.OffenceBonus}, defence bonus={snapshot.DefenceBonus}, resources={snapshot.Resources}, adventures={(snapshot.AdventureCount?.ToString() ?? "?")}, hideMode={snapshot.HideMode ?? "?"}.");
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
                // Official Travian (T4.6): adventure menu anchor /hero/adventures with count in .content.
                ?? parseNumber(document.querySelector('a.adventure[href*="/hero/adventures"] .content')?.textContent || '')
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
            UnassignedPoints: parsed.UnassignedPoints ?? 0);
    }

    private async Task<bool> TryReviveHeroAsync(CancellationToken cancellationToken)
    {
        // Revive UI is on the inventory/attributes page on this Travian version. /hero.php opens Appearance.
        await GotoAsync(HeroInventoryPath, cancellationToken);
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
                // SS: .bigSpeechBubble.levelUp. Official (T4.6): an i.levelUp.show icon shown on
                // almost every page when the hero has unspent attribute points.
                () => !!document.querySelector('.bigSpeechBubble.levelUp, i.levelUp.show, .levelUp.show')
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    // Official Travian (T4.6): the attributes page (/hero/attributes) is its own form with
    // inputs name="power"/"offBonus"/"defBonus"/"productionPoints", a .pointsAvailable counter,
    // per-attribute button.plus / button.minus, and a #savePoints submit (enabled once points are
    // assigned). Allocate by clicking the prioritised attribute's + button, then save.
    private async Task<int> AllocateHeroPointsOfficialAsync(string priority, CancellationToken cancellationToken)
    {
        Notify("[hero:verbose] official hero point allocation entered");
        await GotoAsync("/hero/attributes", cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await EnsureLoggedInAsync();

        try
        {
            await _page.WaitForFunctionAsync(
                """() => !!document.querySelector('input[name="power"], .pointsAvailable')""",
                null,
                new PageWaitForFunctionOptions { Timeout = 6000 });
        }
        catch (TimeoutException)
        {
        }

        var fieldOrder = ParseHeroStatPriority(priority)
            .Select(MapStatToOfficialField)
            .Where(field => field is not null)
            .Select(field => field!)
            .ToList();
        if (fieldOrder.Count == 0)
        {
            fieldOrder = ["power", "offBonus", "defBonus", "productionPoints"];
        }

        var before = await ReadPointsAvailableAsync();
        if (before <= 0)
        {
            Notify("[hero] official allocation: no points available.");
            return 0;
        }

        var remaining = before;
        var used = 0;
        var exhaustedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedFields = new List<string>();
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var activeOrder = fieldOrder.Where(field => !exhaustedFields.Contains(field)).ToList();
            if (activeOrder.Count == 0)
            {
                break;
            }

            var fieldClicked = await ClickOfficialHeroAttributePlusAsync(activeOrder, cancellationToken);
            if (string.IsNullOrWhiteSpace(fieldClicked))
            {
                break;
            }

            await Task.Delay(500, cancellationToken);
            var latest = await ReadPointsAvailableAsync();
            if (latest >= remaining)
            {
                await Task.Delay(500, cancellationToken);
                latest = await ReadPointsAvailableAsync();
            }

            if (latest >= remaining)
            {
                exhaustedFields.Add(fieldClicked);
                Notify($"[hero] official allocation: field={fieldClicked} did not consume a point, trying next priority.");
                continue;
            }

            used += remaining - latest;
            remaining = latest;
            usedFields.Add(fieldClicked);
            Notify($"[hero] official allocation: clicked {fieldClicked}, points remaining={remaining}");
        }

        var after = await ReadPointsAvailableAsync();
        used = Math.Max(0, before - after);
        Notify($"[hero] official allocation: fields={string.Join(",", usedFields.Distinct(StringComparer.OrdinalIgnoreCase))}, points before={before}, after={after}, used={used}");
        if (used <= 0)
        {
            return 0;
        }

        // #savePoints stays disabled until the changes register; wait for it to enable before clicking.
        try
        {
            await _page.WaitForFunctionAsync(
                """() => { const b = document.querySelector('#savePoints'); return !!b && !b.disabled; }""",
                null,
                new PageWaitForFunctionOptions { Timeout = 4000 });
        }
        catch (TimeoutException)
        {
        }

        var saved = await _page.EvaluateAsync<bool>(
            """
            () => {
              const save = document.querySelector('#savePoints, button#savePoints');
              if (save && !save.disabled) { save.click(); return true; }
              return false;
            }
            """);
        if (saved)
        {
            InvalidateCachedHeroAttributeSnapshot();
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        }

        Notify($"[hero] official point allocation: assigned {used}, saved={saved}");
        return used;
    }

    private async Task<int> ReadPointsAvailableAsync()
    {
        return await _page.EvaluateAsync<int>(
            """() => parseInt((document.querySelector('.pointsAvailable')?.textContent || '0').replace(/[^\d]/g, ''), 10) || 0""");
    }

    private async Task<string?> ClickOfficialHeroAttributePlusAsync(IReadOnlyList<string> fieldOrder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<string?>(
            """
            (order) => {
              const robustClick = (el) => {
                el.scrollIntoView && el.scrollIntoView({ block: 'center' });
                const o = { bubbles: true, cancelable: true, view: window };
                try { el.dispatchEvent(new PointerEvent('pointerdown', o)); } catch (e) {}
                el.dispatchEvent(new MouseEvent('mousedown', o));
                try { el.dispatchEvent(new PointerEvent('pointerup', o)); } catch (e) {}
                el.dispatchEvent(new MouseEvent('mouseup', o));
                el.click();
              };
              const plusFor = (name) => {
                const input = document.querySelector(`input[name="${name}"]`);
                if (!input) return null;
                let row = input.parentElement;
                while (row && row !== document.body) {
                  const btn = row.querySelector('button.plus, button.textButtonV2.plus');
                  if (btn) return btn;
                  row = row.parentElement;
                }
                return null;
              };
              for (const name of order) {
                const btn = plusFor(name);
                const ariaDisabled = (btn?.getAttribute?.('aria-disabled') || '').toLowerCase() === 'true';
                if (!btn || btn.disabled || ariaDisabled) continue;
                robustClick(btn);
                return name;
              }
              return null;
            }
            """,
            fieldOrder);
    }

    private static string? MapStatToOfficialField(string stat) => (stat ?? string.Empty).ToLowerInvariant() switch
    {
        "fighting_strength" => "power",
        "offence_bonus" => "offBonus",
        "defence_bonus" => "defBonus",
        "resources" => "productionPoints",
        _ => null,
    };

    private async Task<int> TryAllocateHeroPointsAsync(string priority, CancellationToken cancellationToken)
    {
        if (!_config.IsPrivateServer)
        {
            return await AllocateHeroPointsOfficialAsync(priority, cancellationToken);
        }

        Notify("[hero:verbose] TryAllocateHeroPoints entered");
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
        Notify($"[hero:verbose] allocate DOM diag: {diag}");

        var snapshot = await ReadHeroInventorySnapshotAsync(cancellationToken);
        Notify($"[hero] free attribute points found: {snapshot.FreePoints}");
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
            Notify("[hero] attribute points saved");
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
        await GotoAsync(HeroInventoryPath, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero inventory.", cancellationToken);
        await EnsureLoggedInAsync();

        if (!IsCurrentUrlForPath(HeroInventoryPath))
        {
            await GotoAsync(HeroInventoryPath, cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero attributes tab.", cancellationToken);
        }

        // The attributes table is in DOM regardless of whether the collapsible panel is expanded —
        // we don't block on expansion here. Callers that need to CLICK (e.g. assign points) must
        // call ExpandAttributesPanelIfClosedAsync themselves; pure reads don't.
        var tableReady = await WaitForAttributesTableAsync(cancellationToken, timeoutMs: _config.IsPrivateServer ? 4000 : 1000);
        if (!tableReady && _config.IsPrivateServer)
        {
            Notify($"Hero attributes table missing after tab click — reloading {HeroInventoryPath}.");
            await GotoAsync(HeroInventoryPath, cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            tableReady = await WaitForAttributesTableAsync(cancellationToken, timeoutMs: 6000);
            if (!tableReady)
            {
                Notify($"Hero attributes table still missing after reload. url='{_page.Url}'.");
            }
        }
        else if (!tableReady)
        {
            // Official Travian (T4.6) renders the attributes panel with React and has no
            // a.setPoint / td.pointsValueSetter markup, so this table never appears for the
            // SS reader. Skip the reload + long retry (it just wasted ~13s every loop).
            Notify("Hero attributes table not present (official Travian uses a React attributes panel); skipping retry.");
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
                  // Modern hero V2 attributes page.
                  if (document.querySelector('.heroAttributes input[name="power"], input[name="productionPoints"], .heroAttributes .pointsAvailable')) return true;
                  // Legacy table layout.
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

    // Reads the four resource consumables the hero is carrying (wood=item145, clay=item146,
    // iron=item147, crop=item148) from the hero inventory grid. Official-only data, but the read
    // itself is harmless on any server: a missing item simply reads as 0. Navigates to the hero
    // inventory page so it can be called on demand from the desktop.
    public async Task<HeroInventoryResources> ReadHeroInventoryResourcesAsync(
        CancellationToken cancellationToken = default,
        bool suppressUiSync = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Notify("[hero-inventory] reading resources from inventory");

        // EnsureLoggedInAsync emits a UI-sync that reads the village list (navigating to the
        // profile on the first read of a session). When called as the very first post-login step
        // this would navigate to the profile BEFORE the inventory is read. Suppress it so the
        // inventory really is read first; the village/profile read still happens right after.
        if (suppressUiSync)
        {
            _suppressEnsureUiSyncDepth++;
        }

        try
        {
            await EnsureLoggedInAsync();
            await GotoAsync(HeroInventoryPath, cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            await EnsureLoggedInAsync();

        // Give the React-rendered inventory grid a moment to appear; a timeout just falls through
        // to a best-effort read (missing items read as 0).
        try
        {
            await _page.WaitForSelectorAsync(
                ".heroItems .heroItem .item",
                new PageWaitForSelectorOptions { Timeout = 5000 });
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException)
        {
        }

        string rawJson;
        try
        {
            rawJson = await _page.EvaluateAsync<string>(
                """
                () => {
                  const readCount = (itemClass) => {
                    const item = document.querySelector('.heroItems .item.' + itemClass);
                    if (!item) return 0;
                    const slot = item.closest('.heroItem');
                    const countEl = slot ? slot.querySelector('.count') : null;
                    const text = (countEl?.textContent || '').replace(/[^0-9]/g, '');
                    return text ? Number(text) || 0 : 0;
                  };
                  return JSON.stringify({
                    wood: readCount('item145'),
                    clay: readCount('item146'),
                    iron: readCount('item147'),
                    crop: readCount('item148')
                  });
                }
                """);
        }
        catch (Exception ex)
        {
            Notify($"[hero-inventory] read EvaluateAsync threw: {ex.GetType().Name}: {ex.Message}");
            return new HeroInventoryResources();
        }

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            Notify("[hero-inventory] read returned empty result");
            return new HeroInventoryResources();
        }

        var resources = JsonSerializer.Deserialize<HeroInventoryResources>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new HeroInventoryResources();

            Notify($"[hero-inventory] wood={resources.Wood} clay={resources.Clay} iron={resources.Iron} crop={resources.Crop}");
            UpdateHeroInventoryCache(resources);
            return resources;
        }
        finally
        {
            if (suppressUiSync)
            {
                _suppressEnsureUiSyncDepth--;
            }
        }
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
                      // Strip bidi marks/thousands separators before reading the number.
                      const m = (el.textContent || '').replace(/[^\d-]/g, '');
                      return m ? Number(m) || 0 : 0;
                    };
                    // Reads an attribute's allocated points. Modern hero V2: <input name="power"> etc.
                    // (names: power/offBonus/defBonus/productionPoints). Legacy: row id ("attributepower")
                    // with an <input name^="attribute"> or a <td class="points"> text. Try modern first.
                    const attrPoints = (modernName, legacyRowId) => {
                      const modern = document.querySelector('input[name="' + modernName + '"]');
                      if (modern) return Number(modern.value) || 0;
                      const row = document.getElementById(legacyRowId);
                      if (!row) return 0;
                      const input = row.querySelector('input[type="text"][name^="attribute"]');
                      if (input) return Number(input.value) || 0;
                      const td = row.querySelector('td.points');
                      return td ? readDigit(td) : 0;
                    };
                    // Free points: modern ".pointsAvailable", legacy "#availablePoints".
                    const freePointsEl = document.querySelector('.heroAttributes .pointsAvailable, .pointsAvailable, #availablePoints');
                    const parseTimer = (value) => {
                      const text = (value || '').replace(/\s+/g, ' ').trim();
                      if (!text) return null;
                      const parts = text.split(':').map(v => Number(v));
                      if (parts.some(v => !Number.isFinite(v))) return null;
                      if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2];
                      if (parts.length === 2) return parts[0] * 60 + parts[1];
                      return null;
                    };
                    const statusMessage = document.querySelector('.heroState, .heroStatusMessage');
                    const statusText = (statusMessage?.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                    const revivingIcon = !!document.querySelector('.heroStatus i.heroReviving, i.heroReviving, [class*="heroReviving"]');
                    const reviving = revivingIcon
                      || (/being\s+revived|remaining\s+time|reviv/i.test(statusText)
                        && !!statusMessage?.querySelector('.timer, [counting="down"], .heroStatus101Regenerate'));
                    const deadIcon = !!document.querySelector('.heroStatus i.heroDead, i.heroDead, [class*="heroDead"]');
                    const dead = !reviving && (deadIcon || /hero\s+is\s+dead|\bdead\b|\btot\b|\bdeceased\b/i.test(statusText));
                    const reviveTimerNode = statusMessage?.querySelector('.timer[value], [counting="down"][value], .timer')
                      || document.querySelector('.lineWrapper .inlineIcon.duration .value, .lineWrapper .duration .value');
                    const reviveTimer = reviveTimerNode?.getAttribute?.('value')
                      ? Number(reviveTimerNode.getAttribute('value'))
                      : parseTimer(reviveTimerNode?.textContent || '');
                    const hideSwitch = document.querySelector('.heroHideSwitch input[name="attackBehaviour"], input[name="attackBehaviour"][value="hide"]');

                    return JSON.stringify({
                      ok: true,
                      levelUpAvailable: !!document.querySelector('.bigSpeechBubble.levelUp'),
                      freePoints: readDigit(freePointsEl),
                      fightingStrength: attrPoints('power', 'attributepower'),
                      offenceBonus: attrPoints('offBonus', 'attributeoffBonus'),
                      defenceBonus: attrPoints('defBonus', 'attributedefBonus'),
                      resources: attrPoints('productionPoints', 'attributeproductionPoints'),
                      heroState: reviving ? 'Reviving' : dead ? 'Dead' : 'Alive',
                      reviveRemainingSeconds: Number.isFinite(reviveTimer) ? Math.max(0, Math.trunc(reviveTimer)) : null,
                      hideMode: hideSwitch ? (hideSwitch.checked ? 'hide' : 'fight') : null
                    });
                  } catch (e) {
                    return JSON.stringify({ ok: false, error: String(e && e.message || e) });
                  }
                }
                """);
        }
        catch (Exception ex)
        {
            Notify($"[hero] inventory snapshot EvaluateAsync threw: {ex.GetType().Name}: {ex.Message}");
            return new HeroAttributeSnapshot();
        }
        Notify($"[hero:verbose] inventory snapshot raw JSON: {rawJson}");

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

    public async Task<string> SetHeroHideModeOnlyAsync(string hideMode, CancellationToken cancellationToken = default)
    {
        Notify($"[hero] set hide mode — requested='{hideMode}'");
        var desired = string.Equals(hideMode, "fight", StringComparison.OrdinalIgnoreCase) ? "fight" : "hide";
        if (!_config.IsPrivateServer)
        {
            var officialChanged = await ApplyOfficialHeroHideModeAsync(desired, cancellationToken);
            return officialChanged
                ? $"Hero hide mode applied: {desired}."
                : $"Hero hide mode already '{desired}' — no change.";
        }

        var inVillage = await IsHeroInActiveVillageAsync(cancellationToken);
        if (!inVillage)
        {
            Notify("Hero hide mode skipped: hero is away, avoiding inventory navigation.");
            return "Hero hide mode skipped because hero is away.";
        }

        var changed = await ApplyHeroHideModeAsync(hideMode, cancellationToken);
        return changed
            ? $"Hero hide mode applied: {hideMode}."
            : $"Hero hide mode already '{hideMode}' — no change.";
    }

    private async Task<bool> ApplyOfficialHeroHideModeAsync(string desired, CancellationToken cancellationToken)
    {
        await GotoAsync(HeroAttributesPath, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero attributes.", cancellationToken);
        await EnsureLoggedInAsync();

        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => !!document.querySelector('.heroHideSwitch input[name="attackBehaviour"], input[name="attackBehaviour"][value="hide"]')
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 }).WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            Notify("Hero hide mode switch not found on official attributes page.");
            return false;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"Hero hide mode switch read hit transient navigation context: {ex.Message}");
            return false;
        }

        var changed = await _page.EvaluateAsync<bool>(
            """
            (desired) => {
              const checkbox = document.querySelector('.heroHideSwitch input[name="attackBehaviour"], input[name="attackBehaviour"][value="hide"]');
              if (!checkbox) return false;
              const shouldHide = (desired || '').toLowerCase() !== 'fight';
              if (!!checkbox.checked === shouldHide) return false;
              checkbox.click();
              checkbox.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            }
            """,
            desired);

        if (changed)
        {
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            Notify($"Hero hide mode set to '{desired}' on official attributes page.");
        }

        return changed;
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

        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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
            if (CachedHeroAttributeSnapshotsByKey.TryGetValue(key, out var snapshot))
            {
                return snapshot;
            }
        }

        if (_heroAttributeSnapshotStore.TryLoad(_account.Name, _config.BaseUrl, out var storedSnapshot)
            && storedSnapshot is not null)
        {
            lock (HeroAttributeSnapshotCacheSync)
            {
                CachedHeroAttributeSnapshotsByKey[key] = storedSnapshot;
            }

            return storedSnapshot;
        }

        return null;
    }

    private void SaveCachedHeroAttributeSnapshot(HeroAttributeSnapshot snapshot)
    {
        var key = BuildHeroAttributeSnapshotCacheKey();
        lock (HeroAttributeSnapshotCacheSync)
        {
            CachedHeroAttributeSnapshotsByKey[key] = snapshot with { };
        }

        _heroAttributeSnapshotStore.Save(_account.Name, _config.BaseUrl, snapshot);
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

        // The adventures list on official Travian (T4.6) is React-rendered and is often not in the
        // DOM yet right after navigation. Wait for the adventure rows (Explore buttons) — or the
        // hero-away state — to render before picking; otherwise the pick finds nothing and the
        // dispatch falsely reports "adventure_not_clickable".
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => !!document.querySelector('table.adventureList tbody tr, a.gotoAdventure[href*="start_adventure.php"]')
                   || /on its way to an adventure/i.test(document.body?.innerText || '')
                   || !!document.querySelector('.heroState, [class*="statusRunning"], [class*="heroRunning"]')
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 6000 });
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
        }

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

              const isDisabled = (node) =>
                !node || (node.hasAttribute && node.hasAttribute('disabled'))
                || (node.className || '').toString().toLowerCase().includes('disabled');

              const candidates = Array.from(document.querySelectorAll('a.gotoAdventure[href*="start_adventure.php"], a, button, input[type="submit"]'))
                .filter(node => {
                  if (isDisabled(node)) return false;
                  const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                  const href = (node.getAttribute('href') || '').toLowerCase();
                  return node.matches('a.gotoAdventure[href*="start_adventure.php"]')
                    || text.includes('to the adventure')
                    || text.includes('to adventure')
                    || text.includes('start adventure')
                    || text.includes('explore')
                    || href.includes('hero.php?t=3&kid=')
                    || href.includes('action=start');
                });
              if (candidates.length === 0) return JSON.stringify({ ok: false });

              const entries = candidates.map(node => {
                const row = node.closest('tr');
                const moveCell = row?.querySelector('td.moveTime');
                const duration = parseDuration(moveCell?.textContent || row?.textContent || '');
                return { node, duration };
              });

              if (order === 'shortest') entries.sort((a, b) => a.duration - b.duration);
              const chosen = entries[0];
              chosen.node.click();
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

        // Step 2: confirm the adventure.
        // SS/legacy navigates to start_adventure.php with a #start (button[name="s1"]) button.
        // Official Travian (T4.6) instead opens a React confirmation modal with a "Continue"
        // button (class "...continue...") and does NOT navigate — the modal needs a moment to
        // render, so poll for the confirm button before giving up.
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on adventure detail page.", cancellationToken);
        var fallbackReturnFromDetail = await ReadAdventureReturnSecondsAsync(cancellationToken) ?? fallbackReturnSeconds;
        Notify($"[adventure] picked {pickOrder} adventure, duration={duration}s, hero return ETA={fallbackReturnFromDetail}s");

        var confirmed = false;
        for (var attempt = 0; attempt < 10 && !confirmed; attempt++)
        {
            confirmed = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const isDisabled = (n) => !n
                    || (n.hasAttribute && n.hasAttribute('disabled'))
                    || /(^|\s)disabled(\s|$)/i.test((n.className || '').toString());
                  // SS/legacy: start button on the adventure detail page.
                  const start = document.querySelector('button#start[name="s1"], button[name="s1"], button#start');
                  if (start && !isDisabled(start)) { start.click(); return true; }
                  // Official (T4.6): "Continue" button in the confirmation modal.
                  const cont = Array.from(document.querySelectorAll('button.continue, button'))
                    .find(n => !isDisabled(n) && /\bcontinue\b/i.test((n.value || '') + ' ' + (n.textContent || '')));
                  if (cont) { cont.click(); return true; }
                  // Other localized submit labels.
                  const submit = Array.from(document.querySelectorAll('button, input[type="submit"]'))
                    .find(n => !isDisabled(n) && /to\s+adventure|start\s+adventure/i.test((n.value || '') + ' ' + (n.textContent || '')));
                  if (submit) { submit.click(); return true; }
                  return false;
                }
                """);
            if (!confirmed)
            {
                await Task.Delay(300, cancellationToken);
            }
        }

        if (!confirmed)
        {
            return (false, duration, fallbackReturnFromDetail);
        }

        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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
                    || bodyText.includes('on its way to an adventure') // official Travian (T4.6)
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
