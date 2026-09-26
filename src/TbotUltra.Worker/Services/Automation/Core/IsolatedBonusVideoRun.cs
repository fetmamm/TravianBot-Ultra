using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Worker.Services;

internal enum IsolatedBonusVideoRunStatus
{
    Completed,
    Failed,
    ExecutionFailed,
    CooldownActive,
    TimedOut,
    Unavailable,
}

internal abstract record IsolatedBonusVideoRequest;

internal sealed record AdventureBonusVideoRequest(
    string BoxClass,
    string Label)
    : IsolatedBonusVideoRequest;

internal sealed record ConstructFasterBonusVideoRequest(
    int SlotId,
    int? Gid,
    string BuildingName)
    : IsolatedBonusVideoRequest;

internal sealed record ProductionBonusVideoRequest(string Resource)
    : IsolatedBonusVideoRequest;

internal sealed record IsolatedBonusVideoRunResult(
    IsolatedBonusVideoRunStatus Status,
    string Message,
    BonusVideoFailureKind FailureKind,
    DateTimeOffset? RetryAtUtc = null)
{
    internal int RemainingRetrySeconds(DateTimeOffset nowUtc)
        => RetryAtUtc is { } retryAtUtc
            ? Math.Max(1, (int)Math.Ceiling((retryAtUtc - nowUtc).TotalSeconds))
            : 0;
}

internal interface IIsolatedBonusVideoRunner
{
    IIsolatedBonusVideoOperation BeginOperation();
}

internal interface IIsolatedBonusVideoOperation
{
    Task<IsolatedBonusVideoRunResult> RunAsync(
        IsolatedBonusVideoRequest request,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableIsolatedBonusVideoRunner : IIsolatedBonusVideoRunner
{
    internal static UnavailableIsolatedBonusVideoRunner Instance { get; } = new();

    private UnavailableIsolatedBonusVideoRunner()
    {
    }

    public IIsolatedBonusVideoOperation BeginOperation() => UnavailableOperation.Instance;

    private sealed class UnavailableOperation : IIsolatedBonusVideoOperation
    {
        internal static UnavailableOperation Instance { get; } = new();

        public Task<IsolatedBonusVideoRunResult> RunAsync(
            IsolatedBonusVideoRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IsolatedBonusVideoRunResult(
                IsolatedBonusVideoRunStatus.Unavailable,
                "Isolated bonus-video browser is unavailable.",
                BonusVideoFailureKind.Session));
        }
    }
}
