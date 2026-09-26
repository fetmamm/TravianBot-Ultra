namespace TbotUltra.Worker.Infrastructure;

internal enum BonusVideoFeatureSignal
{
    None,
    RewardConfirmed,
    ExpectedPageMissing,
    ExpectedPageVisible,
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
    TimeSpan PollInterval,
    bool AcceptClosedPlaybackAfterProtectedInterval = false,
    bool RequireDialogAbsentForDefinitiveCompletion = false);

internal readonly record struct BonusVideoPlaybackPollContext(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset NowUtc,
    TimeSpan Elapsed,
    bool ProtectedIntervalElapsed,
    bool PlaybackClosed);

internal readonly record struct BonusVideoFeatureObservation(BonusVideoFeatureSignal Signal);

internal readonly record struct BonusVideoPlayerObservation(
    bool DialogPresent,
    bool DialogOpen,
    bool PlayerPresent);

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

    Task<bool> MaintainPlaybackAsync(
        string label,
        string logPrefix,
        bool muteConfirmed,
        CancellationToken cancellationToken);

    Task<BonusVideoPlayerObservation> ObservePlayerAsync(CancellationToken cancellationToken);

    Task<string?> ReadVisibleProviderFailureAsync(CancellationToken cancellationToken);

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
        Func<BonusVideoPlaybackPollContext, CancellationToken, Task<BonusVideoFeatureObservation>> observeFeatureAsync,
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
        var playbackWasActive = false;

        while (_timeProvider.GetUtcNow() < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _delayAsync(request.PollInterval, cancellationToken);
            muteConfirmed = await adapter.MaintainPlaybackAsync(
                request.Label,
                request.LogPrefix,
                muteConfirmed,
                cancellationToken);

            var nowUtc = _timeProvider.GetUtcNow();
            var elapsed = nowUtc - startedAtUtc.Value;
            var protectedIntervalElapsed = BonusVideoPlaybackPolicy.MayComplete(elapsed.TotalSeconds);
            var player = await adapter.ObservePlayerAsync(cancellationToken);
            var playbackClosed = !player.DialogOpen && !player.PlayerPresent;
            var context = new BonusVideoPlaybackPollContext(
                startedAtUtc.Value,
                nowUtc,
                elapsed,
                protectedIntervalElapsed,
                playbackClosed);
            var feature = await observeFeatureAsync(context, cancellationToken);

            playbackWasActive |= player.PlayerPresent
                || feature.Signal == BonusVideoFeatureSignal.ExpectedPageMissing;
            var completionSignal = ResolveCompletionSignal(
                request,
                feature.Signal,
                player,
                playbackWasActive);

            if (completionSignal == BonusVideoCompletionSignal.Definitive
                || (completionSignal == BonusVideoCompletionSignal.AfterProtectedInterval
                    && protectedIntervalElapsed))
            {
                adapter.Log(
                    $"{request.LogPrefix} {request.Label}: completion accepted after "
                    + $"{elapsed.TotalSeconds:F1}s signal={completionSignal}.");
                await adapter.CompleteAsync(cancellationToken);
                return new BonusVideoPlaybackResult(
                    BonusVideoPlaybackStatus.Completed,
                    startedAtUtc.Value);
            }

            if (completionSignal == BonusVideoCompletionSignal.AfterProtectedInterval
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
            if (BonusVideoPlaybackPolicy.MayAcceptProviderFailure(
                    elapsed.TotalSeconds,
                    consecutiveProviderFailures,
                    player.PlayerPresent))
            {
                adapter.Log(
                    $"{request.LogPrefix} {request.Label}: provider failure confirmed after "
                    + $"{elapsed.TotalSeconds:F1}s confirmations={consecutiveProviderFailures} "
                    + $"playerPresent={player.PlayerPresent}.");
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

    private static BonusVideoCompletionSignal ResolveCompletionSignal(
        BonusVideoPlaybackRequest request,
        BonusVideoFeatureSignal featureSignal,
        BonusVideoPlayerObservation player,
        bool playbackWasActive)
    {
        if (featureSignal == BonusVideoFeatureSignal.RewardConfirmed)
        {
            var dialogFinished = request.RequireDialogAbsentForDefinitiveCompletion
                ? !player.DialogPresent
                : !player.DialogOpen;
            return dialogFinished && !player.PlayerPresent
                ? BonusVideoCompletionSignal.Definitive
                : BonusVideoCompletionSignal.AfterProtectedInterval;
        }

        if (featureSignal == BonusVideoFeatureSignal.ExpectedPageVisible
            && playbackWasActive
            && !player.PlayerPresent)
        {
            return BonusVideoCompletionSignal.Definitive;
        }

        return request.AcceptClosedPlaybackAfterProtectedInterval
            && !player.DialogOpen
            && !player.PlayerPresent
                ? BonusVideoCompletionSignal.AfterProtectedInterval
                : BonusVideoCompletionSignal.None;
    }

    private enum BonusVideoCompletionSignal
    {
        None,
        AfterProtectedInterval,
        Definitive,
    }
}
