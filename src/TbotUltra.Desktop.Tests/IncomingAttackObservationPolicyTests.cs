using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class IncomingAttackObservationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnchangedSignal_DoesNotReadAgainAfterOneMinute()
    {
        var arrivals = new[] { Now.AddMinutes(20), Now.AddMinutes(21) };
        var signal = new IncomingAttackSignal("BRE", Dorf1ArrivalTimesUtc: arrivals);

        var shouldRead = IncomingAttackObservationPolicy.ShouldReadDetails(
            signal, confirmedMovementCount: 2, Now.AddMinutes(-1), Now);

        Assert.False(shouldRead);
    }

    [Fact]
    public void ConfirmedEight_DoesNotReadAgainWhenRenderedCountdownsDrift()
    {
        var confirmedArrivals = Enumerable.Range(0, 8)
            .Select(index => Now.AddMinutes(20 + index))
            .ToArray();
        var nextDorf1Arrivals = confirmedArrivals
            .Select(arrival => arrival.AddSeconds(20))
            .ToArray();
        var signal = new IncomingAttackSignal("BRO", Dorf1ArrivalTimesUtc: nextDorf1Arrivals);
        var shouldRead = IncomingAttackObservationPolicy.ShouldReadDetails(
            signal, confirmedMovementCount: 8, Now.AddSeconds(-30), Now);

        Assert.False(shouldRead);
    }

    [Fact]
    public void ConfirmedCountDropping_DoesNotReadDetailsAgain()
    {
        var confirmedArrivals = Enumerable.Range(0, 8)
            .Select(index => Now.AddMinutes(20 + index))
            .ToArray();
        var signal = new IncomingAttackSignal("BRO", Dorf1ArrivalTimesUtc: confirmedArrivals.Skip(1).ToArray());
        var shouldRead = IncomingAttackObservationPolicy.ShouldReadDetails(
            signal, confirmedMovementCount: 8, Now.AddMinutes(-1), Now);

        Assert.False(shouldRead);
    }

    [Fact]
    public void IncreasedDorf1Count_ReadsImmediately()
    {
        var knownArrival = Now.AddMinutes(20);
        var signal = new IncomingAttackSignal(
            "BRE",
            Dorf1ArrivalTimesUtc: [knownArrival, Now.AddMinutes(21)]);
        var shouldRead = IncomingAttackObservationPolicy.ShouldReadDetails(
            signal, confirmedMovementCount: 1, Now.AddMinutes(-1), Now);

        Assert.True(shouldRead);
    }

    [Fact]
    public void NewPlusMarker_DoesNotReadAgainWithConfirmedMovementHistory()
    {
        var signal = new IncomingAttackSignal("LILAC");

        var shouldRead = IncomingAttackObservationPolicy.ShouldReadDetails(
            signal,
            confirmedMovementCount: 1,
            Now.AddMinutes(-1),
            Now,
            isNewPlusMarker: true);

        Assert.False(shouldRead);
    }

    [Fact]
    public void FirstPlusMarker_ReadsWhenNoMovementHistoryExists()
    {
        var signal = new IncomingAttackSignal("LILAC");

        var shouldRead = IncomingAttackObservationPolicy.ShouldReadDetails(
            signal,
            confirmedMovementCount: null,
            lastReadUtc: null,
            Now,
            isNewPlusMarker: true);

        Assert.True(shouldRead);
    }

    [Fact]
    public void UnchangedFallbackSignal_DoesNotRetryOnEveryDorf1Observation()
    {
        var arrivals = new[] { Now.AddMinutes(20), Now.AddMinutes(21) };
        var signal = new IncomingAttackSignal("BRE", Dorf1ArrivalTimesUtc: arrivals);

        var shouldRead = IncomingAttackObservationPolicy.ShouldReadDetails(
            signal, confirmedMovementCount: null, Now.AddMinutes(-1), Now);

        Assert.False(shouldRead);
    }

    [Fact]
    public void UnconfirmedSignal_ReceivesTenMinuteRetry()
    {
        var arrival = Now.AddMinutes(20);
        var signal = new IncomingAttackSignal("BRE", Dorf1ArrivalTimesUtc: [arrival]);
        var shouldRead = IncomingAttackObservationPolicy.ShouldReadDetails(
            signal, confirmedMovementCount: null, Now.AddMinutes(-10), Now);

        Assert.True(shouldRead);
    }

    [Fact]
    public void ConfirmedSignal_DoesNotReceivePeriodicRefresh()
    {
        var signal = new IncomingAttackSignal("BRE", Dorf1ArrivalTimesUtc: [Now.AddMinutes(20)]);

        var shouldRead = IncomingAttackObservationPolicy.ShouldReadDetails(
            signal, confirmedMovementCount: 1, Now.AddHours(-1), Now);

        Assert.False(shouldRead);
    }

    [Fact]
    public void PendingSignal_WithOnlyArrivedMovements_IsNotKept()
    {
        var signal = new IncomingAttackSignal(
            "BRE",
            Dorf1ArrivalTimesUtc: [Now.AddMinutes(-2), Now]);

        var shouldKeep = IncomingAttackObservationPolicy.ShouldKeepPendingSignal(
            signal,
            hasActiveConfirmedMovements: false,
            hasConfirmedMovementHistory: false,
            Now);

        Assert.False(shouldKeep);
    }

    [Fact]
    public void LegacyPendingSignal_AfterConfirmedMovementArrived_IsNotKept()
    {
        var signal = new IncomingAttackSignal("BRE");

        var shouldKeep = IncomingAttackObservationPolicy.ShouldKeepPendingSignal(
            signal,
            hasActiveConfirmedMovements: false,
            hasConfirmedMovementHistory: true,
            Now);

        Assert.False(shouldKeep);
    }

    [Fact]
    public void PendingSignal_WithFutureArrival_IsKept()
    {
        var signal = new IncomingAttackSignal(
            "BRE",
            Dorf1ArrivalTimesUtc: [Now.AddMinutes(-1), Now.AddMinutes(1)]);

        var shouldKeep = IncomingAttackObservationPolicy.ShouldKeepPendingSignal(
            signal,
            hasActiveConfirmedMovements: false,
            hasConfirmedMovementHistory: true,
            Now);

        Assert.True(shouldKeep);
    }

    [Fact]
    public void UnconfirmedPendingSignal_WithoutArrivalTime_IsKeptForRetry()
    {
        var signal = new IncomingAttackSignal("BRE");

        var shouldKeep = IncomingAttackObservationPolicy.ShouldKeepPendingSignal(
            signal,
            hasActiveConfirmedMovements: false,
            hasConfirmedMovementHistory: false,
            Now);

        Assert.True(shouldKeep);
    }

    [Fact]
    public void ConfirmedSnapshot_CountsOnlyPreviouslyUnknownAttackIds()
    {
        var previous = new[]
        {
            new IncomingAttack("known", "BRE", Now.AddMinutes(10)),
        };
        var current = new[]
        {
            new IncomingAttack("new-2", "BRE", Now.AddMinutes(12)),
            new IncomingAttack("known", "BRE", Now.AddMinutes(10)),
            new IncomingAttack("new-1", "BRE", Now.AddMinutes(11)),
        };

        var count = IncomingAttackObservationPolicy.CountNewConfirmedAttacks(previous, current);

        Assert.Equal(2, count);
    }

    [Fact]
    public void RepeatedConfirmedSnapshot_DoesNotReportNewAttacks()
    {
        var previous = new[]
        {
            new IncomingAttack("one", "BRE", Now.AddMinutes(10)),
            new IncomingAttack("two", "BRE", Now.AddMinutes(11)),
        };
        var reordered = previous.Reverse().ToArray();

        var count = IncomingAttackObservationPolicy.CountNewConfirmedAttacks(previous, reordered);

        Assert.Equal(0, count);
    }

    [Fact]
    public void MultipleNewAttacksInOneSnapshot_AllowOneBulkSound()
    {
        var shouldPlay = IncomingAttackObservationPolicy.ShouldPlaySound(
            soundEnabled: true,
            newAttackCount: 10,
            lastSoundUtc: null,
            TimeSpan.FromMinutes(1),
            Now);

        Assert.True(shouldPlay);
    }

    [Fact]
    public void NewAttacksInsideCooldown_DoNotAllowAnotherSound()
    {
        var shouldPlay = IncomingAttackObservationPolicy.ShouldPlaySound(
            soundEnabled: true,
            newAttackCount: 3,
            lastSoundUtc: Now.AddSeconds(-30),
            TimeSpan.FromMinutes(1),
            Now);

        Assert.False(shouldPlay);
    }

    [Fact]
    public void NewAttacksAfterCooldown_AllowAnotherBulkSound()
    {
        var shouldPlay = IncomingAttackObservationPolicy.ShouldPlaySound(
            soundEnabled: true,
            newAttackCount: 2,
            lastSoundUtc: Now.AddMinutes(-1),
            TimeSpan.FromMinutes(1),
            Now);

        Assert.True(shouldPlay);
    }
}
