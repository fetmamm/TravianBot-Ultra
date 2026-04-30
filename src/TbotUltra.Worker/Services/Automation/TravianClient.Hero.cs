using Microsoft.Playwright;
using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task<HeroAdventureDispatchResult> SendHeroOnAdventureAsync(CancellationToken cancellationToken = default)
    {
        Notify("SendHeroOnAdventureAsync started");

        await EnsureFreshDorf1ForHeroAsync(cancellationToken);

        var sidebar = await ReadHeroSidebarStatusAsync(cancellationToken);
        var statusText = sidebar.StatusText;
        var statusLower = (statusText ?? string.Empty).ToLowerInvariant();
        var inHomeVillage = statusLower.Contains("home village") || statusLower.Contains("in village");
        var isDead = statusLower.Contains("dead") || statusLower.Contains("deceased");
        var isOnTheWay = statusLower.Contains("on the way") || statusLower.Contains("back from") || statusLower.Contains("returning");
        var adventures = sidebar.AdventureCount;

        Notify($"Hero status on dorf1: '{statusText ?? "(unknown)"}', adventures available: {adventures}.");

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
            var etaSeconds = await ReadHeroReturnFromRallyPointAsync(cancellationToken);
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

        var openedList = await ClickAdventureButtonOnDorf1Async(cancellationToken);
        if (!openedList)
        {
            Notify("Could not click adventures button on dorf1. Trying direct adventure pages.");
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

        // Return to dorf1 so the bot leaves the adventure detail page in a known state.
        await GotoAsync(Paths.Resources, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after returning to dorf1.", cancellationToken);

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

    public async Task<int?> RefreshAdventureCountAsync(CancellationToken cancellationToken = default)
    {
        Notify("RefreshAdventureCountAsync started");
        await EnsureFreshDorf1ForHeroAsync(cancellationToken);
        var sidebar = await ReadHeroSidebarStatusAsync(cancellationToken);
        if (!sidebar.AdventureFound)
        {
            Notify("Adventure indicator not found on current page.");
            return null;
        }

        Notify($"Adventures on current page: {sidebar.AdventureCount}.");
        return sidebar.AdventureCount;
    }

    private async Task EnsureFreshDorf1ForHeroAsync(CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening dorf1 for hero check.", cancellationToken);
        await EnsureLoggedInAsync();
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
              if (url.includes('hero_inventory.php') && !url.includes('action=')) return true;
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
        try
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = _config.TimeoutMs,
            }).WaitAsync(cancellationToken);
        }
        catch (PlaywrightException)
        {
            // Best-effort: if waiting fails, fall through and let the next operation surface the real issue.
        }
        catch (TimeoutException)
        {
        }
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
        string statPriority,
        CancellationToken cancellationToken = default)
    {
        Notify("ManageHeroAsync started");
        await GotoAsync(Paths.Hero, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero page.", cancellationToken);
        await EnsureLoggedInAsync();

        var status = await ReadHeroStatusAsync(cancellationToken);
        if (!status.Exists)
        {
            return "Hero page is unavailable for this account.";
        }

        var heroLeveledUp = await HasHeroLevelUpIndicatorAsync(cancellationToken);
        Notify(heroLeveledUp ? "Hero level up detected." : "Hero level up not detected.");

        var actions = new List<string>();
        if (status.IsDead && autoRevive)
        {
            var revived = await TryReviveHeroAsync(cancellationToken);
            actions.Add(revived ? "revive_started" : "revive_not_available");
            status = await ReadHeroStatusAsync(cancellationToken);
        }

        if (autoAssignPoints && (heroLeveledUp || status.UnassignedPoints > 0))
        {
            var allocated = await TryAllocateHeroPointsAsync(statPriority, cancellationToken);
            if (allocated > 0)
            {
                actions.Add($"points_allocated={allocated}");
            }
        }

        var canSendByHp = !status.IsDead && (status.HpPercent ?? 0) >= Math.Clamp(minHpForAdventure, 1, 100);
        if (status.AdventuresAvailable > 0 && canSendByHp)
        {
            var sent = await TrySendHeroToAdventureAsync(cancellationToken);
            actions.Add(sent ? "adventure_sent" : "adventure_not_clickable");
        }

        await GotoAsync(Paths.Hero, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while returning to hero page.", cancellationToken);
        status = await ReadHeroStatusAsync(cancellationToken);
        var summary = $"Hero status: dead={status.IsDead}, hp={status.HpPercent?.ToString() ?? "?"}%, adventures={status.AdventuresAvailable}, points={status.UnassignedPoints}";
        if (actions.Count == 0)
        {
            return $"{summary}. No hero action was needed.";
        }

        return $"{summary}. Actions: {string.Join(", ", actions)}.";
    }

    public async Task<HeroAttributeSnapshot> ReadHeroAttributeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Notify("ReadHeroAttributeSnapshotAsync started");
        await EnsureHeroInventoryAttributesTabAsync(cancellationToken);
        var snapshot = await ReadHeroInventorySnapshotAsync(cancellationToken);
        Notify(
            $"Hero inventory snapshot: free points={snapshot.FreePoints}, fighting strength={snapshot.FightingStrength}, offence bonus={snapshot.OffenceBonus}, defence bonus={snapshot.DefenceBonus}, resources={snapshot.Resources}.");
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

              const text = (document.body?.innerText || '').toLowerCase();
              const dead = /\bdead\b|\btot\b|\bdeceased\b|\bdöd\b/.test(text);

              const hp =
                parseNumber(document.querySelector('#health')?.textContent || '')
                ?? parseNumber(document.querySelector('[class*="health" i]')?.textContent || '')
                ?? parseNumber(document.querySelector('[id*="health" i]')?.textContent || '');

              const adventures =
                parseNumber(document.querySelector('#adventureCount')?.textContent || '')
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
                ?? parseTimer(document.querySelector('.heroReturn .timer')?.textContent || '');

              const exists = !!document.querySelector('#heroImage, #heroStatus, [class*="hero" i]');
              return JSON.stringify({
                exists,
                isDead: dead,
                hpPercent: hp,
                adventuresAvailable: adventures || 0,
                secondsUntilAdventureReady: adventureTimer,
                secondsUntilReturn: returnTimer,
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
            HpPercent: parsed.HpPercent,
            AdventuresAvailable: parsed.AdventuresAvailable ?? 0,
            SecondsUntilAdventureReady: parsed.SecondsUntilAdventureReady,
            SecondsUntilReturn: parsed.SecondsUntilReturn,
            UnassignedPoints: parsed.UnassignedPoints ?? 0);
    }

    private async Task<bool> TryReviveHeroAsync(CancellationToken cancellationToken)
    {
        await GotoAsync(Paths.Hero, cancellationToken);
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
        await EnsureHeroInventoryAttributesTabAsync(cancellationToken);
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
            while (allocated < snapshot.FreePoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentValue = GetHeroAttributeValue(snapshot, stat);
                if (currentValue >= 100)
                {
                    Notify($"Hero attribute maxed: {GetHeroAttributeDisplayName(stat)}.");
                    break;
                }

                var clicked = await ClickHeroAttributePlusAsync(stat, cancellationToken);
                if (!clicked)
                {
                    Notify($"Hero attribute maxed: {GetHeroAttributeDisplayName(stat)}.");
                    break;
                }

                allocated += 1;
                snapshot = snapshot with
                {
                    FreePoints = Math.Max(0, snapshot.FreePoints - 1),
                };
                snapshot = SetHeroAttributeValue(snapshot, stat, currentValue + 1);
                if (GetHeroAttributeValue(snapshot, stat) >= 100)
                {
                    Notify($"Hero attribute maxed: {GetHeroAttributeDisplayName(stat)}.");
                    break;
                }
            }
        }

        if (allocated <= 0)
        {
            return 0;
        }

        var saved = await ClickHeroSaveChangesAsync(cancellationToken);
        if (saved)
        {
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
              if (url.includes('hero_inventory.php') && !url.includes('action=')) return true;
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
    }

    private async Task<HeroAttributeSnapshot> ReadHeroInventorySnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const parseFirstNumber = (value) => {
                const match = clean(value).match(/(\d[\d\s.,']*)/);
                if (!match) return 0;
                const digits = match[1].replace(/[^\d]/g, '');
                if (!digits) return 0;
                const parsed = Number(digits);
                return Number.isFinite(parsed) ? parsed : 0;
              };

              const findRow = (names) => {
                const allRows = Array.from(document.querySelectorAll('tr, .attribute, .heroAttribute, .row'));
                return allRows.find(row => {
                  const text = clean(row.textContent || '').toLowerCase();
                  return names.some(name => text.includes(name));
                }) || null;
              };

              const parseAttributeValue = (row) => {
                if (!row) return 0;
                const candidates = Array.from(row.querySelectorAll('td, div, span, strong'));
                const values = candidates
                  .map(node => parseFirstNumber(node.textContent || ''))
                  .filter(value => Number.isFinite(value) && value >= 0 && value <= 100);
                if (values.length > 0) return Math.max(...values);

                const textValues = clean(row.textContent || '').match(/\d+/g) || [];
                const parsedValues = textValues
                  .map(value => Number(value))
                  .filter(value => Number.isFinite(value) && value >= 0 && value <= 100);
                return parsedValues.length > 0 ? Math.max(...parsedValues) : 0;
              };

              const bodyText = clean(document.body?.innerText || '');
              const freePointsMatch = bodyText.match(/free points[^0-9]*(\d+)/i);
              const freePoints = freePointsMatch ? Number(freePointsMatch[1]) || 0 : 0;

              return JSON.stringify({
                levelUpAvailable: !!document.querySelector('.bigSpeechBubble.levelUp'),
                freePoints,
                fightingStrength: parseAttributeValue(findRow(['fighting strength'])),
                offenceBonus: parseAttributeValue(findRow(['offence bonus', 'offense bonus'])),
                defenceBonus: parseAttributeValue(findRow(['defence bonus', 'defense bonus'])),
                resources: parseAttributeValue(findRow(['resources']))
              });
            }
            """);

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new HeroAttributeSnapshot();
        }

        return JsonSerializer.Deserialize<HeroAttributeSnapshot>(rawJson) ?? new HeroAttributeSnapshot();
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

              const button = row.querySelector('td.pointsValueSetter.add a.setPoint, a.setPoint');
              if (!button) return false;
              const cls = clean(button.className || '');
              if (cls.includes('disabled')) return false;
              button.click();
              return true;
            }
            """,
            stat);
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

    private async Task<bool> TrySendHeroToAdventureAsync(CancellationToken cancellationToken)
    {
        await GotoAsync(Paths.HeroAdventures, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening adventures.", cancellationToken);
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const buttons = Array.from(document.querySelectorAll('button, a, input[type="submit"]'));
              const candidate = buttons.find(node => {
                const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                const cls = (node.className || '').toLowerCase();
                const href = (node.getAttribute('href') || '').toLowerCase();
                const looksLikeSend = text.includes('adventure') || text.includes('send hero') || text.includes('start') || href.includes('adventure');
                const isGold = text.includes('gold') || cls.includes('gold') || text.includes('instant');
                const disabled = node.hasAttribute('disabled') || cls.includes('disabled');
                return looksLikeSend && !isGold && !disabled;
              });
              if (!candidate) return false;
              candidate.click();
              return true;
            }
            """);
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

        [JsonPropertyName("hpPercent")]
        public int? HpPercent { get; init; }

        [JsonPropertyName("adventuresAvailable")]
        public int? AdventuresAvailable { get; init; }

        [JsonPropertyName("secondsUntilAdventureReady")]
        public int? SecondsUntilAdventureReady { get; init; }

        [JsonPropertyName("secondsUntilReturn")]
        public int? SecondsUntilReturn { get; init; }

        [JsonPropertyName("unassignedPoints")]
        public int? UnassignedPoints { get; init; }
    }
}
