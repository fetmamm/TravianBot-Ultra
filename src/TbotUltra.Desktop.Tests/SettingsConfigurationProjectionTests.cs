using System.Text.Json.Nodes;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SettingsConfigurationProjectionTests
{
    [Fact]
    public void Load_ProjectsDomainDefaultsWithoutWpf()
    {
        var settings = SettingsConfigurationAdapter.Load([]);

        Assert.True(settings.General.QuickReloginEnabled);
        Assert.True(settings.Options.AutomaticallyCheckLanguage);
        Assert.Equal(PacingDefaults.ActionPacingTaskMinSeconds, settings.Options.ActionPacingTaskMinSeconds);
        Assert.Equal(ConstructionDefaults.MainBuildingRebuildTargetLevel, settings.Options.ConstructionMainBuildingRebuildTargetLevel);
        Assert.Equal(FarmingDefaults.DefaultLastSentLimitHours, settings.Options.FarmListLastSentLimitHours);
        Assert.Equal(["construction", "hero"], settings.SessionPacing.SmartSleepDeadlineGroups);
        Assert.Equal(Enumerable.Range(0, 24), settings.SessionPacing.SessionAllowedHours);
    }

    [Fact]
    public void Apply_NormalizesEveryDomainAndPreservesUnknownConfiguration()
    {
        var source = new JsonObject { ["future_setting"] = "keep" };
        var current = SettingsConfigurationAdapter.Load(source);
        var edited = current with
        {
            General = current.General with
            {
                DontNotifyNewVersion = true,
                DailyServerResetHour = 99,
            },
            SessionPacing = current.SessionPacing with
            {
                SmartSleepFallbackMinMinutes = 60,
                SmartSleepFallbackMaxMinutes = 20,
                SessionAllowedHours = [-1, 4, 4, 24],
            },
            Options = current.Options with
            {
                ConstructionHumanizeDelayEnabled = !current.Options.ConstructionHumanizeDelayEnabled,
                ConstructionHumanizeQueuePercentMin = 80,
                ConstructionHumanizeQueuePercentMax = 20,
                FarmListLastSentLimitHours = int.MaxValue,
                HeroCropAntiStarveTriggerMinutes = 100,
                HeroCropAntiStarveTargetMinutes = 50,
                ActionPacingTaskMinSeconds = 12,
                ActionPacingTaskMaxSeconds = 2,
            },
        };

        var draft = SettingsConfigurationAdapter.BuildDraft(source, edited);
        var reloaded = SettingsConfigurationAdapter.Load(draft);

        Assert.Equal("keep", draft["future_setting"]!.GetValue<string>());
        Assert.True(reloaded.General.DontNotifyNewVersion);
        Assert.Equal(23, reloaded.General.DailyServerResetHour);
        Assert.Equal(60, reloaded.SessionPacing.SmartSleepFallbackMinMinutes);
        Assert.Equal(60, reloaded.SessionPacing.SmartSleepFallbackMaxMinutes);
        Assert.Equal([4], reloaded.SessionPacing.SessionAllowedHours);
        Assert.Equal(80, reloaded.Options.ConstructionHumanizeQueuePercentMin);
        Assert.Equal(80, reloaded.Options.ConstructionHumanizeQueuePercentMax);
        Assert.Equal(1, reloaded.Options.ConstructionHumanizeStateVersion);
        Assert.Equal(FarmingDefaults.MaxLastSentLimitHours, reloaded.Options.FarmListLastSentLimitHours);
        Assert.Equal(100, reloaded.Options.HeroCropAntiStarveTriggerMinutes);
        Assert.Equal(101, reloaded.Options.HeroCropAntiStarveTargetMinutes);
        Assert.Equal(12, reloaded.Options.ActionPacingTaskMinSeconds);
        Assert.Equal(12, reloaded.Options.ActionPacingTaskMaxSeconds);
    }

    [Fact]
    public void Load_NormalizesInvalidDomainRangesWithoutWpf()
    {
        var source = new JsonObject
        {
            [BotOptionPayloadKeys.SmartSleepFallbackMinMinutes] = 60,
            [BotOptionPayloadKeys.SmartSleepFallbackMaxMinutes] = 20,
            [BotOptionPayloadKeys.SessionPacingRunMinMinutes] = 90,
            [BotOptionPayloadKeys.SessionPacingRunMaxMinutes] = 30,
            [BotOptionPayloadKeys.SessionPacingSleepMinMinutes] = 120,
            [BotOptionPayloadKeys.SessionPacingSleepMaxMinutes] = 45,
            [BotOptionPayloadKeys.HeroCropAntiStarveTriggerMinutes] = 1440,
            [BotOptionPayloadKeys.HeroCropAntiStarveTargetMinutes] = 10,
        };

        var settings = SettingsConfigurationAdapter.Load(source);

        Assert.Equal(60, settings.SessionPacing.SmartSleepFallbackMaxMinutes);
        Assert.Equal(90, settings.SessionPacing.SessionRunMaxMinutes);
        Assert.Equal(120, settings.SessionPacing.SessionSleepMaxMinutes);
        Assert.Equal(1439, settings.Options.HeroCropAntiStarveTriggerMinutes);
        Assert.Equal(1440, settings.Options.HeroCropAntiStarveTargetMinutes);
    }

    [Fact]
    public void Apply_DerivesHumanizeVersionFromAuthoritativeSource()
    {
        var source = new JsonObject
        {
            [BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled] = false,
            [BotOptionPayloadKeys.ConstructionHumanizeStateVersion] = 4,
        };
        var current = SettingsConfigurationAdapter.Load(source);
        var edited = current with
        {
            Options = current.Options with
            {
                ConstructionHumanizeDelayEnabled = true,
                ConstructionHumanizeStateVersion = 999,
            },
        };

        var draft = SettingsConfigurationProjection.Apply(source, edited);

        Assert.Equal(5, draft[BotOptionPayloadKeys.ConstructionHumanizeStateVersion]!.GetValue<int>());
    }

    [Fact]
    public void Apply_RoundTripsTypedCollectionsAndFeatureSelections()
    {
        var current = SettingsConfigurationAdapter.Load([]);
        var edited = current with
        {
            SessionPacing = current.SessionPacing with
            {
                SmartSleepDeadlineGroups = ["farming", "construction"],
                SessionAllowedHours = [3, 7, 21],
            },
            Options = current.Options with
            {
                PostLoginAnalyzeHero = true,
                VillageStatusSweepDorf2Enabled = false,
                VillageStatusSweepSmithyEnabled = true,
                TownHallCelebrationCount = 2,
            },
        };

        var reloaded = SettingsConfigurationAdapter.Load(
            SettingsConfigurationAdapter.BuildDraft([], edited));

        Assert.Equal(["farming", "construction"], reloaded.SessionPacing.SmartSleepDeadlineGroups);
        Assert.Equal([3, 7, 21], reloaded.SessionPacing.SessionAllowedHours);
        Assert.True(reloaded.Options.PostLoginAnalyzeHero);
        Assert.False(reloaded.Options.VillageStatusSweepSmithyEnabled);
        Assert.Equal(2, reloaded.Options.TownHallCelebrationCount);
    }

    [Fact]
    public void ApplyTroopTrainingPanel_WritesDomainSettingsWithoutWpfKeys()
    {
        var source = new JsonObject { ["future_setting"] = "keep" };
        var settings = new TroopTrainingPanelSettingsConfiguration(
            new NpcTradeOptions(
                Enabled: true,
                ConstructionEnabled: false,
                ThresholdPercent: 150,
                AnalyzeWood: true,
                AnalyzeClay: false,
                AnalyzeIron: true,
                AnalyzeCrop: false,
                BuildTimeLimitEnabled: true,
                BuildTimeLimitSeconds: 17),
            BreweryAutoCelebrationEnabled: true,
            AllowGoldSpending: true,
            GoldLimit: -5);

        var draft = SettingsConfigurationProjection.ApplyTroopTrainingPanel(source, settings);

        Assert.Equal("keep", draft["future_setting"]!.GetValue<string>());
        Assert.True(draft[BotOptionPayloadKeys.NpcTradeEnabled]!.GetValue<bool>());
        Assert.Equal(100, draft[BotOptionPayloadKeys.NpcTradeThresholdPercent]!.GetValue<int>());
        Assert.Equal(60, draft[BotOptionPayloadKeys.NpcTradeBuildTimeLimitSeconds]!.GetValue<int>());
        Assert.True(draft[BotOptionPayloadKeys.BreweryAutoCelebrationEnabled]!.GetValue<bool>());
        Assert.True(draft[BotOptionPayloadKeys.AllowGoldSpending]!.GetValue<bool>());
        Assert.Equal(0, draft[BotOptionPayloadKeys.GoldLimit]!.GetValue<int>());
    }
}
