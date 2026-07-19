using System;
using System.Linq;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingsViewModelTests
{
    [Fact]
    public void DescribeLoadedSlots_CountsOccupiedAndFree()
    {
        var vm = new BuildingsViewModel();
        vm.BuildingSlots.Add(new BuildingSlotRow { SlotId = 1, Name = "Main Building", Level = 5 });
        vm.BuildingSlots.Add(new BuildingSlotRow { SlotId = 2, Name = "Empty" });
        vm.BuildingSlots.Add(new BuildingSlotRow { SlotId = 3, Name = "Warehouse", Level = 3 });

        var text = vm.DescribeLoadedSlots("active village 'Capital'");

        Assert.Equal(
            "Buildings loaded for active village 'Capital'. Occupied slots: 2, free slots: 1.",
            text);
    }

    [Fact]
    public void DescribeLoadedSlots_NoSlots_ReportsAllFree()
    {
        var vm = new BuildingsViewModel();

        var text = vm.DescribeLoadedSlots("selected village 'New'");

        Assert.Equal(
            "Buildings loaded for selected village 'New'. Occupied slots: 0, free slots: 0.",
            text);
    }

    [Fact]
    public void CreateBuildingSlotLayout_CoversAllSlots_WithRoundedCoordinates()
    {
        var layout = BuildingsViewModel.CreateBuildingSlotLayout();

        Assert.Equal(22, layout.Count);
        Assert.Equal(Enumerable.Range(19, 22), layout.Keys.OrderBy(id => id));
        foreach (var (left, top) in layout.Values)
        {
            Assert.Equal(left, Math.Round(left, 1));
            Assert.Equal(top, Math.Round(top, 1));
        }
    }

    [Theory]
    [InlineData(26, true)]
    [InlineData(39, true)]
    [InlineData(40, true)]
    [InlineData(19, false)]
    [InlineData(25, false)]
    public void IsPinnedBuildingTopSlot_MatchesPinnedSlots(int slotId, bool expected)
    {
        Assert.Equal(expected, BuildingsViewModel.IsPinnedBuildingTopSlot(slotId));
    }

    [Fact]
    public void ResolveSlotIdentity_BuiltBuildingIsOccupiedWithItsIdentity()
    {
        var building = new Building(25, "Warehouse", 5, null, 10);

        var (occupied, name, level, gid) = BuildingsViewModel.ResolveSlotIdentity(25, building, "Teutons");

        Assert.True(occupied);
        Assert.Equal("Warehouse", name);
        Assert.Equal(5, level);
        Assert.Equal(10, gid);
    }

    [Fact]
    public void ResolveSlotIdentity_MissingBuildingIsEmptySlot()
    {
        var (occupied, name, level, gid) = BuildingsViewModel.ResolveSlotIdentity(25, null, "Teutons");

        Assert.False(occupied);
        Assert.Equal("Empty", name);
        Assert.Null(level);
        Assert.Null(gid);
    }

    [Fact]
    public void ResolveSlotIdentity_KnownGidAtLevelZeroCountsAsOccupied()
    {
        var building = new Building(25, "Cranny", 0, null, 23);

        var (occupied, name, _, gid) = BuildingsViewModel.ResolveSlotIdentity(25, building, "Teutons");

        Assert.True(occupied);
        Assert.Equal("Cranny", name);
        Assert.Equal(23, gid);
    }

    [Fact]
    public void ResolveSlotIdentity_NamedButGidlessLevelZeroSlotStaysEmpty()
    {
        var building = new Building(25, "Cranny", 0, null, null);

        var (occupied, name, _, _) = BuildingsViewModel.ResolveSlotIdentity(25, building, "Teutons");

        Assert.False(occupied);
        Assert.Equal("Empty", name);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Empty")]
    [InlineData("g0")]
    [InlineData("Slot 25")]
    public void ResolveSlotIdentity_PlaceholderNamesAtLevelZeroDoNotOccupy(string placeholder)
    {
        var building = new Building(25, placeholder, 0, null, null);

        var (occupied, name, _, _) = BuildingsViewModel.ResolveSlotIdentity(25, building, "Teutons");

        Assert.False(occupied);
        Assert.Equal("Empty", name);
    }

    [Fact]
    public void ResolveSlotIdentity_UnbuiltRallyPointIsFreeButNamed()
    {
        var building = new Building(39, "Rally Point", 0, null, 16);

        var (occupied, name, level, gid) = BuildingsViewModel.ResolveSlotIdentity(39, building, "Teutons");

        Assert.False(occupied);
        Assert.Equal("Rally Point", name);
        Assert.Equal(0, level);
        Assert.Null(gid);
    }

    [Fact]
    public void ResolveSlotIdentity_BuiltRallyPointIsOccupied()
    {
        var building = new Building(39, "Rally Point", 3, null, 16);

        var (occupied, _, level, _) = BuildingsViewModel.ResolveSlotIdentity(39, building, "Teutons");

        Assert.True(occupied);
        Assert.Equal(3, level);
    }

    [Fact]
    public void ResolveSlotIdentity_UnbuiltWallShowsTribeWallAtLevelZero()
    {
        var building = new Building(40, "City Wall", 0, null, 33);

        var (occupied, name, level, _) = BuildingsViewModel.ResolveSlotIdentity(40, building, "Romans");

        Assert.False(occupied);
        Assert.Equal(0, level);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.NotEqual("Empty", name);
    }

    [Fact]
    public void IsEmptyBuilding_GidZeroPlaceholderIsEmpty()
    {
        Assert.True(BuildingsViewModel.IsEmptyBuilding(new Building(25, "Empty", 0, null, null)));
        Assert.False(BuildingsViewModel.IsEmptyBuilding(new Building(25, "Warehouse", 1, null, 10)));
    }
}
