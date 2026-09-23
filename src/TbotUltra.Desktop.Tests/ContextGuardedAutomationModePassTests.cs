using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ContextGuardedAutomationModePassTests
{
    [Fact]
    public async Task ReadAsync_ForwardsToTheGuardedModePass()
    {
        var inner = new RecordingModePassPort("continuous");
        var port = CreatePort(inner);

        var result = await port.ReadAsync(CurrentContext(), CancellationToken.None);

        Assert.Equal("continuous", result.Candidates[0].TaskName);
        Assert.Equal(1, inner.ReadCount);
    }

    [Fact]
    public async Task CompleteAsync_ForwardsTheOutcomeToTheGuardedModePass()
    {
        var inner = new RecordingModePassPort("continuous");
        var port = CreatePort(inner);
        var action = Candidate();

        await port.CompleteAsync(
            CurrentContext(),
            action,
            AutomationActionOutcome.Deferred,
            CancellationToken.None);

        Assert.Equal(1, inner.CompleteCount);
        Assert.Equal(AutomationActionOutcome.Deferred, inner.LastOutcome);
    }

    [Fact]
    public async Task ReadAsync_RejectsAnAccountChangedDuringTheRun()
    {
        var inner = new RecordingModePassPort("continuous");
        var port = new ContextGuardedAutomationModePass(() => "account-2", () => 7, inner);

        var exception = await Assert.ThrowsAsync<AutomationContextException>(async () =>
            await port.ReadAsync(CurrentContext(), CancellationToken.None));

        Assert.Contains("active account changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AutomationFailureKind.AccountAccess, exception.FailureKind);
        Assert.Equal("active-account-changed", exception.DiagnosticCode);
        Assert.Equal(0, inner.ReadCount);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsABrowserGenerationChangedDuringTheRun()
    {
        var inner = new RecordingModePassPort("continuous");
        var port = new ContextGuardedAutomationModePass(() => "account-1", () => 8, inner);

        var exception = await Assert.ThrowsAsync<AutomationContextException>(async () =>
            await port.ExecuteAsync(CurrentContext(), Candidate(), CancellationToken.None));

        Assert.Contains("browser generation changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AutomationFailureKind.StaleBrowserGeneration, exception.FailureKind);
        Assert.Equal("browser-generation-changed", exception.DiagnosticCode);
        Assert.Equal(0, inner.ExecuteCount);
    }

    private static ContextGuardedAutomationModePass CreatePort(IAutomationModePassPort inner) => new(
        () => "ACCOUNT-1",
        () => 7,
        inner);

    private static AutomationRunContext CurrentContext() => new(
        "account-1",
        new Uri("https://ts1.x1.example/"),
        7);

    private static AutomationCandidate Candidate() => new(
        Guid.NewGuid(),
        "status",
        QueueGroup.Account,
        null,
        0,
        DateTimeOffset.UtcNow);

    private sealed class RecordingModePassPort(string taskName) : IAutomationModePassPort
    {
        internal int ReadCount { get; private set; }

        internal int ExecuteCount { get; private set; }

        internal int CompleteCount { get; private set; }

        internal AutomationActionOutcome? LastOutcome { get; private set; }

        public ValueTask<AutomationStateSnapshot> ReadAsync(
            AutomationRunContext context,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(new AutomationStateSnapshot(
                [new AutomationCandidate(Guid.NewGuid(), taskName, QueueGroup.Account, null, 0, DateTimeOffset.UtcNow)]));
        }

        public ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationRunContext context,
            AutomationCandidate action,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return ValueTask.FromResult(AutomationActionOutcome.Completed);
        }

        public ValueTask CompleteAsync(
            AutomationRunContext context,
            AutomationCandidate action,
            AutomationActionOutcome outcome,
            CancellationToken cancellationToken)
        {
            CompleteCount++;
            LastOutcome = outcome;
            return ValueTask.CompletedTask;
        }
    }
}
