namespace TbotUltra.Worker.Infrastructure;

internal enum BonusVideoCompletionSignal
{
    None,
    AfterProtectedInterval,
    Definitive,
}

internal enum BonusVideoPlaybackStatus
{
    Completed,
    StartUnavailable,
    ProviderFailed,
    TimedOut,
}

internal sealed record BonusVideoPlaybackRequest(
    string Label,
    string LogPrefix,
    TimeSpan PollInterval);

internal readonly record struct BonusVideoPlaybackPollContext(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset NowUtc,
    TimeSpan Elapsed,
    bool ProtectedIntervalElapsed);

internal readonly record struct BonusVideoPlaybackObservation(
    BonusVideoCompletionSignal CompletionSignal,
    bool? PlayerPresent = null);

internal sealed record BonusVideoPlaybackResult(
    BonusVideoPlaybackStatus Status,
    DateTimeOffset? StartedAtUtc = null,
    string? ProviderFailure = null);

internal interface IBonusVideoPlaybackAdapter
{
    Task<DateTimeOffset?> StartAsync(
        string label,
        string logPrefix,
        CancellationToken cancellationToken);

    Task<bool> PreparePollAsync(
        string label,
        string logPrefix,
        bool muteConfirmed,
        CancellationToken cancellationToken);

    Task<string?> ReadVisibleProviderFailureAsync(CancellationToken cancellationToken);

    Task<bool> IsPlayerPresentAsync(CancellationToken cancellationToken);

    Task CompleteAsync(CancellationToken cancellationToken);

    void Log(string message);
}

/// <summary>
/// Owns the feature-neutral Bonus Video Playback lifecycle. Bonus operations provide only their
/// reward observation; browser mechanics and timing stay behind this interface.
/// </summary>
internal sealed class BonusVideoPlayback(
    IBonusVideoPlaybackAdapter adapter,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync = delayAsync
        ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));

    internal async Task<BonusVideoPlaybackResult> RunAsync(
        BonusVideoPlaybackRequest request,
        Func<BonusVideoPlaybackPollContext, CancellationToken, Task<BonusVideoPlaybackObservation>> observeAsync,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = await adapter.StartAsync(
            request.Label,
            request.LogPrefix,
            cancellationToken);
        if (startedAtUtc is null)
        {
            return new BonusVideoPlaybackResult(BonusVideoPlaybackStatus.StartUnavailable);
        }

        var deadlineUtc = startedAtUtc.Value.AddSeconds(BonusVideoPlaybackPolicy.PostPlayTimeoutSeconds);
        var consecutiveProviderFailures = 0;
        var ignoredCompletionLogged = false;
        var ignoredProviderFailureLogged = false;
        var muteConfirmed = false;

        while (_timeProvider.GetUtcNow() < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _delayAsync(request.PollInterval, cancellationToken);
            muteConfirmed = await adapter.PreparePollAsync(
                request.Label,
                request.LogPrefix,
                muteConfirmed,
                cancellationToken);

            var nowUtc = _timeProvider.GetUtcNow();
            var elapsed = nowUtc - startedAtUtc.Value;
            var protectedIntervalElapsed = BonusVideoPlaybackPolicy.MayComplete(elapsed.TotalSeconds);
            var context = new BonusVideoPlaybackPollContext(
                startedAtUtc.Value,
                nowUtc,
                elapsed,
                protectedIntervalElapsed);
            var observation = await observeAsync(context, cancellationToken);

            if (observation.CompletionSignal == BonusVideoCompletionSignal.Definitive
                || (observation.CompletionSignal == BonusVideoCompletionSignal.AfterProtectedInterval
                    && protectedIntervalElapsed))
            {
                adapter.Log(
                    $"{request.LogPrefix} {request.Label}: completion accepted after "
                    + $"{elapsed.TotalSeconds:F1}s signal={observation.CompletionSignal}.");
                await adapter.CompleteAsync(cancellationToken);
                return new BonusVideoPlaybackResult(
                    BonusVideoPlaybackStatus.Completed,
                    startedAtUtc.Value);
            }

            if (observation.CompletionSignal == BonusVideoCompletionSignal.AfterProtectedInterval
                && !ignoredCompletionLogged)
            {
                ignoredCompletionLogged = true;
                adapter.Log(
                    $"{request.LogPrefix} {request.Label}: completion signal ignored during the protected "
                    + $"post-play minute ({BonusVideoPlaybackPolicy.RemainingGraceSeconds(elapsed.TotalSeconds)}s remaining).");
            }

            var providerFailure = await adapter.ReadVisibleProviderFailureAsync(cancellationToken);
            if (providerFailure is null)
            {
                consecutiveProviderFailures = 0;
                continue;
            }

            consecutiveProviderFailures++;
            var playerPresent = observation.PlayerPresent
                ?? await adapter.IsPlayerPresentAsync(cancellationToken);
            if (BonusVideoPlaybackPolicy.MayAcceptProviderFailure(
                    elapsed.TotalSeconds,
                    consecutiveProviderFailures,
                    playerPresent))
            {
                adapter.Log(
                    $"{request.LogPrefix} {request.Label}: provider failure confirmed after "
                    + $"{elapsed.TotalSeconds:F1}s confirmations={consecutiveProviderFailures} "
                    + $"playerPresent={playerPresent}.");
                return new BonusVideoPlaybackResult(
                    BonusVideoPlaybackStatus.ProviderFailed,
                    startedAtUtc.Value,
                    providerFailure);
            }

            if (!protectedIntervalElapsed && !ignoredProviderFailureLogged)
            {
                ignoredProviderFailureLogged = true;
                adapter.Log(
                    $"{request.LogPrefix} {request.Label}: provider text ignored during the protected "
                    + $"post-play minute ({BonusVideoPlaybackPolicy.RemainingGraceSeconds(elapsed.TotalSeconds)}s remaining).");
            }
        }

        adapter.Log(
            $"{request.LogPrefix} {request.Label}: completion was not confirmed within "
            + $"{BonusVideoPlaybackPolicy.PostPlayTimeoutSeconds}s after playback started.");
        return new BonusVideoPlaybackResult(
            BonusVideoPlaybackStatus.TimedOut,
            startedAtUtc.Value);
    }
}
