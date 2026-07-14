namespace TbotUltra.Desktop.Services;

public static class AccountProxyPlanResolver
{
    public static ProxyPlanResolution Resolve(
        AccountProxyPlan plan,
        string accountName,
        DateTimeOffset at,
        AccountProxyRuntimeState runtime)
    {
        var scheduled = Resolve(plan, accountName, at, runtime.ActiveProxyId);
        if (string.IsNullOrWhiteSpace(runtime.RecoveryOverrideProxyId)
            || runtime.RecoveryOverrideUntilUtc is { } until && at >= until)
        {
            return scheduled;
        }

        return new ProxyPlanResolution(
            runtime.RecoveryOverrideProxyId,
            runtime.RecoveryOverrideUntilUtc ?? scheduled.NextTransitionAt,
            scheduled.ProxyId,
            "Using the recovery proxy until the next scheduled sleep switch.");
    }

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
        var proxyId = latest?.ProxyId ?? string.Empty;
        if (latest is null && string.IsNullOrWhiteSpace(proxyId))
        {
            proxyId = fallbackProxyId;
        }

        if (latest is null && string.IsNullOrWhiteSpace(proxyId))
        {
            proxyId = plan.Assignments[0].ProxyId;
        }

        var next = transitions.FirstOrDefault(item => item.At > at && !string.Equals(item.ProxyId, proxyId, StringComparison.OrdinalIgnoreCase));
        return new ProxyPlanResolution(
            proxyId ?? string.Empty,
            next?.At,
            next?.ProxyId ?? string.Empty,
            latest is null
                ? "Using the configured fallback proxy."
                : string.IsNullOrWhiteSpace(proxyId)
                    ? "No proxy is scheduled; direct connection is selected."
                    : "Using the latest varied schedule boundary.");
    }

    private static List<ProxyTransition> BuildTransitions(AccountProxyPlan plan, string accountName, DateTimeOffset from, DateTimeOffset to)
    {
        var events = new List<ProxyBoundaryEvent>();
        var firstDay = DateOnly.FromDateTime(from.LocalDateTime.Date).AddDays(-2);
        var lastDay = DateOnly.FromDateTime(to.LocalDateTime.Date).AddDays(2);
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            for (var assignmentIndex = 0; assignmentIndex < plan.Assignments.Count; assignmentIndex++)
            {
                var assignment = plan.Assignments[assignmentIndex];
                foreach (var block in assignment.TimeBlocks.Where(block => block.Days.Contains(day.DayOfWeek)))
                {
                    var startHour = block.FullDay ? 0 : block.StartHour;
                    var start = day.ToDateTime(new TimeOnly(startHour, 0));
                    var end = block.FullDay
                        ? start.AddDays(1)
                        : day.ToDateTime(new TimeOnly(block.EndHour, 0));
                    if (!block.FullDay && block.EndHour <= block.StartHour)
                    {
                        end = end.AddDays(1);
                    }

                    events.Add(new ProxyBoundaryEvent(Vary(start), assignmentIndex, 1));
                    events.Add(new ProxyBoundaryEvent(Vary(end), assignmentIndex, -1));
                }
            }
        }

        var result = new List<ProxyTransition>();
        var activeCounts = new Dictionary<int, int>();
        string? previousProxyId = null;
        var hasPrevious = false;
        foreach (var group in events.OrderBy(item => item.At).GroupBy(item => item.At))
        {
            foreach (var boundaryEvent in group)
            {
                var count = activeCounts.GetValueOrDefault(boundaryEvent.AssignmentIndex) + boundaryEvent.Delta;
                if (count <= 0)
                {
                    activeCounts.Remove(boundaryEvent.AssignmentIndex);
                }
                else
                {
                    activeCounts[boundaryEvent.AssignmentIndex] = count;
                }
            }

            var selectedIndex = activeCounts.Keys.Order().Cast<int?>().FirstOrDefault();
            var selectedProxyId = selectedIndex is null ? string.Empty : plan.Assignments[selectedIndex.Value].ProxyId;
            if (!hasPrevious || !string.Equals(previousProxyId, selectedProxyId, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new ProxyTransition(group.Key, selectedProxyId));
                previousProxyId = selectedProxyId;
                hasPrevious = true;
            }
        }

        return result;

        DateTimeOffset Vary(DateTime nominal)
        {
            var boundaryDay = DateOnly.FromDateTime(nominal.Date);
            var boundary = new DateTimeOffset(nominal, from.Offset);
            var minutes = StableFraction(accountName, boundaryDay, nominal.Hour)
                * Math.Clamp(plan.VariationPercent, 0, 49) / 100.0 * 60.0;
            return boundary.AddMinutes(minutes);
        }
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
    private sealed record ProxyBoundaryEvent(DateTimeOffset At, int AssignmentIndex, int Delta);
}
