using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TroopTrainingPayloadApplierDomainTests
{
    [Theory]
    [InlineData("barracks")]
    [InlineData("stable")]
    [InlineData("workshop")]
    public void Apply_UsesSameNormalizationForEveryTrainingBuilding(string building)
    {
        var keys = KeysFor(building);
        var payload = new Dictionary<string, string>
        {
            [keys.Enabled] = "true",
            [keys.TroopType] = "Unit",
            [keys.MaxQueueHours] = "10",
            [keys.AmountMode] = "keep_resources",
            [keys.KeepResourcesPercent] = "100",
            [keys.RunMode] = "unknown",
            [keys.MinimumTroops] = "0",
            [keys.MinimumResourcesPercent] = "101",
            [keys.TimedMinMinutes] = "0",
            [keys.TimedMaxMinutes] = "0",
            [keys.CheckWood] = "false",
            [keys.CheckClay] = "false",
            [keys.CheckIron] = "false",
            [keys.CheckCrop] = "false",
        };

        var result = BotOptionsPayloadApplier.Apply(new BotOptions(), payload);
        var values = ValuesFor(result, building);

        Assert.True(values.Enabled);
        Assert.Equal("Unit", values.TroopType);
        Assert.Equal("10", values.MaxQueueHours);
        Assert.Equal("keep_resources", values.AmountMode);
        Assert.Equal(95, values.KeepResourcesPercent);
        Assert.Equal("timed", values.RunMode);
        Assert.Equal(1, values.MinimumTroops);
        Assert.Equal(100, values.MinimumResourcesPercent);
        Assert.Equal(1, values.TimedMinMinutes);
        Assert.Equal(1, values.TimedMaxMinutes);
        Assert.False(values.CheckWood);
        Assert.False(values.CheckClay);
        Assert.False(values.CheckIron);
        Assert.False(values.CheckCrop);
    }

    [Theory]
    [InlineData("9", 30)]
    [InlineData("600", 600)]
    public void Apply_NormalizesFallbackCooldownAndBreweryFlag(string cooldown, int expected)
    {
        var result = BotOptionsPayloadApplier.Apply(new BotOptions(), new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds] = cooldown,
            [BotOptionPayloadKeys.BreweryAutoCelebrationEnabled] = "true",
        });

        Assert.Equal(expected, result.TroopTrainingFallbackCooldownSeconds);
        Assert.True(result.BreweryAutoCelebrationEnabled);
    }

    private static KeySet KeysFor(string building) => building switch
    {
        "barracks" => new(BotOptionPayloadKeys.TroopTrainingBarracksEnabled, BotOptionPayloadKeys.TroopTrainingBarracksTroopType, BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours, BotOptionPayloadKeys.TroopTrainingBarracksAmountMode, BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingBarracksRunMode, BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops, BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingBarracksTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingBarracksTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingBarracksCheckWood, BotOptionPayloadKeys.TroopTrainingBarracksCheckClay, BotOptionPayloadKeys.TroopTrainingBarracksCheckIron, BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop),
        "stable" => new(BotOptionPayloadKeys.TroopTrainingStableEnabled, BotOptionPayloadKeys.TroopTrainingStableTroopType, BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours, BotOptionPayloadKeys.TroopTrainingStableAmountMode, BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingStableRunMode, BotOptionPayloadKeys.TroopTrainingStableMinimumTroops, BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingStableTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingStableTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingStableCheckWood, BotOptionPayloadKeys.TroopTrainingStableCheckClay, BotOptionPayloadKeys.TroopTrainingStableCheckIron, BotOptionPayloadKeys.TroopTrainingStableCheckCrop),
        _ => new(BotOptionPayloadKeys.TroopTrainingWorkshopEnabled, BotOptionPayloadKeys.TroopTrainingWorkshopTroopType, BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours, BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode, BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingWorkshopRunMode, BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops, BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingWorkshopTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingWorkshopTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood, BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay, BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron, BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop),
    };

    private static Values ValuesFor(BotOptions value, string building) => building switch
    {
        "barracks" => new(value.TroopTrainingBarracksEnabled, value.TroopTrainingBarracksTroopType, value.TroopTrainingBarracksMaxQueueHours, value.TroopTrainingBarracksAmountMode, value.TroopTrainingBarracksKeepResourcesPercent, value.TroopTrainingBarracksRunMode, value.TroopTrainingBarracksMinimumTroops, value.TroopTrainingBarracksMinimumResourcesPercent, value.TroopTrainingBarracksTimedMinMinutes, value.TroopTrainingBarracksTimedMaxMinutes, value.TroopTrainingBarracksCheckWood, value.TroopTrainingBarracksCheckClay, value.TroopTrainingBarracksCheckIron, value.TroopTrainingBarracksCheckCrop),
        "stable" => new(value.TroopTrainingStableEnabled, value.TroopTrainingStableTroopType, value.TroopTrainingStableMaxQueueHours, value.TroopTrainingStableAmountMode, value.TroopTrainingStableKeepResourcesPercent, value.TroopTrainingStableRunMode, value.TroopTrainingStableMinimumTroops, value.TroopTrainingStableMinimumResourcesPercent, value.TroopTrainingStableTimedMinMinutes, value.TroopTrainingStableTimedMaxMinutes, value.TroopTrainingStableCheckWood, value.TroopTrainingStableCheckClay, value.TroopTrainingStableCheckIron, value.TroopTrainingStableCheckCrop),
        _ => new(value.TroopTrainingWorkshopEnabled, value.TroopTrainingWorkshopTroopType, value.TroopTrainingWorkshopMaxQueueHours, value.TroopTrainingWorkshopAmountMode, value.TroopTrainingWorkshopKeepResourcesPercent, value.TroopTrainingWorkshopRunMode, value.TroopTrainingWorkshopMinimumTroops, value.TroopTrainingWorkshopMinimumResourcesPercent, value.TroopTrainingWorkshopTimedMinMinutes, value.TroopTrainingWorkshopTimedMaxMinutes, value.TroopTrainingWorkshopCheckWood, value.TroopTrainingWorkshopCheckClay, value.TroopTrainingWorkshopCheckIron, value.TroopTrainingWorkshopCheckCrop),
    };

    private sealed record KeySet(string Enabled, string TroopType, string MaxQueueHours, string AmountMode, string KeepResourcesPercent, string RunMode, string MinimumTroops, string MinimumResourcesPercent, string TimedMinMinutes, string TimedMaxMinutes, string CheckWood, string CheckClay, string CheckIron, string CheckCrop);
    private sealed record Values(bool Enabled, string TroopType, string MaxQueueHours, string AmountMode, int KeepResourcesPercent, string RunMode, int MinimumTroops, int MinimumResourcesPercent, int TimedMinMinutes, int TimedMaxMinutes, bool CheckWood, bool CheckClay, bool CheckIron, bool CheckCrop);
}
