using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BonusVideoPlaybackTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DefinitiveReward_CompletesBeforeProtectedInterval()
    {
        var fixture = new PlaybackFixture();

        var result = await fixture.RunAsync((_, _) => Task.FromResult(
            new BonusVideoPlaybackObservation(BonusVideoCompletionSignal.Definitive)));

        Assert.Equal(BonusVideoPlaybackStatus.Completed, result.Status);
        Assert.Equal(1, fixture.Adapter.CompletedCount);
        Assert.Equal(1, fixture.Adapter.StartCount);
    }

    [Fact]
    public async Task WeakCompletion_WaitsForProtectedInterval()
    {
        var fixture = new PlaybackFixture(TimeSpan.FromSeconds(20));
        var observations = 0;

        var result = await fixture.RunAsync((_, _) =>
        {
            observations++;
            return Task.FromResult(new BonusVideoPlaybackObservation(
                BonusVideoCompletionSignal.AfterProtectedInterval));
        });

        Assert.Equal(BonusVideoPlaybackStatus.Completed, result.Status);
        Assert.Equal(3, observations);
    }

    [Fact]
    public async Task ProviderFailure_IsIgnoredDuringProtectionAndNeedsTwoConfirmationsWithPlayer()
    {
        var fixture = new PlaybackFixture(TimeSpan.FromSeconds(30));
        fixture.Adapter.ProviderFailures.Enqueue("provider failed");
        fixture.Adapter.ProviderFailures.Enqueue("provider failed");

        var result = await fixture.RunAsync((_, _) => Task.FromResult(
            new BonusVideoPlaybackObservation(BonusVideoCompletionSignal.None, PlayerPresent: true)));

        Assert.Equal(BonusVideoPlaybackStatus.ProviderFailed, result.Status);
        Assert.Equal("provider failed", result.ProviderFailure);
        Assert.Contains(fixture.Adapter.Logs, line => line.Contains("ignored during the protected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingPlayer_AllowsOneProviderFailureAfterProtectedInterval()
    {
        var fixture = new PlaybackFixture(TimeSpan.FromSeconds(60));
        fixture.Adapter.ProviderFailures.Enqueue("provider failed");

        var result = await fixture.RunAsync((_, _) => Task.FromResult(
            new BonusVideoPlaybackObservation(BonusVideoCompletionSignal.None, PlayerPresent: false)));

        Assert.Equal(BonusVideoPlaybackStatus.ProviderFailed, result.Status);
    }

    [Fact]
    public async Task MutePreparation_RetriesUntilConfirmed()
    {
        var fixture = new PlaybackFixture();
        fixture.Adapter.MuteResults.Enqueue(false);
        fixture.Adapter.MuteResults.Enqueue(true);

        var polls = 0;
        var result = await fixture.RunAsync((_, _) =>
        {
            polls++;
            return Task.FromResult(new BonusVideoPlaybackObservation(
                polls >= 2 ? BonusVideoCompletionSignal.Definitive : BonusVideoCompletionSignal.None));
        });

        Assert.Equal(BonusVideoPlaybackStatus.Completed, result.Status);
        Assert.Equal(2, fixture.Adapter.PrepareCount);
        Assert.Equal(new[] { false, false }, fixture.Adapter.ReceivedMuteStates);
    }

    [Fact]
    public async Task MissingStart_ReturnsWithoutPolling()
    {
        var fixture = new PlaybackFixture();
        fixture.Adapter.StartedAtUtc = null;

        var result = await fixture.RunAsync((_, _) => throw new InvalidOperationException("must not poll"));

        Assert.Equal(BonusVideoPlaybackStatus.StartUnavailable, result.Status);
        Assert.Equal(0, fixture.Adapter.PrepareCount);
    }

    [Fact]
    public async Task MissingCompletion_ReturnsTypedTimeout()
    {
        var fixture = new PlaybackFixture(TimeSpan.FromSeconds(60));

        var result = await fixture.RunAsync((_, _) => Task.FromResult(
            new BonusVideoPlaybackObservation(BonusVideoCompletionSignal.None)));

        Assert.Equal(BonusVideoPlaybackStatus.TimedOut, result.Status);
        Assert.Equal(0, fixture.Adapter.CompletedCount);
    }

    private sealed class PlaybackFixture
    {
        private readonly MutableTimeProvider _time = new(Now);
        private readonly TimeSpan _pollInterval;

        internal PlaybackFixture(TimeSpan? pollInterval = null)
        {
            _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
            Adapter.StartedAtUtc = Now;
        }

        internal RecordingAdapter Adapter { get; } = new();

        internal Task<BonusVideoPlaybackResult> RunAsync(
            Func<BonusVideoPlaybackPollContext, CancellationToken, Task<BonusVideoPlaybackObservation>> observeAsync)
        {
            var playback = new BonusVideoPlayback(
                Adapter,
                _time,
                (delay, _) =>
                {
                    _time.Advance(delay);
                    return Task.CompletedTask;
                });
            return playback.RunAsync(
                new BonusVideoPlaybackRequest("test", "[test]", _pollInterval),
                observeAsync,
                CancellationToken.None);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan delay) => _now += delay;
    }

    private sealed class RecordingAdapter : IBonusVideoPlaybackAdapter
    {
        internal Queue<string?> ProviderFailures { get; } = new();
        internal Queue<bool> MuteResults { get; } = new();
        internal List<bool> ReceivedMuteStates { get; } = [];
        internal List<string> Logs { get; } = [];
        internal DateTimeOffset? StartedAtUtc { get; set; }
        internal int StartCount { get; private set; }
        internal int PrepareCount { get; private set; }
        internal int CompletedCount { get; private set; }

        public Task<DateTimeOffset?> StartAsync(string label, string logPrefix, CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.FromResult(StartedAtUtc);
        }

        public Task<bool> PreparePollAsync(
            string label,
            string logPrefix,
            bool muteConfirmed,
            CancellationToken cancellationToken)
        {
            PrepareCount++;
            ReceivedMuteStates.Add(muteConfirmed);
            return Task.FromResult(muteConfirmed || (MuteResults.Count > 0 && MuteResults.Dequeue()));
        }

        public Task<string?> ReadVisibleProviderFailureAsync(CancellationToken cancellationToken)
            => Task.FromResult(ProviderFailures.Count > 0 ? ProviderFailures.Dequeue() : null);

        public Task<bool> IsPlayerPresentAsync(CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            CompletedCount++;
            return Task.CompletedTask;
        }

        public void Log(string message) => Logs.Add(message);
    }
}
