using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop.Services;

public static class AccountProxyPlanValidator
{
    public static ProxyPlanValidationResult Validate(
        AccountProxyPlan plan,
        IReadOnlyCollection<ProxyLibraryEntry> library,
        string accountName,
        bool neverUseOwnIp,
        bool sessionPacingEnabled,
        IReadOnlyCollection<int> allowedHours,
        int sleepMinMinutes,
        bool requireHealth)
    {
        var issues = new List<ProxyPlanIssue>();
        if (!plan.Enabled)
        {
            if (neverUseOwnIp)
            {
                Error(issues, "proxy_required", "Never use own IP requires an active proxy setup.");
            }

            return new ProxyPlanValidationResult(issues);
        }

        if (plan.VariationPercent is < 0 or > 49)
        {
            Error(issues, "variation", "Schedule variation must be between 0% and 49%.");
        }

        if (plan.Assignments.Count == 0)
        {
            Error(issues, "no_proxy", "Select at least one proxy.");
            return new ProxyPlanValidationResult(issues);
        }

        var duplicateIds = plan.Assignments
            .GroupBy(item => item.ProxyId?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length == 0 || group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateIds.Count > 0)
        {
            Error(issues, "duplicate_proxy", "Each proxy can only be added once.");
        }

        if (plan.IsRotation && !sessionPacingEnabled)
        {
            Error(issues, "pacing_disabled", "Multiple scheduled proxies require Session pacing to be enabled in Settings.");
        }

        var proxies = library.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in plan.Assignments)
        {
            if (!proxies.TryGetValue(assignment.ProxyId, out var proxy))
            {
                Error(issues, "missing_proxy", $"Proxy '{assignment.ProxyId}' no longer exists in the proxy list.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(proxy.AssignedAccount)
                && !string.Equals(proxy.AssignedAccount, accountName, StringComparison.OrdinalIgnoreCase))
            {
                Error(issues, "locked_proxy", $"Proxy '{proxy.DisplayName}' is locked to account '{proxy.AssignedAccount}'.");
            }

            if (!ProxyParser.TryBuild(proxy.Server, out _, out _))
            {
                Error(issues, "invalid_proxy", $"Proxy '{proxy.DisplayName}' has an invalid endpoint.");
            }

            if (assignment.TimeBlocks.Count == 0)
            {
                Error(issues, "missing_schedule", $"Proxy '{proxy.DisplayName}' has no scheduled time.");
            }

            foreach (var block in assignment.TimeBlocks)
            {
                if (block.Days.Count == 0)
                {
                    Error(issues, "missing_days", $"Proxy '{proxy.DisplayName}' has a block without weekdays.");
                }

                if (!block.FullDay && (block.StartHour is < 0 or > 23 || block.EndHour is < 0 or > 23 || block.StartHour == block.EndHour))
                {
                    Error(issues, "invalid_block", $"Proxy '{proxy.DisplayName}' has an invalid time block. Use Full day for 24-hour coverage.");
                }

                var duration = DurationHours(block);
                if (duration * 60 < Math.Max(5, sleepMinMinutes))
                {
                    Warning(issues, "short_block", $"Proxy '{proxy.DisplayName}' has a block shorter than the minimum sleep duration and may be skipped.");
                }

                if (plan.VariationPercent >= 40 && duration <= 2)
                {
                    Warning(issues, "high_variation", $"Proxy '{proxy.DisplayName}' combines high variation with a short block.");
                }
            }

            if (proxy.LatencyMs is > 2000)
            {
                Warning(issues, "slow_proxy", $"Proxy '{proxy.DisplayName}' is slow ({proxy.LatencyMs} ms).");
            }
            else if (proxy.LastFailureUtc is { } failure && DateTime.UtcNow - failure.ToUniversalTime() < ProxyFailoverService.FailedProxyCooldown)
            {
                Warning(issues, "cooldown", $"Proxy '{proxy.DisplayName}' is currently in recovery cooldown.");
            }
        }

        ValidateCoverageAndOverlap(plan, proxies, allowedHours, neverUseOwnIp, issues);

        if (requireHealth)
        {
            var existing = plan.Assignments
                .Select(item => proxies.GetValueOrDefault(item.ProxyId))
                .Where(item => item is not null)
                .Cast<ProxyLibraryEntry>()
                .ToList();
            if (existing.Count > 0 && existing.All(proxy => proxy.IsWorking != true))
            {
                Error(issues, "no_working_proxy", "No selected proxy passed both the stability and Travian reachability tests.");
            }
            else
            {
                foreach (var failed in existing.Where(proxy => proxy.IsWorking == false))
                {
                    Warning(issues, "failed_fallback", $"Proxy '{failed.DisplayName}' failed validation and will not be used until it recovers.");
                }
            }


            if (neverUseOwnIp)
            {
                var unhealthySlots = new List<string>();
                foreach (var day in Enum.GetValues<DayOfWeek>())
                {
                    foreach (var hour in allowedHours.Where(hour => hour is >= 0 and <= 23).Distinct())
                    {
                        var hasWorkingCoverage = plan.Assignments.Any(assignment =>
                            proxies.GetValueOrDefault(assignment.ProxyId)?.IsWorking == true
                            && assignment.TimeBlocks.Any(block => Covers(block, day, hour)));
                        if (!hasWorkingCoverage)
                        {
                            unhealthySlots.Add($"{day} {hour:00}:00");
                        }
                    }
                }

                if (unhealthySlots.Count > 0)
                {
                    Error(issues, "unhealthy_coverage", $"Working proxies do not cover: {FormatSlots(unhealthySlots)}.");
                }
            }
        }

        if (!neverUseOwnIp)
        {
            Warning(issues, "direct_allowed", "Never use own IP is off; recovery may use the direct connection.");
        }

        return new ProxyPlanValidationResult(issues);
    }

