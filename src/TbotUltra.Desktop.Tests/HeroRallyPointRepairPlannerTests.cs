using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class HeroRallyPointRepairPlannerTests
{
    private static readonly HeroRallyPointRepairRequest Request = new(24443, "WHY", 164, 110);

    [Fact]
    public void MissingRepair_CreatesFixedRallyPointConstructionForHeroHomeVillage()
    {
        var plan = HeroRallyPointRepairPlanner.Plan(Request, Guid.NewGuid(), []);

        Assert.Null(plan.ExistingQueueItemId);
        Assert.True(BuildingConstructPayload.TryFromDictionary(plan.Payload, out var construct));
        Assert.NotNull(construct);
        Assert.Equal(39, construct.SlotId);
        Assert.Equal(16, construct.Gid);
        Assert.Equal(1, construct.TargetLevel);
        Assert.Equal("xy:164|110", plan.Payload[BotOptionPayloadKeys.TargetVillageKey]);
        Assert.Equal("/dorf2.php?newdid=24443", plan.Payload[BotOptionPayloadKeys.TargetVillageUrl]);
    }

    [Fact]
    public void ExistingRepairForSameVillage_IsReused()
    {
        var existingId = Guid.NewGuid();
        var existingPayload = new BuildingConstructPayload(39, 16, "Rally Point", 1).ToDictionary();
        existingPayload[BotOptionPayloadKeys.TargetVillageKey] = "xy:164|110";
        var existing = new QueueItem
        {
            Id = existingId,
            TaskName = "construct_building",
            Status = QueueStatus.Pending,
            Payload = existingPayload,
        };

        var plan = HeroRallyPointRepairPlanner.Plan(Request, Guid.NewGuid(), [existing]);

        Assert.Equal(existingId, plan.ExistingQueueItemId);
    }
}
