namespace TbotUltra.Core.Configuration;

public static class BotOptionsPayloadApplier
{
    public static BotOptions Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var targetVillageName = source.TargetVillageName;
        var targetVillageUrl = source.TargetVillageUrl;
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
        var heroStatPriority = source.HeroStatPriority;
        var upgradeSelectorProfile = source.UpgradeSelectorProfile;

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

                if (key.Equals(BotOptionPayloadKeys.HeroStatPriority, StringComparison.OrdinalIgnoreCase))
                {
                    heroStatPriority = value;
                    continue;
                }

                if (key.Equals(BotOptionPayloadKeys.UpgradeSelectorProfile, StringComparison.OrdinalIgnoreCase))
                {
                    upgradeSelectorProfile = value;
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
            LoopIntervalSeconds = source.LoopIntervalSeconds,
            LoopTasks = source.LoopTasks,
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
            HeroStatPriority = heroStatPriority,
            UpgradeSelectorProfile = upgradeSelectorProfile,
        };
    }
}
