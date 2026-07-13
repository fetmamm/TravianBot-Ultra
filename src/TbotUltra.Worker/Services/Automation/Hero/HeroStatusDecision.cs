namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless interpretation of hero status signals collected from Travian pages.
/// </summary>
internal static class HeroStatusDecision
{
    internal static int ResolveAdventureCount(
        bool sidebarFound,
        int sidebarCount,
        int statusCount)
    {
        return Math.Max(0, sidebarFound ? sidebarCount : statusCount);
    }

    internal static int? TryResolveAdventureCount(
        bool sidebarFound,
        int sidebarCount,
        bool statusExists,
        int statusCount)
    {
        if (sidebarFound)
        {
            return Math.Max(0, sidebarCount);
        }

        return statusExists ? Math.Max(0, statusCount) : null;
    }

    internal static bool IsDeadStatusText(string? statusText)
    {
        var text = (statusText ?? string.Empty).ToLowerInvariant();
        return text.Contains("dead", StringComparison.Ordinal)
            || text.Contains("deceased", StringComparison.Ordinal);
    }

    internal static bool IsAwayStatusText(string? statusText)
    {
        var text = (statusText ?? string.Empty).ToLowerInvariant();
        return text.Contains("on the way", StringComparison.Ordinal)
            || text.Contains("on its way", StringComparison.Ordinal)
            || text.Contains("arrival in", StringComparison.Ordinal)
            || text.Contains("back from", StringComparison.Ordinal)
            || text.Contains("returning", StringComparison.Ordinal);
    }

    internal static int ComputeHpWaitSeconds(
        int? hpPercent,
        int thresholdPercent,
        int regenPerDayPercent,
        int maxDeferSeconds)
    {
        const int fallbackSeconds = 600;
        if (hpPercent is not int hp || regenPerDayPercent <= 0)
        {
            return fallbackSeconds;
        }

        var deficit = thresholdPercent - hp;
        if (deficit <= 0)
        {
            return fallbackSeconds;
        }

        var hours = deficit * 24.0 / regenPerDayPercent;
        var seconds = (int)Math.Ceiling(hours * 3600.0);
        return Math.Clamp(seconds, 60, maxDeferSeconds);
    }
}
