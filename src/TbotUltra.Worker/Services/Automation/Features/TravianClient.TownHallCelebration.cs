using System.Text.Json;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int TownHallCelebrationRetrySeconds = 60;
    private const int TownHallBigCelebrationRequiredLevel = 10;
    internal const string TownHallCelebrationStartLinkSelector =
        ".cta a[href*='a=1'], .cta a[href*='celebr'], td.act a[href*='a=1'], td.act a[href*='celebr']";

    public async Task<string> RunTownHallCelebrationAsync(
        string? requestedMode,
        int targetCount,
        bool restartDelayEnabled,
        double restartDelayMinMinutes,
        double restartDelayMaxMinutes,
        CancellationToken cancellationToken = default)
    {
        Notify("[town-hall] celebration run starting");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var buildings = (await ReadBuildingsStatusAsync(cancellationToken)).Buildings;
        var townHall = ResolveTownHallBuilding(buildings);
        var townHallSlotId = townHall?.SlotId;
        if (townHallSlotId is not > 0)
        {
            townHallSlotId = await TryProbeTownHallSlotOnDorf2Async(cancellationToken);
        }

        if (townHallSlotId is not > 0)
        {
            Notify("[town-hall] skip - Town Hall not found in this village");
            return "Town Hall celebration: Town Hall not found. town_hall_unavailable=missing";
        }

        var goalCount = TownHallCelebrationDefaults.NormalizeCount(targetCount);
        if (goalCount > TownHallCelebrationDefaults.MinCount
            && !await IsTravianPlusActiveAsync(cancellationToken))
        {
            goalCount = TownHallCelebrationDefaults.MinCount;
            Notify("[town-hall] two celebrations requested but Travian Plus is inactive; using one celebration.");
        }

        var restartDelaySeconds = restartDelayEnabled
            ? ResolveRestartDelaySeconds(restartDelayMinMinutes, restartDelayMaxMinutes)
            : 0;

        await GotoAsync(Paths.BuildBySlot(townHallSlotId.Value), cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var mode = TownHallCelebrationDefaults.NormalizeMode(requestedMode);
        var level = townHall?.Level ?? 0;
        if (string.Equals(mode, TownHallCelebrationDefaults.Big, StringComparison.Ordinal)
            && level < TownHallBigCelebrationRequiredLevel)
        {
            Notify($"[town-hall] big requested but Town Hall level is {level}; falling back to small.");
            mode = TownHallCelebrationDefaults.Small;
        }

        var status = await ReadTownHallCelebrationStatusFromCurrentPageAsync(cancellationToken);

        // Target already met: wait until the soonest celebration frees a slot, plus a random restart delay
        // so a new one is not started the instant the timer hits zero.
        if (status.ActiveCount >= goalCount)
        {
            var waitSeconds = ResolveTownHallSlotFreeWaitSeconds(status, restartDelaySeconds);
            Notify($"[town-hall] {status.ActiveCount}/{goalCount} celebration(s) active - waiting {TravianParsing.FormatDuration(waitSeconds)} (incl. {restartDelaySeconds}s restart delay).");
            return $"Town Hall celebration running. queue_wait_seconds={waitSeconds}";
        }

        // Below target: start celebrations until the target is reached or the server/resources stop us.
        _heroTransferOverLimitWaitSeconds = null;
        while (status.ActiveCount < goalCount)
        {
            Notify($"[town-hall] attempting to start {mode} celebration at slot {townHallSlotId.Value} (active {status.ActiveCount}, target {goalCount})");

            // Resource-blocked rows can contain a generic a.research link that opens the hero inventory.
            // Detect the shortfall before selecting a start action so that link is never mistaken for Start.
            var resourceWaitMessage = await TryBuildTownHallCelebrationResourceWaitMessageAsync(mode, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resourceWaitMessage))
            {
                if (!await TryHeroResourceTransferForTownHallAsync(
                        $"Town Hall {mode} celebration (slot {townHallSlotId.Value})",
                        mode,
                        cancellationToken))
                {
                    if (_heroTransferOverLimitWaitSeconds is not null)
                    {
                        resourceWaitMessage = await TryBuildTownHallCelebrationResourceWaitMessageAsync(mode, cancellationToken)
                            ?? resourceWaitMessage;
                    }

                    return resourceWaitMessage;
                }

                Notify("[town-hall] topped up from the hero inventory; retrying start.");
            }

            var startAttempt = await TryStartTownHallCelebrationFromCurrentPageAsync(mode, cancellationToken);
            if (!startAttempt.Started && status.SlotOccupied && status.ActiveCount >= 1)
            {
                // No Plus (or the queue is full): a celebration is already running and no further one can be
                // queued. Wait for the running one to free a slot instead of retrying an impossible start or
                // computing a resource wait for a slot that cannot be filled.
                var waitSeconds = ResolveTownHallSlotFreeWaitSeconds(status, restartDelaySeconds);
                Notify($"[town-hall] a celebration is already running and no more can be queued; waiting {TravianParsing.FormatDuration(waitSeconds)}.");
                return $"Town Hall celebration running. queue_wait_seconds={waitSeconds}";
            }

            if (!startAttempt.Started)
            {
                resourceWaitMessage = await TryBuildTownHallCelebrationResourceWaitMessageAsync(mode, cancellationToken);
                if (!string.IsNullOrWhiteSpace(resourceWaitMessage))
                {
                    // A start row exists but lacks resources (with Plus a second can still be queued) — come
                    // back when resources are ready to fill the remaining slot.
                    return resourceWaitMessage;
                }

                if (status.ActiveCount >= 1)
                {
                    // One celebration is already running but no further slot can be filled right now (e.g. the
                    // server won't queue a second without Plus). Wait for the running one to free a slot.
                    var waitSeconds = ResolveTownHallSlotFreeWaitSeconds(status, restartDelaySeconds);
                    Notify($"[town-hall] cannot queue another celebration now; waiting {TravianParsing.FormatDuration(waitSeconds)} for the running one.");
                    return $"Town Hall celebration running. queue_wait_seconds={waitSeconds}";
                }

                Notify($"[town-hall] start failed - {startAttempt.Message}");
                return $"{startAttempt.Message} queue_wait_seconds={TownHallCelebrationRetrySeconds}";
            }

            var startHref = ResolveUrl(startAttempt.Href);
            if (!string.IsNullOrWhiteSpace(startHref))
            {
                await GotoAsync(startHref, cancellationToken);
                await EnsureLoggedInAsync(cancellationToken: cancellationToken);
            }
            else
            {
                await WaitForPageReadyAsync(cancellationToken);
            }

            await GotoAsync(Paths.BuildBySlot(townHallSlotId.Value), cancellationToken);
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);

            var reread = await ReadTownHallCelebrationStatusFromCurrentPageAsync(cancellationToken);
            if (reread.ActiveCount <= status.ActiveCount)
            {
                Notify("[town-hall] start did not register - will retry");
                return $"Town Hall celebration: start did not register, retrying. queue_wait_seconds={TownHallCelebrationRetrySeconds}";
            }

            status = reread;
        }

        var finalWaitSeconds = ResolveTownHallSlotFreeWaitSeconds(status, restartDelaySeconds);
        Notify($"[town-hall] {mode} celebration active ({status.ActiveCount}/{goalCount}) - {TravianParsing.FormatDuration(finalWaitSeconds)} until next check.");
        return $"Town Hall celebration started. mode={mode} queue_wait_seconds={finalWaitSeconds}";
    }

    // Seconds until the soonest-finishing celebration frees a slot, plus the random restart delay. Used both
    // when the target is already met and when only one can run — so the next celebration never starts the
    // instant the timer hits zero.
    private static int ResolveTownHallSlotFreeWaitSeconds(TownHallCelebrationPageStatus status, int restartDelaySeconds)
    {
        var ongoingSeconds = status.RemainingSeconds
            ?? TravianParsing.ParseDurationToSeconds(status.RemainingText)
            ?? TownHallCelebrationRetrySeconds;
        return Math.Max(1, ongoingSeconds + Math.Max(0, restartDelaySeconds));
    }

    // Random delay (seconds) in the configured min-max minute range. 0/0 (or max<=0) disables it.
    internal static int ResolveRestartDelaySeconds(double minMinutes, double maxMinutes)
    {
        var min = Math.Max(0, minMinutes);
        var max = Math.Max(min, maxMinutes);
        if (max <= 0)
        {
            return 0;
        }

        var minutes = min + (Random.Shared.NextDouble() * (max - min));
        return (int)Math.Round(minutes * 60);
    }

    private static Building? ResolveTownHallBuilding(IReadOnlyList<Building> buildings)
    {
        return buildings.FirstOrDefault(item =>
            item.Gid == 24
            || string.Equals(item.Name, "Town Hall", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<int?> TryProbeTownHallSlotOnDorf2Async(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrentUrlForPath(Paths.Buildings))
            {
                await GotoAsync(Paths.Buildings, cancellationToken);
                await EnsureLoggedInAsync(cancellationToken: cancellationToken);
            }

            var payload = await _page.EvaluateAsync<JsonElement>(
                """
                () => {
                  const slotFromText = (text) => {
                    if (!text) return null;
                    const m1 = String(text).match(/[?&](?:id|a)=(\d{1,2})\b/i);
                    if (m1) return parseInt(m1[1], 10);
                    const m2 = String(text).match(/\baid(\d{1,2})\b/i);
                    if (m2) return parseInt(m2[1], 10);
                    const m3 = String(text).match(/\ba(\d{1,2})\b/i);
                    if (m3) return parseInt(m3[1], 10);
                    return null;
                  };

                  const collectFromElement = (el) => {
                    if (!el) return null;
                    const candidates = [
                      el.getAttribute && el.getAttribute('data-aid'),
                      el.getAttribute && el.getAttribute('data-slot'),
                      el.getAttribute && el.getAttribute('href'),
                      el.className || '',
                      el.outerHTML || ''
                    ];
                    for (const c of candidates) {
                      const slot = slotFromText(c);
                      if (slot && slot >= 19 && slot <= 40) return slot;
                    }
                    let parent = el.parentElement;
                    for (let i = 0; parent && i < 4; i++, parent = parent.parentElement) {
                      const slot = slotFromText((parent.className || '') + ' ' + (parent.getAttribute && parent.getAttribute('href') || ''));
                      if (slot && slot >= 19 && slot <= 40) return slot;
                    }
                    return null;
                  };

                  const selectors = [
                    'div.buildingSlot.g24',
                    'div.buildingSlot[class*=" g24"]',
                    'div.buildingSlot[class^="g24"]',
                    '[data-gid="24"]',
                    'area.g24',
                    'area[class*=" g24"]',
                    'area[class^="g24"]',
                    'a[href*="gid=24"]',
                    'img[alt="Town Hall" i]',
                    '[title="Town Hall" i]'
                  ];

                  for (const sel of selectors) {
                    const nodes = document.querySelectorAll(sel);
                    for (const node of nodes) {
                      const slot = collectFromElement(node);
                      if (slot) return { slotId: slot, source: sel };
                    }
                  }

                  const slots = Array.from(document.querySelectorAll('div.buildingSlot'));
                  for (const slot of slots) {
                    const img = slot.querySelector('img[alt], [title]');
                    const alt = img ? (img.getAttribute('alt') || img.getAttribute('title') || '') : '';
                    if (/town\s*hall/i.test(alt)) {
                      const id = collectFromElement(slot);
                      if (id) return { slotId: id, source: 'alt-town-hall' };
                    }
                  }

                  return { slotId: null, source: null };
                }
                """);

            if (payload.TryGetProperty("slotId", out var slotIdNode)
                && slotIdNode.ValueKind == JsonValueKind.Number
                && slotIdNode.TryGetInt32(out var slot)
                && slot is >= 19 and <= 40)
            {
                var source = payload.TryGetProperty("source", out var sourceNode) ? sourceNode.GetString() : null;
                Notify($"[town-hall:verbose] fallback probe found slot {slot} via {source ?? "unknown"}");
                return slot;
            }
        }
        catch (Exception ex) when (!IsTransientExecutionContextException(ex))
        {
            Notify($"[town-hall:verbose] fallback probe failed: {ex.Message}");
        }

        return null;
    }

    private async Task<TownHallCelebrationPageStatus> ReadTownHallCelebrationStatusFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await _page.EvaluateAsync<JsonElement>(
            """
            (startLinkSelector) => {
              const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
              const root = document.querySelector('.build_details.researches, .researches');
              // The "Ongoing celebration" table (class under_progress) is a SIBLING of the .build_details
              // "Hold a celebration" block, not inside it. Scoping the running lookup to root misses a
              // celebration that just started and reports a false "did not register", so search the whole
              // document — but only the under_progress table that actually mentions a celebration, so an
              // unrelated construction under_progress can never be misread as a running celebration.
              const runningTable = Array.from(document.querySelectorAll('table.under_progress, .under_progress'))
                .find(node => /celebration/i.test(normalize(node.textContent || '')));
              // Each row in the ongoing-celebration table has its own .timer, so the number of timers is
              // the number of active celebrations (1 = one running, 2 = one running + one queued via Plus).
              // The first timer is the one that finishes soonest (the currently running celebration).
              const runningTimers = runningTable ? Array.from(runningTable.querySelectorAll('.timer')) : [];
              const activeCount = runningTimers.length;
              const runningTimer = runningTimers[0] || null;
              const runningText = normalize(runningTimer ? runningTimer.textContent : '');
              const runningValueRaw = runningTimer ? runningTimer.getAttribute('value') : null;
              const runningValue = runningValueRaw ? parseInt(runningValueRaw, 10) : null;
              const inProgressLabel = Array.from(root?.querySelectorAll('.act .none, .under_progress, .act') || [])
                .map(node => normalize(node.textContent || ''))
                .find(text => /celebration is in progress/i.test(text) || /celebration running/i.test(text) || /underway/i.test(text)) || '';
              const rows = Array.from(root?.querySelectorAll('.researches .research, .research') || []);
              const smallRow = rows.find(row => /small\s+celebration/i.test(normalize(row.textContent || '')));
              const startLink = smallRow?.querySelector(startLinkSelector);
              const startButton = smallRow?.querySelector('.cta button:not([disabled]):not(.disabled), td.act button:not([disabled]):not(.disabled)');
              const canStart = (!!startLink) || (!!startButton && !startButton.disabled);
              const actText = normalize(smallRow?.querySelector('.cta, td.act')?.textContent || '');
              const celebrationRunning = !!runningTimer || /celebration is in progress/i.test(inProgressLabel) || /celebration running/i.test(inProgressLabel);
              // Without Travian Plus only one celebration can run at a time: while one is active the start
              // CTA is empty and the row shows "There is already a celebration going on". Flag that so the
              // caller waits for the running one instead of computing a resource wait for a slot it can't fill.
              const slotOccupied = Array.from(document.querySelectorAll('.errorMessage'))
                .map(node => normalize(node.textContent || ''))
                .some(text => /already[^.]*celebration/i.test(text) || /celebration[^.]*going on/i.test(text));
              const statusText = celebrationRunning
                ? 'Celebration running.'
                : canStart
                  ? 'Ready.'
                  : (actText || 'Celebration unavailable.');

              return {
                celebrationRunning,
                activeCount,
                slotOccupied,
                remainingText: runningText,
                remainingSeconds: Number.isFinite(runningValue) && runningValue > 0 ? runningValue : null,
                canStart,
                statusText
              };
            }
            """,
            TownHallCelebrationStartLinkSelector);

        var celebrationRunning = payload.TryGetProperty("celebrationRunning", out var celebrationRunningNode)
            && celebrationRunningNode.ValueKind == JsonValueKind.True;
        var activeCount = 0;
        if (payload.TryGetProperty("activeCount", out var activeCountNode)
            && activeCountNode.ValueKind == JsonValueKind.Number
            && activeCountNode.TryGetInt32(out var parsedActiveCount)
            && parsedActiveCount > 0)
        {
            activeCount = parsedActiveCount;
        }
        var remainingText = payload.TryGetProperty("remainingText", out var remainingTextNode)
            ? remainingTextNode.GetString() ?? string.Empty
            : string.Empty;
        int? remainingSeconds = null;
        if (payload.TryGetProperty("remainingSeconds", out var remainingSecondsNode)
            && remainingSecondsNode.ValueKind == JsonValueKind.Number
            && remainingSecondsNode.TryGetInt32(out var parsedSeconds)
            && parsedSeconds > 0)
        {
            remainingSeconds = parsedSeconds;
        }
        var canStart = payload.TryGetProperty("canStart", out var canStartNode)
            && canStartNode.ValueKind == JsonValueKind.True;
        var slotOccupied = payload.TryGetProperty("slotOccupied", out var slotOccupiedNode)
            && slotOccupiedNode.ValueKind == JsonValueKind.True;
        var statusText = payload.TryGetProperty("statusText", out var statusTextNode)
            ? statusTextNode.GetString() ?? string.Empty
            : string.Empty;

        return new TownHallCelebrationPageStatus(
            celebrationRunning,
            activeCount,
            slotOccupied,
            remainingText,
            remainingSeconds,
            canStart,
            string.IsNullOrWhiteSpace(statusText) ? "Celebration unavailable." : statusText);
    }

    private async Task<TownHallCelebrationStartAttempt> TryStartTownHallCelebrationFromCurrentPageAsync(
        string mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        var payload = await _page.EvaluateAsync<JsonElement>(
            """
            (args) => {
              const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
              const root = document.querySelector('.build_details.researches, .researches');
              const rows = Array.from(root?.querySelectorAll('.researches .research, .research') || []);
              const rowPattern = args.mode === 'big'
                ? /(big|great|large)\s+celebration/i
                : /small\s+celebration/i;
              const row = rows.find(candidate => rowPattern.test(normalize(candidate.textContent || '')));
              if (!row) return { kind: 'none', href: '' };
              const link = row.querySelector(args.startLinkSelector);
              if (link) {
                return { kind: 'link', href: link.getAttribute('href') || '' };
              }
              const button = row.querySelector('.cta button:not([disabled]):not(.disabled), td.act button:not([disabled]):not(.disabled)');
              if (button && !button.disabled) {
                button.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                return { kind: 'button', href: '' };
              }
              return { kind: 'none', href: '' };
            }
            """,
            new { mode, startLinkSelector = TownHallCelebrationStartLinkSelector });

        var kind = payload.TryGetProperty("kind", out var kindNode) ? kindNode.GetString() ?? "none" : "none";
        var href = payload.TryGetProperty("href", out var hrefNode) ? hrefNode.GetString() ?? string.Empty : string.Empty;

        return kind switch
        {
            "link" => new TownHallCelebrationStartAttempt(true, "Town Hall celebration started.", href),
            "button" => new TownHallCelebrationStartAttempt(true, "Town Hall celebration started.", string.Empty),
            _ => new TownHallCelebrationStartAttempt(false, "Town Hall celebration: no start button available.", string.Empty),
        };
    }

    private async Task<string?> TryBuildTownHallCelebrationResourceWaitMessageAsync(
        string mode,
        CancellationToken cancellationToken)
    {
        if (_heroTransferOverLimitWaitSeconds is int heroTransferWaitSeconds)
        {
            _heroTransferOverLimitWaitSeconds = null;
            var heroGateWaitSeconds = Math.Max(1, heroTransferWaitSeconds);
            Notify($"[town-hall] {mode} celebration waiting for hero-resource gate. queue_wait_seconds={heroGateWaitSeconds}");
            return $"Town Hall {mode} celebration blocked by hero resource limits. queue_wait_seconds={heroGateWaitSeconds}";
        }

        var pageTimerWaitSeconds = await ReadTownHallCelebrationResourceTimerSecondsAsync(mode, cancellationToken);
        if (pageTimerWaitSeconds is int timerWaitSeconds && timerWaitSeconds > 0)
        {
            var clampedTimerWaitSeconds = UpgradeMath.ClampResourceWaitSeconds(timerWaitSeconds);
            Notify($"[town-hall] {mode} celebration lacks resources; page timer says wait {clampedTimerWaitSeconds}s.");
            return $"Town Hall {mode} celebration blocked by resources. queue_wait_seconds={clampedTimerWaitSeconds}";
        }

        var shortfall = await ReadUpgradeShortfallOnBuildPageAsync(
            cancellationToken,
            preferTownHallCelebration: true,
            townHallCelebrationMode: mode);
        if (shortfall is null
            || (shortfall.Wood <= 0 && shortfall.Clay <= 0 && shortfall.Iron <= 0 && shortfall.Crop <= 0))
        {
            return null;
        }

        var pageProductionByHour = await ReadCurrentPageResourceProductionByHourAsync(cancellationToken);
        var cachedProductionByHour = await ReadCachedProductionByHourForActiveVillageAsync(cancellationToken);
        var productionByHour = ResourceSnapshotCalculator.MergeProductionByHour(
            pageProductionByHour,
            cachedProductionByHour);
        var productionSource = HasAnyProduction(pageProductionByHour)
            ? HasAnyProduction(cachedProductionByHour) ? "page_or_cached_production" : "page_resources"
            : HasAnyProduction(cachedProductionByHour) ? "cached_production" : "fallback";

        var waitSeconds = UpgradeMath.ComputeResourceAccumulationWaitSeconds(
            shortfall.Wood,
            shortfall.Clay,
            shortfall.Iron,
            shortfall.Crop,
            productionByHour);
        Notify($"[town-hall] {mode} celebration lacks resources "
            + $"(missing wood={shortfall.Wood} clay={shortfall.Clay} iron={shortfall.Iron} crop={shortfall.Crop}); "
            + $"waiting {waitSeconds}s ({productionSource}).");
        return $"Town Hall {mode} celebration blocked by resources. queue_wait_seconds={waitSeconds}";
    }

    private async Task<int?> ReadTownHallCelebrationResourceTimerSecondsAsync(
        string mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await _page.EvaluateAsync<int?>(
                """
                (mode) => {
                  const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
                  const root = document.querySelector('.build_details.researches, .researches');
                  const rowPattern = mode === 'big'
                    ? /(big|great|large)\s+celebration/i
                    : /small\s+celebration/i;
                  const rows = Array.from(root?.querySelectorAll('.researches .research, .research') || []);
                  // Never fall back to the whole page: the building's own upgrade block has a .timer
                  // ("Enough resources on ...") that would be misread as the celebration's wait.
                  const row = rows.find(candidate => rowPattern.test(normalize(candidate.textContent || '')));
                  if (!row) return null;
                  const timer = row.querySelector('.errorMessage .timer, .timer');
                  const raw = timer?.getAttribute('value') || timer?.getAttribute('data-value') || '';
                  const parsed = raw ? parseInt(raw, 10) : NaN;
                  if (Number.isFinite(parsed) && parsed > 0) return parsed;
                  const text = normalize(timer?.textContent || '');
                  const match = text.match(/(?:(\d{1,3}):)?(\d{1,2}):(\d{1,2})/);
                  if (!match) return null;
                  const h = match[1] ? parseInt(match[1], 10) : 0;
                  const m = parseInt(match[2], 10);
                  const s = parseInt(match[3], 10);
                  return h * 3600 + m * 60 + s;
                }
                """,
                mode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify($"[town-hall:verbose] resource timer read failed: {ex.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, double?>> ReadCurrentPageResourceProductionByHourAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await _page.EvaluateAsync<Dictionary<string, double?>>(
                """
                () => {
                  const source = window.resources?.production || {};
                  const read = key => {
                    const value = source[key];
                    const parsed = typeof value === 'number' ? value : Number(String(value || '').replace(/[^0-9.-]/g, ''));
                    return Number.isFinite(parsed) ? parsed : null;
                  };
                  return {
                    wood: read('l1'),
                    clay: read('l2'),
                    iron: read('l3'),
                    crop: read('l4')
                  };
                }
                """);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify($"[town-hall:verbose] page production read failed: {ex.Message}");
            return new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed record TownHallCelebrationPageStatus(
        bool CelebrationRunning,
        int ActiveCount,
        bool SlotOccupied,
        string RemainingText,
        int? RemainingSeconds,
        bool CanStart,
        string StatusText);

    private sealed record TownHallCelebrationStartAttempt(
        bool Started,
        string Message,
        string Href);
}
