using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal sealed record AutomationCandidate(
    Guid Id,
    string TaskName,
    QueueGroup Group,
    string? VillageKey,
    int Priority,
    DateTimeOffset NextAttemptAt,
    AutomationDecisionRequirement? Decision = null)
{
    internal static AutomationCandidate FromQueueItem(QueueItem item) => new(
        item.Id,
        item.TaskName,
        item.Group,
        item.Payload.GetValueOrDefault(BotOptionPayloadKeys.TargetVillageKey),
        item.Priority,
        item.NextAttemptAt);
}

internal sealed record AutomationDecisionRequirement(string Code);

internal sealed record AutomationStateSnapshot(
    IReadOnlyList<AutomationCandidate> Candidates,
    bool IsComplete = false,
    DateTimeOffset? NextWakeAt = null);

internal interface IAutomationModePassPort
{
    ValueTask<AutomationStateSnapshot> ReadAsync(
        AutomationRunContext context,
        CancellationToken cancellationToken);

    ValueTask<AutomationActionOutcome> ExecuteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        AutomationActionOutcome outcome,
        CancellationToken cancellationToken);
}

internal sealed class ContextGuardedAutomationModePass(
    Func<string?> activeAccountKey,
    Func<long> browserGeneration,
    IAutomationModePassPort inner) : IAutomationModePassPort
{
    public ValueTask<AutomationStateSnapshot> ReadAsync(
        AutomationRunContext context,
        CancellationToken cancellationToken)
    {
        EnsureCurrentContext(context);
        return inner.ReadAsync(context, cancellationToken);
    }

    public ValueTask<AutomationActionOutcome> ExecuteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        CancellationToken cancellationToken)
    {
        EnsureCurrentContext(context);
        return inner.ExecuteAsync(context, action, cancellationToken);
    }

    public ValueTask CompleteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        AutomationActionOutcome outcome,
        CancellationToken cancellationToken)
    {
        EnsureCurrentContext(context);
        return inner.CompleteAsync(context, action, outcome, cancellationToken);
    }

    private void EnsureCurrentContext(AutomationRunContext context)
    {
        if (!string.Equals(
                context.AccountKey,
                activeAccountKey(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new AutomationContextException(
                AutomationFailureKind.AccountAccess,
                "active-account-changed",
                "The active account changed during the automation run.");
        }

        if (context.BrowserGeneration != browserGeneration())
        {
            throw new AutomationContextException(
                AutomationFailureKind.StaleBrowserGeneration,
                "browser-generation-changed",
                "The browser generation changed during the automation run.");
        }
    }
}

internal sealed class DelegateAutomationModePassPort(
    Func<CancellationToken, ValueTask<AutomationStateSnapshot>> readAsync,
    Func<AutomationCandidate, CancellationToken, ValueTask<AutomationActionOutcome>> executeAsync,
    Func<AutomationCandidate, AutomationActionOutcome, CancellationToken, ValueTask>? completeAsync = null)
    : IAutomationModePassPort
{
    public ValueTask<AutomationStateSnapshot> ReadAsync(
        AutomationRunContext context,
        CancellationToken cancellationToken) => readAsync(cancellationToken);

    public ValueTask<AutomationActionOutcome> ExecuteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        CancellationToken cancellationToken) => executeAsync(action, cancellationToken);

    public ValueTask CompleteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        AutomationActionOutcome outcome,
        CancellationToken cancellationToken) =>
        completeAsync?.Invoke(action, outcome, cancellationToken) ?? ValueTask.CompletedTask;
}

internal sealed class EmptyAutomationModePass : IAutomationModePassPort
{
    internal static EmptyAutomationModePass Instance { get; } = new();

    public ValueTask<AutomationStateSnapshot> ReadAsync(
        AutomationRunContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new AutomationStateSnapshot([]));

    public ValueTask<AutomationActionOutcome> ExecuteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(AutomationActionOutcome.Skipped);

    public ValueTask CompleteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        AutomationActionOutcome outcome,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
