using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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

    internal static readonly string[] AccountScopedKeyValues =
    [
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
        BotOptionPayloadKeys.IncreaseAdventuresToHard,
        BotOptionPayloadKeys.ReduceAdventureTime,
        BotOptionPayloadKeys.AutoCollectTasksEnabled,
        BotOptionPayloadKeys.AutoCollectDailyQuestsEnabled,
        BotOptionPayloadKeys.CollectStepDelayMinMs,
        BotOptionPayloadKeys.CollectStepDelayMaxMs,
        BotOptionPayloadKeys.HeroResourceTransferEnabled,
        BotOptionPayloadKeys.HeroResourceMaxUseEnabled,
        BotOptionPayloadKeys.HeroResourceMaxUsePerResource,
        BotOptionPayloadKeys.HeroResourceUseConstruction,
        BotOptionPayloadKeys.HeroResourceUseSmithy,
        BotOptionPayloadKeys.HeroResourceUseBrewery,
        BotOptionPayloadKeys.HeroResourceUseTownHall,
        BotOptionPayloadKeys.ContinuousFarmListNames,
        BotOptionPayloadKeys.ContinuousFarmListIds,
        BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes,
        BotOptionPayloadKeys.ContinuousFarmDispatchDelayVariationPercent,
        BotOptionPayloadKeys.ContinuousFarmSendMode,
        BotOptionPayloadKeys.TownHallCelebrationMode,
        BotOptionPayloadKeys.ContinuousFarmDeactivateLosses,
        BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses,
        BotOptionPayloadKeys.QueueWaitThresholdMode,
        BotOptionPayloadKeys.PostLoginAnalyzeFarmlists,
        BotOptionPayloadKeys.PostLoginAnalyzeHero,
        BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory,
        BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue,
        BotOptionPayloadKeys.PostLoginAnalyzeBrewery,
        BotOptionPayloadKeys.PostLoginAnalyzeNewVillages,
        BotOptionPayloadKeys.SessionPacingEnabled,
        BotOptionPayloadKeys.SessionPacingMaxRunMinutes,
        BotOptionPayloadKeys.SessionPacingSleepMinutes,
        BotOptionPayloadKeys.SessionPacingVariationPercent,
        BotOptionPayloadKeys.SessionPacingAllowedHours,
        BotOptionPayloadKeys.SessionPacingDailyMaxHours,
        BotOptionPayloadKeys.SessionPacingRuntimeDate,
        BotOptionPayloadKeys.SessionPacingRuntimeSeconds,
        BotOptionPayloadKeys.SessionPacingDailyHistory,
        BotOptionPayloadKeys.ActionPacingEnabled,
        BotOptionPayloadKeys.ActionPacingTaskMinSeconds,
        BotOptionPayloadKeys.ActionPacingTaskMaxSeconds,
        BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds,
        BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds,
        BotOptionPayloadKeys.ActionPacingClickMinSeconds,
        BotOptionPayloadKeys.ActionPacingClickMaxSeconds,
        BotOptionPayloadKeys.ActionPacingLoopMinSeconds,
        BotOptionPayloadKeys.ActionPacingLoopMaxSeconds,
        BotOptionPayloadKeys.FarmListStepDelayMinSeconds,
        BotOptionPayloadKeys.FarmListStepDelayMaxSeconds,
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
        BotOptionPayloadKeys.AllowGoldSpending,
        BotOptionPayloadKeys.GoldLimit,
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
        BotOptionPayloadKeys.ReinforcementsSendIntervalHours,
        BotOptionPayloadKeys.ReinforcementsSendVariationPercent,
        BotOptionPayloadKeys.UpgradeSelectorProfile,
        BotOptionPayloadKeys.CaptchaAutoSolveEnabled,
        BotOptionPayloadKeys.CaptchaSolverTimeoutSeconds,
        BotOptionPayloadKeys.CaptchaSolverMaxAttempts,
        "loop_interval_seconds",
        "human_like_enabled",
        "human_like_speed",
        "allow_silver_spending",
        "silver_limit",
        "loop_tasks",
        "continuous_loop_groups",
        "continuous_loop_group_order",
        "dashboard_visible_groups",
        "natar_village_selection",
        "addFarmsTroopCount",
    ];

    private static readonly HashSet<string> AccountScopedKeys = new(AccountScopedKeyValues, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> DeprecatedTechnicalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "server_flavor",
        "login_path",
        "village_overview_path",
    };

    // Serializes all config file I/O. bot.json and the per-account settings.json are read and written
    // from many concurrent contexts (UI dispatcher, continuous loop, and several background Task.Run
    // refreshes). Unsynchronized File.ReadAllText/File.WriteAllText calls overlapped and produced
    // "The process cannot access the file ... because it is being used by another process".
    private static readonly object FileIoLock = new();

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
        => LoadForAccount(GetActiveAccountName());

    public JsonObject LoadForAccount(string accountName)
    {
        var config = LoadGlobal();
        RemoveDeprecatedTechnicalKeys(config);

        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(_projectRoot))
        {
            return config;
        }

        var accountConfig = LoadAccountSettings(accountName);
        MigrateLegacyAccountScopedValues(accountName, config, accountConfig);
        foreach (var pair in accountConfig)
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

        var raw = ReadAllTextShared(_configPath);
        var node = JsonNode.Parse(raw)?.AsObject();
        if (node is null)
        {
            throw new InvalidOperationException("Config file is invalid JSON.");
        }

        return node;
    }

    public void SaveGlobal(JsonObject config)
    {
        var globalConfig = config.DeepClone().AsObject();
        RemoveDeprecatedTechnicalKeys(globalConfig);
        foreach (var key in AccountScopedKeys)
        {
            globalConfig.Remove(key);
        }

        SaveJson(_configPath, globalConfig);
    }

    public void Save(JsonObject config)
        => SaveForAccount(GetActiveAccountName(), config);

    public void SaveForAccount(string accountName, JsonObject config)
    {
        RemoveDeprecatedTechnicalKeys(config);

        if (!string.IsNullOrWhiteSpace(accountName) && !string.IsNullOrWhiteSpace(_projectRoot))
        {
            SaveAccountScopedValues(accountName, config);
        }

        var globalConfig = config.DeepClone().AsObject();
        if (!string.IsNullOrWhiteSpace(accountName) && !string.IsNullOrWhiteSpace(_projectRoot))
        {
            foreach (var key in AccountScopedKeys)
            {
                globalConfig.Remove(key);
            }
        }

        RemoveDeprecatedTechnicalKeys(globalConfig);
        SaveJson(_configPath, globalConfig);
    }

    public void RemoveLegacyReinforcementRulesForAccount(string accountName)
    {
        var accountKey = AccountStoragePaths.NormalizeAccountKey(accountName);
        var globalConfig = LoadGlobal();
        if (globalConfig[BotOptionPayloadKeys.ReinforcementsTroopRules] is not JsonArray rules)
        {
            return;
        }

        var kept = rules
            .OfType<JsonObject>()
            .Where(rule =>
            {
                var ruleAccount = rule["accountName"]?.GetValue<string>() ?? string.Empty;
                return string.IsNullOrWhiteSpace(ruleAccount)
                    || !string.Equals(
                        AccountStoragePaths.NormalizeAccountKey(ruleAccount),
                        accountKey,
                        StringComparison.Ordinal);
            })
            .Select(rule => rule.DeepClone())
            .ToArray();
        if (kept.Length == rules.Count)
        {
            return;
        }

        globalConfig[BotOptionPayloadKeys.ReinforcementsTroopRules] = new JsonArray(kept);
        SaveJson(_configPath, globalConfig);
    }

    // Resets settings to defaults for the ACTIVE account only: global non-identity keys (shared program
    // settings) plus the active account's own settings file. Other accounts' settings are left untouched
    // so a reset on one account doesn't wipe the rest.
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
        ClearActiveAccountSettingsFile();
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

    private void MigrateLegacyAccountScopedValues(
        string accountName,
        JsonObject globalConfig,
        JsonObject accountConfig)
    {
        var globalChanged = false;
        var accountChanged = false;
        foreach (var key in AccountScopedKeys)
        {
            if (!globalConfig.TryGetPropertyValue(key, out var value))
            {
                continue;
            }

            if (string.Equals(key, BotOptionPayloadKeys.ReinforcementsTroopRules, StringComparison.OrdinalIgnoreCase)
                && value is JsonArray rules)
            {
                if (!accountConfig.ContainsKey(key))
                {
                    accountConfig[key] = CloneAccountScopedValueForAccount(key, value, accountName);
                    accountChanged = true;
                }

                MigrateLegacyReinforcementRulesForOtherAccounts(rules, accountName);
                globalConfig.Remove(key);
                globalChanged = true;
                continue;
            }

            if (!accountConfig.ContainsKey(key))
            {
                accountConfig[key] = CloneAccountScopedValueForAccount(key, value, accountName);
                accountChanged = true;
            }

            globalConfig.Remove(key);
            globalChanged = true;
        }

        if (accountChanged)
        {
            var accountPath = AccountStoragePaths.AccountSettingsPath(_projectRoot!, accountName);
            SaveJson(accountPath, accountConfig);
        }

        if (globalChanged)
        {
            SaveJson(_configPath, globalConfig);
        }
    }

    private static JsonNode? CloneAccountScopedValueForAccount(string key, JsonNode? value, string accountName)
    {
        if (!string.Equals(key, BotOptionPayloadKeys.ReinforcementsTroopRules, StringComparison.OrdinalIgnoreCase)
            || value is not JsonArray rules)
        {
            return value?.DeepClone();
        }

        var accountKey = AccountStoragePaths.NormalizeAccountKey(accountName);
        var matchingRules = rules
            .OfType<JsonObject>()
            .Where(rule =>
            {
                var ruleAccount = rule["accountName"]?.GetValue<string>() ?? string.Empty;
                return string.IsNullOrWhiteSpace(ruleAccount)
                    || string.Equals(
                        AccountStoragePaths.NormalizeAccountKey(ruleAccount),
                        accountKey,
                        StringComparison.Ordinal);
            })
            .Select(rule => rule.DeepClone())
            .ToArray();
        return new JsonArray(matchingRules);
    }

    private void MigrateLegacyReinforcementRulesForOtherAccounts(JsonArray rules, string activeAccountName)
    {
        var activeAccountKey = AccountStoragePaths.NormalizeAccountKey(activeAccountName);
        var accountGroups = rules
            .OfType<JsonObject>()
            .Select(rule => new
            {
                Rule = rule,
                AccountName = rule["accountName"]?.GetValue<string>()?.Trim() ?? string.Empty,
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.AccountName))
            .GroupBy(
                item => AccountStoragePaths.NormalizeAccountKey(item.AccountName),
                StringComparer.Ordinal)
            .Where(group => !string.Equals(group.Key, activeAccountKey, StringComparison.Ordinal));

        foreach (var group in accountGroups)
        {
            var targetAccountName = group.First().AccountName;
            var targetConfig = LoadAccountSettings(targetAccountName);
            if (targetConfig.ContainsKey(BotOptionPayloadKeys.ReinforcementsTroopRules))
            {
                continue;
            }

            var accountRules = group
                .Select(item => item.Rule.DeepClone())
                .ToArray();
            targetConfig[BotOptionPayloadKeys.ReinforcementsTroopRules] = new JsonArray(accountRules);
            var targetPath = AccountStoragePaths.AccountSettingsPath(_projectRoot!, targetAccountName);
            SaveJson(targetPath, targetConfig);
        }
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
            var raw = ReadAllTextShared(path);
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

    private void ClearActiveAccountSettingsFile()
    {
        if (string.IsNullOrWhiteSpace(_projectRoot))
        {
            return;
        }

        var accountName = GetActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        var path = AccountStoragePaths.AccountSettingsPath(_projectRoot, accountName);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            SaveJson(path, new JsonObject());
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
        WriteAllTextShared(path, config.ToJsonString(options));
    }

    private static string ReadAllTextShared(string path)
    {
        lock (FileIoLock)
        {
            return RetryFileIo(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            });
        }
    }

    private static void WriteAllTextShared(string path, string content)
    {
        // Lock serializes in-process writers; AtomicFile makes the write itself crash-safe so a
        // crash mid-write cannot leave bot.json as corrupt/partial JSON.
        lock (FileIoLock)
        {
            AtomicFile.WriteAllText(path, content);
        }
    }

    // Small retry for transient sharing violations (e.g. antivirus/indexer briefly holding the file).
    // The lock removes in-process contention; this covers the rare external locker.
    private static T RetryFileIo<T>(Func<T> action)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(40 * attempt);
            }
        }
    }
}
