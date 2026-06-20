using System.Text.Json;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int TownHallCelebrationRetrySeconds = 60;
    private const int TownHallBigCelebrationRequiredLevel = 10;

    public async Task<string> RunTownHallCelebrationAsync(
        string? requestedMode,
        CancellationToken cancellationToken = default)
    {
        Notify("[town-hall] celebration run starting");
        await EnsureLoggedInAsync();

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
            return "Town Hall celebration: Town Hall not found. queue_wait_seconds=600";
        }

        await GotoAsync(Paths.BuildBySlot(townHallSlotId.Value), cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Town Hall.", cancellationToken);
        await EnsureLoggedInAsync();

        var pageStatus = await ReadTownHallCelebrationStatusFromCurrentPageAsync(cancellationToken);
        var runningSeconds = pageStatus.RemainingSeconds ?? TravianParsing.ParseDurationToSeconds(pageStatus.RemainingText);
        if (pageStatus.CelebrationRunning && runningSeconds is > 0)
        {
            Notify($"[town-hall] already running - {TravianParsing.FormatDuration(runningSeconds.Value)} remaining");
            return $"Town Hall celebration running. queue_wait_seconds={Math.Max(1, runningSeconds.Value)}";
        }

        var mode = TownHallCelebrationDefaults.NormalizeMode(requestedMode);
        var level = townHall?.Level ?? 0;
        if (string.Equals(mode, TownHallCelebrationDefaults.Big, StringComparison.Ordinal)
            && level < TownHallBigCelebrationRequiredLevel)
        {
            Notify($"[town-hall] big requested but Town Hall level is {level}; falling back to small.");
            mode = TownHallCelebrationDefaults.Small;
        }

        if (string.Equals(mode, TownHallCelebrationDefaults.Big, StringComparison.Ordinal))
        {
            Notify("[town-hall] big celebration requested but its start selector is not verified yet.");
            return "Town Hall celebration: big celebration start selector not verified yet. queue_wait_seconds=600";
        }

        Notify($"[town-hall] attempting to start {mode} celebration at slot {townHallSlotId.Value}");
        var startAttempt = await TryStartTownHallCelebrationFromCurrentPageAsync(mode, cancellationToken);
        if (!startAttempt.Started)
        {
            if (await TryHeroResourceTransferForTownHallAsync(
                    $"Town Hall {mode} celebration (slot {townHallSlotId.Value})", cancellationToken))
            {
                Notify("[town-hall] topped up from the hero inventory; retrying start.");
                startAttempt = await TryStartTownHallCelebrationFromCurrentPageAsync(mode, cancellationToken);
            }
        }

        if (!startAttempt.Started)
        {
            var resourceWaitMessage = await TryBuildTownHallCelebrationResourceWaitMessageAsync(mode, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resourceWaitMessage))
            {
                return resourceWaitMessage;
            }

            Notify($"[town-hall] start failed - {startAttempt.Message}");
            return $"{startAttempt.Message} queue_wait_seconds={TownHallCelebrationRetrySeconds}";
        }

        var startHref = ResolveUrl(startAttempt.Href);
        if (!string.IsNullOrWhiteSpace(startHref))
        {
            await GotoAsync(startHref, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared after starting Town Hall celebration.", cancellationToken);
            await EnsureLoggedInAsync();
        }
        else
        {
            await WaitForPageReadyAsync(cancellationToken);
        }

        await GotoAsync(Paths.BuildBySlot(townHallSlotId.Value), cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after navigating back to Town Hall.", cancellationToken);
        await EnsureLoggedInAsync();

        var startedStatus = await ReadTownHallCelebrationStatusFromCurrentPageAsync(cancellationToken);
        var remainingSeconds = startedStatus.RemainingSeconds
            ?? TravianParsing.ParseDurationToSeconds(startedStatus.RemainingText)
            ?? TownHallCelebrationRetrySeconds;

        if (!startedStatus.CelebrationRunning)
        {
            Notify("[town-hall] start did not register - will retry");
            return $"Town Hall celebration: start did not register, retrying. queue_wait_seconds={TownHallCelebrationRetrySeconds}";
        }

        Notify($"[town-hall] {mode} celebration started - {TravianParsing.FormatDuration(Math.Max(1, remainingSeconds))} remaining");
        return $"Town Hall celebration started. mode={mode} queue_wait_seconds={Math.Max(1, remainingSeconds)}";
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
                await PauseForManualStepIfVisibleAsync("Manual verification appeared while probing Town Hall slot on dorf2.", cancellationToken);
                await EnsureLoggedInAsync();
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
            () => {
              const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
              const root = document.querySelector('.build_details') || document;
              const runningTimer =
                root.querySelector('.under_progress .timer, table.under_progress .timer, .under_progress span.timer');
              const runningText = normalize(runningTimer ? runningTimer.textContent : '');
              const runningValueRaw = runningTimer ? runningTimer.getAttribute('value') : null;
              const runningValue = runningValueRaw ? parseInt(runningValueRaw, 10) : null;
              const inProgressLabel = Array.from(root.querySelectorAll('.act .none, .under_progress, .act'))
                .map(node => normalize(node.textContent || ''))
                .find(text => /celebration is in progress/i.test(text) || /celebration running/i.test(text) || /underway/i.test(text)) || '';
              const rows = Array.from(root.querySelectorAll('.research, tr, li, .row, .information'));
              const smallRow = rows.find(row => /small\s+celebration/i.test(normalize(row.textContent || '')));
              const startLink = smallRow?.querySelector('.cta a.research, .cta a[href*="a=1"], .cta a[href*="celebr"], td.act a.research, td.act a[href*="a=1"], td.act a[href*="celebr"]')
                || root.querySelector('.cta a.research, .cta a[href*="a=1"], td.act a.research, td.act a[href*="a=1"]');
              const startButton = smallRow?.querySelector('.cta button:not([disabled]):not(.disabled), td.act button:not([disabled]):not(.disabled)')
                || root.querySelector('.cta button:not([disabled]):not(.disabled), td.act button:not([disabled]):not(.disabled)');
              const canStart = (!!startLink) || (!!startButton && !startButton.disabled);
              const actText = normalize((smallRow?.querySelector('.cta, td.act') || root.querySelector('.cta, td.act'))?.textContent || '');
              const celebrationRunning = !!runningTimer || /celebration is in progress/i.test(inProgressLabel) || /celebration running/i.test(inProgressLabel);
              const statusText = celebrationRunning
                ? 'Celebration running.'
                : canStart
                  ? 'Ready.'
                  : (actText || 'Celebration unavailable.');

              return {
                celebrationRunning,
                remainingText: runningText,
                remainingSeconds: Number.isFinite(runningValue) && runningValue > 0 ? runningValue : null,
                canStart,
                statusText
              };
            }
            """);

        var celebrationRunning = payload.TryGetProperty("celebrationRunning", out var celebrationRunningNode)
            && celebrationRunningNode.ValueKind == JsonValueKind.True;
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
        var statusText = payload.TryGetProperty("statusText", out var statusTextNode)
            ? statusTextNode.GetString() ?? string.Empty
            : string.Empty;

        return new TownHallCelebrationPageStatus(
            celebrationRunning,
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
        var payload = await _page.EvaluateAsync<JsonElement>(
            """
            (mode) => {
              const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
              const root = document.querySelector('.build_details') || document;
              const rows = Array.from(root.querySelectorAll('.research, tr, li, .row, .information'));
              const rowPattern = mode === 'big'
                ? /(big|great|large)\s+celebration/i
                : /small\s+celebration/i;
              const row = rows.find(candidate => rowPattern.test(normalize(candidate.textContent || '')));
              const scope = row || root;
              const link = scope.querySelector('.cta a.research, .cta a[href*="a=1"], .cta a[href*="celebr"], td.act a.research, td.act a[href*="a=1"], td.act a[href*="celebr"]')
                || (mode === 'small' ? root.querySelector('.cta a.research, .cta a[href*="a=1"], td.act a.research, td.act a[href*="a=1"]') : null);
              if (link) {
                return { kind: 'link', href: link.getAttribute('href') || '' };
              }
              const button = scope.querySelector('.cta button:not([disabled]):not(.disabled), td.act button:not([disabled]):not(.disabled)')
                || (mode === 'small' ? root.querySelector('.cta button:not([disabled]):not(.disabled), td.act button:not([disabled]):not(.disabled)') : null);
              if (button && !button.disabled) {
                button.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                return { kind: 'button', href: '' };
              }
              return { kind: 'none', href: '' };
            }
            """,
            mode);

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
            preferTownHallCelebration: true);
        if (shortfall is null
            || (shortfall.Wood <= 0 && shortfall.Clay <= 0 && shortfall.Iron <= 0 && shortfall.Crop <= 0))
        {
            return null;
        }

        var productionByHour = await ReadCurrentPageResourceProductionByHourAsync(cancellationToken);
        var productionSource = "page_resources";
        if (!HasAnyProduction(productionByHour))
        {
            productionByHour = await ReadCachedProductionByHourForActiveVillageAsync(cancellationToken);
            productionSource = HasAnyProduction(productionByHour) ? "cached_production" : "fallback";
        }

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
                  const root = document.querySelector('.build_details') || document;
                  const rowPattern = mode === 'big'
                    ? /(big|great|large)\s+celebration/i
                    : /small\s+celebration/i;
                  const rows = Array.from(root.querySelectorAll('.research, tr, li, .row, .information'));
                  const row = rows.find(candidate => rowPattern.test(normalize(candidate.textContent || ''))) || root;
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
        string RemainingText,
        int? RemainingSeconds,
        bool CanStart,
        string StatusText);

    private sealed record TownHallCelebrationStartAttempt(
        bool Started,
        string Message,
        string Href);
}
