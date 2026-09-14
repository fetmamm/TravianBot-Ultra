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
            [keys.AutomaticResourceSelection] = "true",
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
        Assert.True(values.AutomaticResourceSelection);
    }

    [Theory]
    [InlineData("9")]
    [InlineData("600")]
    public void Apply_PreservesGlobalFallbackCooldownAndAppliesBreweryFlag(string payloadCooldown)
    {
        var source = new BotOptions { TroopTrainingFallbackCooldownSeconds = 300 };
        var result = BotOptionsPayloadApplier.Apply(source, new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds] = payloadCooldown,
            [BotOptionPayloadKeys.BreweryAutoCelebrationEnabled] = "true",
        });

        Assert.Equal(300, result.TroopTrainingFallbackCooldownSeconds);
        Assert.True(result.BreweryAutoCelebrationEnabled);
    }

    private static KeySet KeysFor(string building) => building switch
    {
        "barracks" => new(BotOptionPayloadKeys.TroopTrainingBarracksEnabled, BotOptionPayloadKeys.TroopTrainingBarracksTroopType, BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours, BotOptionPayloadKeys.TroopTrainingBarracksAmountMode, BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingBarracksRunMode, BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops, BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingBarracksTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingBarracksTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingBarracksCheckWood, BotOptionPayloadKeys.TroopTrainingBarracksCheckClay, BotOptionPayloadKeys.TroopTrainingBarracksCheckIron, BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop, BotOptionPayloadKeys.TroopTrainingBarracksAutomaticResourceSelection),
        "stable" => new(BotOptionPayloadKeys.TroopTrainingStableEnabled, BotOptionPayloadKeys.TroopTrainingStableTroopType, BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours, BotOptionPayloadKeys.TroopTrainingStableAmountMode, BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingStableRunMode, BotOptionPayloadKeys.TroopTrainingStableMinimumTroops, BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingStableTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingStableTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingStableCheckWood, BotOptionPayloadKeys.TroopTrainingStableCheckClay, BotOptionPayloadKeys.TroopTrainingStableCheckIron, BotOptionPayloadKeys.TroopTrainingStableCheckCrop, BotOptionPayloadKeys.TroopTrainingStableAutomaticResourceSelection),
        _ => new(BotOptionPayloadKeys.TroopTrainingWorkshopEnabled, BotOptionPayloadKeys.TroopTrainingWorkshopTroopType, BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours, BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode, BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingWorkshopRunMode, BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops, BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingWorkshopTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingWorkshopTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood, BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay, BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron, BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop, BotOptionPayloadKeys.TroopTrainingWorkshopAutomaticResourceSelection),
    };

    private static Values ValuesFor(BotOptions value, string building) => building switch
    {
        "barracks" => new(value.TroopTrainingBarracksEnabled, value.TroopTrainingBarracksTroopType, value.TroopTrainingBarracksMaxQueueHours, value.TroopTrainingBarracksAmountMode, value.TroopTrainingBarracksKeepResourcesPercent, value.TroopTrainingBarracksRunMode, value.TroopTrainingBarracksMinimumTroops, value.TroopTrainingBarracksMinimumResourcesPercent, value.TroopTrainingBarracksTimedMinMinutes, value.TroopTrainingBarracksTimedMaxMinutes, value.TroopTrainingBarracksCheckWood, value.TroopTrainingBarracksCheckClay, value.TroopTrainingBarracksCheckIron, value.TroopTrainingBarracksCheckCrop, value.TroopTrainingBarracksAutomaticResourceSelection),
        "stable" => new(value.TroopTrainingStableEnabled, value.TroopTrainingStableTroopType, value.TroopTrainingStableMaxQueueHours, value.TroopTrainingStableAmountMode, value.TroopTrainingStableKeepResourcesPercent, value.TroopTrainingStableRunMode, value.TroopTrainingStableMinimumTroops, value.TroopTrainingStableMinimumResourcesPercent, value.TroopTrainingStableTimedMinMinutes, value.TroopTrainingStableTimedMaxMinutes, value.TroopTrainingStableCheckWood, value.TroopTrainingStableCheckClay, value.TroopTrainingStableCheckIron, value.TroopTrainingStableCheckCrop, value.TroopTrainingStableAutomaticResourceSelection),
        _ => new(value.TroopTrainingWorkshopEnabled, value.TroopTrainingWorkshopTroopType, value.TroopTrainingWorkshopMaxQueueHours, value.TroopTrainingWorkshopAmountMode, value.TroopTrainingWorkshopKeepResourcesPercent, value.TroopTrainingWorkshopRunMode, value.TroopTrainingWorkshopMinimumTroops, value.TroopTrainingWorkshopMinimumResourcesPercent, value.TroopTrainingWorkshopTimedMinMinutes, value.TroopTrainingWorkshopTimedMaxMinutes, value.TroopTrainingWorkshopCheckWood, value.TroopTrainingWorkshopCheckClay, value.TroopTrainingWorkshopCheckIron, value.TroopTrainingWorkshopCheckCrop, value.TroopTrainingWorkshopAutomaticResourceSelection),
    };

    private sealed record KeySet(string Enabled, string TroopType, string MaxQueueHours, string AmountMode, string KeepResourcesPercent, string RunMode, string MinimumTroops, string MinimumResourcesPercent, string TimedMinMinutes, string TimedMaxMinutes, string CheckWood, string CheckClay, string CheckIron, string CheckCrop, string AutomaticResourceSelection);
    private sealed record Values(bool Enabled, string TroopType, string MaxQueueHours, string AmountMode, int KeepResourcesPercent, string RunMode, int MinimumTroops, int MinimumResourcesPercent, int TimedMinMinutes, int TimedMaxMinutes, bool CheckWood, bool CheckClay, bool CheckIron, bool CheckCrop, bool AutomaticResourceSelection);
}
