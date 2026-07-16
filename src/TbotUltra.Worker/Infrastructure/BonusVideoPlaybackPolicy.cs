namespace TbotUltra.Worker.Infrastructure;

internal static class BonusVideoPlaybackPolicy
{
    internal const int MinimumPostPlaySeconds = 60;
    internal const int PostPlayTimeoutSeconds = 120;
    internal const int ProviderFailureConfirmations = 2;
    internal const int IsolatedActionTimeoutSeconds = 240;

    internal static bool MayComplete(double elapsedPostPlaySeconds)
        => elapsedPostPlaySeconds >= MinimumPostPlaySeconds;

    internal static bool MayAcceptProviderFailure(
        double elapsedPostPlaySeconds,
        int consecutiveConfirmations,
        bool playerPresent)
    {
        if (!MayComplete(elapsedPostPlaySeconds))
        {
            return false;
        }

        return !playerPresent || consecutiveConfirmations >= ProviderFailureConfirmations;
    }

    internal static int RemainingGraceSeconds(double elapsedPostPlaySeconds)
        => Math.Max(0, (int)Math.Ceiling(MinimumPostPlaySeconds - elapsedPostPlaySeconds));
}
