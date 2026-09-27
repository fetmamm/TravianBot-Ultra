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
                ConstructionHumanizeQueuePercentMin = 80,
                ConstructionHumanizeQueuePercentMax = 20,
                FarmListLastSentLimitHours = int.MaxValue,
                HeroCropAntiStarveTriggerMinutes = 0,
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
        Assert.Equal(FarmingDefaults.MaxLastSentLimitHours, reloaded.Options.FarmListLastSentLimitHours);
        Assert.Equal(1, reloaded.Options.HeroCropAntiStarveTriggerMinutes);
        Assert.Equal(12, reloaded.Options.ActionPacingTaskMinSeconds);
        Assert.Equal(12, reloaded.Options.ActionPacingTaskMaxSeconds);
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
}
