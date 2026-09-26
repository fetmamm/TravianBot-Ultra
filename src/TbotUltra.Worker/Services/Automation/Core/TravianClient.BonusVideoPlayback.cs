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

        public async Task<bool> PreparePollAsync(
            string label,
            string logPrefix,
            bool muteConfirmed,
            CancellationToken cancellationToken)
        {
            await owner.TryClickBonusVideoSkipAdAsync(label, logPrefix, cancellationToken);
            return muteConfirmed
                || await owner.MuteBonusVideoAsync(label, logPrefix, cancellationToken);
        }

        public Task<string?> ReadVisibleProviderFailureAsync(CancellationToken cancellationToken)
            => owner.TryReadVisibleBonusVideoFailureAsync(cancellationToken);

        public Task<bool> IsPlayerPresentAsync(CancellationToken cancellationToken)
            => owner.IsBonusVideoPlayerPresentAsync(cancellationToken);

        public Task CompleteAsync(CancellationToken cancellationToken)
            => owner.DelayBeforeClickAsync(cancellationToken);

        public void Log(string message) => owner.Notify(message);
    }
}
