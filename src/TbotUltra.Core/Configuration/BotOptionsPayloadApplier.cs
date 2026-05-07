namespace TbotUltra.Core.Configuration;

public static class BotOptionsPayloadApplier
{
    public static BotOptions Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var targetVillageName = source.TargetVillageName;
        var targetVillageUrl = source.TargetVillageUrl;
        var captchaAutoSolveEnabled = source.CaptchaAutoSolveEnabled;
        var captchaSolverTimeoutSeconds = source.CaptchaSolverTimeoutSeconds;
        var captchaSolverMaxAttempts = source.CaptchaSolverMaxAttempts;
        var resourceUpgradeSlotId = source.ResourceUpgradeSlotId;
        var resourceUpgradeTargetLevel = source.ResourceUpgradeTargetLevel;
        var resourceUpgradeMaxAttempts = source.ResourceUpgradeMaxAttempts;
        var buildingUpgradeSlotId = source.BuildingUpgradeSlotId;
        var buildingUpgradeTargetLevel = source.BuildingUpgradeTargetLevel;
        var buildingUpgradeMaxAttempts = source.BuildingUpgradeMaxAttempts;
        var buildingConstructSlotId = source.BuildingConstructSlotId;
        var buildingConstructGid = source.BuildingConstructGid;
        var buildingConstructName = source.BuildingConstructName;
        var targetBuildingSlotOrName = source.TargetBuildingSlotOrName;
        var targetLevel = source.TargetLevel;
        var heroMinHpForAdventure = source.HeroMinHpForAdventure;
        var heroAutoRevive = source.HeroAutoRevive;
        var heroAutoAssignPoints = source.HeroAutoAssignPoints;
        var heroStatPriority = source.HeroStatPriority;
        var heroAdventurePickOrder = source.HeroAdventurePickOrder;
        var heroHideMode = source.HeroHideMode;
        var upgradeSelectorProfile = source.UpgradeSelectorProfile;
        var natarVillageSelection = source.NatarVillageSelection;
        var continuousFarmListNames = source.ContinuousFarmListNames;
        var continuousFarmDispatchDelayMinutes = source.ContinuousFarmDispatchDelayMinutes;
        var queueWaitThresholdMode = source.QueueWaitThresholdMode;
        var postLoginAnalyzeFarmlists = source.PostLoginAnalyzeFarmlists;
        var postLoginAnalyzeHero = source.PostLoginAnalyzeHero;
        var postLoginReadTroopTrainingQueue = source.PostLoginReadTroopTrainingQueue;

