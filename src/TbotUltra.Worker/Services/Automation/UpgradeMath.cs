namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless upgrade timing/level math extracted from <see cref="TravianClient"/>:
/// wait-second clamping and the level safety caps used to bound upgrade loops.
/// Pure functions so they can be unit-tested in isolation.
/// </summary>
internal static class UpgradeMath
{
    internal static int ComputeUpgradeWaitSeconds(int? detectedSeconds)
        => Math.Max(1, Math.Min((detectedSeconds ?? 0) + 1, 12 * 60 * 60));

    internal static int ClampResourceWaitSeconds(int? detectedSeconds)
    {
        const int min = 30;
        const int fallback = 5 * 60;
        const int max = 12 * 60 * 60;
        if (detectedSeconds is not int s || s <= 0) return fallback;
        if (s < min) return min;
        if (s > max) return max;
        return s + 1;
    }

    internal static int ComputeBuildingUpgradeSafetyCap(int targetLevel)
        => Math.Max(1, targetLevel + 5);

    internal static int ComputeResourceUpgradeSafetyCap(int targetLevel)
        => Math.Max(10, targetLevel + 8);

    internal static int ResolveResourceMaxLevelFallback(bool? isCapital)
    {
        return isCapital == true ? 40 : 10;
    }
}
