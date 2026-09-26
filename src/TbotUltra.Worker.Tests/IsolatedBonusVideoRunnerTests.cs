using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class IsolatedBonusVideoRunnerTests
{
    [Fact]
    public async Task Operation_AppliesExistingCooldownOnlyToItsFirstRequest()
    {
        var cooldownChecks = new List<bool>();
        var operation = new IsolatedBonusVideoOperation((_, enforceExistingCooldown, _) =>
        {
            cooldownChecks.Add(enforceExistingCooldown);
            return Task.FromResult(Completed());
        });

        await operation.RunAsync(new ProductionBonusVideoRequest("lumber"), CancellationToken.None);
        await operation.RunAsync(new ProductionBonusVideoRequest("clay"), CancellationToken.None);

        Assert.Equal([true, false], cooldownChecks);
    }

    [Fact]
    public async Task UnavailableRunner_ReturnsTypedOutcome()
    {
        var result = await UnavailableIsolatedBonusVideoRunner.Instance
            .BeginOperation()
            .RunAsync(
                new AdventureBonusVideoRequest("difficulty", "Hard"),
                CancellationToken.None);

        Assert.Equal(IsolatedBonusVideoRunStatus.Unavailable, result.Status);
        Assert.Equal(BonusVideoFailureKind.Session, result.FailureKind);
    }

    [Fact]
    public async Task ClosedBrowserSession_ReturnsTypedOutcome()
    {
        var session = new BrowserSession(
            new BotOptions { BaseUrl = "https://example.invalid" },
            new AccountOptions { Name = "test", Username = "test", Password = "test" },
            Path.GetTempPath());

        var result = await session
            .BeginIsolatedBonusVideoOperation(new TravianSessionCache(), interactive: false)
            .RunAsync(
                new AdventureBonusVideoRequest("difficulty", "Hard"),
                CancellationToken.None);

        Assert.Equal(IsolatedBonusVideoRunStatus.ExecutionFailed, result.Status);
        Assert.Equal(BonusVideoFailureKind.Session, result.FailureKind);
    }

    [Theory]
    [InlineData((int)IsolatedBonusVideoRunStatus.Completed)]
    [InlineData((int)IsolatedBonusVideoRunStatus.TimedOut)]
    public async Task Containment_ClosesAfterTypedOutcome(int statusValue)
    {
        var status = (IsolatedBonusVideoRunStatus)statusValue;
        var closed = false;

        var result = await IsolatedBonusVideoContainment.RunAsync(
            () => Task.FromResult(new IsolatedBonusVideoRunResult(
                status,
                status.ToString(),
                status == IsolatedBonusVideoRunStatus.TimedOut
                    ? BonusVideoFailureKind.Timeout
                    : BonusVideoFailureKind.None)),
            () =>
            {
                closed = true;
                return Task.CompletedTask;
            });

        Assert.Equal(status, result.Status);
        Assert.True(closed);
    }

    [Fact]
    public async Task Containment_ClosesAfterUnexpectedFailure()
    {
        var closed = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IsolatedBonusVideoContainment.RunAsync<IsolatedBonusVideoRunResult>(
                () => throw new InvalidOperationException("failed"),
                () =>
                {
                    closed = true;
                    return Task.CompletedTask;
                }));

        Assert.True(closed);
    }

    [Fact]
    public async Task Runner_InterfaceReturnsTypedOutcome()
    {
        var expected = new IsolatedBonusVideoRunResult(
            IsolatedBonusVideoRunStatus.CooldownActive,
            "cooldown",
            BonusVideoFailureKind.NoAdOrCookies,
            new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var operation = new RecordingOperation(expected);
        var runner = new RecordingRunner(operation);
        var request = new ProductionBonusVideoRequest("crop");

        var actual = await runner.BeginOperation().RunAsync(request, CancellationToken.None);

        Assert.Same(request, operation.Request);
        Assert.Equal(expected, actual);
    }

    private static IsolatedBonusVideoRunResult Completed()
        => new(
            IsolatedBonusVideoRunStatus.Completed,
            "completed",
            BonusVideoFailureKind.None);

    private sealed class RecordingRunner(IIsolatedBonusVideoOperation operation) : IIsolatedBonusVideoRunner
    {
        public IIsolatedBonusVideoOperation BeginOperation() => operation;
    }

    private sealed class RecordingOperation(IsolatedBonusVideoRunResult result) : IIsolatedBonusVideoOperation
    {
        internal IsolatedBonusVideoRequest? Request { get; private set; }

        public Task<IsolatedBonusVideoRunResult> RunAsync(
            IsolatedBonusVideoRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(result);
        }
    }
}