        if (payload is not null)
        {
            foreach (var pair in payload)
            {
                var key = pair.Key.Trim();
                var value = pair.Value.Trim();
                if (key.Length == 0 || value.Length == 0)
                {
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TargetVillageName, StringComparison.OrdinalIgnoreCase))
                {
                    targetVillageName = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TargetVillageUrl, StringComparison.OrdinalIgnoreCase))
                {
                    targetVillageUrl = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.CaptchaAutoSolveEnabled, StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out var autoSolve))
                {
                    captchaAutoSolveEnabled = autoSolve;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.CaptchaSolverTimeoutSeconds, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var timeoutSeconds))
                {
                    captchaSolverTimeoutSeconds = timeoutSeconds;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.CaptchaSolverMaxAttempts, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var maxAttempts))
                {
                    captchaSolverMaxAttempts = maxAttempts;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceUpgradeSlotId, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var resourceSlot))
                {
                    resourceUpgradeSlotId = resourceSlot;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceUpgradeTargetLevel, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var resourceTarget))
                {
                    resourceUpgradeTargetLevel = resourceTarget;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceUpgradeMaxAttempts, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var resourceAttempts))
                {
                    resourceUpgradeMaxAttempts = resourceAttempts;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.BuildingUpgradeSlotId, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var buildingSlot))
                {
                    buildingUpgradeSlotId = buildingSlot;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.BuildingUpgradeTargetLevel, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var buildingTarget))
                {
                    buildingUpgradeTargetLevel = buildingTarget;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.BuildingUpgradeMaxAttempts, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var buildingAttempts))
                {
                    buildingUpgradeMaxAttempts = buildingAttempts;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.BuildingConstructSlotId, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var constructSlot))
                {
                    buildingConstructSlotId = constructSlot;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.BuildingConstructGid, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var constructGid))
                {
                    buildingConstructGid = constructGid;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.BuildingConstructName, StringComparison.OrdinalIgnoreCase))
                {
                    buildingConstructName = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TargetBuildingSlotOrName, StringComparison.OrdinalIgnoreCase))
                {
                    targetBuildingSlotOrName = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TargetLevel, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var demolishTargetLevel))
                {
                    targetLevel = demolishTargetLevel;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.HeroMinHpForAdventure, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var minHp))
                {
                    heroMinHpForAdventure = minHp;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.HeroAutoRevive, StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out var autoRevive))
                {
                    heroAutoRevive = autoRevive;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.HeroAutoAssignPoints, StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out var autoAssignPoints))
                {
                    heroAutoAssignPoints = autoAssignPoints;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.HeroStatPriority, StringComparison.OrdinalIgnoreCase))
                {
                    heroStatPriority = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.HeroAdventurePickOrder, StringComparison.OrdinalIgnoreCase))
                {
                    heroAdventurePickOrder = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.HeroHideMode, StringComparison.OrdinalIgnoreCase))
                {
                    var normalized = value.Equals("fight", StringComparison.OrdinalIgnoreCase) ? "fight" : "hide";
                    heroHideMode = normalized;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.UpgradeSelectorProfile, StringComparison.OrdinalIgnoreCase))
                {
                    upgradeSelectorProfile = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ContinuousFarmListNames, StringComparison.OrdinalIgnoreCase))
                {
                    continuousFarmListNames = value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var dispatchDelayMinutes))
                {
                    continuousFarmDispatchDelayMinutes = Math.Clamp(dispatchDelayMinutes, 1, 5);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.QueueWaitThresholdMode, StringComparison.OrdinalIgnoreCase))
                {
                    queueWaitThresholdMode = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.PostLoginAnalyzeFarmlists, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var analyzeFarmlists))
                {
                    postLoginAnalyzeFarmlists = analyzeFarmlists;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.PostLoginAnalyzeHero, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var analyzeHero))
                {
                    postLoginAnalyzeHero = analyzeHero;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var readTroopTrainingQueue))
                {
                    postLoginReadTroopTrainingQueue = readTroopTrainingQueue;
                }
            }
        }

        return new BotOptions
        {
            ServerName = source.ServerName,
            BaseUrl = source.BaseUrl,
            LoginPath = source.LoginPath,
            VillageOverviewPath = source.VillageOverviewPath,
            Headless = source.Headless,
            TimeoutMs = source.TimeoutMs,
            ManualLoginTimeoutSeconds = source.ManualLoginTimeoutSeconds,
            CaptchaAutoSolveEnabled = captchaAutoSolveEnabled,
            CaptchaSolverTimeoutSeconds = captchaSolverTimeoutSeconds,
            CaptchaSolverMaxAttempts = captchaSolverMaxAttempts,
            LoopIntervalSeconds = source.LoopIntervalSeconds,
            LoopTasks = source.LoopTasks,
            ContinuousLoopGroups = source.ContinuousLoopGroups,
            ContinuousFarmListNames = continuousFarmListNames,
            ContinuousFarmDispatchDelayMinutes = continuousFarmDispatchDelayMinutes,
            QueueWaitThresholdMode = queueWaitThresholdMode,
            PostLoginAnalyzeFarmlists = postLoginAnalyzeFarmlists,
            PostLoginAnalyzeHero = postLoginAnalyzeHero,
            PostLoginReadTroopTrainingQueue = postLoginReadTroopTrainingQueue,
            GithubReleasesUrl = source.GithubReleasesUrl,
            HumanLikeEnabled = source.HumanLikeEnabled,
            HumanLikeSpeed = source.HumanLikeSpeed,
            TargetVillageName = targetVillageName,
            TargetVillageUrl = targetVillageUrl,
            AllowGoldSpending = source.AllowGoldSpending,
            AllowSilverSpending = source.AllowSilverSpending,
            GoldLimit = source.GoldLimit,
            SilverLimit = source.SilverLimit,
            ResourceUpgradeSlotId = resourceUpgradeSlotId,
            ResourceUpgradeTargetLevel = resourceUpgradeTargetLevel,
            ResourceUpgradeMaxAttempts = resourceUpgradeMaxAttempts,
            BuildingUpgradeSlotId = buildingUpgradeSlotId,
            BuildingUpgradeTargetLevel = buildingUpgradeTargetLevel,
            BuildingUpgradeMaxAttempts = buildingUpgradeMaxAttempts,
            BuildingConstructSlotId = buildingConstructSlotId,
            BuildingConstructGid = buildingConstructGid,
            BuildingConstructName = buildingConstructName,
            TargetBuildingSlotOrName = targetBuildingSlotOrName,
            TargetLevel = targetLevel,
            HeroMinHpForAdventure = heroMinHpForAdventure,
            HeroAutoRevive = heroAutoRevive,
            HeroAutoAssignPoints = heroAutoAssignPoints,
            HeroStatPriority = heroStatPriority,
            HeroAdventurePickOrder = heroAdventurePickOrder,
            HeroHideMode = heroHideMode,
            UpgradeSelectorProfile = upgradeSelectorProfile,
            NatarVillageSelection = natarVillageSelection,
        };
    }
}
