using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TroopTrainingQuickSettingsTests
{
    [Fact]
    public void ApplySelections_OnlyChangesBuildingEnabledAndTroopType()
    {
        var barracks = new TroopTrainingBuildingPayload(
            false, "Legionnaire", "10", "keep_resources", 25, "timed", 40, 80, 20, 40, true, false, true, false);
        var stable = new TroopTrainingBuildingPayload(
            true, "Equites Legati", "20", "maximum", 0, "resource_percent", 5, 65, 30, 180, false, true, false, true);
        var workshop = new TroopTrainingBuildingPayload(
            true, "Fire Catapult", "no_limit", "maximum", 10, "resource_percent", 1, 50, 60, 240, true, true, true, true);
        var source = new TroopTrainingPayload(barracks, stable, workshop, 600);

        var result = TroopTrainingQuickSettings.ApplySelections(
            source,
            new[]
            {
                new TroopTrainingQuickBuildingSelection(TroopTrainingBuildingType.Barracks, true, " Imperian "),
                new TroopTrainingQuickBuildingSelection(TroopTrainingBuildingType.Stable, false, "Equites Imperatoris"),
            });

        Assert.True(result.Barracks.Enabled);
        Assert.Equal("Imperian", result.Barracks.TroopType);
        Assert.Equal(barracks.MaxQueueHours, result.Barracks.MaxQueueHours);
        Assert.Equal(barracks.AmountMode, result.Barracks.AmountMode);
        Assert.Equal(barracks.KeepResourcesPercent, result.Barracks.KeepResourcesPercent);
        Assert.Equal(barracks.RunMode, result.Barracks.RunMode);
        Assert.Equal(barracks.MinimumTroops, result.Barracks.MinimumTroops);
        Assert.Equal(barracks.MinimumResourcesPercent, result.Barracks.MinimumResourcesPercent);
        Assert.Equal(barracks.TimedMinMinutes, result.Barracks.TimedMinMinutes);
        Assert.Equal(barracks.TimedMaxMinutes, result.Barracks.TimedMaxMinutes);
        Assert.Equal(barracks.CheckWood, result.Barracks.CheckWood);
        Assert.Equal(barracks.CheckClay, result.Barracks.CheckClay);
        Assert.Equal(barracks.CheckIron, result.Barracks.CheckIron);
        Assert.Equal(barracks.CheckCrop, result.Barracks.CheckCrop);

        Assert.False(result.Stable.Enabled);
        Assert.Equal("Equites Imperatoris", result.Stable.TroopType);
        Assert.Equal(stable.MaxQueueHours, result.Stable.MaxQueueHours);
        Assert.Equal(stable.AmountMode, result.Stable.AmountMode);
        Assert.Equal(stable.KeepResourcesPercent, result.Stable.KeepResourcesPercent);
        Assert.Equal(stable.RunMode, result.Stable.RunMode);
        Assert.Equal(stable.MinimumTroops, result.Stable.MinimumTroops);
        Assert.Equal(stable.MinimumResourcesPercent, result.Stable.MinimumResourcesPercent);
        Assert.Equal(stable.TimedMinMinutes, result.Stable.TimedMinMinutes);
        Assert.Equal(stable.TimedMaxMinutes, result.Stable.TimedMaxMinutes);
        Assert.Equal(stable.CheckWood, result.Stable.CheckWood);
        Assert.Equal(stable.CheckClay, result.Stable.CheckClay);
        Assert.Equal(stable.CheckIron, result.Stable.CheckIron);
        Assert.Equal(stable.CheckCrop, result.Stable.CheckCrop);

        Assert.Equal(workshop, result.Workshop);
        Assert.Equal(600, result.FallbackCooldownSeconds);
    }
}
