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
}
