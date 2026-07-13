using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ReinforcementPayloadApplierTests
{
    [Fact]
    public void Apply_MapsAndNormalizesWholeReinforcementDomain()
    {
        var payload = new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.ReinforcementsEnabled] = "true",
            [BotOptionPayloadKeys.ReinforcementsTargetVillageName] = "Target",
            [BotOptionPayloadKeys.ReinforcementsSourceVillageNames] = "One,Two,one",
            [BotOptionPayloadKeys.ReinforcementsTroopRules] = """[{"troopType":"Spearman","amountMode":"fixed","amount":5,"isEnabled":true},{"troopType":"spearman","amountMode":"fixed","amount":8,"isEnabled":true}]""",
            [BotOptionPayloadKeys.ReinforcementsSendMinMinutes] = "0",
            [BotOptionPayloadKeys.ReinforcementsSendMaxMinutes] = "0",
        };

        var result = BotOptionsPayloadApplier.Apply(new BotOptions(), payload);

        Assert.True(result.ReinforcementsEnabled);
        Assert.Equal("Target", result.ReinforcementsTargetVillageName);
        Assert.Equal(new[] { "One", "Two" }, result.ReinforcementsSourceVillageNames);
        Assert.Single(result.ReinforcementsTroopRules);
        Assert.Equal(ReinforcementSendDefaults.DefaultSendMinMinutes, result.ReinforcementsSendMinMinutes);
        Assert.Equal(ReinforcementSendDefaults.DefaultSendMaxMinutes, result.ReinforcementsSendMaxMinutes);
    }

    [Fact]
    public void Apply_InvalidValuesPreserveScalarsAndInvalidRulesBecomeEmptyAsBefore()
    {
        var source = new BotOptions
        {
            ReinforcementsEnabled = true,
            ReinforcementsSendMinMinutes = 45,
            ReinforcementsTroopRules = [new ReinforcementTroopRule { TroopType = "Paladin" }],
        };
        var payload = new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.ReinforcementsEnabled] = "invalid",
            [BotOptionPayloadKeys.ReinforcementsSendMinMinutes] = "invalid",
            [BotOptionPayloadKeys.ReinforcementsTroopRules] = "not-json",
        };

        var result = BotOptionsPayloadApplier.Apply(source, payload);

        Assert.True(result.ReinforcementsEnabled);
        Assert.Equal(45, result.ReinforcementsSendMinMinutes);
        Assert.Empty(result.ReinforcementsTroopRules);
    }
}
