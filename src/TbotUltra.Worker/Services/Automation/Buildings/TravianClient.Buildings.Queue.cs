using System.Globalization;
using System.Text.Json;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Construction queue snapshots, slot availability and humanized defer decisions.
public sealed partial class TravianClient
{
    private async Task<IReadOnlyList<BuildQueueItem>> ReadBuildQueueAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading the build queue.", cancellationToken);
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const parseNumber = (value) => {
                if (value == null || value === '') return null;
                const parsed = Number(value);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const readUrlParam = (href, names) => {
                if (!href) return null;
                try {
                  const url = new URL(href, window.location.href);
                  for (const name of names) {
                    const parsed = parseNumber(url.searchParams.get(name));
                    if (parsed != null) return parsed;
                  }
                } catch {
                  for (const name of names) {
                    const match = href.match(new RegExp(`[?&]${name}=(\\d{1,2})(?:\\D|$)`, 'i'));
                    if (match) return Number(match[1]);
                  }
                }
                return null;
              };
              const readElementNumber = (element, attrs, regexes) => {
                for (const attr of attrs) {
                  const parsed = parseNumber(element.getAttribute(attr));
                  if (parsed != null) return parsed;
                }
                const classText = String(element.className || '');
                for (const regex of regexes) {
                  const match = classText.match(regex);
                  if (match) return Number(match[1]);
                }
                return null;
              };
              const selectors = [
                '.buildingList li',
                '#building_contract li',
                '.underConstruction',
                '.buildDuration',
                'table.buildingList tr'
              ];

              // Each matched element is one active construction. Count them per element (NOT deduped by
              // text): two simultaneous upgrades of the same building/field have identical text and must
              // still count as two. Return the first selector that yields any entries.
              for (const selector of selectors) {
                const items = [];
                for (const element of document.querySelectorAll(selector)) {
                  const text = (element.textContent || '').replace(/\s+/g, ' ').trim();
                  if (!text) continue;
                  const timeElement = element.querySelector('.timer, .countdown, .value, [counting="down"], [id^="timer"]');
                  const nameElement = element.querySelector('.name');
                  const durationElement = element.querySelector('.buildDuration');
                  // Broad fallback selectors such as ".buildingList li" can also match nested action/
                  // detail list items. Only count rows that carry actual construction identity/timing.
                  if (!timeElement && !nameElement && !durationElement) continue;
                  const timeLeft = timeElement ? (timeElement.textContent || '').trim() : null;
                  const link = element.querySelector('a[href*="build.php"], a[href*="dorf1.php"], a[href*="dorf2.php"]');
                  const href = link ? (link.getAttribute('href') || '') : '';
                  const slotId =
                    readUrlParam(href, ['id', 'a'])
                    ?? readElementNumber(element, ['data-aid', 'data-slot', 'data-slot-id', 'data-building-slot-id', 'data-id'], [/\baid(\d{1,2})\b/i, /\ba(\d{1,2})\b/i]);
                  const gid =
                    readUrlParam(href, ['gid'])
                    ?? readElementNumber(element, ['data-gid', 'data-building-gid', 'data-type'], [/\bg(\d{1,2})\b/i]);
                  items.push({ text, timeLeft, slotId, gid, href: href || null });
                }
                if (items.length) return JSON.stringify(items);
              }
              return JSON.stringify([]);
            }
            """);

        var raw = string.IsNullOrWhiteSpace(rawJson)
            ? new List<BuildQueueJs>()
            : JsonSerializer.Deserialize<List<BuildQueueJs>>(rawJson) ?? new List<BuildQueueJs>();

        raw ??= [];
        return raw
            .Where(i => !string.IsNullOrWhiteSpace(i.Text))
            .Select(i => new BuildQueueItem(i.Text!, i.TimeLeft, i.SlotId, i.Gid, i.Href))
            .ToList();
    }


    public async Task<IReadOnlyList<ActiveConstruction>> ReadActiveConstructionsAsync(
        CancellationToken cancellationToken = default,
        bool allowNavigationToBuildings = true)
    {
        // Cache hit collapses the 4-5 calls a single upgrade iteration makes (CheckQueueOrDefer,
        // ReadHighestKnownQueuedBuildingLevel, ReadQueuedBuildingWaitSeconds, level-advance poll)
        // into one network round-trip. GotoAsync invalidates the cache automatically.
        if (_cachedActiveConstructions is not null
            && DateTimeOffset.UtcNow - _cachedActiveConstructionsAt < ActiveConstructionsCacheTtl)
        {
            _lastActiveConstructionsFromOverview = _cachedActiveConstructionsFromOverview;
            return _cachedActiveConstructions;
        }

        LogFunctionStarted();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading active constructions.", cancellationToken);
        _lastActiveConstructionsFromOverview = false;

        var raw = await ReadActiveConstructionsOnCurrentPageAsync();
        // The construction queue only renders on dorf1/dorf2 (source of truth). If the current page
        // has none and we are not already on a village overview, read it on dorf2. Some build
        // pages can otherwise report an empty queue and the slot gate wrongly thinks a slot is free.
        if (raw.Count == 0
            && allowNavigationToBuildings
            && !IsCurrentUrlForPath(Paths.Buildings)
            && !IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Buildings, cancellationToken);
            raw = await ReadActiveConstructionsOnCurrentPageAsync();
        }

        var readFromOverview =
            IsCurrentUrlForPath(Paths.Buildings) || IsCurrentUrlForPath(Paths.Resources);
        if (readFromOverview && raw.Count == 0)
        {
            // Empty is destructive state. Confirm it twice on a page that actually owns Travian's
            // construction queue before allowing desktop cache merge to clear a prior non-empty list.
            await Task.Delay(350, cancellationToken);
            raw = await ReadActiveConstructionsOnCurrentPageAsync();
            Notify($"[construction-status:verbose] confirmed empty overview queue with second DOM read.");
        }

        var result = raw
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i =>
            {
                var remainingSeconds = i.TimeLeftSeconds ?? TravianParsing.ParseDurationToSeconds(i.FinishAtText);
                return new ActiveConstruction(
                Kind: i.Kind switch
                {
                    "Resource" => ConstructionKind.Resource,
                    "Building" => ConstructionKind.Building,
                    _ => ConstructionKind.Unknown
                },
                Name: i.Name!,
                Level: i.Level,
                TimeLeftSeconds: remainingSeconds,
                FinishAtText: i.FinishAtText,
                Finish: remainingSeconds is > 0 ? TimerSnapshot.FromRemaining(remainingSeconds.Value) : null,
                SlotId: i.SlotId,
                Gid: i.Gid,
                Href: i.Href);
            })
            .ToList();

        _cachedActiveConstructions = result;
        _cachedActiveConstructionsAt = DateTimeOffset.UtcNow;
        _cachedActiveConstructionsFromOverview = readFromOverview;
        _lastActiveConstructionsFromOverview = readFromOverview;
        return result;

        async Task<List<ActiveConstructionJs>> ReadActiveConstructionsOnCurrentPageAsync()
        {
            var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const parseNumber = (value) => {
                if (value == null || value === '') return null;
                const parsed = Number(value);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const readUrlParam = (href, names) => {
                if (!href) return null;
                try {
                  const url = new URL(href, window.location.href);
                  for (const name of names) {
                    const parsed = parseNumber(url.searchParams.get(name));
                    if (parsed != null) return parsed;
                  }
                } catch {
                  for (const name of names) {
                    const match = href.match(new RegExp(`[?&]${name}=(\\d{1,2})(?:\\D|$)`, 'i'));
                    if (match) return Number(match[1]);
                  }
                }
                return null;
              };
              const readElementNumber = (element, attrs, regexes) => {
                for (const attr of attrs) {
                  const parsed = parseNumber(element.getAttribute(attr));
                  if (parsed != null) return parsed;
                }
                const classText = String(element.className || '');
                for (const regex of regexes) {
                  const match = classText.match(regex);
                  if (match) return Number(match[1]);
                }
                return null;
              };
              const items = [];
              const lis = document.querySelectorAll('.boxes.buildingList ul li, .buildingList ul li');
              for (const li of lis) {
                const nameEl = li.querySelector('.name');
                if (!nameEl) continue;
                const fullName = (nameEl.textContent || '').replace(/\s+/g, ' ').trim();
                if (!fullName) continue;

                const lvlEl = nameEl.querySelector('.lvl');
                const lvlText = (lvlEl?.textContent || '').trim();
                const lvlMatch = lvlText.match(/(\d{1,3})/);
                const level = lvlMatch ? Number(lvlMatch[1]) : null;
                const baseName = lvlEl ? fullName.replace(lvlText, '').trim() : fullName;

                const timer = li.querySelector('.timer, [counting="down"]');
                let timeLeft = null;
                if (timer) {
                  const v = timer.getAttribute('value') || timer.getAttribute('data-value');
                  if (v && !isNaN(Number(v))) timeLeft = Number(v);
                }
                const finishText = (li.querySelector('.buildDuration')?.textContent || '').replace(/\s+/g, ' ').trim();

                const resourceNames = /(woodcutter|clay\s*pit|iron\s*mine|crop\s*land|cropland|skogshugg|lerg|j[äa]rng|s[äa]desf|holzf[äa]ller|lehmgrube|eisenmine|getreidefarm|bois|argile|fer|c[ée]r[ée]ales)/i;
                let kind = 'Unknown';
                if (resourceNames.test(baseName)) kind = 'Resource';
                else if (baseName) kind = 'Building';

                const link = li.querySelector('a[href*="build.php"], a[href*="dorf1.php"], a[href*="dorf2.php"]');
                const href = link ? (link.getAttribute('href') || '') : '';
                const slotId =
                  readUrlParam(href, ['id', 'a'])
                  ?? readElementNumber(li, ['data-aid', 'data-slot', 'data-slot-id', 'data-building-slot-id', 'data-id'], [/\baid(\d{1,2})\b/i, /\ba(\d{1,2})\b/i]);
                const gid =
                  readUrlParam(href, ['gid'])
                  ?? readElementNumber(li, ['data-gid', 'data-building-gid', 'data-type'], [/\bg(\d{1,2})\b/i]);

                items.push({ kind, name: baseName, level, timeLeftSeconds: timeLeft, finishAtText: finishText, slotId, gid, href: href || null });
              }
              return JSON.stringify(items);
            }
            """);

            return string.IsNullOrWhiteSpace(rawJson)
                ? new List<ActiveConstructionJs>()
                : JsonSerializer.Deserialize<List<ActiveConstructionJs>>(rawJson) ?? new List<ActiveConstructionJs>();
        }
    }

    public async Task<ConstructionSlotStatus> EvaluateConstructionSlotsAsync(
        string tribe,
        bool travianPlusActive,
        CancellationToken cancellationToken = default,
        bool allowNavigationToBuildings = true)
    {
        LogFunctionStarted();
        var active = await ReadActiveConstructionsAsync(cancellationToken, allowNavigationToBuildings);
        return ConstructionSlots.Compute(active, tribe, travianPlusActive);
    }

    private async Task<(string Tribe, bool PlusActive)> GetCachedTribeAndPlusAsync(CancellationToken cancellationToken)
    {
        // Tribe is immutable for an account — cache for the entire session, only cleared on logout.
        var tribe = _sessionTribe ?? _cachedTribe;
        if (string.IsNullOrWhiteSpace(tribe) || string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            tribe = await ReadTribeAsync(cancellationToken);
            _cachedTribe = tribe;
            if (!string.IsNullOrWhiteSpace(tribe) && !string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                _sessionTribe = tribe;
            }
            _cachedTribePlusAt = DateTimeOffset.UtcNow;
        }

        var plusActive = await IsTravianPlusActiveAsync(cancellationToken);
        if (_cachedTravianPlusActive != plusActive)
        {
            Notify($"[plus] active={plusActive} (changed)");
            _cachedTravianPlusActive = plusActive;
        }
        return (tribe!, plusActive);
    }

    // Non-blocking pre-flight check. Returns null if a slot is free for `kind`, otherwise
    // a defer message containing queue_wait_seconds=N for the program queue to pick up.
    // Use this instead of WaitForConstructionSlotIfBusyAsync when you want the task to be
    // re-queued by the desktop auto-queue rather than sleep inside the worker call.
    internal async Task<string?> CheckQueueOrDeferAsync(
        ConstructionKind kind,
        int slotId,
        int upgrades,
        CancellationToken cancellationToken,
        bool allowNavigationToBuildings = true)
    {
        var (tribe, plusActive) = await GetCachedTribeAndPlusAsync(cancellationToken);
        if (!allowNavigationToBuildings)
        {
            InvalidateActiveConstructionsCache();
        }

        var status = await EvaluateConstructionSlotsAsync(tribe, plusActive, cancellationToken, allowNavigationToBuildings);
        var canStart = kind == ConstructionKind.Resource ? status.CanStartResource : status.CanStartBuilding;
        if (canStart)
        {
            return null;
        }

        var isRomans = string.Equals(tribe, "Romans", StringComparison.OrdinalIgnoreCase);
        var villageToken = await ResolveConstructionHumanizeVillageTokenAsync(cancellationToken);

        // Keep the humanize transition memory fresh while the slot is full. This records the category
        // as "occupied" during the queue-full wait, so when the slot finally frees the humanize gate
        // sees previous>0 and applies the no-Plus delay (the gate itself only runs once the slot is free).
        if (_config.ConstructionHumanizeDelayEnabled)
        {
            var ongoingInCategory = (isRomans
                    ? status.Active.Where(a => kind == ConstructionKind.Resource
                        ? a.Kind == ConstructionKind.Resource
                        : a.Kind != ConstructionKind.Resource)
                    : status.Active)
                .Count(a => a.TimeLeftSeconds is int v && v > 0);
            _session.ConstructionOngoingByKey[ConstructionCategoryKey(kind, isRomans, villageToken)] = ongoingInCategory;
        }

        var relevantActive = (isRomans
                ? status.Active.Where(a => kind == ConstructionKind.Resource
                    ? a.Kind == ConstructionKind.Resource
                    : a.Kind != ConstructionKind.Resource)
                : status.Active)
            .Where(a => a.TimeLeftSeconds is int v && v > 0)
            .ToList();
        var relevantWait = relevantActive
            .Select(a => a.TimeLeftSeconds!.Value)
            .DefaultIfEmpty(status.ShortestWaitSeconds ?? 0)
            .Min();

        // Schedule the humanized start from the same authoritative snapshot that proved the slot is
        // full. With two queued constructions we already know both absolute finishes, so waiting until
        // the first finishes merely to navigate back and calculate a percentage of the second is wasteful.
        // Persisting the combined wait in the session also makes the later retry proceed without
        // recomputing a new random delay.
        var humanizedWait = TryScheduleHumanizedStartAfterFullQueue(
            kind,
            slotId,
            relevantActive,
            relevantWait,
            isRomans,
            villageToken,
            out var humanizeExtraSeconds);
        if (humanizedWait is int scheduledWait)
        {
            relevantWait = scheduledWait;
        }
        // When the page gave us an actual timer, trust it (+1s race buffer so we don't poll
        // before the slot frees). The 5s floor only matters when we had no live timer at all —
        // without it we'd thrash polling if relevantWait==0.
        var wait = relevantWait > 0 ? relevantWait + 1 : 5;
        var label = kind == ConstructionKind.Resource ? "Resource slot" : "Slot";
        return $"{label} {slotId}: build queue full ({status.ResourceSlotsUsed}/{status.ResourceSlotsMax} resource, {status.BuildingSlotsUsed}/{status.BuildingSlotsMax} building, plus={plusActive}). Deferring upgrade. Upgrades performed: {upgrades}. queue_wait_seconds={wait} queue_humanize_extra_seconds={humanizeExtraSeconds}";
    }

    private int? TryScheduleHumanizedStartAfterFullQueue(
        ConstructionKind kind,
        int slotId,
        IReadOnlyList<ActiveConstruction> relevantActive,
        int slotFreeWaitSeconds,
        bool isRomans,
        string villageToken,
        out int extraSeconds)
    {
        extraSeconds = 0;
        if (!_config.ConstructionHumanizeDelayEnabled || slotFreeWaitSeconds <= 0)
        {
            return null;
        }

        var slotKey = $"{villageToken}:{kind}:{slotId}";
        var now = DateTimeOffset.UtcNow;
        if (_session.ConstructionHumanizeUntilBySlot.TryGetValue(slotKey, out var existingUntil))
        {
            var existingWait = (int)Math.Ceiling((existingUntil - now).TotalSeconds);
            extraSeconds = Math.Max(0, existingWait - slotFreeWaitSeconds);
            return existingWait > 0 ? existingWait : null;
        }

        var decision = ConstructionHumanizeCalculator.CalculateAfterFullQueue(
            relevantActive.Select(item => item.TimeLeftSeconds!.Value).ToList(),
            slotFreeWaitSeconds,
            _config.ConstructionHumanizeQueuePercentMin,
            _config.ConstructionHumanizeQueuePercentMax,
            _config.ConstructionHumanizeMaxDelayMinutes,
            _config.ConstructionHumanizeNoPlusMinMinutes,
            _config.ConstructionHumanizeNoPlusMaxMinutes,
            RandomInRange);
        var delaySeconds = decision.DelaySeconds;
        var reason = decision.Reason;

        if (delaySeconds < 1)
        {
            return null;
        }

        extraSeconds = (int)Math.Ceiling(delaySeconds);
        var combinedWait = slotFreeWaitSeconds + extraSeconds;
        _session.ConstructionHumanizeUntilBySlot[slotKey] = now.AddSeconds(combinedWait);
        _session.ConstructionOngoingByKey[ConstructionCategoryKey(kind, isRomans, villageToken)] = relevantActive.Count;
        Notify(
            $"[construction-humanize] slot {slotId}: scheduled from queue overview; " +
            $"slotFree={slotFreeWaitSeconds}s delay={Math.Ceiling(delaySeconds):F0}s total={combinedWait}s ({reason}). " +
            $"queue_wait_seconds={combinedWait}");
        return combinedWait;
    }

    // Human-like pause before starting the next construction, so the bot does not react to a freed
    // build slot faster than a person would. Called at the real start gates only, after
    // CheckQueueOrDeferAsync has confirmed a slot is free. Returns a defer message
    // (queue_wait_seconds=N) so the desktop re-queues the task and shows a per-village "next attempt"
    // countdown — the loop keeps working on other groups meanwhile (non-blocking). Returns null to
    // build immediately. Two cases (see PacingDefaults.ConstructionHumanize*):
    //  - A build is already running in this slot's category (with Plus the next one is placed in the
    //    Travian queue): defer a random 5-20% of the running build's remaining time, capped. The
    //    queued build still starts only when the current finishes, so a sub-100% delay loses no progress.
    //  - Only one slot and it just freed (a build finished): percentage has nothing to measure against,
    //    so defer a random value in the no-Plus minute range instead.
    // A genuinely idle first build (nothing was running here) starts immediately, exactly as before.
    private async Task<string?> MaybeGetConstructionHumanizeDeferAsync(
        ConstructionKind kind,
        int slotId,
        CancellationToken cancellationToken,
        bool allowNavigationToBuildings = true)
    {
        if (!_config.ConstructionHumanizeDelayEnabled)
        {
            return null;
        }

        try
        {
            var villageToken = await ResolveConstructionHumanizeVillageTokenAsync(cancellationToken);
            var slotKey = $"{villageToken}:{kind}:{slotId}";
            var now = DateTimeOffset.UtcNow;

            // Retry after a delay we already scheduled for THIS build: let it proceed (or wait out the
            // small remainder). Without this the % case would recompute and defer forever behind a live
            // build, since the slot is still free each retry.
            if (_session.ConstructionHumanizeUntilBySlot.TryGetValue(slotKey, out var scheduledUntil))
            {
                var remainingSeconds = (scheduledUntil - now).TotalSeconds;
                if (remainingSeconds <= 1)
                {
                    _session.ConstructionHumanizeUntilBySlot.Remove(slotKey);
                    return null;
                }

                var remainingWait = (int)Math.Ceiling(remainingSeconds);
                Notify($"[construction-humanize] slot {slotId}: {remainingWait}s left before start. queue_wait_seconds={remainingWait}");
                return $"Slot {slotId}: humanized construction start delay. queue_wait_seconds={remainingWait}";
            }

            var (tribe, plusActive) = await GetCachedTribeAndPlusAsync(cancellationToken);
            var status = await EvaluateConstructionSlotsAsync(
                tribe,
                plusActive,
                cancellationToken,
                allowNavigationToBuildings);
            var isRomans = string.Equals(tribe, "Romans", StringComparison.OrdinalIgnoreCase);

            // Remaining timers of constructions competing for the same slot category as this start.
            // Mirror the exact isRomans filter used elsewhere: Romans have separate resource/building
            // slots, every other tribe shares one build pool for fields and buildings.
            var ongoingRemaining = (isRomans
                    ? status.Active.Where(a => kind == ConstructionKind.Resource
                        ? a.Kind == ConstructionKind.Resource
                        : a.Kind != ConstructionKind.Resource)
                    : status.Active)
                .Where(a => a.TimeLeftSeconds is int v && v > 0)
                .Select(a => a.TimeLeftSeconds!.Value)
                .ToList();
            var ongoingCount = ongoingRemaining.Count;

            // Per (village, category) transition memory so we can tell "a build just finished, slot
            // freed" from a genuinely idle start. CheckQueueOrDeferAsync keeps this fresh on its
            // queue-full defers too, so the previous>0 signal survives the wait for the slot.
            var categoryKey = ConstructionCategoryKey(kind, isRomans, villageToken);
            var previousOngoingCount = _session.ConstructionOngoingByKey.GetValueOrDefault(categoryKey, 0);
            _session.ConstructionOngoingByKey[categoryKey] = ongoingCount;

            double delaySeconds;
            string reason;
            if (ongoingCount >= 1)
            {
                // Placing behind a running build (Plus queue). Use the shortest remaining timer so the
                // delay stays below every ongoing build → the queued one is placed before any finish.
                var refRemaining = ongoingRemaining.Min();
                var pct = RandomInRange(
                    _config.ConstructionHumanizeQueuePercentMin,
                    _config.ConstructionHumanizeQueuePercentMax) / 100.0;
                var capSeconds = Math.Max(0, _config.ConstructionHumanizeMaxDelayMinutes) * 60.0;
                delaySeconds = Math.Min(refRemaining * pct, capSeconds);
                reason = $"percent {pct * 100:F0}% of {refRemaining}s remaining, cap {_config.ConstructionHumanizeMaxDelayMinutes:F0}m";
            }
            else if (previousOngoingCount > 0)
            {
                // Single-slot category that just freed (a build finished). No percentage reference.
                var minutes = RandomInRange(
                    _config.ConstructionHumanizeNoPlusMinMinutes,
                    _config.ConstructionHumanizeNoPlusMaxMinutes);
                delaySeconds = minutes * 60.0;
                reason = $"no-plus {minutes:F1}m after slot freed";
            }
            else
            {
                // Nothing was building here → start immediately, as before.
                return null;
            }

            if (delaySeconds < 1)
            {
                return null;
            }

            var waitSeconds = (int)Math.Ceiling(delaySeconds);
            _session.ConstructionHumanizeUntilBySlot[slotKey] = now.AddSeconds(waitSeconds);
            Notify($"[construction-humanize] slot {slotId}: waiting {waitSeconds}s before start ({reason}). queue_wait_seconds={waitSeconds}");
            return $"Slot {slotId}: humanized construction start delay ({reason}). queue_wait_seconds={waitSeconds}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A humanization delay must never block the actual build — log and proceed.
            Notify($"[construction-humanize] skipped due to error: {ex.Message}");
            return null;
        }
    }

    // Key for the per-village humanize transition memory. Non-Romans share one build slot for fields
    // and buildings; Romans have separate resource/building slots — mirror that so the categories
    // don't clobber each other.
    private static string ConstructionCategoryKey(ConstructionKind kind, bool isRomans, string villageToken)
    {
        return isRomans
            ? $"{villageToken}:{(kind == ConstructionKind.Resource ? "resource" : "building")}"
            : $"{villageToken}:shared";
    }

    private async Task<string> ResolveConstructionHumanizeVillageTokenAsync(CancellationToken cancellationToken)
    {
        if (TravianUrls.TryParseNewdid(_page.Url) is int newdid)
        {
            return newdid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(activeVillage) ? "current" : activeVillage.Trim();
    }

    private static double RandomInRange(double min, double max)
    {
        return max <= min ? Math.Max(0, min) : min + (Random.Shared.NextDouble() * (max - min));
    }

    public async Task<int> WaitForConstructionSlotIfBusyAsync(
        ConstructionKind kind,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        var (tribe, plusActive) = await GetCachedTribeAndPlusAsync(cancellationToken);
        var status = await EvaluateConstructionSlotsAsync(tribe, plusActive, cancellationToken);

        var canStart = kind == ConstructionKind.Resource ? status.CanStartResource : status.CanStartBuilding;
        if (canStart)
        {
            return 0;
        }

        var isRomans = string.Equals(tribe, "Romans", StringComparison.OrdinalIgnoreCase);
        var relevantItems = isRomans
            ? status.Active.Where(a => kind == ConstructionKind.Resource
                ? a.Kind == ConstructionKind.Resource
                : a.Kind != ConstructionKind.Resource)
            : status.Active;

        var relevant = relevantItems
            .Where(a => a.TimeLeftSeconds is int v && v > 0)
            .Select(a => a.TimeLeftSeconds!.Value)
            .DefaultIfEmpty(0)
            .Min();

        var wait = relevant > 0 ? relevant : status.ShortestWaitSeconds ?? 0;
        if (wait <= 0)
        {
            return 0;
        }

        Notify($"Construction slot busy for {kind}; waiting {wait}s (tribe={tribe}, plus={plusActive}). queue_wait_seconds={wait}");
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait + 1, 12 * 60 * 60)), cancellationToken);
        return wait;
    }

}
