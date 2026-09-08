using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class IncomingAttackRallyPointPolicyTests
{
    [Fact]
    public void GetConstructionState_CompleteOverviewWithEmptyFixedSlot_IsMissing()
    {
        var buildings = Enumerable.Range(19, 22).ToDictionary(
            slotId => slotId,
            slotId => new BuildingInfo
            {
                SlotId = slotId,
                BuildingCode = slotId == 26 ? "g15" : string.Empty,
                BuildingName = slotId == 26 ? "Main Building" : "Empty",
                Level = slotId == 26 ? 10 : 0,
                LevelKnown = slotId == 26,
                HasOccupancyEvidence = slotId == 26,
            });
        var scan = new BuildingOverviewScanResult
        {
            Buildings = buildings,
            Metrics = BuildingOverviewScanPolicy.Evaluate(
                buildings.Count,
                missingBuildingCodeCount: 0,
                unknownLevelCount: 0,
                hasMainBuilding: true,
                hasRallyPoint: false),
        };

        var state = IncomingAttackRallyPointPolicy.GetConstructionState(scan);

        Assert.Equal(RallyPointConstructionState.Missing, state);
    }

    [Fact]
    public void GetConstructionState_RallyPointBuilding_IsConstructed()
    {
        var scan = CreateScan(slotCount: 22, new BuildingInfo
        {
            SlotId = 39,
            BuildingCode = "g16",
            BuildingName = "Rally Point",
            Level = 3,
            LevelKnown = true,
            HasOccupancyEvidence = true,
        });

        Assert.Equal(
            RallyPointConstructionState.Constructed,
            IncomingAttackRallyPointPolicy.GetConstructionState(scan));
    }

    [Fact]
    public void GetConstructionState_EmptySlotLinkWithoutGid_IsMissing()
    {
        var scan = CreateScan(slotCount: 22, new BuildingInfo
        {
            SlotId = 39,
            BuildingName = "Unknown building",
            HasOccupancyEvidence = true,
        });

        Assert.Equal(
            RallyPointConstructionState.Missing,
            IncomingAttackRallyPointPolicy.GetConstructionState(scan));
    }

    [Fact]
    public void GetConstructionState_CompleteOverviewThatOmitsEmptyFixedSlot_IsMissing()
    {
        var scan = CreateScan(slotCount: 22, new BuildingInfo
        {
            SlotId = 39,
            BuildingName = "Empty",
        });
        scan.Buildings.Remove(39);

        Assert.Equal(
            RallyPointConstructionState.Missing,
            IncomingAttackRallyPointPolicy.GetConstructionState(scan));
    }

    [Fact]
    public void GetConstructionState_IncompleteOverview_IsUnknown()
    {
        var scan = CreateScan(slotCount: 12, new BuildingInfo
        {
            SlotId = 39,
            BuildingName = "Empty",
        });

        Assert.Equal(
            RallyPointConstructionState.Unknown,
            IncomingAttackRallyPointPolicy.GetConstructionState(scan));
    }

    private static BuildingOverviewScanResult CreateScan(int slotCount, BuildingInfo rallyPointSlot)
    {
        var buildings = Enumerable.Range(19, slotCount).ToDictionary(
            slotId => slotId,
            slotId => slotId == 39
                ? rallyPointSlot
                : new BuildingInfo
                {
                    SlotId = slotId,
                    BuildingCode = slotId == 26 ? "g15" : string.Empty,
                    BuildingName = slotId == 26 ? "Main Building" : "Empty",
                    Level = slotId == 26 ? 10 : 0,
                    LevelKnown = slotId == 26,
                    HasOccupancyEvidence = slotId == 26,
                });
        return new BuildingOverviewScanResult
        {
            Buildings = buildings,
            Metrics = BuildingOverviewScanPolicy.Evaluate(
                buildings.Count,
                missingBuildingCodeCount: 0,
                unknownLevelCount: 0,
                hasMainBuilding: true,
                hasRallyPoint: rallyPointSlot.BuildingCode == "g16"),
        };
    }
}
