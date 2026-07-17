using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TroopTrainingQuickSettingsTests
{
    private static TroopTrainingPayload BuildSourcePayload()
    {
        var barracks = new TroopTrainingBuildingPayload(
            false, "Clubswinger", "10", "keep_resources", 25, "timed", 40, 80, 20, 40, true, true, true, true);
        var stable = new TroopTrainingBuildingPayload(
            true, "Paladin", "20", "maximum", 0, "resource_percent", 5, 65, 30, 180, true, true, true, true);
        var workshop = new TroopTrainingBuildingPayload(
            true, "Ram", "no_limit", "maximum", 10, "resource_percent", 1, 50, 60, 240, true, true, true, true);
        return new TroopTrainingPayload(barracks, stable, workshop, 600);
    }

    [Fact]
    public void BuildPayload_WithoutEdits_RoundTripsSourceSettings()
    {
        var source = BuildSourcePayload();
        var row = new TroopTrainingQuickVillageRow("v1", "Village 1", true, source, "Teutons");

        var result = row.BuildPayload();

        Assert.Equal(source, result);
    }

    [Fact]
    public void BuildPayload_AppliesAllEditedSettings()
    {
        var source = BuildSourcePayload();
        var row = new TroopTrainingQuickVillageRow("v1", "Village 1", true, source, "Teutons");

        row.Barracks.IsEnabled = true;
        row.Barracks.SelectedTroop = "Spearman";
        row.Barracks.MaxQueueMode = "5";
        row.Barracks.AmountMode = "maximum";
        row.Barracks.RunMode = "resource_percent";
        row.Barracks.MinimumResourcesPercent = 70;
        row.Stable.TimedMinMinutes = 15;
        row.Stable.TimedMaxMinutes = 45;
        row.Workshop.IsEnabled = false;
        row.CheckWood = false;
        row.CheckCrop = false;
        row.FallbackCooldownSeconds = 120;

        var result = row.BuildPayload();

        Assert.True(result.Barracks.Enabled);
        Assert.Equal("Spearman", result.Barracks.TroopType);
        Assert.Equal("5", result.Barracks.MaxQueueHours);
        Assert.Equal("maximum", result.Barracks.AmountMode);
        Assert.Equal("resource_percent", result.Barracks.RunMode);
        Assert.Equal(70, result.Barracks.MinimumResourcesPercent);
        // MinimumTroops has no UI and must be preserved from the source payload.
        Assert.Equal(source.Barracks.MinimumTroops, result.Barracks.MinimumTroops);

        Assert.Equal(15, result.Stable.TimedMinMinutes);
        Assert.Equal(45, result.Stable.TimedMaxMinutes);
        Assert.False(result.Workshop.Enabled);

        // Resource checks are village-level in the popup and written to all three buildings.
        foreach (var building in new[] { result.Barracks, result.Stable, result.Workshop })
        {
            Assert.False(building.CheckWood);
            Assert.True(building.CheckClay);
            Assert.True(building.CheckIron);
            Assert.False(building.CheckCrop);
        }

        Assert.Equal(120, result.FallbackCooldownSeconds);
    }

    [Fact]
    public void FallbackCooldown_NormalizesUnknownValuesTo30()
    {
        var source = BuildSourcePayload() with { FallbackCooldownSeconds = 77 };
        var row = new TroopTrainingQuickVillageRow("v1", "Village 1", true, source, "Teutons");

        Assert.Equal(30, row.FallbackCooldownSeconds);
    }

    [Fact]
    public void VillageRows_UseTheirOwnTribeTroops()
    {
        var source = BuildSourcePayload();
        var roman = new TroopTrainingQuickVillageRow("roman", "Roman village", true, source, "Romans");
        var gaul = new TroopTrainingQuickVillageRow("gaul", "Gaul village", true, source, "Gauls");

        Assert.Contains("Legionnaire", roman.Barracks.TroopOptions);
        Assert.DoesNotContain("Phalanx", roman.Barracks.TroopOptions);
        Assert.Contains("Phalanx", gaul.Barracks.TroopOptions);
        Assert.DoesNotContain("Legionnaire", gaul.Barracks.TroopOptions);
    }

    [Fact]
    public void UnknownVillageTribe_DisablesTrainingWithoutGenericFallbackTroops()
    {
        var row = new TroopTrainingQuickVillageRow("unknown", "Unscanned", true, BuildSourcePayload(), "Unknown");

        Assert.False(row.IsBuildTroopsEnabled);
        Assert.Empty(row.Barracks.TroopOptions);
        Assert.Empty(row.Stable.TroopOptions);
        Assert.Empty(row.Workshop.TroopOptions);
    }
}
