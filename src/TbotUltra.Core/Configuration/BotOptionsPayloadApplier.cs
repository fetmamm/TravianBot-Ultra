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
        var resourceBuildStrategy = source.ResourceBuildStrategy;
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
        var heroAutoUseOintments = source.HeroAutoUseOintments;
        var heroStatPriority = source.HeroStatPriority;
        var heroAdventurePickOrder = source.HeroAdventurePickOrder;
        var heroHideMode = source.HeroHideMode;
        var heroContinuousAdventures = source.HeroContinuousAdventures;
        var upgradeSelectorProfile = source.UpgradeSelectorProfile;
        var natarVillageSelection = source.NatarVillageSelection;
        var continuousFarmListNames = source.ContinuousFarmListNames;
        var continuousFarmDispatchDelayMinutes = source.ContinuousFarmDispatchDelayMinutes;
        var queueWaitThresholdMode = source.QueueWaitThresholdMode;
        var postLoginAnalyzeFarmlists = source.PostLoginAnalyzeFarmlists;
        var postLoginAnalyzeHero = source.PostLoginAnalyzeHero;
        var postLoginReadTroopTrainingQueue = source.PostLoginReadTroopTrainingQueue;
        var postLoginAnalyzeBrewery = source.PostLoginAnalyzeBrewery;
        var troopTrainingBarracksEnabled = source.TroopTrainingBarracksEnabled;
        var troopTrainingBarracksTroopType = source.TroopTrainingBarracksTroopType;
        var troopTrainingBarracksMaxQueueHours = source.TroopTrainingBarracksMaxQueueHours;
        var troopTrainingBarracksAmountMode = source.TroopTrainingBarracksAmountMode;
        var troopTrainingBarracksKeepResourcesPercent = source.TroopTrainingBarracksKeepResourcesPercent;
        var troopTrainingBarracksRunMode = source.TroopTrainingBarracksRunMode;
        var troopTrainingBarracksMinimumTroops = source.TroopTrainingBarracksMinimumTroops;
        var troopTrainingBarracksMinimumResourcesPercent = source.TroopTrainingBarracksMinimumResourcesPercent;
        var troopTrainingBarracksCheckWood = source.TroopTrainingBarracksCheckWood;
        var troopTrainingBarracksCheckClay = source.TroopTrainingBarracksCheckClay;
        var troopTrainingBarracksCheckIron = source.TroopTrainingBarracksCheckIron;
        var troopTrainingBarracksCheckCrop = source.TroopTrainingBarracksCheckCrop;
        var troopTrainingStableEnabled = source.TroopTrainingStableEnabled;
        var troopTrainingStableTroopType = source.TroopTrainingStableTroopType;
        var troopTrainingStableMaxQueueHours = source.TroopTrainingStableMaxQueueHours;
        var troopTrainingStableAmountMode = source.TroopTrainingStableAmountMode;
        var troopTrainingStableKeepResourcesPercent = source.TroopTrainingStableKeepResourcesPercent;
        var troopTrainingStableRunMode = source.TroopTrainingStableRunMode;
        var troopTrainingStableMinimumTroops = source.TroopTrainingStableMinimumTroops;
        var troopTrainingStableMinimumResourcesPercent = source.TroopTrainingStableMinimumResourcesPercent;
        var troopTrainingStableCheckWood = source.TroopTrainingStableCheckWood;
        var troopTrainingStableCheckClay = source.TroopTrainingStableCheckClay;
        var troopTrainingStableCheckIron = source.TroopTrainingStableCheckIron;
        var troopTrainingStableCheckCrop = source.TroopTrainingStableCheckCrop;
        var troopTrainingWorkshopEnabled = source.TroopTrainingWorkshopEnabled;
        var troopTrainingWorkshopTroopType = source.TroopTrainingWorkshopTroopType;
        var troopTrainingWorkshopMaxQueueHours = source.TroopTrainingWorkshopMaxQueueHours;
        var troopTrainingWorkshopAmountMode = source.TroopTrainingWorkshopAmountMode;
        var troopTrainingWorkshopKeepResourcesPercent = source.TroopTrainingWorkshopKeepResourcesPercent;
        var troopTrainingWorkshopRunMode = source.TroopTrainingWorkshopRunMode;
        var troopTrainingWorkshopMinimumTroops = source.TroopTrainingWorkshopMinimumTroops;
        var troopTrainingWorkshopMinimumResourcesPercent = source.TroopTrainingWorkshopMinimumResourcesPercent;
        var troopTrainingWorkshopCheckWood = source.TroopTrainingWorkshopCheckWood;
        var troopTrainingWorkshopCheckClay = source.TroopTrainingWorkshopCheckClay;
        var troopTrainingWorkshopCheckIron = source.TroopTrainingWorkshopCheckIron;
        var troopTrainingWorkshopCheckCrop = source.TroopTrainingWorkshopCheckCrop;
        var troopTrainingFallbackCooldownSeconds = source.TroopTrainingFallbackCooldownSeconds;
        var breweryAutoCelebrationEnabled = source.BreweryAutoCelebrationEnabled;
        var npcTradeEnabled = source.NpcTradeEnabled;
        var npcTradeConstructionEnabled = source.NpcTradeConstructionEnabled;
        var npcTradeThresholdPercent = source.NpcTradeThresholdPercent;
        var npcTradeAnalyzeWood = source.NpcTradeAnalyzeWood;
        var npcTradeAnalyzeClay = source.NpcTradeAnalyzeClay;
        var npcTradeAnalyzeIron = source.NpcTradeAnalyzeIron;
        var npcTradeAnalyzeCrop = source.NpcTradeAnalyzeCrop;
        var npcTradeBuildTimeLimitEnabled = source.NpcTradeBuildTimeLimitEnabled;
        var npcTradeBuildTimeLimitSeconds = source.NpcTradeBuildTimeLimitSeconds;
        var resourceTransferEnabled = source.ResourceTransferEnabled;
        var resourceTransferTargetVillageName = source.ResourceTransferTargetVillageName;
        var resourceTransferSourceVillageNames = source.ResourceTransferSourceVillageNames;
        var resourceTransferSourceThresholdPercent = source.ResourceTransferSourceThresholdPercent;
        var resourceTransferSourceKeepPercent = source.ResourceTransferSourceKeepPercent;
        var resourceTransferTargetFillPercent = source.ResourceTransferTargetFillPercent;
        var resourceTransferSendWood = source.ResourceTransferSendWood;
        var resourceTransferSendClay = source.ResourceTransferSendClay;
        var resourceTransferSendIron = source.ResourceTransferSendIron;
        var resourceTransferSendCrop = source.ResourceTransferSendCrop;
        var reinforcementsEnabled = source.ReinforcementsEnabled;
        var reinforcementsTargetVillageName = source.ReinforcementsTargetVillageName;
        var reinforcementsSourceVillageNames = source.ReinforcementsSourceVillageNames;
        var reinforcementsTroopRules = source.ReinforcementsTroopRules;

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

                if (key.Equals(BotOptionPayloadKeys.ResourceBuildStrategy, StringComparison.OrdinalIgnoreCase))
                {
                    resourceBuildStrategy = value.Equals("smart", StringComparison.OrdinalIgnoreCase) ? "smart" : "lowest_first";
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

                if (key.Equals(BotOptionPayloadKeys.HeroAutoUseOintments, StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out var autoUseOintments))
                {
                    heroAutoUseOintments = autoUseOintments;
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

                if (key.Equals(BotOptionPayloadKeys.HeroContinuousAdventures, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var continuousAdventures))
                {
                    heroContinuousAdventures = continuousAdventures;
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

                if (key.Equals(BotOptionPayloadKeys.PostLoginAnalyzeBrewery, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var analyzeBrewery))
                {
                    postLoginAnalyzeBrewery = analyzeBrewery;
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
                    troopTrainingBarracksMinimumResourcesPercent = Math.Clamp(barracksMinimumResourcesPercent, 0, 100);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksCheckWood, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var barracksCheckWood))
                {
                    troopTrainingBarracksCheckWood = barracksCheckWood;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksCheckClay, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var barracksCheckClay))
                {
                    troopTrainingBarracksCheckClay = barracksCheckClay;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksCheckIron, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var barracksCheckIron))
                {
                    troopTrainingBarracksCheckIron = barracksCheckIron;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var barracksCheckCrop))
                {
                    troopTrainingBarracksCheckCrop = barracksCheckCrop;
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
                    troopTrainingStableMinimumResourcesPercent = Math.Clamp(stableMinimumResourcesPercent, 0, 100);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableCheckWood, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var stableCheckWood))
                {
                    troopTrainingStableCheckWood = stableCheckWood;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableCheckClay, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var stableCheckClay))
                {
                    troopTrainingStableCheckClay = stableCheckClay;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableCheckIron, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var stableCheckIron))
                {
                    troopTrainingStableCheckIron = stableCheckIron;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingStableCheckCrop, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var stableCheckCrop))
                {
                    troopTrainingStableCheckCrop = stableCheckCrop;
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
                    troopTrainingWorkshopMinimumResourcesPercent = Math.Clamp(workshopMinimumResourcesPercent, 0, 100);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var workshopCheckWood))
                {
                    troopTrainingWorkshopCheckWood = workshopCheckWood;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var workshopCheckClay))
                {
                    troopTrainingWorkshopCheckClay = workshopCheckClay;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var workshopCheckIron))
                {
                    troopTrainingWorkshopCheckIron = workshopCheckIron;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var workshopCheckCrop))
                {
                    troopTrainingWorkshopCheckCrop = workshopCheckCrop;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var troopTrainingFallbackCooldown))
                {
                    troopTrainingFallbackCooldownSeconds = troopTrainingFallbackCooldown switch
                    {
                        10 or 30 or 60 or 120 or 300 or 600 => troopTrainingFallbackCooldown,
                        _ => 30,
                    };
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.BreweryAutoCelebrationEnabled, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var autoCelebrationEnabled))
                {
                    breweryAutoCelebrationEnabled = autoCelebrationEnabled;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.NpcTradeEnabled, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var npcEnabled))
                {
                    npcTradeEnabled = npcEnabled;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.NpcTradeConstructionEnabled, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var npcConstructionEnabled))
                {
                    npcTradeConstructionEnabled = npcConstructionEnabled;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.NpcTradeThresholdPercent, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var npcThreshold))
                {
                    npcTradeThresholdPercent = Math.Clamp(npcThreshold, 1, 100);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.NpcTradeAnalyzeWood, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var npcWood))
                {
                    npcTradeAnalyzeWood = npcWood;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.NpcTradeAnalyzeClay, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var npcClay))
                {
                    npcTradeAnalyzeClay = npcClay;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.NpcTradeAnalyzeIron, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var npcIron))
                {
                    npcTradeAnalyzeIron = npcIron;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.NpcTradeAnalyzeCrop, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var npcCrop))
                {
                    npcTradeAnalyzeCrop = npcCrop;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.NpcTradeBuildTimeLimitEnabled, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var npcBuildTimeLimitEnabled))
                {
                    npcTradeBuildTimeLimitEnabled = npcBuildTimeLimitEnabled;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.NpcTradeBuildTimeLimitSeconds, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var npcBuildTimeLimitSeconds))
                {
                    npcTradeBuildTimeLimitSeconds = npcBuildTimeLimitSeconds switch
                    {
                        30 or 60 or 300 or 1200 or 3600 => npcBuildTimeLimitSeconds,
                        _ => 60,
                    };
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceTransferEnabled, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var transferEnabled))
                {
                    resourceTransferEnabled = transferEnabled;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceTransferTargetVillageName, StringComparison.OrdinalIgnoreCase))
                {
                    resourceTransferTargetVillageName = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceTransferSourceVillageNames, StringComparison.OrdinalIgnoreCase))
                {
                    resourceTransferSourceVillageNames = value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var transferSourceThreshold))
                {
                    resourceTransferSourceThresholdPercent = Math.Clamp(transferSourceThreshold, 0, 100);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceTransferSourceKeepPercent, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var transferSourceKeep))
                {
                    resourceTransferSourceKeepPercent = Math.Clamp(transferSourceKeep, 0, 99);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceTransferTargetFillPercent, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var transferTargetFill))
                {
                    resourceTransferTargetFillPercent = Math.Clamp(transferTargetFill, 0, 100);
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceTransferSendWood, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var transferWood))
                {
                    resourceTransferSendWood = transferWood;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceTransferSendClay, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var transferClay))
                {
                    resourceTransferSendClay = transferClay;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceTransferSendIron, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var transferIron))
                {
                    resourceTransferSendIron = transferIron;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ResourceTransferSendCrop, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var transferCrop))
                {
                    resourceTransferSendCrop = transferCrop;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ReinforcementsEnabled, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var reinforcementsEnabledValue))
                {
                    reinforcementsEnabled = reinforcementsEnabledValue;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ReinforcementsTargetVillageName, StringComparison.OrdinalIgnoreCase))
                {
                    reinforcementsTargetVillageName = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ReinforcementsSourceVillageNames, StringComparison.OrdinalIgnoreCase))
                {
                    reinforcementsSourceVillageNames = value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.ReinforcementsTroopRules, StringComparison.OrdinalIgnoreCase))
                {
                    reinforcementsTroopRules = ParseReinforcementTroopRules(value);
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
            PostLoginAnalyzeBrewery = postLoginAnalyzeBrewery,
            TroopTrainingBarracksEnabled = troopTrainingBarracksEnabled,
            TroopTrainingBarracksTroopType = troopTrainingBarracksTroopType,
            TroopTrainingBarracksMaxQueueHours = troopTrainingBarracksMaxQueueHours,
            TroopTrainingBarracksAmountMode = troopTrainingBarracksAmountMode,
            TroopTrainingBarracksKeepResourcesPercent = troopTrainingBarracksKeepResourcesPercent,
            TroopTrainingBarracksRunMode = troopTrainingBarracksRunMode,
            TroopTrainingBarracksMinimumTroops = troopTrainingBarracksMinimumTroops,
            TroopTrainingBarracksMinimumResourcesPercent = troopTrainingBarracksMinimumResourcesPercent,
            TroopTrainingBarracksCheckWood = troopTrainingBarracksCheckWood,
            TroopTrainingBarracksCheckClay = troopTrainingBarracksCheckClay,
            TroopTrainingBarracksCheckIron = troopTrainingBarracksCheckIron,
            TroopTrainingBarracksCheckCrop = troopTrainingBarracksCheckCrop,
            TroopTrainingStableEnabled = troopTrainingStableEnabled,
            TroopTrainingStableTroopType = troopTrainingStableTroopType,
            TroopTrainingStableMaxQueueHours = troopTrainingStableMaxQueueHours,
            TroopTrainingStableAmountMode = troopTrainingStableAmountMode,
            TroopTrainingStableKeepResourcesPercent = troopTrainingStableKeepResourcesPercent,
            TroopTrainingStableRunMode = troopTrainingStableRunMode,
            TroopTrainingStableMinimumTroops = troopTrainingStableMinimumTroops,
            TroopTrainingStableMinimumResourcesPercent = troopTrainingStableMinimumResourcesPercent,
            TroopTrainingStableCheckWood = troopTrainingStableCheckWood,
            TroopTrainingStableCheckClay = troopTrainingStableCheckClay,
            TroopTrainingStableCheckIron = troopTrainingStableCheckIron,
            TroopTrainingStableCheckCrop = troopTrainingStableCheckCrop,
            TroopTrainingWorkshopEnabled = troopTrainingWorkshopEnabled,
            TroopTrainingWorkshopTroopType = troopTrainingWorkshopTroopType,
            TroopTrainingWorkshopMaxQueueHours = troopTrainingWorkshopMaxQueueHours,
            TroopTrainingWorkshopAmountMode = troopTrainingWorkshopAmountMode,
            TroopTrainingWorkshopKeepResourcesPercent = troopTrainingWorkshopKeepResourcesPercent,
            TroopTrainingWorkshopRunMode = troopTrainingWorkshopRunMode,
            TroopTrainingWorkshopMinimumTroops = troopTrainingWorkshopMinimumTroops,
            TroopTrainingWorkshopMinimumResourcesPercent = troopTrainingWorkshopMinimumResourcesPercent,
            TroopTrainingWorkshopCheckWood = troopTrainingWorkshopCheckWood,
            TroopTrainingWorkshopCheckClay = troopTrainingWorkshopCheckClay,
            TroopTrainingWorkshopCheckIron = troopTrainingWorkshopCheckIron,
            TroopTrainingWorkshopCheckCrop = troopTrainingWorkshopCheckCrop,
            TroopTrainingFallbackCooldownSeconds = troopTrainingFallbackCooldownSeconds,
            BreweryAutoCelebrationEnabled = breweryAutoCelebrationEnabled,
            NpcTradeEnabled = npcTradeEnabled,
            NpcTradeConstructionEnabled = npcTradeConstructionEnabled,
            NpcTradeThresholdPercent = npcTradeThresholdPercent,
            NpcTradeAnalyzeWood = npcTradeAnalyzeWood,
            NpcTradeAnalyzeClay = npcTradeAnalyzeClay,
            NpcTradeAnalyzeIron = npcTradeAnalyzeIron,
            NpcTradeAnalyzeCrop = npcTradeAnalyzeCrop,
            NpcTradeBuildTimeLimitEnabled = npcTradeBuildTimeLimitEnabled,
            NpcTradeBuildTimeLimitSeconds = npcTradeBuildTimeLimitSeconds,
            ResourceTransferEnabled = resourceTransferEnabled,
            ResourceTransferTargetVillageName = resourceTransferTargetVillageName,
            ResourceTransferSourceVillageNames = resourceTransferSourceVillageNames,
            ResourceTransferSourceThresholdPercent = resourceTransferSourceThresholdPercent,
            ResourceTransferSourceKeepPercent = resourceTransferSourceKeepPercent,
            ResourceTransferTargetFillPercent = resourceTransferTargetFillPercent,
            ResourceTransferSendWood = resourceTransferSendWood,
            ResourceTransferSendClay = resourceTransferSendClay,
            ResourceTransferSendIron = resourceTransferSendIron,
            ResourceTransferSendCrop = resourceTransferSendCrop,
            ReinforcementsEnabled = reinforcementsEnabled,
            ReinforcementsTargetVillageName = reinforcementsTargetVillageName,
            ReinforcementsSourceVillageNames = reinforcementsSourceVillageNames,
            ReinforcementsTroopRules = reinforcementsTroopRules,
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
            ResourceBuildStrategy = resourceBuildStrategy,
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
            HeroAutoUseOintments = heroAutoUseOintments,
            HeroStatPriority = heroStatPriority,
            HeroAdventurePickOrder = heroAdventurePickOrder,
            HeroHideMode = heroHideMode,
            HeroContinuousAdventures = heroContinuousAdventures,
            UpgradeSelectorProfile = upgradeSelectorProfile,
            NatarVillageSelection = natarVillageSelection,
        };
    }

    private static List<ReinforcementTroopRule> ParseReinforcementTroopRules(string value)
    {
        try
        {
            var rules = System.Text.Json.JsonSerializer.Deserialize<List<ReinforcementTroopRule>>(
                value,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            return rules
                .Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.TroopType))
                .Select(rule => rule.Normalize())
                .GroupBy(rule => $"{rule.AccountName}\u001f{rule.SourceVillageName}\u001f{rule.TroopType}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
