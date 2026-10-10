using TbotUltra.Core.Construction;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private async Task<ConstructionAffordabilityDecision> EvaluateLiveConstructionAffordabilityAsync(
        BuildingLevelStats cost,
        string label,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            var currentPage = await ReadResourceSnapshotAsync(cancellationToken, allowRecovery: false, maxAttempts: 1);
            var stock = ConstructionAffordabilityOperation.TryParseStock(currentPage.Resources);
            if (stock is not null
                && currentPage.Capacities.Warehouse is > 0
                && currentPage.Capacities.Granary is > 0)
            {
                var cachedProduction = await ReadCachedProductionByHourForActiveVillageAsync(cancellationToken);
                var productionByHour = ResourceSnapshotCalculator.MergeProductionByHour(
                    currentPage.ProductionByHour,
                    cachedProduction);
                var decision = await EvaluateConstructionAffordabilityAsync(
                    cost,
                    label,
                    currentPage.Resources,
                    productionByHour,
                    currentPage.Capacities.Warehouse,
                    currentPage.Capacities.Granary,
                    isLive: true,
                    cancellationToken);

                if (!decision.NeedsProductionRefresh)
                {
                    Notify($"[construction-preflight] label='{label}' used live current-page stock and {(cachedProduction.Values.Any(value => value is not null) ? "cached" : "current-page")} production; Dorf1 navigation skipped.");
                    return decision;
                }

                Notify($"[construction-preflight] label='{label}' has a resource deficit and no complete production rate; reading Dorf1 to calculate the wait.");
            }
            else
            {
                Notify($"[construction-preflight] label='{label}' has incomplete current-page stock or capacity; reading Dorf1.");
            }
        }

        await EnsureResourceFieldsPageAsync(
            cancellationToken,
            $"Manual verification appeared while preparing construction affordability for {label}.");
        var snapshot = await ReadResourceSnapshotAsync(cancellationToken);
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        var cachedSnapshot = TryGetCachedVillageResourceSnapshot(activeVillage, activeCoords);
        var dorf1ProductionByHour = ResourceSnapshotCalculator.MergeProductionByHour(
            snapshot.ProductionByHour,
            cachedSnapshot?.ProductionByHour);
        SaveCachedVillageResourceSnapshot(
            activeVillage,
            [],
            snapshot.Capacities,
            dorf1ProductionByHour,
            activeCoords);
        return await EvaluateConstructionAffordabilityAsync(
            cost,
            label,
            snapshot.Resources,
            dorf1ProductionByHour,
            snapshot.Capacities.Warehouse,
            snapshot.Capacities.Granary,
            isLive: true,
            cancellationToken);
    }

    private async Task<ConstructionAffordabilityDecision> EvaluateConstructionAffordabilityAsync(
        BuildingLevelStats cost,
        string label,
        IReadOnlyDictionary<string, string> resources,
        IReadOnlyDictionary<string, double?> productionByHour,
        long? warehouseCapacity,
        long? granaryCapacity,
        bool isLive,
        CancellationToken cancellationToken)
    {
        var operation = new ConstructionAffordabilityOperation(_config, Notify);
        var gold = _config.NpcTradeConstructionEnabled && _config.AllowGoldSpending
            ? (await ReadCurrencyAsync(cancellationToken)).Gold
            : null;
        var serverUtcOffset = await ResolveConstructionServerUtcOffsetAsync(cancellationToken);
        var dailyBudgetAvailable = operation.IsNpcDailyBudgetAvailable(
            _projectRoot,
            _account.Name,
            _config.BaseUrl,
            serverUtcOffset,
            DateTimeOffset.UtcNow);
        var stateKey = await BuildConstructionAffordabilityStateKeyAsync(label, cancellationToken);
        var heroSnapshot = TryGetCachedHeroInventorySnapshot();
        var now = DateTimeOffset.UtcNow;
        var decision = operation.Evaluate(
            cost,
            resources,
            productionByHour,
            warehouseCapacity,
            granaryCapacity,
            isLive,
            heroSnapshot,
            gold,
            dailyBudgetAvailable,
            stateKey,
            now);

        if (decision.ShouldRevalidateHero)
        {
            var latestSnapshot = TryGetCachedHeroInventorySnapshot();
            if (latestSnapshot != heroSnapshot)
            {
                decision = operation.Evaluate(
                    cost,
                    resources,
                    productionByHour,
                    warehouseCapacity,
                    granaryCapacity,
                    isLive,
                    latestSnapshot,
                    gold,
                    dailyBudgetAvailable,
                    stateKey,
                    DateTimeOffset.UtcNow);
            }
            if (decision.ShouldRevalidateHero)
            {
                decision = decision with
                {
                    ShouldOpenBuildPage = true,
                    Reason = "Hero inventory revalidation is due on the exact build-page transfer dialog",
                };
                Notify($"[construction-preflight] label='{label}' Hero revalidation is due; the shared cooldown starts only after a readable dialog or confirmed empty response.");
            }
        }

        Notify(ConstructionAffordabilityOperation.FormatLog(
            label,
            cost,
            decision,
            TryGetCachedHeroInventorySnapshot(),
            gold,
            dailyBudgetAvailable));
        return decision;
    }

    private async Task<TimeSpan> ResolveConstructionServerUtcOffsetAsync(CancellationToken cancellationToken)
    {
        if (_session.CachedServerUtcOffset is { } cached)
        {
            return cached;
        }

        var detected = await ReadProductionBonusServerUtcOffsetAsync(cancellationToken) ?? TimeSpan.Zero;
        _session.CachedServerUtcOffset = detected;
        return detected;
    }

    private async Task<string> BuildConstructionAffordabilityStateKeyAsync(
        string label,
        CancellationToken cancellationToken)
    {
        var village = await ReadActiveVillageNameAsync(cancellationToken);
        var coords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        return $"{BuildHeroInventoryCacheKey()}|{coords.X?.ToString() ?? "?"}|{coords.Y?.ToString() ?? "?"}|{village}|{label}";
    }

    private async Task<string?> EvaluateBuildingUpgradeAffordabilityAsync(
        int slotId,
        int? gid,
        string buildingName,
        int nextLevel,
        int upgrades,
        CancellationToken cancellationToken)
    {
        if (gid is null)
        {
            return $"Slot {slotId}: preflight could not resolve gid for '{buildingName}'. "
                + $"Upgrades performed: {upgrades}. queue_wait_seconds=1";
        }

        var catalogCost = BuildingCatalogService.CostFor(gid.Value, nextLevel);
        if (catalogCost is null)
        {
            Notify($"[construction-preflight] Building slot {slotId}: catalog cost for gid {gid.Value} level {nextLevel} is unavailable; retaining live build-page fallback.");
            return null;
        }

        var label = $"Building slot {slotId} ({buildingName}) upgrade to level {nextLevel}";
        var decision = await EvaluateLiveConstructionAffordabilityAsync(catalogCost, label, cancellationToken);
        return decision.ShouldOpenBuildPage
            ? null
            : ConstructionAffordabilityOperation.BuildBlockedResult(label, decision, DateTimeOffset.UtcNow);
    }
}
