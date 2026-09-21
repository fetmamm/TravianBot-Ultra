using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ContinuousVillageStatusRoundTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DueRound_VerifiesVisitsAndSchedulesThroughOneSeam()
    {
        var port = new InMemoryPort
        {
            NextRoundUtc = Now,
            Villages = [new VillageStatusRoundVillage("1:2", "Alpha", null)],
        };
        var round = new ContinuousVillageStatusRound(
            new VillageStatusRoundCoordinator((_, _) => 0),
            port,
            new FixedTimeProvider(Now));

        await round.RunIfDueAsync(
            new BotOptions
            {
                VillageStatusSweepEnabled = true,
                VillageStatusSweepRoundMinMinutes = 10,
                VillageStatusSweepRoundMaxMinutes = 20,
            },
            default);

        Assert.Equal(
            ["membership", "load", "activity", "prepare", "visit:1:2", "schedule:10-20", "dispose"],
            port.Trace);
    }

    [Fact]
    public async Task FutureRound_DoesNotTouchTheAdapter()
    {
        var port = new InMemoryPort { NextRoundUtc = Now.AddMinutes(1) };
        var round = new ContinuousVillageStatusRound(
            new VillageStatusRoundCoordinator(),
            port,
            new FixedTimeProvider(Now));

        await round.RunIfDueAsync(
            new BotOptions { VillageStatusSweepEnabled = true },
            default);

        Assert.Empty(port.Trace);
    }

    private sealed class InMemoryPort : IContinuousVillageStatusRoundPort
    {
        public List<string> Trace { get; } = [];
        public DateTimeOffset NextRoundUtc { get; init; }
        public IReadOnlyList<VillageStatusRoundVillage> Villages { get; init; } = [];
        public bool CancelAfterFirstDelay { get; set; }
        public int DelayCount { get; private set; }
        public string? ActiveAccountName => "account-1";
        public string? ActiveVillageKey { get; init; }
        public DateTimeOffset GetNextRoundUtc() => NextRoundUtc;
        public ValueTask<bool> EnsureVillageMembershipVerifiedAsync(
            BotOptions options,
            CancellationToken cancellationToken)
        {
            Trace.Add("membership");
            return ValueTask.FromResult(true);
        }
        public ValueTask<IReadOnlyList<VillageStatusRoundVillage>> LoadVillagesAsync(BotOptions options)
        {
            Trace.Add("load");
            return ValueTask.FromResult(Villages);
        }
        public IDisposable BeginRoundActivity(int villageCount)
        {
            Trace.Add("activity");
            return new CallbackDisposable(() => Trace.Add("dispose"));
        }
        public VillageStatusRoundScheduleResult ScheduleNext(
            string? expectedAccountName,
            int minMinutes,
            int maxMinutes)
        {
            Trace.Add($"schedule:{minMinutes}-{maxMinutes}");
            return new VillageStatusRoundScheduleResult(
                Now.AddMinutes(minMinutes),
                WasPersisted: true,
                AccountChanged: false);
        }
        public ValueTask PrepareAsync(CancellationToken cancellationToken)
        {
            Trace.Add("prepare");
            return ValueTask.CompletedTask;
        }
        public ValueTask<VillageStatusRoundVisitResult> VisitAsync(
            VillageStatusRoundVillage village,
            int villageNumber,
            int villageCount,
            bool inboxStatusChecked,
            CancellationToken cancellationToken)
        {
            Trace.Add($"visit:{village.Key}");
            return ValueTask.FromResult(new VillageStatusRoundVisitResult(true, false));
        }
        public ValueTask DelayBeforeNextVillageAsync(CancellationToken cancellationToken)
        {
            DelayCount++;
            if (CancelAfterFirstDelay && DelayCount == 1)
                throw new OperationCanceledException();
            return ValueTask.CompletedTask;
        }
        public void Log(string message) { }
    }

    [Fact]
    public async Task PostLoginRound_StartsAtActiveVillage_AndDoesNotRequireRecurringScan()
    {
        var port = new InMemoryPort
        {
            NextRoundUtc = Now.AddHours(1),
            ActiveVillageKey = "b",
            Villages = [new("a", "A", null), new("b", "B", null), new("c", "C", null)],
        };
        var round = new ContinuousVillageStatusRound(
            new VillageStatusRoundCoordinator((_, _) => 0), port, new FixedTimeProvider(Now));

        round.RequestLoginRound();
        await round.RunIfDueAsync(new BotOptions { VillageStatusSweepEnabled = false }, default);

        Assert.Equal(["visit:b", "visit:c", "visit:a"],
            port.Trace.Where(entry => entry.StartsWith("visit:")).ToArray());
        Assert.DoesNotContain(port.Trace, entry => entry.StartsWith("schedule:"));
        Assert.False(round.LoginRoundPending);
    }

    [Fact]
    public async Task PostLoginRound_ResumesRemainingVillagesAfterInterruption()
    {
        var port = new InMemoryPort
        {
            NextRoundUtc = Now.AddHours(1),
            ActiveVillageKey = "b",
            Villages = [new("a", "A", null), new("b", "B", null)],
            CancelAfterFirstDelay = true,
        };
        var round = new ContinuousVillageStatusRound(
            new VillageStatusRoundCoordinator((_, _) => 0), port, new FixedTimeProvider(Now));
        round.RequestLoginRound();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await round.RunIfDueAsync(new BotOptions { VillageStatusSweepEnabled = false }, default));
        Assert.True(round.LoginRoundPending);
        await round.RunIfDueAsync(new BotOptions { VillageStatusSweepEnabled = false }, default);

        Assert.Equal(["visit:b", "visit:a"],
            port.Trace.Where(entry => entry.StartsWith("visit:")).ToArray());
        Assert.False(round.LoginRoundPending);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}
