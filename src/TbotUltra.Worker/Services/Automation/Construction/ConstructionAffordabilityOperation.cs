using System.Collections.Concurrent;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Construction;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Adapts live Worker snapshots and account policy to Core's pure affordability planner.
/// It also preserves an offer's future deadline across unchanged scans in this process.
/// </summary>
internal sealed class ConstructionAffordabilityOperation
{
    internal static readonly TimeSpan FallbackWait = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, ConstructionAffordabilityDeadlineState> Deadlines = new(StringComparer.Ordinal);
    private readonly BotOptions _config;
    private readonly Action<string>? _log;

    internal ConstructionAffordabilityOperation(BotOptions config, Action<string>? log = null)
    {
        _config = config;
        _log = log;
    }

    internal ConstructionAffordabilityDecision Evaluate(
        BuildingLevelStats cost,
        IReadOnlyDictionary<string, string> resources,
        IReadOnlyDictionary<string, double?> productionByHour,
        long? warehouseCapacity,
        long? granaryCapacity,
        bool isLive,
        HeroInventorySnapshot? heroSnapshot,
        int? gold,
        bool dailyBudgetAvailable,
        string stateKey,
        DateTimeOffset now)
    {
        var heroInventory = heroSnapshot is null
            ? null
            : ToAmounts(heroSnapshot.Resources);
        var stock = TryParseStock(resources);
        var decision = ConstructionAffordabilityPlanner.Evaluate(new ConstructionAffordabilityRequest(
            ToAmounts(cost),
            new ConstructionVillageResourceSnapshot(
                stock,
                ToProduction(productionByHour),
                warehouseCapacity,
                granaryCapacity,
                isLive),
            new ConstructionHeroRecoveryPolicy(
                Enabled: _config.HeroResourceTransferEnabled && _config.HeroResourceUseConstruction,
                Inventory: heroInventory,
                MaxUseEnabled: _config.HeroResourceMaxUseEnabled,
                MaxUsePerResource: _config.HeroResourceMaxUsePerResource,
                NextRevalidationAtUtc: heroSnapshot?.ConstructionProbe?.NextProbeAtUtc),
            new ConstructionNpcRecoveryPolicy(
                Enabled: _config.NpcTradeConstructionEnabled,
                AllowGoldSpending: _config.AllowGoldSpending,
                Gold: gold,
                GoldLimit: _config.GoldLimit,
                GoldCost: 3,
                DailyBudgetAvailable: dailyBudgetAvailable,
                BuildTimeLimitEnabled: _config.NpcTradeBuildTimeLimitEnabled,
                BuildTimeLimitSeconds: TravianClient.NormalizeNpcTradeBuildTimeLimitSeconds(_config.NpcTradeBuildTimeLimitSeconds)),
            now,
            FallbackWait));

        return StabilizeDeadline(stateKey, decision, stock, heroInventory, now);
    }

    internal bool IsNpcDailyBudgetAvailable(
        string projectRoot,
        string accountName,
        string? serverUrl,
        TimeSpan serverUtcOffset,
        DateTimeOffset now)
    {
        if (!_config.NpcTradeConstructionEnabled || !_config.AllowGoldSpending)
        {
            return false;
        }

        try
        {
            var serverDate = DateOnly.FromDateTime(now.ToOffset(serverUtcOffset).Date);
            var path = AccountStoragePaths.DailySpendingStatePath(projectRoot, accountName, serverUrl);
            var state = new DailySpendingStore(path).Read(serverDate);
            return state.GoldSpent <= Math.Max(0, _config.DailyGoldSpendingLimit) - 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _log?.Invoke($"[construction-preflight] NPC daily budget could not be read: {ex.Message}");
            return false;
        }
    }

    internal static string BuildBlockedResult(
        string label,
        ConstructionAffordabilityDecision decision,
        DateTimeOffset now)
    {
        var waitSeconds = decision.NextAttemptAtUtc is { } deadline
            ? Math.Max(1, (int)Math.Ceiling((deadline - now).TotalSeconds))
            : (int)FallbackWait.TotalSeconds;
        return $"{label} blocked by construction affordability preflight ({decision.Reason}). "
            + $"wait_reason=construction_affordability queue_wait_seconds={waitSeconds}";
    }

