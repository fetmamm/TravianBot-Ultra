using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AccountProxyPlanTests
{
    [Fact]
    public void Validate_BlocksAllowedHourGapWhenOwnIpIsForbidden()
    {
        var first = Proxy("first", "1.1.1.1");
        var second = Proxy("second", "2.2.2.2");
        var plan = Plan(
            Assignment(first.Id, Block(2, 4)),
            Assignment(second.Id, Block(5, 6)));

        var result = AccountProxyPlanValidator.Validate(
            plan,
            [first, second],
            "alice",
            neverUseOwnIp: true,
            sessionPacingEnabled: true,
            allowedHours: [2, 3, 4, 5],
            sleepMinMinutes: 20,
            requireHealth: false);

        Assert.Contains(result.Errors, issue => issue.Code == "coverage_gap" && issue.Message.Contains("04:00"));
    }

    [Fact]
    public void Validate_AcceptsAdjacentBlocksWithFullAllowedCoverage()
    {
        var first = Proxy("first", "1.1.1.1");
        var second = Proxy("second", "2.2.2.2");
        var plan = Plan(
            Assignment(first.Id, Block(2, 4)),
            Assignment(second.Id, Block(4, 6)));

        var result = AccountProxyPlanValidator.Validate(
            plan,
            [first, second],
            "alice",
            neverUseOwnIp: true,
            sessionPacingEnabled: true,
            allowedHours: [2, 3, 4, 5],
            sleepMinMinutes: 20,
            requireHealth: false);

        Assert.DoesNotContain(result.Errors, issue => issue.Code is "coverage_gap" or "overlap");
    }

    [Fact]
    public void Validate_DetectsOvernightOverlap()
    {
        var first = Proxy("first", "1.1.1.1");
        var second = Proxy("second", "2.2.2.2");
        var overnight = Block(22, 3);
        var overlap = Block(2, 4);
        var plan = Plan(Assignment(first.Id, overnight), Assignment(second.Id, overlap));

        var result = AccountProxyPlanValidator.Validate(
            plan,
            [first, second],
            "alice",
            neverUseOwnIp: false,
            sessionPacingEnabled: true,
            allowedHours: Enumerable.Range(0, 24).ToArray(),
            sleepMinMinutes: 20,
            requireHealth: false);

        Assert.Contains(result.Errors, issue => issue.Code == "overlap");
    }

    [Fact]
    public void Resolve_VariationIsStableAndNextProxyChangesAcrossBoundary()
    {
        var first = Proxy("first", "1.1.1.1");
        var second = Proxy("second", "2.2.2.2");
        var plan = Plan(
            Assignment(first.Id, Block(0, 12)),
            Assignment(second.Id, Block(12, 0)));
        plan.VariationPercent = 30;
        var morning = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.FromHours(2));

        var firstRead = AccountProxyPlanResolver.Resolve(plan, "alice", morning);
        var secondRead = AccountProxyPlanResolver.Resolve(plan, "alice", morning);

        Assert.Equal(first.Id, firstRead.ProxyId);
        Assert.Equal(second.Id, firstRead.NextProxyId);
        Assert.Equal(firstRead.NextTransitionAt, secondRead.NextTransitionAt);
        Assert.InRange(firstRead.NextTransitionAt!.Value.Hour, 11, 12);
    }

    [Fact]
    public void Validate_RequiresPacingForMultipleProxies()
    {
        var first = Proxy("first", "1.1.1.1");
        var second = Proxy("second", "2.2.2.2");
        var plan = Plan(Assignment(first.Id, Block(0, 12)), Assignment(second.Id, Block(12, 0)));

        var result = AccountProxyPlanValidator.Validate(
            plan,
            [first, second],
            "alice",
            neverUseOwnIp: false,
            sessionPacingEnabled: false,
            allowedHours: Enumerable.Range(0, 24).ToArray(),
            sleepMinMinutes: 20,
            requireHealth: false);

        Assert.Contains(result.Errors, issue => issue.Code == "pacing_disabled");
    }

    [Fact]
    public void Normalize_MergesAdjacentBlocksForSameProxyAndDays()
    {
        var plan = Plan(new AccountProxyAssignment
        {
            ProxyId = "first",
            TimeBlocks = [Block(2, 4), Block(4, 6)],
        });

        var normalized = AccountProxyPlanNormalizer.Normalize(plan);

        var block = Assert.Single(Assert.Single(normalized.Assignments).TimeBlocks);
        Assert.Equal(2, block.StartHour);
        Assert.Equal(6, block.EndHour);
    }

    [Fact]
    public void TimelineRow_ConvertsCheckedHoursIntoSeparateBlocks()
    {
        var row = new ProxyTimelineRow("first", "Weekdays");
        row.Hours[2] = true;
        row.Hours[3] = true;
        row.Hours[6] = true;

        var blocks = row.BuildTimeBlocks();

        Assert.Collection(
            blocks.OrderBy(block => block.StartHour),
            block => { Assert.Equal(2, block.StartHour); Assert.Equal(4, block.EndHour); Assert.Equal(5, block.Days.Count); },
            block => { Assert.Equal(6, block.StartHour); Assert.Equal(7, block.EndHour); Assert.Equal(5, block.Days.Count); });
    }

    [Fact]
    public void TimelineRow_MergesMidnightHoursIntoOvernightBlock()
    {
        var row = new ProxyTimelineRow("first", "All days");
        row.Hours[22] = true;
        row.Hours[23] = true;
        row.Hours[0] = true;
        row.Hours[1] = true;

        var block = Assert.Single(row.BuildTimeBlocks());

        Assert.Equal(22, block.StartHour);
        Assert.Equal(2, block.EndHour);
        Assert.Equal(7, block.Days.Count);
    }

    private static AccountProxyPlan Plan(params AccountProxyAssignment[] assignments) => new()
    {
        Enabled = true,
        Assignments = assignments.ToList(),
    };

    private static AccountProxyAssignment Assignment(string proxyId, ProxyTimeBlock block) => new()
    {
        ProxyId = proxyId,
        TimeBlocks = [block],
    };

    private static ProxyTimeBlock Block(int startHour, int endHour) => new()
    {
        Days = Enum.GetValues<DayOfWeek>().ToList(),
        StartHour = startHour,
        EndHour = endHour,
    };

    private static ProxyLibraryEntry Proxy(string id, string host) => new()
    {
        Id = id,
        Name = id,
        Scheme = "socks5",
        Host = host,
        Port = 1080,
        AssignedAccount = "alice",
        IsWorking = true,
        CreatedAtUtc = DateTime.UtcNow,
    };
}
