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
              // Official exposes authoritative queue rows through .buildingList li on dorf1/dorf2.
              // Do not fall back to .underConstruction (overview slot marker), .buildDuration
              // (timer fragment), or #building_contract (empty-slot building choices): none is a
              // queue row and transient rendering otherwise looks like an unrelated queue change.
              const selectors = ['.buildingList li'];

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
        bool allowNavigationToBuildings = true,
        ActiveConstructionReadMode readMode = ActiveConstructionReadMode.FreshForMutation)
    {
        using var trace = _browserTrace.BeginOperation(
            "READ",
            "active-constructions",
            $"scope=current-page allowNavigation={allowNavigationToBuildings} mode={readMode}");
        // Cache hit collapses the 4-5 calls a single upgrade iteration makes (CheckQueueOrDefer,
        // ReadHighestKnownQueuedBuildingLevel, ReadQueuedBuildingWaitSeconds, level-advance poll)
        // into one network round-trip. GotoAsync invalidates the cache automatically.
        var now = DateTimeOffset.UtcNow;
        var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        var currentVillageKey = activeCoords.X.HasValue && activeCoords.Y.HasValue
            ? $"xy:{activeCoords.X.Value}|{activeCoords.Y.Value}"
            : null;
        var cached = _cachedActiveConstructions;
        var cachedDeadlineReached = cached?.Any(item => item.Finish?.IsFinishedAt(now) == true) == true;
        if (CanUseActiveConstructionsCache(
                cached,
                _cachedActiveConstructionsAt,
                now,
                readMode,
                _cachedActiveConstructionsVillageKey,
                currentVillageKey))
        {
            var ageMs = (long)(now - _cachedActiveConstructionsAt).TotalMilliseconds;
            _lastActiveConstructionsFromOverview = _cachedActiveConstructionsFromOverview;
            _browserTrace.Event(
                "CACHE",
                "active-constructions-hit",
                "hit",
                $"ageMs={ageMs} count={cached!.Count} fromOverview={_cachedActiveConstructionsFromOverview} " +
                $"village={currentVillageKey} mode={readMode}");
            trace.Complete(
                "success",
                $"source=cache count={cached.Count} ageMs={ageMs}");
            return cached;
        }

        _browserTrace.Event(
            "CACHE",
            "active-constructions-miss",
            "miss",
            $"reason={(cachedDeadlineReached
                ? "known-deadline-reached"
                : cached is not null && !string.Equals(_cachedActiveConstructionsVillageKey, currentVillageKey, StringComparison.OrdinalIgnoreCase)
                    ? "village-owner-changed"
                    : "missing-or-expired")} cachedVillage={_cachedActiveConstructionsVillageKey ?? "-"} " +
            $"currentVillage={currentVillageKey ?? "-"} mode={readMode}");

        LogFunctionStarted();
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
            using (var wait = _browserTrace.BeginOperation(
                       "WAIT",
                       "confirm-empty-construction-queue",
                       "plannedMs=350 condition=second-overview-DOM-read"))
            {
                await Task.Delay(350, cancellationToken);
                wait.Complete("success", "actual=delay-completed");
            }
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
        if (currentVillageKey is null)
        {
            activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
            currentVillageKey = activeCoords.X.HasValue && activeCoords.Y.HasValue
                ? $"xy:{activeCoords.X.Value}|{activeCoords.Y.Value}"
                : null;
        }
        _cachedActiveConstructionsVillageKey = currentVillageKey;
        _lastActiveConstructionsFromOverview = readFromOverview;
        trace.Complete(
            "success",
            $"source=live count={result.Count} fromOverview={readFromOverview}");
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

    internal static bool CanUseActiveConstructionsCache(
        IReadOnlyList<ActiveConstruction>? cached,
        DateTimeOffset cachedAt,
        DateTimeOffset now,
        ActiveConstructionReadMode readMode,
        string? cachedVillageKey,
        string? currentVillageKey)
    {
        if (cached is null
            || string.IsNullOrWhiteSpace(cachedVillageKey)
            || string.IsNullOrWhiteSpace(currentVillageKey)
            || !string.Equals(cachedVillageKey, currentVillageKey, StringComparison.OrdinalIgnoreCase)
            || cached.Any(item => item.Finish?.IsFinishedAt(now) == true))
        {
            return false;
        }

        var cacheTtl = readMode == ActiveConstructionReadMode.CachedForObservation
            ? ActiveConstructionsObservationCacheTtl
            : ActiveConstructionsMutationCacheTtl;
        return now - cachedAt < cacheTtl;
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
        // Construction capacity belongs to the active village tribe, not the avatar/account tribe.
        var tribe = await ReadActiveVillageTribeAsync(cancellationToken);

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
        SynchronizeConstructionHumanizeState();
        var (tribe, plusActive) = await GetCachedTribeAndPlusAsync(cancellationToken);
        if (!TbotUltra.Core.Travian.TroopCatalog.IsKnownTribe(tribe))
        {
            Notify($"[tribe] construction deferred because the active village tribe is unknown (slot={slotId}, kind={kind}).");
            return "Construction deferred until the active village tribe can be detected. queue_wait_seconds=60";
        }

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
        SynchronizeConstructionHumanizeState();
        if (!_config.ConstructionHumanizeDelayEnabled)
        {
            return null;
        }

        try
        {
            var existingDecision = await TryGetExistingConstructionHumanizeDecisionAsync(kind, slotId, cancellationToken);
            if (existingDecision.Handled)
            {
                return existingDecision.DeferMessage;
            }

            var villageToken = await ResolveConstructionHumanizeVillageTokenAsync(cancellationToken);
            var slotKey = $"{villageToken}:{kind}:{slotId}";
            var now = DateTimeOffset.UtcNow;

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

    // Existing per-slot deadlines are authoritative until they expire. Checking them before any
    // building-overview read lets a deferred retry return without opening dorf2 merely to rediscover
    // the same humanized wait. A pre-sleep execution consumes the deadline but still performs the
    // normal live queue gate before clicking.
    private async Task<(bool Handled, string? DeferMessage)> TryGetExistingConstructionHumanizeDecisionAsync(
        ConstructionKind kind,
        int slotId,
        CancellationToken cancellationToken)
    {
        SynchronizeConstructionHumanizeState();
        if (!_config.ConstructionHumanizeDelayEnabled)
        {
            return (false, null);
        }

        var villageToken = await ResolveConstructionHumanizeVillageTokenAsync(cancellationToken);
        var slotKey = $"{villageToken}:{kind}:{slotId}";
        var loginFillActive = ConstructionLoginFillPolicy.IsActive(
            _config.ConstructionLoginFill,
            _config.ConstructionLoginFillExpiresAtUnixSeconds,
            DateTimeOffset.UtcNow);
        if (_config.ConstructionPreSleepFill || loginFillActive)
        {
            _session.ConstructionHumanizeUntilBySlot.Remove(slotKey);
            var fillReason = _config.ConstructionPreSleepFill ? "pre-sleep fill" : "login fill";
            Notify($"[construction-humanize] slot {slotId}: {fillReason} — existing delay consumed before page reads.");
            return (true, null);
        }

        if (!_session.ConstructionHumanizeUntilBySlot.TryGetValue(slotKey, out var scheduledUntil))
        {
            return (false, null);
        }

        var remainingWait = ConstructionHumanizeCalculator.ResolveExistingWaitSeconds(
            DateTimeOffset.UtcNow,
            scheduledUntil);
        if (remainingWait == 0)
        {
            _session.ConstructionHumanizeUntilBySlot.Remove(slotKey);
            return (true, null);
        }

        Notify($"[construction-humanize] slot {slotId}: {remainingWait}s left before start; overview read skipped. queue_wait_seconds={remainingWait}");
        return (true, $"Slot {slotId}: humanized construction start delay. queue_wait_seconds={remainingWait}");
    }

    private void SynchronizeConstructionHumanizeState()
    {
        if (_session.SynchronizeConstructionHumanizeState(_config.ConstructionHumanizeStateVersion))
        {
            Notify($"[construction-humanize] synchronized state version {_config.ConstructionHumanizeStateVersion}; stale session waits cleared.");
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
            return VillageIdentityReconciler.BuildStableVillageToken(newdid, (null, null), null);
        }

        var activeDid = await TryReadActiveVillageDidFromCurrentPageAsync(cancellationToken);
        if (activeDid.HasValue)
        {
            return VillageIdentityReconciler.BuildStableVillageToken(activeDid, (null, null), null);
        }

        var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        if (VillageIdentityReconciler.HasCoordinates(activeCoords))
        {
            return VillageIdentityReconciler.BuildStableVillageToken(null, activeCoords, null);
        }

        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        return VillageIdentityReconciler.BuildStableVillageToken(null, (null, null), activeVillage);
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
        if (!TbotUltra.Core.Travian.TroopCatalog.IsKnownTribe(tribe))
        {
            Notify("[tribe] construction slot wait deferred because the active village tribe is unknown. queue_wait_seconds=60");
            return 60;
        }

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