    public static bool Covers(ProxyTimeBlock block, DayOfWeek day, int hour)
    {
        if (block.FullDay)
        {
            return block.Days.Contains(day);
        }

        if (block.StartHour < block.EndHour)
        {
            return block.Days.Contains(day) && hour >= block.StartHour && hour < block.EndHour;
        }

        if (block.Days.Contains(day) && hour >= block.StartHour)
        {
            return true;
        }

        var previousDay = day == DayOfWeek.Sunday ? DayOfWeek.Saturday : (DayOfWeek)((int)day - 1);
        return block.Days.Contains(previousDay) && hour < block.EndHour;
    }

    private static void ValidateCoverageAndOverlap(
        AccountProxyPlan plan,
        IReadOnlyDictionary<string, ProxyLibraryEntry> proxies,
        IReadOnlyCollection<int> allowedHours,
        bool neverUseOwnIp,
        List<ProxyPlanIssue> issues)
    {
        var allowed = allowedHours.Where(hour => hour is >= 0 and <= 23).Distinct().Order().ToList();
        var overlaps = new List<string>();
        var gaps = new List<string>();
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            foreach (var hour in Enumerable.Range(0, 24))
            {
                var covering = plan.Assignments
                    .Where(assignment => proxies.ContainsKey(assignment.ProxyId))
                    .Where(assignment => assignment.TimeBlocks.Any(block => Covers(block, day, hour)))
                    .ToList();
                if (covering.Count > 1)
                {
                    overlaps.Add($"{day} {hour:00}:00");
                }

                if (allowed.Contains(hour) && covering.Count == 0)
                {
                    gaps.Add($"{day} {hour:00}:00");
                }
            }
        }

        if (overlaps.Count > 0)
        {
            Error(issues, "overlap", $"Different proxies overlap at: {FormatSlots(overlaps)}.");
        }

        if (gaps.Count > 0)
        {
            var message = $"No proxy covers allowed runtime at: {FormatSlots(gaps)}.";
            if (neverUseOwnIp)
            {
                Error(issues, "coverage_gap", message + " Never use own IP requires complete coverage.");
            }
            else
            {
                Warning(issues, "coverage_gap", message + " The latest proxy will continue through these gaps.");
            }
        }

        if (allowed.Count == 0)
        {
            Warning(issues, "no_allowed_hours", "No Allowed hours are selected, so the account will never run automatically.");
        }

        foreach (var assignment in plan.Assignments.Where(item => proxies.ContainsKey(item.ProxyId)))
        {
            var used = Enum.GetValues<DayOfWeek>().Any(day => allowed.Any(hour => assignment.TimeBlocks.Any(block => Covers(block, day, hour))));
            if (!used)
            {
                Warning(issues, "outside_allowed_hours", $"Proxy '{proxies[assignment.ProxyId].DisplayName}' has no schedule overlapping Allowed hours.");
            }
        }

        if (plan.Assignments.Count > 1)
        {
            var coveredSlots = 7 * Math.Max(1, allowed.Count);
            var dominant = plan.Assignments.Max(assignment => Enum.GetValues<DayOfWeek>()
                .Sum(day => allowed.Count(hour => assignment.TimeBlocks.Any(block => Covers(block, day, hour)))));
            if (coveredSlots > 0 && dominant >= coveredSlots * 0.9)
            {
                Warning(issues, "limited_rotation", "One proxy covers almost all allowed runtime, so actual rotation will be limited.");
            }
        }
    }

    private static int DurationHours(ProxyTimeBlock block)
        => block.FullDay ? 24 : block.EndHour > block.StartHour ? block.EndHour - block.StartHour : 24 - block.StartHour + block.EndHour;

    private static void Error(List<ProxyPlanIssue> issues, string code, string message)
    {
        if (!issues.Any(issue => issue.Severity == ProxyPlanIssueSeverity.Error && issue.Code == code && issue.Message == message))
        {
            issues.Add(new ProxyPlanIssue(ProxyPlanIssueSeverity.Error, code, message));
        }
    }

    private static string FormatSlots(IReadOnlyCollection<string> slots)
    {
        var shown = slots.Take(8).ToList();
        return string.Join(", ", shown) + (slots.Count > shown.Count ? $" and {slots.Count - shown.Count} more" : string.Empty);
    }

    private static void Warning(List<ProxyPlanIssue> issues, string code, string message)
    {
        if (!issues.Any(issue => issue.Severity == ProxyPlanIssueSeverity.Warning && issue.Code == code && issue.Message == message))
        {
            issues.Add(new ProxyPlanIssue(ProxyPlanIssueSeverity.Warning, code, message));
        }
    }
}
