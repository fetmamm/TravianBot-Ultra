using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services;

public sealed class BotConfigStore
{
    private static readonly HashSet<string> GlobalIdentityKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "server_name",
        "base_url",
    };

    private static readonly HashSet<string> AccountScopedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        BotOptionPayloadKeys.TargetVillageName,
        BotOptionPayloadKeys.TargetVillageUrl,
        BotOptionPayloadKeys.ResourceUpgradeSlotId,
        BotOptionPayloadKeys.ResourceUpgradeTargetLevel,
        BotOptionPayloadKeys.ResourceUpgradeMaxAttempts,
        BotOptionPayloadKeys.ResourceBuildStrategy,
        BotOptionPayloadKeys.BuildingUpgradeSlotId,
        BotOptionPayloadKeys.BuildingUpgradeTargetLevel,
        BotOptionPayloadKeys.BuildingUpgradeMaxAttempts,
        BotOptionPayloadKeys.BuildingConstructSlotId,
        BotOptionPayloadKeys.BuildingConstructGid,
        BotOptionPayloadKeys.BuildingConstructName,
        BotOptionPayloadKeys.TargetBuildingSlotOrName,
        BotOptionPayloadKeys.TargetLevel,
        BotOptionPayloadKeys.HeroMinHpForAdventure,
        BotOptionPayloadKeys.HeroHpRegenPerDayPercent,
        BotOptionPayloadKeys.HeroAutoRevive,
        BotOptionPayloadKeys.HeroAutoAssignPoints,
        BotOptionPayloadKeys.HeroAutoUseOintments,
        BotOptionPayloadKeys.HeroStatPriority,
        BotOptionPayloadKeys.HeroAdventurePickOrder,
        BotOptionPayloadKeys.HeroHideModeEnabled,
        BotOptionPayloadKeys.HeroHideMode,
        BotOptionPayloadKeys.HeroContinuousAdventures,
        BotOptionPayloadKeys.AutoCollectTasksEnabled,
        BotOptionPayloadKeys.ContinuousFarmListNames,
        BotOptionPayloadKeys.ContinuousFarmListIds,
        BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes,
        BotOptionPayloadKeys.QueueWaitThresholdMode,
        BotOptionPayloadKeys.PostLoginAnalyzeFarmlists,
        BotOptionPayloadKeys.PostLoginAnalyzeHero,
        BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue,
        BotOptionPayloadKeys.PostLoginAnalyzeBrewery,
        BotOptionPayloadKeys.TroopTrainingBarracksEnabled,
        BotOptionPayloadKeys.TroopTrainingBarracksTroopType,
        BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours,
        BotOptionPayloadKeys.TroopTrainingBarracksAmountMode,
        BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingBarracksRunMode,
        BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops,
        BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingBarracksCheckWood,
        BotOptionPayloadKeys.TroopTrainingBarracksCheckClay,
        BotOptionPayloadKeys.TroopTrainingBarracksCheckIron,
        BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop,
        BotOptionPayloadKeys.TroopTrainingStableEnabled,
        BotOptionPayloadKeys.TroopTrainingStableTroopType,
        BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours,
        BotOptionPayloadKeys.TroopTrainingStableAmountMode,
        BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingStableRunMode,
        BotOptionPayloadKeys.TroopTrainingStableMinimumTroops,
        BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingStableCheckWood,
        BotOptionPayloadKeys.TroopTrainingStableCheckClay,
        BotOptionPayloadKeys.TroopTrainingStableCheckIron,
        BotOptionPayloadKeys.TroopTrainingStableCheckCrop,
        BotOptionPayloadKeys.TroopTrainingWorkshopEnabled,
        BotOptionPayloadKeys.TroopTrainingWorkshopTroopType,
        BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours,
        BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode,
        BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingWorkshopRunMode,
        BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops,
        BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood,
        BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay,
        BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron,
        BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop,
        BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds,
        BotOptionPayloadKeys.BreweryAutoCelebrationEnabled,
        BotOptionPayloadKeys.NpcTradeEnabled,
        BotOptionPayloadKeys.NpcTradeConstructionEnabled,
        BotOptionPayloadKeys.NpcTradeThresholdPercent,
        BotOptionPayloadKeys.NpcTradeAnalyzeWood,
        BotOptionPayloadKeys.NpcTradeAnalyzeClay,
        BotOptionPayloadKeys.NpcTradeAnalyzeIron,
        BotOptionPayloadKeys.NpcTradeAnalyzeCrop,
        BotOptionPayloadKeys.NpcTradeBuildTimeLimitEnabled,
        BotOptionPayloadKeys.NpcTradeBuildTimeLimitSeconds,
        BotOptionPayloadKeys.ResourceTransferEnabled,
        BotOptionPayloadKeys.ResourceTransferTargetVillageName,
        BotOptionPayloadKeys.ResourceTransferSourceVillageNames,
        BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent,
        BotOptionPayloadKeys.ResourceTransferSourceKeepPercent,
        BotOptionPayloadKeys.ResourceTransferTargetFillPercent,
        BotOptionPayloadKeys.ResourceTransferSendWood,
        BotOptionPayloadKeys.ResourceTransferSendClay,
        BotOptionPayloadKeys.ResourceTransferSendIron,
        BotOptionPayloadKeys.ResourceTransferSendCrop,
        BotOptionPayloadKeys.ReinforcementsEnabled,
        BotOptionPayloadKeys.ReinforcementsTargetVillageName,
        BotOptionPayloadKeys.ReinforcementsSourceVillageNames,
        BotOptionPayloadKeys.ReinforcementsTroopRules,
        BotOptionPayloadKeys.UpgradeSelectorProfile,
        "loop_tasks",
        "continuous_loop_groups",
        "continuous_loop_group_order",
        "dashboard_visible_groups",
        "natar_village_selection",
        "addFarmsTroopCount",
    };

    private static readonly HashSet<string> DeprecatedTechnicalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "server_flavor",
        "login_path",
        "village_overview_path",
    };

    private readonly string _configPath;
    private readonly string? _projectRoot;
    private readonly Func<string>? _activeAccountNameProvider;

    public BotConfigStore(string configPath, string? projectRoot = null, Func<string>? activeAccountNameProvider = null)
    {
        _configPath = configPath;
        _projectRoot = projectRoot;
        _activeAccountNameProvider = activeAccountNameProvider;
    }

    public JsonObject Load()
    {
        var config = LoadGlobal();
        RemoveDeprecatedTechnicalKeys(config);

        var accountName = GetActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return config;
        }

        foreach (var pair in LoadAccountSettings(accountName))
        {
            config[pair.Key] = pair.Value?.DeepClone();
        }

        return config;
    }

    public JsonObject LoadGlobal()
    {
        if (!File.Exists(_configPath))
        {
            throw new InvalidOperationException($"Config file not found: {_configPath}");
        }

        var raw = File.ReadAllText(_configPath);
        var node = JsonNode.Parse(raw)?.AsObject();
        if (node is null)
        {
            throw new InvalidOperationException("Config file is invalid JSON.");
        }

        return node;
    }

    public void Save(JsonObject config)
    {
        RemoveDeprecatedTechnicalKeys(config);

        var accountName = GetActiveAccountName();
        if (!string.IsNullOrWhiteSpace(accountName))
        {
            SaveAccountScopedValues(accountName, config);
        }

        var globalConfig = config.DeepClone().AsObject();
        if (!string.IsNullOrWhiteSpace(accountName))
        {
            foreach (var key in AccountScopedKeys)
            {
                globalConfig.Remove(key);
            }
        }

        RemoveDeprecatedTechnicalKeys(globalConfig);
        SaveJson(_configPath, globalConfig);
    }

    public void ResetSettingsToDefaults()
    {
        var globalConfig = LoadGlobal();
        var resetGlobalConfig = new JsonObject();
        foreach (var key in GlobalIdentityKeys)
        {
            if (globalConfig.TryGetPropertyValue(key, out var value))
            {
                resetGlobalConfig[key] = value?.DeepClone();
            }
        }

        SaveJson(_configPath, resetGlobalConfig);
        ClearAccountSettingsFiles();
    }

    private void SaveAccountScopedValues(string accountName, JsonObject config)
    {
        var accountConfig = LoadAccountSettings(accountName);
        foreach (var key in AccountScopedKeys)
        {
            if (config.TryGetPropertyValue(key, out var value))
            {
                accountConfig[key] = value?.DeepClone();
            }
        }

        RemoveDeprecatedTechnicalKeys(accountConfig);
        var path = AccountStoragePaths.AccountSettingsPath(_projectRoot!, accountName);
        SaveJson(path, accountConfig);
    }

    private JsonObject LoadAccountSettings(string accountName)
    {
        if (string.IsNullOrWhiteSpace(_projectRoot))
        {
            return new JsonObject();
        }

        var path = AccountStoragePaths.AccountSettingsPath(_projectRoot, accountName);
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            var raw = File.ReadAllText(path);
            var node = JsonNode.Parse(raw)?.AsObject();
            if (node is null)
            {
                return new JsonObject();
            }

            RemoveDeprecatedTechnicalKeys(node);
            return node;
        }
        catch
        {
            return new JsonObject();
        }
    }

    private void ClearAccountSettingsFiles()
    {
        if (string.IsNullOrWhiteSpace(_projectRoot))
        {
            return;
        }

        var accountsPath = Path.Combine(_projectRoot, AccountStoragePaths.AccountsRelativeDirectory);
        if (!Directory.Exists(accountsPath))
        {
            return;
        }

        foreach (var settingsPath in Directory.EnumerateFiles(accountsPath, "settings.json", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(settingsPath);
            }
            catch
            {
                SaveJson(settingsPath, new JsonObject());
            }
        }
    }

    private string GetActiveAccountName()
    {
        if (string.IsNullOrWhiteSpace(_projectRoot) || _activeAccountNameProvider is null)
        {
            return string.Empty;
        }

        try
        {
            return _activeAccountNameProvider() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void RemoveDeprecatedTechnicalKeys(JsonObject config)
    {
        foreach (var key in DeprecatedTechnicalKeys)
        {
            config.Remove(key);
        }
    }

    private static void SaveJson(string path, JsonObject config)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, config.ToJsonString(options));
    }
}
