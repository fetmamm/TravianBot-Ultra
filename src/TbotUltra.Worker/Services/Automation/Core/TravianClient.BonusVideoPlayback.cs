using System.Text.Json;
using Microsoft.Playwright;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private BonusVideoPlayback CreateBonusVideoPlayback()
        => new(new TravianClientBonusVideoPlaybackAdapter(this));

    private sealed class TravianClientBonusVideoPlaybackAdapter(TravianClient owner)
        : IBonusVideoPlaybackAdapter
    {
        public Task<DateTimeOffset?> StartAsync(
            string label,
            string logPrefix,
            CancellationToken cancellationToken)
            => owner.StartBonusVideoPlayerAsync(label, logPrefix, cancellationToken);

        public async Task<bool> MaintainPlaybackAsync(
            string label,
            string logPrefix,
            bool muteConfirmed,
            CancellationToken cancellationToken)
        {
            await owner.TryClickBonusVideoSkipAdAsync(label, logPrefix, cancellationToken);
            return muteConfirmed
                || await owner.MuteBonusVideoAsync(label, logPrefix, cancellationToken);
        }

        public async Task<BonusVideoPlayerObservation> ObservePlayerAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var rawJson = await owner._page.EvaluateAsync<string>(
                    """
                    () => {
                      const dialog = document.querySelector('#videoFeature');
                      return JSON.stringify({
                        dialogPresent: !!dialog,
                        dialogOpen: !!dialog && !String(dialog.className || '').includes('hide'),
                        playerPresent: !!document.querySelector('#videoArea, #videoFeature iframe')
                      });
                    }
                    """);
                using var doc = JsonDocument.Parse(rawJson ?? "{}");
                var root = doc.RootElement;
                return new BonusVideoPlayerObservation(
                    GetBoolean(root, "dialogPresent"),
                    GetBoolean(root, "dialogOpen"),
                    GetBoolean(root, "playerPresent"));
            }
            catch (PlaywrightException ex) when (IsBonusVideoNavigationTransition(ex))
            {
                // A navigation transition is unknown playback state. Treat the player as present so
                // neither completion nor provider failure can be accepted from a transient read.
                return new BonusVideoPlayerObservation(
                    DialogPresent: false,
                    DialogOpen: false,
                    PlayerPresent: true);
            }
        }

        public Task<string?> ReadVisibleProviderFailureAsync(CancellationToken cancellationToken)
            => owner.TryReadVisibleBonusVideoFailureAsync(cancellationToken);

        public Task CompleteAsync(CancellationToken cancellationToken)
            => owner.DelayBeforeClickAsync(cancellationToken);

        public void Log(string message) => owner.Notify(message);
    }
}
