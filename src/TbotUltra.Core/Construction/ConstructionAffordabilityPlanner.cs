namespace TbotUltra.Core.Construction;

public enum ConstructionAffordabilityOutcome
{
    Affordable,
    Recoverable,
    Blocked,
    Unknown,
}

public enum ConstructionRecoveryKind
{
    None,
    Hero,
    Npc,
}

public sealed record ConstructionResourceAmounts(long Wood, long Clay, long Iron, long Crop)
{
    public long Total => checked(Wood + Clay + Iron + Crop);

    public bool Covers(ConstructionResourceAmounts required) =>
        Wood >= required.Wood
        && Clay >= required.Clay
        && Iron >= required.Iron
        && Crop >= required.Crop;

    public bool AnyIncreaseFrom(ConstructionResourceAmounts other) =>
        Wood > other.Wood || Clay > other.Clay || Iron > other.Iron || Crop > other.Crop;

    public ConstructionResourceAmounts DeficitFrom(ConstructionResourceAmounts required) => new(
        Math.Max(0, required.Wood - Wood),
        Math.Max(0, required.Clay - Clay),
        Math.Max(0, required.Iron - Iron),
        Math.Max(0, required.Crop - Crop));
}

public sealed record ConstructionProductionRates(double? Wood, double? Clay, double? Iron, double? Crop)
{
    public bool IsComplete => IsKnown(Wood) && IsKnown(Clay) && IsKnown(Iron) && IsKnown(Crop);

    private static bool IsKnown(double? value) => value is not null
        && !double.IsNaN(value.Value)
        && !double.IsInfinity(value.Value);
}

public sealed record ConstructionVillageResourceSnapshot(
    ConstructionResourceAmounts? Stock,
    ConstructionProductionRates ProductionPerHour,
    long? WarehouseCapacity,
    long? GranaryCapacity,
    bool IsLive);

public sealed record ConstructionHeroRecoveryPolicy(
    bool Enabled,
    ConstructionResourceAmounts? Inventory,
    bool MaxUseEnabled,
    int MaxUsePerResource,
    DateTimeOffset? NextRevalidationAtUtc);

public sealed record ConstructionNpcRecoveryPolicy(
    bool Enabled,
    bool AllowGoldSpending,
    int? Gold,
    int GoldLimit,
    int GoldCost,
    bool DailyBudgetAvailable,
    bool BuildTimeLimitEnabled,
    int BuildTimeLimitSeconds);

public sealed record ConstructionAffordabilityRequest(
    ConstructionResourceAmounts Cost,
    ConstructionVillageResourceSnapshot Village,
    ConstructionHeroRecoveryPolicy Hero,
    ConstructionNpcRecoveryPolicy Npc,
    DateTimeOffset NowUtc,
    TimeSpan FallbackWait);

public sealed record ConstructionAffordabilityDecision(
    ConstructionAffordabilityOutcome Outcome,
    ConstructionRecoveryKind Recovery,
    bool ShouldOpenBuildPage,
    bool ShouldRevalidateHero,
    DateTimeOffset? NextAttemptAtUtc,
    ConstructionResourceAmounts Stock,
    ConstructionResourceAmounts Deficit,
    string Reason);

public sealed record ConstructionAffordabilityDeadlineState(
    DateTimeOffset DeadlineAtUtc,
    ConstructionResourceAmounts Stock,
    ConstructionResourceAmounts? Hero);

public static class ConstructionAffordabilityDeadlinePolicy
{
    public static ConstructionAffordabilityDeadlineState Select(
        ConstructionAffordabilityDeadlineState existing,
        ConstructionAffordabilityDeadlineState proposed,
        DateTimeOffset now)
    {
        if (existing.DeadlineAtUtc <= now)
        {
            return proposed;
        }

        var resourcesIncreased = proposed.Stock.AnyIncreaseFrom(existing.Stock);
        var heroIncreased = proposed.Hero is { } currentHero
            && (existing.Hero is not { } priorHero || currentHero.AnyIncreaseFrom(priorHero));
        if (!resourcesIncreased && !heroIncreased)
        {
            return existing;
        }

        return proposed.DeadlineAtUtc < existing.DeadlineAtUtc ? proposed : existing;
    }
}

