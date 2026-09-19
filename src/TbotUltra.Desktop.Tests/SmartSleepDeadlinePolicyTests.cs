using System.Text.Json.Nodes;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SmartSleepDeadlinePolicyTests
{
    [Fact]
    public void MissingConfiguration_DefaultsToConstructionAndHero()
    {
        var groups = SmartSleepDeadlinePolicy.ReadGroups(null);

        Assert.Equal(2, groups.Count);
        Assert.Contains(QueueGroup.Construction, groups);
        Assert.Contains(QueueGroup.Hero, groups);
    }

    [Fact]
    public void ExplicitConfiguration_OnlyUsesSelectedGroups()
    {
        var groups = SmartSleepDeadlinePolicy.ReadGroups(new JsonArray("hero", "construction"));

        Assert.Equal(2, groups.Count);
        Assert.Contains(QueueGroup.Hero, groups);
        Assert.Contains(QueueGroup.Construction, groups);
        Assert.DoesNotContain(QueueGroup.Farming, groups);
    }

    [Fact]
    public void ResolveNextDelay_IgnoresUnselectedQueueGroupsAndConstructionForecast()
    {
        var now = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
        var farming = new QueueItem
        {
            TaskName = "send_farmlists",
            Group = QueueGroup.Farming,
            Status = QueueStatus.Pending,
            NextAttemptAt = now.AddMinutes(30),
        };
        var hero = new QueueItem
        {
            TaskName = "hero_manage",
            Group = QueueGroup.Hero,
            Status = QueueStatus.Pending,
            NextAttemptAt = now.AddHours(2),
        };

        var delay = SmartSleepDeadlinePolicy.ResolveNextDelay(
            now,
            [farming, hero],
            new HashSet<QueueGroup> { QueueGroup.Hero },
            nextConstructionAvailabilityUtc: now.AddMinutes(10));

        Assert.Equal(TimeSpan.FromHours(2), delay);
    }

    [Fact]
    public void ResolveNextDelay_NoSelectedTaskDeadline_UsesFallbackPath()
    {
        var now = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
        var farming = new QueueItem
        {
            TaskName = "send_farmlists",
            Group = QueueGroup.Farming,
            Status = QueueStatus.Pending,
            NextAttemptAt = now.AddMinutes(30),
        };

        var delay = SmartSleepDeadlinePolicy.ResolveNextDelay(
            now,
            [farming],
            new HashSet<QueueGroup> { QueueGroup.Hero },
            nextConstructionAvailabilityUtc: null);

        Assert.Null(delay);
    }
}
