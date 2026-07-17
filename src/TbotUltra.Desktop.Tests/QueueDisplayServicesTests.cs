using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class QueueDisplayServicesTests
{
    [Fact]
    public void Format_ResourceUpgrade_UsesPayloadNameAndSlot()
    {
        var item = Item(
            "upgrade_resource_to_level",
            new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.ResourceUpgradeSlotId] = "4",
                [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = "8",
                [BotOptionPayloadKeys.ResourceUpgradeName] = "Crop",
            });

        var displayName = QueueDisplayNameFormatter.Format(item, _ => "Wood", _ => null, resourceFieldMaxLevel: 18);

        Assert.Equal("Upgrade Crop slot 4 to level 8", displayName);
    }

    [Fact]
    public void Format_BuildingUpgradeToMax_UsesResolverFallback()
    {
        var item = Item(
            "upgrade_building_to_max",
            new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.BuildingUpgradeSlotId] = "19",
            });

        var displayName = QueueDisplayNameFormatter.Format(item, _ => null, slotId => slotId == 19 ? "Main Building" : null, 18);

        Assert.Equal("Upgrade Main Building to max level (slot 19)", displayName);
    }

    [Fact]
    public void Format_SendFarmlists_UsesSelectedListNames()
    {
        var item = Item(
            "send_farmlists",
            new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.ContinuousFarmListNames] = "Raiders, Oasis",
            },
            displayName: "Send farms");

        var displayName = QueueDisplayNameFormatter.Format(item, _ => null, _ => null, 18);

        Assert.Equal("Send farmlists: Raiders, Oasis", displayName);
    }

    [Fact]
    public void Create_Row_FormatsEstimateAndSyntheticRunningStatus()
    {
        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.Parse("2026-06-17T10:00:00Z");
        var nextAttemptAt = createdAt.AddMinutes(5);
        var item = Item("upgrade_building_to_level", new Dictionary<string, string>());
        item.Id = id;
        item.Status = QueueStatus.Pending;
        item.Retries = 1;
        item.MaxRetries = 3;
        item.IsRuntimeOnly = true;
        item.CreatedAt = createdAt;
        item.NextAttemptAt = nextAttemptAt;

        var row = QueueItemRowFactory.Create(
            item,
            new QueueItemEstimate(true, 3661, 1234, 5678, 90, 12),
            displayRunningId: id,
            resolveVillageName: _ => "Capital",
            resolveVillageKey: _ => "newdid:123",
            resolveDisplayName: _ => "Upgrade Main Building to level 10",
            formatServerTime: time => $"server:{time.ToUnixTimeSeconds()}");

        Assert.Equal(QueueStatus.Running, row.Status);
        Assert.Equal("Construction", row.GroupName);
        Assert.Equal("Capital", row.VillageName);
        Assert.Equal("newdid:123", row.VillageKey);
        Assert.Equal("Upgrade Main Building to level 10", row.DisplayName);
        Assert.Equal("1/3", row.RetriesText);
        Assert.True(row.IsRuntimeOnly);
        Assert.Equal($"server:{nextAttemptAt.ToUnixTimeSeconds()}", row.NextAttemptAtServer);
        Assert.True(row.HasEstimate);
        Assert.Equal("1h 1m", row.BuildTimeText);
        Assert.Equal("1,234", row.WoodText);
        Assert.Equal("5,678", row.ClayText);
        Assert.Equal("90", row.IronText);
        Assert.Equal("12", row.CropText);
        Assert.Equal("1,234 | 5,678 | 90 | 12", row.CostText);
    }

    [Fact]
    public void Create_Row_MarksAutomaticConstructionRepair()
    {
        var item = Item(
            "construct_building",
            new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.AutoAddedBy] = BotOptionPayloadKeys.AutoAddedByConstructionRequirementRepair,
                [BotOptionPayloadKeys.AutoAddedReason] = "construct missing prerequisite Academy",
            });

        var row = QueueItemRowFactory.Create(
            item,
            QueueItemEstimate.None,
            displayRunningId: null,
            resolveVillageName: _ => "1660",
            resolveVillageKey: _ => "xy:1|2",
            resolveDisplayName: _ => "Construct Academy to level 1 (slot 30)",
            formatServerTime: _ => "-");

        Assert.True(row.IsAutomaticRepair);
        Assert.Equal("construct missing prerequisite Academy", row.AutomaticRepairReason);
        Assert.Equal("[AUTO FIX] Construct Academy to level 1 (slot 30)", row.DisplayName);
    }

    [Fact]
    public void FormatQueueDurationTooltip_ShowsNormalAndConstructFasterTimes()
    {
        var tooltip = QueueItemRowFactory.FormatQueueDurationTooltip(180000);

        Assert.Equal("Time: 2d 2h\nTime (25%): 1d 13h", tooltip);
    }

    private static QueueItem Item(string taskName, Dictionary<string, string> payload, string? displayName = null)
        => new()
        {
            TaskName = taskName,
            DisplayName = displayName,
            Group = QueueGroup.Construction,
            Payload = payload,
        };
}
