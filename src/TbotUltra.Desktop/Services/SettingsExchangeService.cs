using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services;

internal sealed record SettingsImportSkipped(string Key, string Reason);

internal sealed record SettingsImportResult(
    JsonObject MergedConfig,
    IReadOnlyList<string> ChangedKeys,
    IReadOnlyList<string> ChangedCategories,
    IReadOnlyList<SettingsImportSkipped> SkippedSettings,
    int SchemaVersion,
    string AppVersion,
    DateTimeOffset ExportedAtUtc,
    bool EnablesGoldSpending,
    bool EnablesSilverSpending,
    bool EnablesRiskyDailyRuntime);

internal sealed class SettingsExchangeService
{
    public const int CurrentSchemaVersion = 1;
    public const string FileExtension = ".tbot-settings.json";
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly IReadOnlyDictionary<string, PortableSetting> PortableSettings = CreatePortableSettings();

    private static readonly (string MinKey, string MaxKey)[] OrderedRanges =
    [
        (BotOptionPayloadKeys.SessionPacingRunMinMinutes, BotOptionPayloadKeys.SessionPacingRunMaxMinutes),
        (BotOptionPayloadKeys.SessionPacingSleepMinMinutes, BotOptionPayloadKeys.SessionPacingSleepMaxMinutes),
        (BotOptionPayloadKeys.SmartSleepFallbackMinMinutes, BotOptionPayloadKeys.SmartSleepFallbackMaxMinutes),
        (BotOptionPayloadKeys.ActionPacingTaskMinSeconds, BotOptionPayloadKeys.ActionPacingTaskMaxSeconds),
        (BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds, BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds),
        (BotOptionPayloadKeys.ActionPacingClickMinSeconds, BotOptionPayloadKeys.ActionPacingClickMaxSeconds),
        (BotOptionPayloadKeys.ActionPacingLoopMinSeconds, BotOptionPayloadKeys.ActionPacingLoopMaxSeconds),
        (BotOptionPayloadKeys.ContinuousKeepAliveMinMinutes, BotOptionPayloadKeys.ContinuousKeepAliveMaxMinutes),
        (BotOptionPayloadKeys.FarmListStepDelayMinSeconds, BotOptionPayloadKeys.FarmListStepDelayMaxSeconds),
        (BotOptionPayloadKeys.CollectStepDelayMinSeconds, BotOptionPayloadKeys.CollectStepDelayMaxSeconds),
        (BotOptionPayloadKeys.VillageStatusSweepRoundMinMinutes, BotOptionPayloadKeys.VillageStatusSweepRoundMaxMinutes),
        (BotOptionPayloadKeys.VillageStatusSweepVillageMinSeconds, BotOptionPayloadKeys.VillageStatusSweepVillageMaxSeconds),
        (BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMinMinutes, BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMaxMinutes),
        (BotOptionPayloadKeys.ActionPacingIdleBreakDurationMinMinutes, BotOptionPayloadKeys.ActionPacingIdleBreakDurationMaxMinutes),
        (BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMinMinutes, BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMaxMinutes),
        (BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMin, BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMax),
        (BotOptionPayloadKeys.ConstructionHumanizeNoPlusMinMinutes, BotOptionPayloadKeys.ConstructionHumanizeNoPlusMaxMinutes),
        (BotOptionPayloadKeys.DemolishDelayMinMinutes, BotOptionPayloadKeys.DemolishDelayMaxMinutes),
        (BotOptionPayloadKeys.HeroAdventureRestartDelayMinMinutes, BotOptionPayloadKeys.HeroAdventureRestartDelayMaxMinutes),
        (BotOptionPayloadKeys.SmithyUpgradeRestartDelayMinMinutes, BotOptionPayloadKeys.SmithyUpgradeRestartDelayMaxMinutes),
        (BotOptionPayloadKeys.TownHallCelebrationRestartDelayMinMinutes, BotOptionPayloadKeys.TownHallCelebrationRestartDelayMaxMinutes),
    ];

