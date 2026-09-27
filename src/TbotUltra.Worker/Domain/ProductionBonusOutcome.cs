namespace TbotUltra.Worker.Domain;

public static class ProductionBonusResources
{
    public static IReadOnlyList<string> All { get; } = ["lumber", "clay", "iron", "crop"];
}

public enum ProductionBonusNextAttemptKind
{
    Immediate,
    DailyReset,
    RelativeDelay,
}

public sealed record ProductionBonusResourceState(
    string Resource,
    int Bonus,
    int RemainingSeconds,
    ProductionBonusNextAttemptKind NextAttemptKind,
    int RetryAfterSeconds,
    bool CanActivate);

public enum ProductionBonusRunIntent
{
    Inspect,
    Activate,
}

public sealed record ProductionBonusObservation(
    IReadOnlyList<ProductionBonusResourceState> Resources,
    TimeSpan? ServerUtcOffset,
    bool AccountDeletionPending = false)
{
    public IReadOnlyList<string> ActivatableResources => Resources
        .Where(resource => resource.CanActivate)
        .Select(resource => resource.Resource)
        .ToList();
}

public enum ProductionBonusOutcomeStatus
{
    Observed,
    AccountDeletionPending,
    Deferred,
    Failed,
}

public sealed record ProductionBonusOutcome
{
    private ProductionBonusOutcome(
        ProductionBonusOutcomeStatus status,
        string message,
        IReadOnlyList<ProductionBonusResourceState> resources,
        TimeSpan? serverUtcOffset = null,
        bool freeVideoAvailable = false,
        IReadOnlyList<string>? attemptedResources = null,
        IReadOnlyList<string>? unconfirmedResources = null,
        DateTimeOffset? retryAtUtc = null)
    {
        Status = status;
        Message = message;
        Resources = resources;
        ServerUtcOffset = serverUtcOffset;
        FreeVideoAvailable = freeVideoAvailable;
        AttemptedResources = attemptedResources ?? [];
        UnconfirmedResources = unconfirmedResources ?? [];
        RetryAtUtc = retryAtUtc;
    }

    public ProductionBonusOutcomeStatus Status { get; }
    public string Message { get; }
    public IReadOnlyList<ProductionBonusResourceState> Resources { get; }
    public TimeSpan? ServerUtcOffset { get; }
    public bool FreeVideoAvailable { get; }
    public IReadOnlyList<string> AttemptedResources { get; }
    public IReadOnlyList<string> UnconfirmedResources { get; }
    public DateTimeOffset? RetryAtUtc { get; }

    public bool HasObservation => Resources.Count > 0;

    public bool ShouldActivateAfterScan =>
        Status == ProductionBonusOutcomeStatus.Observed
        && FreeVideoAvailable
        && Resources.All(resource => resource.RemainingSeconds <= 0);

    public static ProductionBonusOutcome Observed(
        string message,
        IReadOnlyList<ProductionBonusResourceState> resources,
        TimeSpan? serverUtcOffset,
        bool freeVideoAvailable,
        IReadOnlyList<string>? attemptedResources = null,
        IReadOnlyList<string>? unconfirmedResources = null)
        => new(
            ProductionBonusOutcomeStatus.Observed,
            message,
            resources,
            serverUtcOffset,
            freeVideoAvailable,
            attemptedResources ?? [],
            unconfirmedResources ?? []);

    public static ProductionBonusOutcome AccountDeletionPending(string message)
        => new(ProductionBonusOutcomeStatus.AccountDeletionPending, message, []);

    public static ProductionBonusOutcome Deferred(string message, DateTimeOffset retryAtUtc)
        => new(ProductionBonusOutcomeStatus.Deferred, message, [], retryAtUtc: retryAtUtc);

    public static ProductionBonusOutcome Failed(string message)
        => new(ProductionBonusOutcomeStatus.Failed, message, []);
}
