using Microsoft.Playwright;
using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Hero status, home/away, health and timer reads.
public sealed partial class TravianClient
{
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
                await GotoAsync(Paths.HeroAttributes, cancellationToken);
                await WaitForPageReadyAsync(cancellationToken);
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

        await EnsureLoggedInAsync();
        if (!forceReload || !onDorf1)
        {
            return;
        }

        Notify("[hero:verbose] reloading dorf1 to refresh hero sidebar");
        await ReloadPageTracedAsync(
            _page,
            "refresh hero sidebar",
            new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded },
            cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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

    private async Task<int?> ReadHeroReturnFromRallyPointAsync(CancellationToken cancellationToken)
    {
        Notify("[hero:verbose] ReadHeroReturnFromRallyPoint starting");
        await GotoAsync(Paths.RallyPointTroops, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load

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
            if (!IsCurrentUrlForPath(Paths.HeroAdventures))
            {
                await GotoAsync(Paths.HeroAdventures, cancellationToken);
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
            await GotoAsync(Paths.HeroAttributes, cancellationToken);
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

        await GotoAsync(Paths.HeroAttributes, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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

}