    internal static string FormatLog(
        string label,
        BuildingLevelStats cost,
        ConstructionAffordabilityDecision decision,
        HeroInventorySnapshot? hero,
        int? gold,
        bool dailyBudgetAvailable)
    {
        var next = decision.NextAttemptAtUtc?.ToString("O") ?? "-";
        var heroStock = hero is null
            ? "unknown"
            : $"{hero.Resources.Wood}/{hero.Resources.Clay}/{hero.Resources.Iron}/{hero.Resources.Crop}";
        var heroNext = hero?.ConstructionProbe?.NextProbeAtUtc?.ToString("O") ?? "-";
        return $"[construction-preflight] label='{label}' "
            + $"cost={cost.Wood}/{cost.Clay}/{cost.Iron}/{cost.Crop} "
            + $"stock={Format(decision.Stock)} deficit={Format(decision.Deficit)} "
            + $"hero={heroStock} hero_next={heroNext} npc_gold={(gold?.ToString() ?? "unknown")} npc_daily_budget={dailyBudgetAvailable} "
            + $"outcome={decision.Outcome} recovery={decision.Recovery} open_build_page={decision.ShouldOpenBuildPage} "
            + $"next={next} reason='{decision.Reason}'.";
    }

    internal static ConstructionResourceAmounts? TryParseStock(IReadOnlyDictionary<string, string> resources)
    {
        return TryRead(resources, "wood", out var wood)
            && TryRead(resources, "clay", out var clay)
            && TryRead(resources, "iron", out var iron)
            && TryRead(resources, "crop", out var crop)
                ? new ConstructionResourceAmounts(wood, clay, iron, crop)
                : null;
    }

    private static ConstructionAffordabilityDecision StabilizeDeadline(
        string stateKey,
        ConstructionAffordabilityDecision decision,
        ConstructionResourceAmounts? stock,
        ConstructionResourceAmounts? hero,
        DateTimeOffset now)
    {
        if (decision.Outcome != ConstructionAffordabilityOutcome.Blocked
            || decision.NextAttemptAtUtc is not { } proposedDeadline
            || stock is null)
        {
            Deadlines.TryRemove(stateKey, out _);
            return decision;
        }

        var proposed = new ConstructionAffordabilityDeadlineState(proposedDeadline, stock, hero);
        var selected = Deadlines.AddOrUpdate(
            stateKey,
            proposed,
            (_, existing) => ConstructionAffordabilityDeadlinePolicy.Select(existing, proposed, now));
        return decision with { NextAttemptAtUtc = selected.DeadlineAtUtc };
    }

    private static ConstructionResourceAmounts ToAmounts(BuildingLevelStats cost) =>
        new(cost.Wood, cost.Clay, cost.Iron, cost.Crop);

    private static ConstructionResourceAmounts ToAmounts(HeroInventoryResources resources) =>
        new(resources.Wood, resources.Clay, resources.Iron, resources.Crop);

    private static ConstructionProductionRates ToProduction(IReadOnlyDictionary<string, double?> production)
    {
        production.TryGetValue("wood", out var wood);
        production.TryGetValue("clay", out var clay);
        production.TryGetValue("iron", out var iron);
        production.TryGetValue("crop", out var crop);
        return new ConstructionProductionRates(wood, clay, iron, crop);
    }

    private static bool TryRead(IReadOnlyDictionary<string, string> resources, string key, out long value)
    {
        value = 0;
        if (!resources.TryGetValue(key, out var raw)
            || TravianParsing.TryParseResourceValue(raw) is not long parsed)
        {
            return false;
        }

        value = Math.Max(0, parsed);
        return true;
    }

    private static string Format(ConstructionResourceAmounts values) =>
        $"{values.Wood}/{values.Clay}/{values.Iron}/{values.Crop}";

}