/// <summary>
/// Calculates whether a construction offer is affordable or locally recoverable.
/// Browser navigation, persisted retry state and live click safety stay outside Core.
/// </summary>
public static class ConstructionAffordabilityPlanner
{
    private static readonly ConstructionResourceAmounts Empty = new(0, 0, 0, 0);

    public static ConstructionAffordabilityDecision Evaluate(ConstructionAffordabilityRequest request)
    {
        var stock = request.Village.Stock;
        if (!request.Village.IsLive
            || stock is null
            || request.Village.WarehouseCapacity is not > 0
            || request.Village.GranaryCapacity is not > 0)
        {
            return Decision(
                ConstructionAffordabilityOutcome.Unknown,
                ConstructionRecoveryKind.None,
                shouldOpenBuildPage: true,
                shouldRevalidateHero: false,
                nextAttemptAtUtc: null,
                stock ?? Empty,
                Empty,
                "live Dorf1 resource snapshot is incomplete");
        }

        var deficit = stock.DeficitFrom(request.Cost);
        if (stock.Covers(request.Cost))
        {
            return Decision(
                ConstructionAffordabilityOutcome.Affordable,
                ConstructionRecoveryKind.None,
                shouldOpenBuildPage: true,
                shouldRevalidateHero: false,
                nextAttemptAtUtc: null,
                stock,
                deficit,
                "village stock covers the catalog cost");
        }

        if (request.Cost.Wood > request.Village.WarehouseCapacity.Value
            || request.Cost.Clay > request.Village.WarehouseCapacity.Value
            || request.Cost.Iron > request.Village.WarehouseCapacity.Value
            || request.Cost.Crop > request.Village.GranaryCapacity.Value)
        {
            return Decision(
                ConstructionAffordabilityOutcome.Unknown,
                ConstructionRecoveryKind.None,
                shouldOpenBuildPage: true,
                shouldRevalidateHero: false,
                nextAttemptAtUtc: null,
                stock,
                deficit,
                "catalog cost exceeds current storage capacity; live page remains authoritative");
        }

        // Production is required before any negative affordability decision. Without it, neither
        // the production deadline nor the NPC build-time gate can be proven from the live overview.
        if (!request.Village.ProductionPerHour.IsComplete)
        {
            return Decision(
                ConstructionAffordabilityOutcome.Unknown,
                ConstructionRecoveryKind.None,
                shouldOpenBuildPage: true,
                shouldRevalidateHero: false,
                nextAttemptAtUtc: null,
                stock,
                deficit,
                "live Dorf1 production snapshot is incomplete");
        }

        if (request.Hero.Enabled && request.Hero.Inventory is { } heroInventory)
        {
            var maxPerResource = request.Hero.MaxUseEnabled
                ? Math.Max(0, request.Hero.MaxUsePerResource)
                : int.MaxValue;
            if (deficit.Wood <= Math.Min(heroInventory.Wood, maxPerResource)
                && deficit.Clay <= Math.Min(heroInventory.Clay, maxPerResource)
                && deficit.Iron <= Math.Min(heroInventory.Iron, maxPerResource)
                && deficit.Crop <= Math.Min(heroInventory.Crop, maxPerResource))
            {
                return Decision(
                    ConstructionAffordabilityOutcome.Recoverable,
                    ConstructionRecoveryKind.Hero,
                    shouldOpenBuildPage: true,
                    shouldRevalidateHero: false,
                    nextAttemptAtUtc: null,
                    stock,
                    deficit,
                    "cached Hero inventory can cover every resource deficit");
            }
        }

        var productionDeadline = ComputeProductionDeadline(request, deficit);
        var npcEligible = request.Npc.Enabled
            && request.Npc.AllowGoldSpending
            && request.Npc.Gold is int gold
            && gold - request.Npc.GoldCost >= request.Npc.GoldLimit
            && request.Npc.DailyBudgetAvailable
            && stock.Total >= request.Cost.Total
            && (!request.Npc.BuildTimeLimitEnabled
                || productionDeadline is null
                || (productionDeadline.Value - request.NowUtc).TotalSeconds > Math.Max(0, request.Npc.BuildTimeLimitSeconds));
        if (npcEligible)
        {
            return Decision(
                ConstructionAffordabilityOutcome.Recoverable,
                ConstructionRecoveryKind.Npc,
                shouldOpenBuildPage: true,
                shouldRevalidateHero: false,
                nextAttemptAtUtc: null,
                stock,
                deficit,
                "NPC trade can redistribute sufficient total village stock and all local gold gates pass");
        }

        if (request.Hero.Enabled
            && (request.Hero.NextRevalidationAtUtc is null
                || request.Hero.NextRevalidationAtUtc <= request.NowUtc))
        {
            return Decision(
                ConstructionAffordabilityOutcome.Unknown,
                ConstructionRecoveryKind.None,
                shouldOpenBuildPage: false,
                shouldRevalidateHero: true,
                nextAttemptAtUtc: null,
                stock,
                deficit,
                "Hero inventory is missing or insufficient and its shared revalidation is due");
        }

        var fallbackDeadline = request.NowUtc.Add(request.FallbackWait <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(15)
            : request.FallbackWait);
        var nextAttemptAt = new[]
            {
                productionDeadline,
                request.Hero.Enabled ? request.Hero.NextRevalidationAtUtc : null,
                fallbackDeadline,
            }
            .Where(value => value is not null && value > request.NowUtc)
            .Min();
        return Decision(
            ConstructionAffordabilityOutcome.Blocked,
            ConstructionRecoveryKind.None,
            shouldOpenBuildPage: false,
            shouldRevalidateHero: false,
            nextAttemptAt,
            stock,
            deficit,
            "neither village stock, cached Hero inventory nor NPC policy can fund the catalog cost");
    }

