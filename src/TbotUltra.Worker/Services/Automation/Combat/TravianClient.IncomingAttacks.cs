using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private sealed record IncomingAttackSignalRead(
        IReadOnlyList<IncomingAttackSignal> Signals,
        bool PlusOverviewWasRead);

    public async Task<IncomingAttackSnapshot> ReadIncomingAttacksAsync(
        string villageName,
        string? villageUrl,
        string? villageKey,
        CancellationToken cancellationToken = default)
    {
        using var trace = _browserTrace.BeginOperation("READ", "incoming-attacks", $"village={villageName} source=rally-point");
        Notify($"[incoming-attacks] reading Rally Point for '{villageName}'.");
        await SwitchToVillageByIdentityAsync(villageName, villageUrl, villageKey, cancellationToken, skipFeatureRefresh: true);
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var coords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        var resolvedKey = coords.X.HasValue && coords.Y.HasValue ? $"xy:{coords.X.Value}|{coords.Y.Value}" : villageKey;

        var dorf1Html = await _page.ContentAsync();
        var dorf1ObservedAtUtc = _serverTimeUtc ?? DateTimeOffset.UtcNow;
        var dorf1Signals = IncomingAttackDomParser.ParseDorf1Signals(
            dorf1Html, activeVillage, villageUrl, coords.X, coords.Y, dorf1ObservedAtUtc);
        var activeSignal = dorf1Signals.FirstOrDefault(signal =>
            signal.Dorf1ArrivalTimesUtc is not null
            && ((coords.X.HasValue && coords.Y.HasValue && signal.CoordX == coords.X && signal.CoordY == coords.Y)
                || string.Equals(signal.VillageName, activeVillage, StringComparison.OrdinalIgnoreCase)));
        if (activeSignal is null)
        {
            Notify($"[incoming-attacks] clear Dorf1 read for '{activeVillage}'; Rally Point was skipped.");
            return new IncomingAttackSnapshot(activeVillage, resolvedKey, coords.X, coords.Y, dorf1ObservedAtUtc, []);
        }

        var fallbackArrivals = activeSignal.Dorf1ArrivalTimesUtc ?? [];

        var rallyPointState = await ReadRallyPointConstructionStateAsync(cancellationToken);
        if (rallyPointState == RallyPointConstructionState.Missing)
        {
            Notify($"[incoming-attacks] Rally Point is not constructed in '{activeVillage}'; using {fallbackArrivals.Count} red Dorf1 timer(s).");
            trace.Complete("fallback", $"village={activeVillage} reason=rally-point-missing timers={fallbackArrivals.Count}");
            return new IncomingAttackSnapshot(activeVillage, resolvedKey, coords.X, coords.Y, dorf1ObservedAtUtc, [], false, fallbackArrivals);
        }

        Notify("[incoming-attacks] opening Rally Point incoming-only overview.");
        try
        {
            await GotoAsync("/build.php?gid=16&tt=1&filter=1&subfilters=1", cancellationToken);
            if (IsCurrentUrlForPath(Paths.Buildings))
            {
                Notify($"[incoming-attacks] Rally Point is not constructed in '{activeVillage}'; Travian returned to Dorf2. Using {fallbackArrivals.Count} red Dorf1 timer(s).");
                trace.Complete("fallback", $"village={activeVillage} reason=rally-point-redirect timers={fallbackArrivals.Count}");
                return new IncomingAttackSnapshot(activeVillage, resolvedKey, coords.X, coords.Y, dorf1ObservedAtUtc, [], false, fallbackArrivals);
            }
            await EnsureIncomingAttackFilterAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && fallbackArrivals.Count > 0)
        {
            Notify($"[incoming-attacks] Rally Point read failed; using {fallbackArrivals.Count} red Dorf1 timer(s): {ex.Message}");
            return new IncomingAttackSnapshot(activeVillage, resolvedKey, coords.X, coords.Y, dorf1ObservedAtUtc, [], false, fallbackArrivals);
        }
        var observedAtUtc = _serverTimeUtc ?? DateTimeOffset.UtcNow;
        var attacks = await ReadAllIncomingAttackPagesAsync(
            activeVillage,
            resolvedKey,
            coords.X,
            coords.Y,
            observedAtUtc,
            cancellationToken);
        Notify($"[incoming-attacks] read {attacks.Count} movement(s) for '{activeVillage}'.");
        trace.Complete("success", $"village={activeVillage} count={attacks.Count}");
        return new IncomingAttackSnapshot(activeVillage, resolvedKey, coords.X, coords.Y, observedAtUtc, attacks, true, fallbackArrivals);
    }

    private async Task<IReadOnlyList<IncomingAttack>> ReadAllIncomingAttackPagesAsync(
        string activeVillage,
        string? resolvedKey,
        int? coordX,
        int? coordY,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        var attacksById = new Dictionary<string, IncomingAttack>(StringComparer.Ordinal);
        var visitedNextLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageNumber = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var html = await _page.ContentAsync();
            if (!IncomingAttackDomParser.HasOnlyIncomingFilterActive(html))
            {
                throw new InvalidOperationException("Rally Point incoming filter could not be verified; previous attack data was kept.");
            }

            var pageAttacks = IncomingAttackDomParser.ParseIncomingAttacks(
                html,
                activeVillage,
                resolvedKey,
                coordX,
                coordY,
                observedAtUtc);
            foreach (var attack in pageAttacks)
            {
                attacksById.TryAdd(attack.Id, attack);
            }

            Notify($"[incoming-attacks:verbose] Rally Point page {pageNumber}: {pageAttacks.Count} movement(s). total={attacksById.Count}");

            var nextPage = _page.Locator(".paginatorTop .paginator a.next:has(img[alt='next page'])").First;
            if (await nextPage.CountAsync().WaitAsync(cancellationToken) == 0
                || !await nextPage.IsVisibleAsync().WaitAsync(cancellationToken))
            {
                break;
            }

            var nextHref = await nextPage.GetAttributeAsync("href").WaitAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(nextHref) || !visitedNextLinks.Add(nextHref))
            {
                throw new InvalidOperationException("Rally Point pagination repeated the same next-page link; previous attack data was kept.");
            }

            await DelayBeforeClickAsync(cancellationToken, "incoming attacks: next page");
            await ClickLocatorAsync(nextPage, "incoming-attacks-next-page", cancellationToken);
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded).WaitAsync(cancellationToken);
            pageNumber++;
        }

        return attacksById.Values
            .OrderBy(attack => attack.ArrivalAtUtc)
            .ToList();
    }

    private async Task<RallyPointConstructionState> ReadRallyPointConstructionStateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await GotoAsync(Paths.Buildings, cancellationToken);
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);

            var firstState = IncomingAttackRallyPointPolicy.GetConstructionState(
                await ScanBuildingOverviewAsync(cancellationToken));
            if (firstState != RallyPointConstructionState.Missing)
            {
                return firstState;
            }

            Notify("[incoming-attacks] Rally Point slot looked empty on Dorf2; reloading once to confirm.");
            await ReloadOrGotoAsync(Paths.Buildings, cancellationToken);
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);
            return IncomingAttackRallyPointPolicy.GetConstructionState(
                await ScanBuildingOverviewAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify($"[incoming-attacks] Dorf2 Rally Point check was inconclusive; continuing with the Rally Point overview: {ex.Message}");
            return RallyPointConstructionState.Unknown;
        }
    }

    private async Task<IncomingAttackSignalRead?> ReadIncomingAttackSignalsOnCurrentPageAsync(
        string activeVillage,
        string? activeVillageUrl,
        int? activeCoordX,
        int? activeCoordY,
        CancellationToken cancellationToken)
    {
        var isDorf1 = IsCurrentUrlForPath(Paths.Resources);
        if (!isDorf1 && _cachedTravianPlusActive != true)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var html = await _page.ContentAsync();
        var observedAtUtc = _serverTimeUtc ?? DateTimeOffset.UtcNow;
        var signals = IncomingAttackDomParser.ParseDorf1Signals(
            html,
            activeVillage,
            activeVillageUrl,
            activeCoordX,
            activeCoordY,
            observedAtUtc);
        var plusOverviewWasRead = _cachedTravianPlusActive == true
                                  && IncomingAttackDomParser.HasPlusVillageOverview(html);
        if (!isDorf1 && signals.Count > 0)
        {
            Notify($"[incoming-attacks:verbose] Plus village overview found {signals.Count} signal(s) on the current page.");
        }
        return new IncomingAttackSignalRead(signals, plusOverviewWasRead);
    }

    private async Task<bool?> ReadTroopPresenceOnCurrentDorf1Async(CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Resources)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return IncomingAttackDomParser.ParseDorf1HasTroopsAtHome(await _page.ContentAsync());
    }

    private async Task EnsureIncomingAttackFilterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var category = _page.Locator("button.iconFilter:has(img.filterCategory1)").First;
        try
        {
            await category.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5000,
            });
        }
        catch (PlaywrightException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Rally Point incoming category control was not found after waiting for the overview to render.");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var html = await _page.ContentAsync();
            var action = IncomingAttackDomParser.GetRequiredFilterAction(html);
            if (action == IncomingAttackFilterAction.Verified)
            {
                return;
            }

            var (selector, reason, traceName) = action switch
            {
                IncomingAttackFilterAction.EnableIncomingCategory =>
                    ("button.iconFilter:has(img.filterCategory1)", "enable incoming category", "incoming-attacks-enable-category"),
                IncomingAttackFilterAction.EnableIncomingSubfilter =>
                    ("button.iconFilter:has(img.subFilterCategory1)", "enable incoming attacks", "incoming-attacks-enable-subfilter"),
                IncomingAttackFilterAction.DisableReinforcementsSubfilter =>
                    ("button.iconFilter.iconFilterActive:has(img.subFilterCategory2)", "disable reinforcements", "incoming-attacks-disable-reinforcements"),
                IncomingAttackFilterAction.DisableReturningSubfilter =>
                    ("button.iconFilter.iconFilterActive:has(img.subFilterCategory3)", "disable returning troops", "incoming-attacks-disable-returning"),
                _ => throw new InvalidOperationException("Unsupported Rally Point filter action."),
            };

            var control = _page.Locator(selector).First;
            await control.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
            await DelayBeforeClickAsync(cancellationToken, $"incoming attacks: {reason}");
            await ClickLocatorAsync(control, traceName, cancellationToken);
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }

        throw new InvalidOperationException("Rally Point incoming filter could not be isolated.");
    }
}
