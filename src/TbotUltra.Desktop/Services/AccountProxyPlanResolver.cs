namespace TbotUltra.Desktop.Services;

public static class AccountProxyPlanResolver
{
    public static ProxyPlanResolution Resolve(
        AccountProxyPlan plan,
        string accountName,
        DateTimeOffset at,
        string? fallbackProxyId = null)
    {
        if (!plan.Enabled || plan.Assignments.Count == 0)
        {
            return new ProxyPlanResolution(string.Empty, null, string.Empty, "Proxy rotation is disabled.");
        }

        var transitions = BuildTransitions(plan, accountName, at.AddDays(-8), at.AddDays(8));
        var latest = transitions.LastOrDefault(item => item.At <= at);
        var proxyId = latest?.ProxyId;
        if (string.IsNullOrWhiteSpace(proxyId))
        {
            proxyId = fallbackProxyId;
        }

        if (string.IsNullOrWhiteSpace(proxyId))
        {
            proxyId = plan.Assignments[0].ProxyId;
        }

        var next = transitions.FirstOrDefault(item => item.At > at && !string.Equals(item.ProxyId, proxyId, StringComparison.OrdinalIgnoreCase));
        return new ProxyPlanResolution(
            proxyId ?? string.Empty,
            next?.At,
            next?.ProxyId ?? string.Empty,
            latest is null ? "Using the configured fallback proxy." : "Using the latest varied schedule boundary.");
    }

    private static List<ProxyTransition> BuildTransitions(AccountProxyPlan plan, string accountName, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<ProxyTransition>();
        var firstDay = DateOnly.FromDateTime(from.LocalDateTime.Date).AddDays(-1);
        var lastDay = DateOnly.FromDateTime(to.LocalDateTime.Date).AddDays(1);
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            foreach (var assignment in plan.Assignments)
            {
                foreach (var block in assignment.TimeBlocks.Where(block => block.Days.Contains(day.DayOfWeek)))
                {
                    var hour = block.FullDay ? 0 : block.StartHour;
                    var boundary = new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, 0)), atOffset(from));
                    var minutes = StableFraction(accountName, day, hour) * Math.Clamp(plan.VariationPercent, 0, 49) / 100.0 * 60.0;
                    result.Add(new ProxyTransition(boundary.AddMinutes(minutes), assignment.ProxyId));
                }
            }
        }

        return result
            .OrderBy(item => item.At)
            .GroupBy(item => item.At)
            .Select(group => group.First())
            .ToList();

        static TimeSpan atOffset(DateTimeOffset value) => value.Offset;
    }

    private static double StableFraction(string accountName, DateOnly day, int hour)
    {
        var accountSeed = 1469598103934665603UL;
        foreach (var ch in accountName ?? string.Empty)
        {
            accountSeed ^= char.ToLowerInvariant(ch);
            accountSeed *= 1099511628211UL;
        }

        var z = unchecked(accountSeed ^ ((ulong)day.DayNumber * 24UL + (ulong)hour + 0x9E3779B97F4A7C15UL));
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z / (double)ulong.MaxValue * 2) - 1;
    }

    private sealed record ProxyTransition(DateTimeOffset At, string ProxyId);
}