    public void Export(string path, JsonObject normalizedDraft, string appVersion, DateTimeOffset exportedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(normalizedDraft);

        var settings = new JsonObject();
        foreach (var descriptor in PortableSettings.Values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!normalizedDraft.TryGetPropertyValue(descriptor.Key, out var value) || value is null)
            {
                continue;
            }

            if (!descriptor.TryValidate(value, out _, out var reason))
            {
                throw new InvalidDataException($"Setting '{descriptor.Key}' is invalid: {reason}");
            }

            settings[descriptor.Key] = value.DeepClone();
        }

        var normalizedAppVersion = string.IsNullOrWhiteSpace(appVersion) ? "dev" : appVersion.Trim();
        if (normalizedAppVersion.Length > 100)
        {
            throw new InvalidDataException("The application version metadata is too long.");
        }

        var document = new JsonObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["appVersion"] = normalizedAppVersion,
            ["exportedAtUtc"] = exportedAtUtc.ToUniversalTime(),
            ["settings"] = settings,
        };
        AtomicFile.WriteAllText(path, document.ToJsonString(JsonOptions));
    }

    public SettingsImportResult Import(string path, JsonObject currentDraft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(currentDraft);

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The settings file was not found.", path);
        }

        if (file.Length > MaxFileBytes)
        {
            throw new InvalidDataException("The settings file is larger than the 1 MB limit.");
        }

        return ImportJson(File.ReadAllText(path), currentDraft);
    }

    internal SettingsImportResult ImportJson(string json, JsonObject currentDraft)
    {
        JsonObject document;
        try
        {
            document = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("The settings file must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The settings file is not valid JSON: {ex.Message}", ex);
        }

        if (!TryReadInt(document["schemaVersion"], out var schemaVersion) || schemaVersion < 1)
        {
            throw new InvalidDataException("The settings file has no supported schema version.");
        }

        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException("This settings file was created with a newer schema. Update Tbot Ultra before importing it.");
        }

        if (!TryReadString(document["appVersion"], out var appVersion)
            || appVersion.Length > 100
            || !TryReadDateTimeOffset(document["exportedAtUtc"], out var exportedAtUtc)
            || document["settings"] is not JsonObject importedSettings)
        {
            throw new InvalidDataException("The settings file is missing required export metadata or its settings object.");
        }

        var accepted = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var skipped = new List<SettingsImportSkipped>();
        foreach (var (key, value) in importedSettings)
        {
            if (!PortableSettings.TryGetValue(key, out var descriptor))
            {
                skipped.Add(new SettingsImportSkipped(key, "Unknown or non-portable setting."));
                continue;
            }

            if (value is null)
            {
                skipped.Add(new SettingsImportSkipped(key, "A setting value cannot be null."));
                continue;
            }

            if (!descriptor.TryValidate(value, out var normalized, out var reason))
            {
                skipped.Add(new SettingsImportSkipped(key, reason));
                continue;
            }

            accepted[key] = normalized;
        }

        RemoveInvalidRanges(currentDraft, accepted, skipped);
        RemoveInvalidAntiStarveRange(currentDraft, accepted, skipped);

        var merged = (JsonObject)currentDraft.DeepClone();
        var changedKeys = new List<string>();
        var changedCategories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in accepted.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (JsonNode.DeepEquals(merged[key], value))
            {
                continue;
            }

            merged[key] = value.DeepClone();
            changedKeys.Add(key);
            changedCategories.Add(PortableSettings[key].Category);
        }

        return new SettingsImportResult(
            merged,
            changedKeys,
            changedCategories.OrderBy(category => category, StringComparer.Ordinal).ToList(),
            skipped,
            schemaVersion,
            appVersion,
            exportedAtUtc.ToUniversalTime(),
            BecameEnabled(currentDraft, accepted, BotOptionPayloadKeys.AllowGoldSpending),
            BecameEnabled(currentDraft, accepted, "allow_silver_spending"),
            BecameRiskyDailyRuntime(currentDraft, accepted));
    }

    private static void RemoveInvalidRanges(
        JsonObject currentDraft,
        IDictionary<string, JsonNode> accepted,
        ICollection<SettingsImportSkipped> skipped)
    {
        foreach (var (minKey, maxKey) in OrderedRanges)
        {
            var importedMin = accepted.ContainsKey(minKey);
            var importedMax = accepted.ContainsKey(maxKey);
            if (!importedMin && !importedMax)
            {
                continue;
            }

            var minNode = importedMin ? accepted[minKey] : currentDraft[minKey];
            var maxNode = importedMax ? accepted[maxKey] : currentDraft[maxKey];
            if (!TryReadDouble(minNode, out var min) || !TryReadDouble(maxNode, out var max) || max >= min)
            {
                continue;
            }

            if (importedMin)
            {
                accepted.Remove(minKey);
                skipped.Add(new SettingsImportSkipped(minKey, $"Would make the minimum greater than '{maxKey}'."));
            }

            if (importedMax)
            {
                accepted.Remove(maxKey);
                skipped.Add(new SettingsImportSkipped(maxKey, $"Would make the maximum lower than '{minKey}'."));
            }
        }
    }

    private static void RemoveInvalidAntiStarveRange(
        JsonObject currentDraft,
        IDictionary<string, JsonNode> accepted,
        ICollection<SettingsImportSkipped> skipped)
    {
        const string reason = "The anti-starve target must be greater than the trigger.";
        var triggerKey = BotOptionPayloadKeys.HeroCropAntiStarveTriggerMinutes;
        var targetKey = BotOptionPayloadKeys.HeroCropAntiStarveTargetMinutes;
        var importedTrigger = accepted.ContainsKey(triggerKey);
        var importedTarget = accepted.ContainsKey(targetKey);
        if (!importedTrigger && !importedTarget)
        {
            return;
        }

        var triggerNode = importedTrigger ? accepted[triggerKey] : currentDraft[triggerKey];
        var targetNode = importedTarget ? accepted[targetKey] : currentDraft[targetKey];
        if (!TryReadInt(triggerNode, out var trigger) || !TryReadInt(targetNode, out var target) || target > trigger)
        {
            return;
        }

        if (importedTrigger)
        {
            accepted.Remove(triggerKey);
            skipped.Add(new SettingsImportSkipped(triggerKey, reason));
        }

        if (importedTarget)
        {
            accepted.Remove(targetKey);
            skipped.Add(new SettingsImportSkipped(targetKey, reason));
        }
    }

    private static bool BecameEnabled(JsonObject current, IReadOnlyDictionary<string, JsonNode> accepted, string key)
        => accepted.TryGetValue(key, out var imported)
            && TryReadBool(imported, out var importedValue)
            && importedValue
            && (!TryReadBool(current[key], out var currentValue) || !currentValue);

    private static bool BecameRiskyDailyRuntime(JsonObject current, IReadOnlyDictionary<string, JsonNode> accepted)
    {
        var key = BotOptionPayloadKeys.SessionPacingDailyMaxHours;
        if (!accepted.TryGetValue(key, out var imported) || !TryReadInt(imported, out var importedHours))
        {
            return false;
        }

        var importedIsRisky = importedHours == 0 || importedHours > PacingDefaults.SessionPacingDailyMaxHours;
        var currentIsRisky = TryReadInt(current[key], out var currentHours)
            && (currentHours == 0 || currentHours > PacingDefaults.SessionPacingDailyMaxHours);
        return importedIsRisky && !currentIsRisky;
    }

    private static IReadOnlyDictionary<string, PortableSetting> CreatePortableSettings()
    {
        var settings = new Dictionary<string, PortableSetting>(StringComparer.Ordinal);
        void Add(PortableSetting setting) => settings.Add(setting.Key, setting);

        foreach (var key in new[]
        {
            BotOptionPayloadKeys.PostLoginQuickReloginEnabled,
            BotOptionPayloadKeys.AutomaticallyCheckLanguage,
            BotOptionPayloadKeys.TurnOffVideoSound,
            BotOptionPayloadKeys.PostLoginAnalyzeFarmlists,
            BotOptionPayloadKeys.PostLoginAnalyzeHero,
            BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue,
            BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory,
            BotOptionPayloadKeys.PostLoginAnalyzeNewVillages,
            BotOptionPayloadKeys.PostLoginAnalyzeNewAccount,
        }) Add(Bool(key, "General"));
        Add(Bool("allow_silver_spending", "NpcTrade"));
        Add(Bool(BotOptionPayloadKeys.AllowGoldSpending, "NpcTrade"));
        Add(Int(BotOptionPayloadKeys.GoldLimit, "NpcTrade", 0, int.MaxValue));
        Add(Int(BotOptionPayloadKeys.DailyGoldSpendingLimit, "NpcTrade", 0, int.MaxValue));
        Add(Int(BotOptionPayloadKeys.SilverLimit, "NpcTrade", 0, int.MaxValue));
        Add(Int(BotOptionPayloadKeys.DailySilverSpendingLimit, "NpcTrade", 0, int.MaxValue));

        foreach (var key in new[]
        {
            BotOptionPayloadKeys.SessionPacingEnabled,
            BotOptionPayloadKeys.SmartSleepEnabled,
            BotOptionPayloadKeys.ActionPacingEnabled,
            BotOptionPayloadKeys.ContinuousKeepAliveEnabled,
            BotOptionPayloadKeys.VillageStatusSweepEnabled,
            BotOptionPayloadKeys.VillageStatusSweepDorf1Enabled,
            BotOptionPayloadKeys.VillageStatusSweepDorf2Enabled,
            BotOptionPayloadKeys.VillageStatusSweepSmithyEnabled,
            BotOptionPayloadKeys.VillageStatusSweepBarracksEnabled,
            BotOptionPayloadKeys.VillageStatusSweepStableEnabled,
            BotOptionPayloadKeys.VillageStatusSweepWorkshopEnabled,
            BotOptionPayloadKeys.VillageStatusSweepTownHallEnabled,
            BotOptionPayloadKeys.ActionPacingIdleBreakEnabled,
            BotOptionPayloadKeys.ActionPacingIdleBrowseEnabled,
            BotOptionPayloadKeys.ActionPacingIdleBrowsePageMap,
            BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatistics,
            BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsHero,
            BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsTop10,
            BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsDefenders,
            BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsAttackers,
            BotOptionPayloadKeys.ActionPacingIdleBrowsePageReports,
            BotOptionPayloadKeys.ActionPacingIdleBrowsePageMessages,
        }) Add(Bool(key, "Pacing"));
        Add(Int(BotOptionPayloadKeys.SessionPacingRunMinMinutes, "Pacing", 1, 10080));
        Add(Int(BotOptionPayloadKeys.SessionPacingRunMaxMinutes, "Pacing", 1, 10080));
        Add(Int(BotOptionPayloadKeys.SessionPacingSleepMinMinutes, "Pacing", 5, 10080));
        Add(Int(BotOptionPayloadKeys.SessionPacingSleepMaxMinutes, "Pacing", 5, 10080));
        Add(Int(BotOptionPayloadKeys.SessionPacingDailyMaxHours, "Pacing", 0, 24));
        Add(Int(BotOptionPayloadKeys.SessionPacingDailyMaxVariationPercent, "Pacing", 0, 50, [0, 10, 20, 30, 40, 50]));
        Add(Hours(BotOptionPayloadKeys.SessionPacingAllowedHours, "Pacing"));
        Add(Int(BotOptionPayloadKeys.SessionPacingHoursVariationPercent, "Pacing", 0, 30, [0, 10, 20, 30]));
        Add(Int(BotOptionPayloadKeys.SmartSleepMinimumOpportunityMinutes, "Pacing", 1, 1440));
        Add(Int(BotOptionPayloadKeys.SmartSleepWakeBeforeMinutes, "Pacing", 0, 1440));
        Add(Int(BotOptionPayloadKeys.SmartSleepWakeAfterMinutes, "Pacing", 0, 1440));
        Add(Int(BotOptionPayloadKeys.SmartSleepFallbackMinMinutes, "Pacing", 1, 10080));
        Add(Int(BotOptionPayloadKeys.SmartSleepFallbackMaxMinutes, "Pacing", 1, 10080));
        Add(Int(BotOptionPayloadKeys.ShortVillageDeferSeconds, "Pacing", 20, 90, PacingDefaults.ShortVillageDeferChoicesSeconds));
        Add(Int(BotOptionPayloadKeys.ContinuousKeepAliveMinMinutes, "Pacing", 1, 1440));
        Add(Int(BotOptionPayloadKeys.ContinuousKeepAliveMaxMinutes, "Pacing", 1, 1440));
        Add(Int(BotOptionPayloadKeys.VillageStatusSweepRoundMinMinutes, "Pacing", 1, 1440));
        Add(Int(BotOptionPayloadKeys.VillageStatusSweepRoundMaxMinutes, "Pacing", 1, 1440));
        foreach (var key in new[]
        {
            BotOptionPayloadKeys.ActionPacingTaskMinSeconds, BotOptionPayloadKeys.ActionPacingTaskMaxSeconds,
            BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds, BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds,
            BotOptionPayloadKeys.ActionPacingClickMinSeconds, BotOptionPayloadKeys.ActionPacingClickMaxSeconds,
            BotOptionPayloadKeys.ActionPacingLoopMinSeconds, BotOptionPayloadKeys.ActionPacingLoopMaxSeconds,
            BotOptionPayloadKeys.FarmListStepDelayMinSeconds, BotOptionPayloadKeys.FarmListStepDelayMaxSeconds,
            BotOptionPayloadKeys.CollectStepDelayMinSeconds, BotOptionPayloadKeys.CollectStepDelayMaxSeconds,
            BotOptionPayloadKeys.VillageStatusSweepVillageMinSeconds, BotOptionPayloadKeys.VillageStatusSweepVillageMaxSeconds,
            BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMinMinutes, BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMaxMinutes,
            BotOptionPayloadKeys.ActionPacingIdleBreakDurationMinMinutes, BotOptionPayloadKeys.ActionPacingIdleBreakDurationMaxMinutes,
            BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMinMinutes, BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMaxMinutes,
        }) Add(Double(key, "Pacing", 0, 3600));

        Add(Bool(BotOptionPayloadKeys.ConstructionCropShortageRecoveryEnabled, "Construction"));
        Add(Bool(BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled, "Construction"));
        Add(Int(BotOptionPayloadKeys.ConstructionStorageUpgradeLevelsAhead, "Construction", ConstructionDefaults.StorageUpgradeLevelsAheadMin, ConstructionDefaults.StorageUpgradeLevelsAheadMax));
        Add(Double(BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMin, "Construction", 0, 99));
        Add(Double(BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMax, "Construction", 0, 99));
        Add(Double(BotOptionPayloadKeys.ConstructionHumanizeMaxDelayMinutes, "Construction", 0, 600));
        Add(Double(BotOptionPayloadKeys.ConstructionHumanizeNoPlusMinMinutes, "Construction", 0, 600));
        Add(Double(BotOptionPayloadKeys.ConstructionHumanizeNoPlusMaxMinutes, "Construction", 0, 600));
        Add(Int(BotOptionPayloadKeys.DemolishDelayMinMinutes, "Construction", 0, 1440));
        Add(Int(BotOptionPayloadKeys.DemolishDelayMaxMinutes, "Construction", 0, 1440));

        Add(Bool(BotOptionPayloadKeys.HeroAdventureRestartDelayEnabled, "Hero"));
        Add(Double(BotOptionPayloadKeys.HeroAdventureRestartDelayMinMinutes, "Hero", 0, double.MaxValue));
        Add(Double(BotOptionPayloadKeys.HeroAdventureRestartDelayMaxMinutes, "Hero", 0, double.MaxValue));
        Add(Int(BotOptionPayloadKeys.HeroHpRegenPerDayPercent, "Hero", 20, 100, [20, 30, 40, 50, 60, 70, 80, 90, 100]));
        Add(Bool(BotOptionPayloadKeys.HeroCropAntiStarveEnabled, "Hero"));
        Add(Int(BotOptionPayloadKeys.HeroCropAntiStarveTriggerMinutes, "Hero", 1, 1440));
        Add(Int(BotOptionPayloadKeys.HeroCropAntiStarveTargetMinutes, "Hero", 1, 1440));
        Add(Int(BotOptionPayloadKeys.HeroCropAntiStarveMaxCropPerTransfer, "Hero", 1, int.MaxValue));
        Add(Int(BotOptionPayloadKeys.HeroCropAntiStarveMinHeroCropRemaining, "Hero", 0, int.MaxValue));

        Add(Bool(BotOptionPayloadKeys.ShowFarmListLastSentTimer, "Farming"));
        Add(Bool(BotOptionPayloadKeys.FarmListLastSentLimitEnabled, "Farming"));
        Add(Int(BotOptionPayloadKeys.FarmListLastSentLimitHours, "Farming", 1, FarmingDefaults.MaxLastSentLimitHours));

        Add(Bool(BotOptionPayloadKeys.SmithyUpgradeRestartDelayEnabled, "Troops"));
        Add(Double(BotOptionPayloadKeys.SmithyUpgradeRestartDelayMinMinutes, "Troops", 0, double.MaxValue));
        Add(Double(BotOptionPayloadKeys.SmithyUpgradeRestartDelayMaxMinutes, "Troops", 0, double.MaxValue));
        Add(Int(BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds, "Troops", 10, 600, [10, 30, 60, 120, 300, 600]));

        Add(Int(BotOptionPayloadKeys.TownHallCelebrationCount, "Celebrations", TownHallCelebrationDefaults.MinCount, TownHallCelebrationDefaults.MaxCount));
        Add(Bool(BotOptionPayloadKeys.TownHallCelebrationRestartDelayEnabled, "Celebrations"));
        Add(Double(BotOptionPayloadKeys.TownHallCelebrationRestartDelayMinMinutes, "Celebrations", 0, double.MaxValue));
        Add(Double(BotOptionPayloadKeys.TownHallCelebrationRestartDelayMaxMinutes, "Celebrations", 0, double.MaxValue));

        return settings;
    }

    private static PortableSetting Bool(string key, string category)
        => new(key, category, (JsonNode node, out JsonNode normalized, out string reason) =>
        {
            if (TryReadBool(node, out var value))
            {
                normalized = JsonValue.Create(value)!;
                reason = string.Empty;
                return true;
            }

            normalized = null!;
            reason = "Expected true or false.";
            return false;
        });

    private static PortableSetting Int(string key, string category, int min, int max, IReadOnlyCollection<int>? allowed = null)
        => new(key, category, (JsonNode node, out JsonNode normalized, out string reason) =>
        {
            if (TryReadInt(node, out var value) && value >= min && value <= max && (allowed is null || allowed.Contains(value)))
            {
                normalized = JsonValue.Create(value)!;
                reason = string.Empty;
                return true;
            }

            normalized = null!;
            reason = allowed is null
                ? $"Expected a whole number from {min} to {max}."
                : $"Expected one of: {string.Join(", ", allowed)}.";
            return false;
        });

    private static PortableSetting Double(string key, string category, double min, double max)
        => new(key, category, (JsonNode node, out JsonNode normalized, out string reason) =>
        {
            if (TryReadDouble(node, out var value) && double.IsFinite(value) && value >= min && value <= max)
            {
                normalized = JsonValue.Create(value)!;
                reason = string.Empty;
                return true;
            }

            normalized = null!;
            reason = $"Expected a number from {min} to {max}.";
            return false;
        });

    private static PortableSetting Hours(string key, string category)
        => new(key, category, (JsonNode node, out JsonNode normalized, out string reason) =>
        {
            if (node is not JsonArray array)
            {
                normalized = null!;
                reason = "Expected an array of server-local hours from 0 to 23.";
                return false;
            }

            var hours = new List<int>();
            foreach (var item in array)
            {
                if (!TryReadInt(item, out var hour) || hour is < 0 or > 23 || hours.Contains(hour))
                {
                    normalized = null!;
                    reason = "Allowed hours must be unique whole numbers from 0 to 23.";
                    return false;
                }

                hours.Add(hour);
            }

            normalized = new JsonArray(hours.OrderBy(hour => hour).Select(hour => JsonValue.Create(hour)).ToArray());
            reason = string.Empty;
            return true;
        });

    private static bool TryReadBool(JsonNode? node, out bool value)
    {
        value = default;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static bool TryReadInt(JsonNode? node, out int value)
    {
        value = default;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static bool TryReadDouble(JsonNode? node, out double value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out value))
        {
            return true;
        }

        if (TryReadInt(node, out var integer))
        {
            value = integer;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
        {
            value = text.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadDateTimeOffset(JsonNode? node, out DateTimeOffset value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out value))
        {
            return true;
        }

        if (TryReadString(node, out var text) && DateTimeOffset.TryParse(text, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private delegate bool SettingValidator(JsonNode node, out JsonNode normalized, out string reason);

    private sealed record PortableSetting(string Key, string Category, SettingValidator TryValidate);
}
