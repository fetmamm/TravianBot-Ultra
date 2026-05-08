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
        var troopTrainingBarracksEnabled = source.TroopTrainingBarracksEnabled;
        var troopTrainingBarracksTroopType = source.TroopTrainingBarracksTroopType;
        var troopTrainingBarracksMaxQueueHours = source.TroopTrainingBarracksMaxQueueHours;
        var troopTrainingBarracksAmountMode = source.TroopTrainingBarracksAmountMode;
        var troopTrainingBarracksKeepResourcesPercent = source.TroopTrainingBarracksKeepResourcesPercent;
        var troopTrainingBarracksRunMode = source.TroopTrainingBarracksRunMode;
        var troopTrainingBarracksMinimumTroops = source.TroopTrainingBarracksMinimumTroops;
        var troopTrainingBarracksMinimumResourcesPercent = source.TroopTrainingBarracksMinimumResourcesPercent;
        var troopTrainingStableEnabled = source.TroopTrainingStableEnabled;
        var troopTrainingStableTroopType = source.TroopTrainingStableTroopType;
        var troopTrainingStableMaxQueueHours = source.TroopTrainingStableMaxQueueHours;
        var troopTrainingStableAmountMode = source.TroopTrainingStableAmountMode;
        var troopTrainingStableKeepResourcesPercent = source.TroopTrainingStableKeepResourcesPercent;
        var troopTrainingStableRunMode = source.TroopTrainingStableRunMode;
        var troopTrainingStableMinimumTroops = source.TroopTrainingStableMinimumTroops;
        var troopTrainingStableMinimumResourcesPercent = source.TroopTrainingStableMinimumResourcesPercent;
        var troopTrainingWorkshopEnabled = source.TroopTrainingWorkshopEnabled;
        var troopTrainingWorkshopTroopType = source.TroopTrainingWorkshopTroopType;
        var troopTrainingWorkshopMaxQueueHours = source.TroopTrainingWorkshopMaxQueueHours;
        var troopTrainingWorkshopAmountMode = source.TroopTrainingWorkshopAmountMode;
        var troopTrainingWorkshopKeepResourcesPercent = source.TroopTrainingWorkshopKeepResourcesPercent;
        var troopTrainingWorkshopRunMode = source.TroopTrainingWorkshopRunMode;
        var troopTrainingWorkshopMinimumTroops = source.TroopTrainingWorkshopMinimumTroops;
        var troopTrainingWorkshopMinimumResourcesPercent = source.TroopTrainingWorkshopMinimumResourcesPercent;

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
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksEnabled, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var barracksEnabled))
                {
                    troopTrainingBarracksEnabled = barracksEnabled;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksTroopType, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingBarracksTroopType = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingBarracksMaxQueueHours = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksAmountMode, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingBarracksAmountMode = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var barracksKeepPercent))
                {
                    troopTrainingBarracksKeepResourcesPercent = Math.Clamp(barracksKeepPercent, 0, 95);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksRunMode, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingBarracksRunMode = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var barracksMinimumTroops))
                {
                    troopTrainingBarracksMinimumTroops = Math.Max(1, barracksMinimumTroops);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var barracksMinimumResourcesPercent))
                {
                    troopTrainingBarracksMinimumResourcesPercent = Math.Clamp(barracksMinimumResourcesPercent, 1, 100);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableEnabled, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var stableEnabled))
                {
                    troopTrainingStableEnabled = stableEnabled;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableTroopType, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingStableTroopType = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingStableMaxQueueHours = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableAmountMode, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingStableAmountMode = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var stableKeepPercent))
                {
                    troopTrainingStableKeepResourcesPercent = Math.Clamp(stableKeepPercent, 0, 95);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableRunMode, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingStableRunMode = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableMinimumTroops, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var stableMinimumTroops))
                {
                    troopTrainingStableMinimumTroops = Math.Max(1, stableMinimumTroops);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var stableMinimumResourcesPercent))
                {
                    troopTrainingStableMinimumResourcesPercent = Math.Clamp(stableMinimumResourcesPercent, 1, 100);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopEnabled, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var workshopEnabled))
                {
                    troopTrainingWorkshopEnabled = workshopEnabled;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopTroopType, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingWorkshopTroopType = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingWorkshopMaxQueueHours = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingWorkshopAmountMode = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var workshopKeepPercent))
                {
                    troopTrainingWorkshopKeepResourcesPercent = Math.Clamp(workshopKeepPercent, 0, 95);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopRunMode, StringComparison.OrdinalIgnoreCase))
                {
                    troopTrainingWorkshopRunMode = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var workshopMinimumTroops))
                {
                    troopTrainingWorkshopMinimumTroops = Math.Max(1, workshopMinimumTroops);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var workshopMinimumResourcesPercent))
                {
                    troopTrainingWorkshopMinimumResourcesPercent = Math.Clamp(workshopMinimumResourcesPercent, 1, 100);
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
            TroopTrainingBarracksEnabled = troopTrainingBarracksEnabled,
            TroopTrainingBarracksTroopType = troopTrainingBarracksTroopType,
            TroopTrainingBarracksMaxQueueHours = troopTrainingBarracksMaxQueueHours,
            TroopTrainingBarracksAmountMode = troopTrainingBarracksAmountMode,
            TroopTrainingBarracksKeepResourcesPercent = troopTrainingBarracksKeepResourcesPercent,
            TroopTrainingBarracksRunMode = troopTrainingBarracksRunMode,
            TroopTrainingBarracksMinimumTroops = troopTrainingBarracksMinimumTroops,
            TroopTrainingBarracksMinimumResourcesPercent = troopTrainingBarracksMinimumResourcesPercent,
            TroopTrainingStableEnabled = troopTrainingStableEnabled,
            TroopTrainingStableTroopType = troopTrainingStableTroopType,
            TroopTrainingStableMaxQueueHours = troopTrainingStableMaxQueueHours,
            TroopTrainingStableAmountMode = troopTrainingStableAmountMode,
            TroopTrainingStableKeepResourcesPercent = troopTrainingStableKeepResourcesPercent,
            TroopTrainingStableRunMode = troopTrainingStableRunMode,
            TroopTrainingStableMinimumTroops = troopTrainingStableMinimumTroops,
            TroopTrainingStableMinimumResourcesPercent = troopTrainingStableMinimumResourcesPercent,
            TroopTrainingWorkshopEnabled = troopTrainingWorkshopEnabled,
            TroopTrainingWorkshopTroopType = troopTrainingWorkshopTroopType,
            TroopTrainingWorkshopMaxQueueHours = troopTrainingWorkshopMaxQueueHours,
            TroopTrainingWorkshopAmountMode = troopTrainingWorkshopAmountMode,
            TroopTrainingWorkshopKeepResourcesPercent = troopTrainingWorkshopKeepResourcesPercent,
            TroopTrainingWorkshopRunMode = troopTrainingWorkshopRunMode,
            TroopTrainingWorkshopMinimumTroops = troopTrainingWorkshopMinimumTroops,
            TroopTrainingWorkshopMinimumResourcesPercent = troopTrainingWorkshopMinimumResourcesPercent,
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
