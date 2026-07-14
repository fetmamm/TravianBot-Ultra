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
    private async Task OpenAdventureListWithFallbackAsync(CancellationToken cancellationToken)
    {
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load

        if (await HasAdventureEntryOnPageAsync(cancellationToken))
        {
            return;
        }

        await GotoAsync(Paths.HeroAdventures, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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
            await GotoAsync(Paths.HeroAdventures, cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            if (await IsHeroAdventuresPageAsync(cancellationToken))
            {
                return;
            }
        }
        catch (Exception ex) when (IsTransientExecutionContextException(ex))
        {
            Notify($"Hero adventures page hit transient navigation issue; retrying {Paths.HeroAdventures}. {ex.Message}");
        }

        await GotoAsync(Paths.HeroAdventures, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
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
                await GotoAsync(Paths.HeroAttributes, cancellationToken);
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
            var waitSeconds = HeroStatusDecision.ComputeHpWaitSeconds(
                hpPercent,
                minHpThreshold,
                heroHpRegenPerDayPercent,
                HeroLowHpMaxDeferSeconds);
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