    private static DateTimeOffset? ComputeProductionDeadline(
        ConstructionAffordabilityRequest request,
        ConstructionResourceAmounts deficit)
    {
        var waits = new[]
        {
            WaitSeconds(deficit.Wood, request.Village.ProductionPerHour.Wood),
            WaitSeconds(deficit.Clay, request.Village.ProductionPerHour.Clay),
            WaitSeconds(deficit.Iron, request.Village.ProductionPerHour.Iron),
            WaitSeconds(deficit.Crop, request.Village.ProductionPerHour.Crop),
        };
        if (waits.Any(wait => wait is null))
        {
            return null;
        }

        var longest = waits.Max() ?? 0;
        return longest > 0 ? request.NowUtc.AddSeconds(longest) : null;
    }

    private static long? WaitSeconds(long deficit, double? productionPerHour)
    {
        if (deficit <= 0)
        {
            return 0;
        }
        if (productionPerHour is not > 0)
        {
            return null;
        }

        return Math.Max(1L, (long)Math.Ceiling((deficit / productionPerHour.Value) * 3600d));
    }

    private static ConstructionAffordabilityDecision Decision(
        ConstructionAffordabilityOutcome outcome,
        ConstructionRecoveryKind recovery,
        bool shouldOpenBuildPage,
        bool shouldRevalidateHero,
        DateTimeOffset? nextAttemptAtUtc,
        ConstructionResourceAmounts stock,
        ConstructionResourceAmounts deficit,
        string reason) => new(
            outcome,
            recovery,
            shouldOpenBuildPage,
            shouldRevalidateHero,
            nextAttemptAtUtc,
            stock,
            deficit,
            reason);
}
