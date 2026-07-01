using TbotUltra.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BotOptionsPayloadApplierTests
{
    [Fact]
    public void FromConfiguration_NewVillageStartupAnalysis_DefaultsEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["server_name"] = "srv",
                ["base_url"] = "https://example.com",
            })
            .Build();

        Assert.True(BotOptionsFactory.FromConfiguration(configuration).PostLoginAnalyzeNewVillages);
    }

    [Fact]
    public void Apply_OverridesNewVillageStartupAnalysis()
    {
        var source = new BotOptions { PostLoginAnalyzeNewVillages = true };

        var result = BotOptionsPayloadApplier.Apply(source, new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.PostLoginAnalyzeNewVillages] = "false",
        });

        Assert.False(result.PostLoginAnalyzeNewVillages);
    }

    [Fact]
    public void Apply_OverridesConfiguredFields_FromPayload()
    {
        var source = new BotOptions
        {
            ServerName = "srv",
            BaseUrl = "https://example.com",
            ResourceUpgradeSlotId = 1,
            ResourceUpgradeTargetLevel = 2,
            BuildingUpgradeSlotId = 3,
            BuildingUpgradeTargetLevel = 4,
            BuildingConstructSlotId = 5,
            BuildingConstructGid = 6,
            BuildingConstructName = "Main Building",
            TargetBuildingSlotOrName = "20",
            TargetLevel = 5,
            HeroMinHpForAdventure = 60,
            HeroAutoRevive = true,
            HeroAutoAssignPoints = true,
            HeroAutoUseOintments = false,
            HeroStatPriority = "offense,resource,regeneration",
            AutoCollectDailyQuestsEnabled = false,
            UpgradeSelectorProfile = "auto",
            TroopTrainingBarracksEnabled = true,
            TroopTrainingBarracksTroopType = "Legionnaire",
            TroopTrainingBarracksMaxQueueHours = "10",
            TroopTrainingBarracksAmountMode = "maximum",
            TroopTrainingBarracksKeepResourcesPercent = 10,
            NpcTradeConstructionEnabled = false,
            ResourceTransferEnabled = false,
            ResourceTransferTargetVillageName = "Old target",
            ResourceTransferSourceVillageNames = ["Old source"],
            ReinforcementsEnabled = false,
            ReinforcementsTargetVillageName = "Old reinforcement target",
            ReinforcementsSourceVillageNames = ["Old reinforcement source"],
            ReinforcementsSendIntervalHours = 24,
            ReinforcementsSendVariationPercent = 90,
        };

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeSlotId] = "9",
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = "10",
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = "11",
            [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = "12",
            [BotOptionPayloadKeys.BuildingConstructSlotId] = "13",
            [BotOptionPayloadKeys.BuildingConstructGid] = "14",
            [BotOptionPayloadKeys.BuildingConstructName] = "Barracks",
            [BotOptionPayloadKeys.TargetBuildingSlotOrName] = "Main Building",
            [BotOptionPayloadKeys.TargetLevel] = "10",
            [BotOptionPayloadKeys.HeroMinHpForAdventure] = "75",
            [BotOptionPayloadKeys.HeroAutoRevive] = "false",
            [BotOptionPayloadKeys.HeroAutoAssignPoints] = "false",
            [BotOptionPayloadKeys.HeroAutoUseOintments] = "true",
            [BotOptionPayloadKeys.HeroStatPriority] = "resource,offense",
            [BotOptionPayloadKeys.SmithyUpgradeTargets] = "u21=20;u24=10",
            [BotOptionPayloadKeys.AutoCollectDailyQuestsEnabled] = "true",
            [BotOptionPayloadKeys.UpgradeSelectorProfile] = "strict_green",
            [BotOptionPayloadKeys.TroopTrainingBarracksEnabled] = "false",
            [BotOptionPayloadKeys.TroopTrainingBarracksTroopType] = "Praetorian",
            [BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours] = "50",
            [BotOptionPayloadKeys.TroopTrainingBarracksAmountMode] = "keep_resources",
            [BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent] = "25",
            [BotOptionPayloadKeys.NpcTradeConstructionEnabled] = "true",
            [BotOptionPayloadKeys.ResourceTransferEnabled] = "true",
            [BotOptionPayloadKeys.ResourceTransferTargetVillageName] = "Target",
            [BotOptionPayloadKeys.ResourceTransferSourceVillageNames] = "A,B,A",
            [BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent] = "90",
            [BotOptionPayloadKeys.ResourceTransferSourceKeepPercent] = "65",
            [BotOptionPayloadKeys.ResourceTransferTargetFillPercent] = "95",
            [BotOptionPayloadKeys.ResourceTransferSendCrop] = "false",
            [BotOptionPayloadKeys.ReinforcementsEnabled] = "true",
            [BotOptionPayloadKeys.ReinforcementsTargetVillageName] = "Reinforcement target",
            [BotOptionPayloadKeys.ReinforcementsSourceVillageNames] = "S1,S2,S1",
            [BotOptionPayloadKeys.ReinforcementsTroopRules] = """[{"troopType":"Spearman","amountMode":"all_available","amount":1,"isEnabled":true},{"troopType":"Paladin","amountMode":"fixed","amount":25,"isEnabled":true}]""",
            [BotOptionPayloadKeys.ReinforcementsSendIntervalHours] = "8",
            [BotOptionPayloadKeys.ReinforcementsSendVariationPercent] = "25",
        };

        var result = BotOptionsPayloadApplier.Apply(source, payload);

        Assert.Equal(9, result.ResourceUpgradeSlotId);
        Assert.Equal(10, result.ResourceUpgradeTargetLevel);
        Assert.Equal(11, result.BuildingUpgradeSlotId);
        Assert.Equal(12, result.BuildingUpgradeTargetLevel);
        Assert.Equal(13, result.BuildingConstructSlotId);
        Assert.Equal(14, result.BuildingConstructGid);
        Assert.Equal("Barracks", result.BuildingConstructName);
        Assert.Equal("Main Building", result.TargetBuildingSlotOrName);
        Assert.Equal(10, result.TargetLevel);
        Assert.Equal(75, result.HeroMinHpForAdventure);
        Assert.False(result.HeroAutoRevive);
        Assert.False(result.HeroAutoAssignPoints);
        Assert.True(result.HeroAutoUseOintments);
        Assert.Equal("resource,offense", result.HeroStatPriority);
        Assert.Equal("u21=20;u24=10", result.SmithyUpgradeTargets);
        Assert.True(result.AutoCollectDailyQuestsEnabled);
        Assert.Equal("strict_green", result.UpgradeSelectorProfile);
        Assert.False(result.TroopTrainingBarracksEnabled);
        Assert.Equal("Praetorian", result.TroopTrainingBarracksTroopType);
        Assert.Equal("50", result.TroopTrainingBarracksMaxQueueHours);
        Assert.Equal("keep_resources", result.TroopTrainingBarracksAmountMode);
        Assert.Equal(25, result.TroopTrainingBarracksKeepResourcesPercent);
        Assert.True(result.NpcTradeConstructionEnabled);
        Assert.True(result.ResourceTransferEnabled);
        Assert.Equal("Target", result.ResourceTransferTargetVillageName);
        Assert.Equal(["A", "B"], result.ResourceTransferSourceVillageNames);
        Assert.Equal(90, result.ResourceTransferSourceThresholdPercent);
        Assert.Equal(65, result.ResourceTransferSourceKeepPercent);
        Assert.Equal(95, result.ResourceTransferTargetFillPercent);
        Assert.True(result.ResourceTransferSendWood);
        Assert.False(result.ResourceTransferSendCrop);
        Assert.True(result.ReinforcementsEnabled);
        Assert.Equal("Reinforcement target", result.ReinforcementsTargetVillageName);
        Assert.Equal(["S1", "S2"], result.ReinforcementsSourceVillageNames);
        Assert.Equal(2, result.ReinforcementsTroopRules.Count);
        Assert.Equal("Spearman", result.ReinforcementsTroopRules[0].TroopType);
        Assert.Equal("all_available", result.ReinforcementsTroopRules[0].AmountMode);
        Assert.Equal("Paladin", result.ReinforcementsTroopRules[1].TroopType);
        Assert.Equal(25, result.ReinforcementsTroopRules[1].Amount);
        Assert.Equal(8, result.ReinforcementsSendIntervalHours);
        Assert.Equal(25, result.ReinforcementsSendVariationPercent);
    }

    [Fact]
    public void FromConfiguration_LoadsResourceTransferSettings()
    {
        var values = new Dictionary<string, string?>
        {
            ["server_name"] = "srv",
            ["base_url"] = "https://example.com",
            [BotOptionPayloadKeys.ResourceTransferEnabled] = "true",
            [BotOptionPayloadKeys.ResourceTransferTargetVillageName] = "Target",
            [$"{BotOptionPayloadKeys.ResourceTransferSourceVillageNames}:0"] = "A",
            [$"{BotOptionPayloadKeys.ResourceTransferSourceVillageNames}:1"] = "B",
            [BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent] = "90",
            [BotOptionPayloadKeys.ResourceTransferSourceKeepPercent] = "65",
            [BotOptionPayloadKeys.ResourceTransferTargetFillPercent] = "95",
            [BotOptionPayloadKeys.ResourceTransferSendIron] = "false",
            [BotOptionPayloadKeys.HeroAutoUseOintments] = "true",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.True(options.ResourceTransferEnabled);
        Assert.Equal("Target", options.ResourceTransferTargetVillageName);
        Assert.Equal(["A", "B"], options.ResourceTransferSourceVillageNames);
        Assert.Equal(90, options.ResourceTransferSourceThresholdPercent);
        Assert.Equal(65, options.ResourceTransferSourceKeepPercent);
        Assert.Equal(95, options.ResourceTransferTargetFillPercent);
        Assert.False(options.ResourceTransferSendIron);
        Assert.True(options.ResourceTransferSendWood);
        Assert.True(options.HeroAutoUseOintments);
    }

    [Fact]
    public void FromConfiguration_DefaultsHeroResourceMaxUse_EnabledAt5000()
    {
        var values = new Dictionary<string, string?>
        {
            ["server_name"] = "srv",
            ["base_url"] = "https://example.com",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.True(options.HeroResourceMaxUseEnabled);
        Assert.Equal(5000, options.HeroResourceMaxUsePerResource);
    }

    [Fact]
    public void FromConfiguration_DefaultsHeroResourceConsumers_ConstructionOnly()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["server_name"] = "srv",
                ["base_url"] = "https://example.com",
            })
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.True(options.HeroResourceUseConstruction);
        Assert.False(options.HeroResourceUseSmithy);
        Assert.False(options.HeroResourceUseBrewery);
        Assert.False(options.HeroResourceUseTownHall);
    }

    [Fact]
    public void Apply_OverridesHeroResourceMaxUse_FromPayload()
    {
        var source = new BotOptions
        {
            ServerName = "srv",
            BaseUrl = "https://example.com",
            HeroResourceMaxUseEnabled = true,
            HeroResourceMaxUsePerResource = 5000,
        };

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.HeroResourceMaxUseEnabled] = "false",
            [BotOptionPayloadKeys.HeroResourceMaxUsePerResource] = "12000",
        };

        var result = BotOptionsPayloadApplier.Apply(source, payload);

        Assert.False(result.HeroResourceMaxUseEnabled);
        Assert.Equal(12000, result.HeroResourceMaxUsePerResource);
    }

    [Fact]
    public void FromConfiguration_UsesSafeOfficialGoldAndNpcDefaults()
    {
        var values = new Dictionary<string, string?>
        {
            ["server_name"] = "Official",
            ["base_url"] = "https://ts50.x5.europe.travian.com",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.False(options.NpcTradeEnabled);
        Assert.False(options.NpcTradeConstructionEnabled);
        Assert.False(options.AllowGoldSpending);
        Assert.Equal(300, options.GoldLimit);
    }

    [Fact]
    public void FromConfiguration_UsesResourcesFirstHeroPriorityOnOfficialByDefault()
    {
        var values = new Dictionary<string, string?>
        {
            ["server_name"] = "Official",
            ["base_url"] = "https://ts50.x5.europe.travian.com",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.Equal("resources,fighting_strength,offence_bonus,defence_bonus", options.HeroStatPriority);
    }

    [Fact]
    public void FromConfiguration_PreservesExplicitHeroPriorityOnOfficial()
    {
        var values = new Dictionary<string, string?>
        {
            ["server_name"] = "Official",
            ["base_url"] = "https://ts50.x5.europe.travian.com",
            [BotOptionPayloadKeys.HeroStatPriority] = "offence_bonus,resources,fighting_strength,defence_bonus",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.Equal("offence_bonus,resources,fighting_strength,defence_bonus", options.HeroStatPriority);
    }

    [Fact]
    public void FromConfiguration_ExplicitGoldAndNpcSettingsOverrideOfficialDefaults()
    {
        var values = new Dictionary<string, string?>
        {
            ["server_name"] = "Official",
            ["base_url"] = "https://ts50.x5.europe.travian.com",
            [BotOptionPayloadKeys.NpcTradeEnabled] = "true",
            [BotOptionPayloadKeys.NpcTradeConstructionEnabled] = "true",
            [BotOptionPayloadKeys.AllowGoldSpending] = "true",
            [BotOptionPayloadKeys.GoldLimit] = "500",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.True(options.NpcTradeEnabled);
        Assert.True(options.NpcTradeConstructionEnabled);
        Assert.True(options.AllowGoldSpending);
        Assert.Equal(500, options.GoldLimit);
    }

    [Fact]
    public void FromConfiguration_LoadsReinforcementSettings()
    {
        var values = new Dictionary<string, string?>
        {
            ["server_name"] = "srv",
            ["base_url"] = "https://example.com",
            [BotOptionPayloadKeys.ReinforcementsEnabled] = "true",
            [BotOptionPayloadKeys.ReinforcementsTargetVillageName] = "Target",
            [BotOptionPayloadKeys.ReinforcementsSendIntervalHours] = "12",
            [BotOptionPayloadKeys.ReinforcementsSendVariationPercent] = "50",
            [$"{BotOptionPayloadKeys.ReinforcementsSourceVillageNames}:0"] = "A",
            [$"{BotOptionPayloadKeys.ReinforcementsSourceVillageNames}:1"] = "B",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:0:troopType"] = "Spearman",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:0:amountMode"] = "all_available",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:0:amount"] = "1",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:0:isEnabled"] = "true",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.True(options.ReinforcementsEnabled);
        Assert.Equal("Target", options.ReinforcementsTargetVillageName);
        Assert.Equal(12, options.ReinforcementsSendIntervalHours);
        Assert.Equal(50, options.ReinforcementsSendVariationPercent);
        Assert.Equal(["A", "B"], options.ReinforcementsSourceVillageNames);
        var rule = Assert.Single(options.ReinforcementsTroopRules);
        Assert.Equal("Spearman", rule.TroopType);
        Assert.Equal("all_available", rule.AmountMode);
        Assert.True(rule.IsEnabled);
    }

    [Fact]
    public void FromConfiguration_KeepsVillageSpecificReinforcementRules()
    {
        var values = new Dictionary<string, string?>
        {
            ["server_name"] = "srv",
            ["base_url"] = "https://example.com",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:0:sourceVillageName"] = "",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:0:troopType"] = "Clubswinger",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:0:amountMode"] = "fixed",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:0:amount"] = "1",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:0:isEnabled"] = "false",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:1:sourceVillageName"] = "Source",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:1:troopType"] = "Clubswinger",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:1:amountMode"] = "fixed",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:1:amount"] = "10",
            [$"{BotOptionPayloadKeys.ReinforcementsTroopRules}:1:isEnabled"] = "true",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.Equal(2, options.ReinforcementsTroopRules.Count);
        var sourceRule = Assert.Single(options.ReinforcementsTroopRules, rule => rule.SourceVillageName == "Source");
        Assert.Equal("Clubswinger", sourceRule.TroopType);
        Assert.Equal(10, sourceRule.Amount);
        Assert.True(sourceRule.IsEnabled);
    }

    [Fact]
    public void FromConfiguration_LoadsFarmingDefaultsAndNormalizesDelay()
    {
        var defaultConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["server_name"] = "srv",
                ["base_url"] = "https://example.com",
            })
            .Build();

        var defaultOptions = BotOptionsFactory.FromConfiguration(defaultConfiguration);

        Assert.Equal(20, defaultOptions.ContinuousFarmDispatchDelayMinutes);
        Assert.Equal(20, defaultOptions.ContinuousFarmDispatchDelayVariationPercent);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["server_name"] = "srv",
                ["base_url"] = "https://example.com",
                [BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes] = "4",
                [BotOptionPayloadKeys.ContinuousFarmDispatchDelayVariationPercent] = "18",
            })
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.Equal(3, options.ContinuousFarmDispatchDelayMinutes);
        Assert.Equal(20, options.ContinuousFarmDispatchDelayVariationPercent);
        Assert.Equal(FarmingDefaults.SendModeListPerList, options.ContinuousFarmSendMode);
        Assert.False(options.ContinuousFarmDeactivateLosses);
        Assert.False(options.ContinuousFarmDeactivateOasisLosses);
    }

    [Fact]
    public void FromConfiguration_UsesConservativePacingDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["server_name"] = "srv",
                ["base_url"] = "https://example.com",
            })
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.True(options.ActionPacingEnabled);
        Assert.Equal(0.8, options.ActionPacingTaskMinSeconds);
        Assert.Equal(3.0, options.ActionPacingTaskMaxSeconds);
        Assert.Equal(0.8, options.ActionPacingPageLoadMinSeconds);
        Assert.Equal(1.8, options.ActionPacingPageLoadMaxSeconds);
        Assert.Equal(0.6, options.ActionPacingClickMinSeconds);
        Assert.Equal(1.8, options.ActionPacingClickMaxSeconds);
        Assert.Equal(4.0, options.ActionPacingLoopMinSeconds);
        Assert.Equal(25.0, options.ActionPacingLoopMaxSeconds);
        Assert.Equal(1.5, options.FarmListStepDelayMinSeconds);
        Assert.Equal(4.0, options.FarmListStepDelayMaxSeconds);
        Assert.Equal(0.8, options.CollectStepDelayMinSeconds);
        Assert.Equal(2.5, options.CollectStepDelayMaxSeconds);
        Assert.False(options.IncreaseAdventuresToHard);
    }

    [Fact]
    public void FromConfiguration_MapsLegacyHumanLikeToActionPacingFallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["server_name"] = "srv",
                ["base_url"] = "https://example.com",
                ["human_like_enabled"] = "true",
                ["human_like_speed"] = "slow",
            })
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.True(options.HumanLikeEnabled);
        Assert.True(options.ActionPacingEnabled);
        Assert.Equal(2.5, options.ActionPacingTaskMinSeconds);
        Assert.Equal(5.0, options.ActionPacingTaskMaxSeconds);
        Assert.Equal(2.5, options.ActionPacingClickMinSeconds);
        Assert.Equal(5.0, options.ActionPacingClickMaxSeconds);
        Assert.Equal(2.5, options.FarmListStepDelayMinSeconds);
        Assert.Equal(5.0, options.FarmListStepDelayMaxSeconds);
    }

    [Fact]
    public void Apply_OverridesFarmingRuntimeSettings()
    {
        var source = new BotOptions
        {
            ContinuousFarmDispatchDelayMinutes = FarmingDefaults.DefaultDispatchDelayMinutes,
            ContinuousFarmDispatchDelayVariationPercent = FarmingDefaults.DefaultDispatchDelayVariationPercent,
            ContinuousFarmSendMode = FarmingDefaults.SendModeListPerList,
            TownHallCelebrationMode = TownHallCelebrationDefaults.Small,
            HeroResourceUseTownHall = false,
        };

        var result = BotOptionsPayloadApplier.Apply(source, new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes] = "90",
            [BotOptionPayloadKeys.ContinuousFarmDispatchDelayVariationPercent] = "50",
            [BotOptionPayloadKeys.ContinuousFarmSendMode] = FarmingDefaults.SendModeAllAtOnce,
            [BotOptionPayloadKeys.TownHallCelebrationMode] = TownHallCelebrationDefaults.Big,
            [BotOptionPayloadKeys.ContinuousFarmDeactivateLosses] = "true",
            [BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses] = "true",
            [BotOptionPayloadKeys.ContinuousFarmNextListIndex] = "7",
            [BotOptionPayloadKeys.HeroResourceUseTownHall] = "true",
        });

        Assert.Equal(90, result.ContinuousFarmDispatchDelayMinutes);
        Assert.Equal(50, result.ContinuousFarmDispatchDelayVariationPercent);
        Assert.Equal(FarmingDefaults.SendModeAllAtOnce, result.ContinuousFarmSendMode);
        Assert.Equal(TownHallCelebrationDefaults.Big, result.TownHallCelebrationMode);
        Assert.True(result.ContinuousFarmDeactivateLosses);
        Assert.True(result.ContinuousFarmDeactivateOasisLosses);
        Assert.Equal(7, result.ContinuousFarmNextListIndex);
        Assert.True(result.HeroResourceUseTownHall);
    }

    [Fact]
    public void ReinforcementSendDefaults_NormalizesChoices()
    {
        Assert.Equal(5, ReinforcementSendDefaults.NormalizeIntervalHours(0));
        Assert.Equal(5, ReinforcementSendDefaults.NormalizeIntervalHours(6));
        Assert.Equal(0, ReinforcementSendDefaults.NormalizeVariationPercent(0));
        Assert.Equal(25, ReinforcementSendDefaults.NormalizeVariationPercent(24));
        Assert.Equal(TimeSpan.FromHours(1), ReinforcementSendDefaults.CalculateSendDelay(1, 0));
    }
}
