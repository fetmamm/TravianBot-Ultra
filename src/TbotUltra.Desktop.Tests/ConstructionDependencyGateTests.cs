using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionDependencyGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ResolveConstructDelay_WaitsForActivePrerequisite()
    {
        var item = CreateStableConstructItem();
        var finish = TimerSnapshot.FromRemaining(180, Now);
        var status = CreateStatus(
            buildings: [new Building(37, "Smithy", 2, "build.php?id=37", 13)],
            activeConstructions:
            [
                new ActiveConstruction(
                    ConstructionKind.Building,
                    "Smithy",
                    3,
                    180,
                    "00:03:00",
                    finish),
            ]);

        var result = ConstructionDependencyGate.ResolveConstructDelay(item, status, Now);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromSeconds(190), result!.Delay);
        Assert.Contains("Smithy 3+", result.Detail);
    }

    [Fact]
    public void ResolveConstructDelay_AllowsConstructWhenPrerequisiteFinished()
    {
        var item = CreateStableConstructItem();
        var status = CreateStatus(
            buildings: [new Building(37, "Smithy", 3, "build.php?id=37", 13)],
            activeConstructions: []);

        var result = ConstructionDependencyGate.ResolveConstructDelay(item, status, Now);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveConstructDelay_DoesNotWaitWhenMissingPrerequisiteIsNotActive()
    {
        var item = CreateStableConstructItem();
        var status = CreateStatus(
            buildings: [new Building(37, "Smithy", 2, "build.php?id=37", 13)],
            activeConstructions: []);

        var result = ConstructionDependencyGate.ResolveConstructDelay(item, status, Now);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveConstructRequirementGuard_FailsWhenPrerequisiteIsMissingAndNotQueued()
    {
        var item = CreateStableConstructItem();
        var status = CreateStatus(
            buildings: [new Building(37, "Smithy", 3, "build.php?id=37", 13)],
            activeConstructions: []);

        var result = ConstructionDependencyGate.ResolveConstructRequirementGuard(item, status, [], Now);

        Assert.Equal(ConstructionRequirementGuardAction.FailMissingPrerequisite, result.Action);
        Assert.Contains("Academy 5+", result.Detail);
    }

    [Fact]
    public void ResolveConstructRequirementGuard_DefersWhenPrerequisiteIsActive()
    {
        var item = CreateStableConstructItem();
        var finish = TimerSnapshot.FromRemaining(180, Now);
        var status = CreateStatus(
            buildings: [new Building(37, "Smithy", 3, "build.php?id=37", 13)],
            activeConstructions:
            [
                new ActiveConstruction(
                    ConstructionKind.Building,
                    "Academy",
                    5,
                    180,
                    "00:03:00",
                    finish),
            ]);

        var result = ConstructionDependencyGate.ResolveConstructRequirementGuard(item, status, [], Now);

        Assert.Equal(ConstructionRequirementGuardAction.DeferForActivePrerequisite, result.Action);
        Assert.Equal(TimeSpan.FromSeconds(190), result.Delay);
        Assert.Contains("Academy 5+", result.Detail);
    }

    [Fact]
    public void ResolveConstructRequirementGuard_DefersWhenPrerequisiteIsQueued()
    {
        var item = CreateStableConstructItem();
        var status = CreateStatus(
            buildings: [new Building(37, "Smithy", 3, "build.php?id=37", 13)],
            activeConstructions: []);
        var academyConstruct = new QueueItem
        {
            TaskName = "construct_building",
            Status = QueueStatus.Pending,
            Payload = new BuildingConstructPayload(30, 22, "Academy").ToDictionary(),
        };
        var academyUpgrade = new QueueItem
        {
            TaskName = "upgrade_building_to_level",
            Status = QueueStatus.Pending,
            Payload = new BuildingUpgradePayload(30, 5, "Academy").ToDictionary(),
        };

        var result = ConstructionDependencyGate.ResolveConstructRequirementGuard(
            item,
            status,
            [academyConstruct, academyUpgrade],
            Now);

        Assert.Equal(ConstructionRequirementGuardAction.DeferForQueuedPrerequisite, result.Action);
        Assert.Equal(TimeSpan.FromSeconds(60), result.Delay);
        Assert.Contains("Academy 5+", result.Detail);
    }

    private static QueueItem CreateStableConstructItem()
    {
        return new QueueItem
        {
            TaskName = "construct_building",
            Status = QueueStatus.Pending,
            NextAttemptAt = Now,
            Payload = new BuildingConstructPayload(29, 20, "Stable").ToDictionary(),
        };
    }

    private static VillageStatus CreateStatus(
        IReadOnlyList<Building> buildings,
        IReadOnlyList<ActiveConstruction> activeConstructions)
    {
        return new VillageStatus(
            ActiveVillage: "1440",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: buildings,
            BuildQueue: [],
            Tribe: "Teutons",
            VillageCount: 1,
            ActiveConstructions: activeConstructions,
            ActiveConstructionsFromOverview: true);
    }
}
