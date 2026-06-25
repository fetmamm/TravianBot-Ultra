namespace TbotUltra.Worker.Domain;

public sealed record BotTaskExecutionResult(IReadOnlyList<BotTaskResult> Tasks)
{
    public BotTaskResult? LastTask => Tasks.Count == 0 ? null : Tasks[^1];

    public static BotTaskExecutionResult Empty { get; } = new([]);
}

public sealed record BotTaskResult(
    string TaskName,
    string? Message,
    ConstructionTaskOutcome ConstructionOutcome);

public enum ConstructionTaskOutcome
{
    None = 0,
    QueuedOrInProgress = 1,
    ConfirmedComplete = 2,
    AlreadySatisfied = 3,
    WaitingOrBlocked = 4,
    UnknownSuccess = 5,
    // Construct target slot already holds the building (confirmed live). The task can never construct and
    // is removed from the queue rather than retried/failed.
    AlreadyExists = 6,
}
