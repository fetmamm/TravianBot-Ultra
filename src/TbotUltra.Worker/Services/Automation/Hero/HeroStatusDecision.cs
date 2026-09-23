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

    internal static int ResolveAwayRetrySeconds(int? returnSeconds, bool isReinforcing)
    {
        if (returnSeconds is > 0)
        {
            return returnSeconds.Value;
        }

        return isReinforcing ? 30 * 60 : 15 * 60;
    }

    internal static bool ResolveIsInVillage(
        bool reinforcing,
        bool running,
        bool home,
        int? legacyStatus,
        bool officialAway,
        string? sidebarText)
    {
        if (reinforcing || running || officialAway)
        {
            return false;
        }

        if (home)
        {
            return true;
        }

        if (legacyStatus.HasValue)
        {
            return legacyStatus is 50 or 100;
        }

        var text = (sidebarText ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (text.Contains("home", StringComparison.Ordinal)
            || text.Contains("in this village", StringComparison.Ordinal)
            || text.Contains("in der heimat", StringComparison.Ordinal))
        {
            return true;
        }

        return !(text.Contains("på väg", StringComparison.Ordinal)
            || text.Contains("on the way", StringComparison.Ordinal)
            || text.Contains("adventure", StringComparison.Ordinal)
            || text.Contains("äventyr", StringComparison.Ordinal)
            || text.Contains("abenteuer", StringComparison.Ordinal)
            || text.Contains("dead", StringComparison.Ordinal)
            || text.Contains("tot", StringComparison.Ordinal)
            || text.Contains("död", StringComparison.Ordinal));
    }

    internal static bool IsAdventureDispatchConfirmed(
        bool activeAdventurePage,
        bool isInVillage,
        bool isDead,
        bool isReviving)
        => activeAdventurePage || (!isInVillage && !isDead && !isReviving);

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

        var seconds = (int)Math.Ceiling(deficit * 86400.0 / regenPerDayPercent);
        return Math.Clamp(seconds, 60, Math.Max(60, maxDeferSeconds));
    }

}
