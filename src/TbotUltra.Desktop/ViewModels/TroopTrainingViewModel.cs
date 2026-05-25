using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model for the Troops tab's "Build troops" card. Owns the three
/// per-building rule rows (Barracks / Stable / Workshop) plus all the
/// pure logic that operates on them: load from / write to <see cref="BotOptions"/>,
/// recompute the troop dropdown for the current tribe, apply queue status
/// from a <see cref="VillageStatus"/>, tick countdowns, etc.
///
/// Service-bound work (fetching building or queue status from the worker)
/// stays on MainWindow; the VM exposes <see cref="ConfigChanged"/> so
/// MainWindow can persist + update group running indicators when the user
/// edits a row.
/// </summary>
public sealed class TroopTrainingViewModel : BaseViewModel
{
    private static readonly string[] RelevantOptionProperties =
    [
        nameof(TroopTrainingBuildingOption.IsEnabled),
        nameof(TroopTrainingBuildingOption.SelectedTroop),
        nameof(TroopTrainingBuildingOption.MaxQueueMode),
        nameof(TroopTrainingBuildingOption.AmountMode),
        nameof(TroopTrainingBuildingOption.KeepResourcesPercent),
        nameof(TroopTrainingBuildingOption.RunMode),
        nameof(TroopTrainingBuildingOption.MinimumTroops),
        nameof(TroopTrainingBuildingOption.MinimumResourcesPercent),
    ];

    private bool _isConfigSuppressed;
    private string _infoText = "Configure troop building rules and refresh queues when needed.";
    private bool _checkWood = true;
    private bool _checkClay = true;
    private bool _checkIron = true;
    private bool _checkCrop = true;
    private int _fallbackCooldownSeconds = 30;
    private bool _autoCelebrationEnabled;
    private bool _autoCelebrationExplicitlyConfigured;
    private bool _isAutoCelebrationAvailableForCurrentTribe;
    private bool _autoCelebrationCanStart;
    private int? _autoCelebrationRemainingSeconds;
    private string _autoCelebrationStatusText = "Teutons only.";
    private bool _npcTradeEnabled;
    private bool _npcTradeConstructionEnabled;
    private int _npcTradeThresholdPercent = 90;
    private bool _npcTradeAnalyzeWood = true;
    private bool _npcTradeAnalyzeClay = true;
    private bool _npcTradeAnalyzeIron = true;
    private bool _npcTradeAnalyzeCrop = true;
    private bool _allowGoldSpending;
    private int _goldLimit = 100;

    /// <summary>The three building rules shown as rows on the panel.</summary>
    public ObservableCollection<TroopTrainingBuildingOption> Buildings { get; } = [];

    /// <summary>
    /// Status / hint line shown above the row list (e.g. "Queued: build troops.",
    /// "Troop training queues refreshed.", error messages from the refresh
    /// button). MainWindow's button handlers and LoadConfigToUi push values here.
    /// </summary>
    public string InfoText
    {
        get => _infoText;
        set => SetProperty(ref _infoText, value);
    }

    /// <summary>
    /// Raised when a user-driven option change crosses one of the persisted
    /// fields. MainWindow subscribes to call <c>PersistTroopTrainingConfig</c>
    /// and <c>UpdateAutomationLoopRunningIndicators</c>. The VM suppresses
    /// the event during bulk loads / dropdown rebuilds.
    /// </summary>
    public event Action? ConfigChanged;

    public bool CheckWood
    {
        get => _checkWood;
        set
        {
            if (!SetProperty(ref _checkWood, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool CheckClay
    {
        get => _checkClay;
        set
        {
            if (!SetProperty(ref _checkClay, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool CheckIron
    {
        get => _checkIron;
        set
        {
            if (!SetProperty(ref _checkIron, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool CheckCrop
    {
        get => _checkCrop;
        set
        {
            if (!SetProperty(ref _checkCrop, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public int FallbackCooldownSeconds
    {
        get => _fallbackCooldownSeconds;
        set
        {
            var normalized = value switch
            {
                10 or 30 or 60 or 120 or 300 or 600 => value,
                _ => 30,
            };

            if (!SetProperty(ref _fallbackCooldownSeconds, normalized))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeEnabled
    {
        get => _npcTradeEnabled;
        set
        {
            if (!SetProperty(ref _npcTradeEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAnyNpcTradeEnabled));
            OnPropertyChanged(nameof(NpcTradeStatusText));
            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public int NpcTradeThresholdPercent
    {
        get => _npcTradeThresholdPercent;
        set
        {
            var normalized = Math.Clamp(value, 1, 100);
            if (!SetProperty(ref _npcTradeThresholdPercent, normalized))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeConstructionEnabled
    {
        get => _npcTradeConstructionEnabled;
        set
        {
            if (!SetProperty(ref _npcTradeConstructionEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAnyNpcTradeEnabled));
            OnPropertyChanged(nameof(NpcTradeStatusText));
            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeAnalyzeWood
    {
        get => _npcTradeAnalyzeWood;
        set
        {
            if (!SetProperty(ref _npcTradeAnalyzeWood, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeAnalyzeClay
    {
        get => _npcTradeAnalyzeClay;
        set
        {
            if (!SetProperty(ref _npcTradeAnalyzeClay, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeAnalyzeIron
    {
        get => _npcTradeAnalyzeIron;
        set
        {
            if (!SetProperty(ref _npcTradeAnalyzeIron, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeAnalyzeCrop
    {
        get => _npcTradeAnalyzeCrop;
        set
        {
            if (!SetProperty(ref _npcTradeAnalyzeCrop, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool IsAnyNpcTradeEnabled => NpcTradeEnabled || NpcTradeConstructionEnabled;

    public string NpcTradeStatusText => (NpcTradeEnabled, NpcTradeConstructionEnabled) switch
    {
        (true, true) => "Trades for troops, buildings, and resource fields.",
        (true, false) => "Trades while building troops.",
        (false, true) => "Trades while upgrading buildings and resource fields.",
        _ => "NPC trade is off.",
    };

    public bool AllowGoldSpending
    {
        get => _allowGoldSpending;
        set
        {
            if (!SetProperty(ref _allowGoldSpending, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public int GoldLimit
    {
        get => _goldLimit;
        set
        {
            var normalized = Math.Clamp(value, 0, 1000);
            if (!SetProperty(ref _goldLimit, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(GoldLimitText));
            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public string GoldLimitText => $"Gold limit: {GoldLimit}";

    /// <summary>
    /// Populates <see cref="Buildings"/> with the three building types and
    /// hooks per-item PropertyChanged for change tracking. Idempotent.
    /// </summary>
    public void Initialize()
    {
        if (Buildings.Count > 0)
        {
            return;
        }

        foreach (var option in new[]
                 {
                     new TroopTrainingBuildingOption { BuildingType = TroopTrainingBuildingType.Barracks, Title = "Barracks" },
                     new TroopTrainingBuildingOption { BuildingType = TroopTrainingBuildingType.Stable, Title = "Stable" },
                     new TroopTrainingBuildingOption { BuildingType = TroopTrainingBuildingType.Workshop, Title = "Workshop" },
                 })
        {
            option.PropertyChanged += OnOptionPropertyChanged;
            Buildings.Add(option);
        }
    }

    /// <summary>
    /// Bulk-applies persisted settings from <see cref="BotOptions"/> onto the
    /// existing rows. Suppresses <see cref="ConfigChanged"/> during the update.
    /// </summary>
    public void ApplyConfigToBuildings(BotOptions options, bool hasExplicitAutoCelebrationSetting, bool? autoCelebrationOverride = null)
    {
        _isConfigSuppressed = true;
        try
        {
            foreach (var option in Buildings)
            {
                switch (option.BuildingType)
                {
                    case TroopTrainingBuildingType.Barracks:
                        option.IsEnabled = options.TroopTrainingBarracksEnabled;
                        option.SelectedTroop = options.TroopTrainingBarracksTroopType;
                        option.MaxQueueMode = options.TroopTrainingBarracksMaxQueueHours;
                        option.AmountMode = options.TroopTrainingBarracksAmountMode;
                        option.KeepResourcesPercent = options.TroopTrainingBarracksKeepResourcesPercent;
                        option.RunMode = options.TroopTrainingBarracksRunMode;
                        option.MinimumTroops = options.TroopTrainingBarracksMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingBarracksMinimumResourcesPercent;
                        break;
                    case TroopTrainingBuildingType.Stable:
                        option.IsEnabled = options.TroopTrainingStableEnabled;
                        option.SelectedTroop = options.TroopTrainingStableTroopType;
                        option.MaxQueueMode = options.TroopTrainingStableMaxQueueHours;
                        option.AmountMode = options.TroopTrainingStableAmountMode;
                        option.KeepResourcesPercent = options.TroopTrainingStableKeepResourcesPercent;
                        option.RunMode = options.TroopTrainingStableRunMode;
                        option.MinimumTroops = options.TroopTrainingStableMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingStableMinimumResourcesPercent;
                        break;
                    case TroopTrainingBuildingType.Workshop:
                        option.IsEnabled = options.TroopTrainingWorkshopEnabled;
                        option.SelectedTroop = options.TroopTrainingWorkshopTroopType;
                        option.MaxQueueMode = options.TroopTrainingWorkshopMaxQueueHours;
                        option.AmountMode = options.TroopTrainingWorkshopAmountMode;
                        option.KeepResourcesPercent = options.TroopTrainingWorkshopKeepResourcesPercent;
                        option.RunMode = options.TroopTrainingWorkshopRunMode;
                        option.MinimumTroops = options.TroopTrainingWorkshopMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingWorkshopMinimumResourcesPercent;
                        break;
                }
            }

            CheckWood = options.TroopTrainingBarracksCheckWood;
            CheckClay = options.TroopTrainingBarracksCheckClay;
            CheckIron = options.TroopTrainingBarracksCheckIron;
            CheckCrop = options.TroopTrainingBarracksCheckCrop;
            FallbackCooldownSeconds = options.TroopTrainingFallbackCooldownSeconds;
            NpcTradeEnabled = options.NpcTradeEnabled;
            NpcTradeConstructionEnabled = options.NpcTradeConstructionEnabled;
            NpcTradeThresholdPercent = options.NpcTradeThresholdPercent;
            NpcTradeAnalyzeWood = options.NpcTradeAnalyzeWood;
            NpcTradeAnalyzeClay = options.NpcTradeAnalyzeClay;
            NpcTradeAnalyzeIron = options.NpcTradeAnalyzeIron;
            NpcTradeAnalyzeCrop = options.NpcTradeAnalyzeCrop;
            AllowGoldSpending = options.AllowGoldSpending;
            GoldLimit = options.GoldLimit;
            _autoCelebrationExplicitlyConfigured = hasExplicitAutoCelebrationSetting;
            AutoCelebrationEnabled = autoCelebrationOverride ?? options.BreweryAutoCelebrationEnabled;
        }
        finally
        {
            _isConfigSuppressed = false;
        }
    }

    /// <summary>
    /// Writes current rows back into a freshly loaded config <see cref="JsonObject"/>.
    /// Caller is responsible for persisting (e.g. <c>BotConfigStore.Save</c>).
    /// </summary>
    public void WriteToConfig(JsonObject config)
    {
        foreach (var option in Buildings)
        {
            switch (option.BuildingType)
            {
                case TroopTrainingBuildingType.Barracks:
                    config[BotOptionPayloadKeys.TroopTrainingBarracksEnabled] = option.IsEnabled;
                    config[BotOptionPayloadKeys.TroopTrainingBarracksTroopType] = option.SelectedTroop;
                    config[BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours] = option.MaxQueueMode;
                    config[BotOptionPayloadKeys.TroopTrainingBarracksAmountMode] = option.AmountMode;
                    config[BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent] = option.KeepResourcesPercent;
                    config[BotOptionPayloadKeys.TroopTrainingBarracksRunMode] = option.RunMode;
                    config[BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops] = option.MinimumTroops;
                    config[BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent] = option.MinimumResourcesPercent;
                    config[BotOptionPayloadKeys.TroopTrainingBarracksCheckWood] = CheckWood;
                    config[BotOptionPayloadKeys.TroopTrainingBarracksCheckClay] = CheckClay;
                    config[BotOptionPayloadKeys.TroopTrainingBarracksCheckIron] = CheckIron;
                    config[BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop] = CheckCrop;
                    break;
                case TroopTrainingBuildingType.Stable:
                    config[BotOptionPayloadKeys.TroopTrainingStableEnabled] = option.IsEnabled;
                    config[BotOptionPayloadKeys.TroopTrainingStableTroopType] = option.SelectedTroop;
                    config[BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours] = option.MaxQueueMode;
                    config[BotOptionPayloadKeys.TroopTrainingStableAmountMode] = option.AmountMode;
                    config[BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent] = option.KeepResourcesPercent;
                    config[BotOptionPayloadKeys.TroopTrainingStableRunMode] = option.RunMode;
                    config[BotOptionPayloadKeys.TroopTrainingStableMinimumTroops] = option.MinimumTroops;
                    config[BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent] = option.MinimumResourcesPercent;
                    config[BotOptionPayloadKeys.TroopTrainingStableCheckWood] = CheckWood;
                    config[BotOptionPayloadKeys.TroopTrainingStableCheckClay] = CheckClay;
                    config[BotOptionPayloadKeys.TroopTrainingStableCheckIron] = CheckIron;
                    config[BotOptionPayloadKeys.TroopTrainingStableCheckCrop] = CheckCrop;
                    break;
                case TroopTrainingBuildingType.Workshop:
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopEnabled] = option.IsEnabled;
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopTroopType] = option.SelectedTroop;
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours] = option.MaxQueueMode;
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode] = option.AmountMode;
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent] = option.KeepResourcesPercent;
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopRunMode] = option.RunMode;
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops] = option.MinimumTroops;
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent] = option.MinimumResourcesPercent;
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood] = CheckWood;
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay] = CheckClay;
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron] = CheckIron;
                    config[BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop] = CheckCrop;
                    break;
            }
        }

        config[BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds] = FallbackCooldownSeconds;
        config[BotOptionPayloadKeys.BreweryAutoCelebrationEnabled] = AutoCelebrationEnabled;
        config[BotOptionPayloadKeys.NpcTradeEnabled] = NpcTradeEnabled;
        config[BotOptionPayloadKeys.NpcTradeConstructionEnabled] = NpcTradeConstructionEnabled;
        config[BotOptionPayloadKeys.NpcTradeThresholdPercent] = NpcTradeThresholdPercent;
        config[BotOptionPayloadKeys.NpcTradeAnalyzeWood] = NpcTradeAnalyzeWood;
        config[BotOptionPayloadKeys.NpcTradeAnalyzeClay] = NpcTradeAnalyzeClay;
        config[BotOptionPayloadKeys.NpcTradeAnalyzeIron] = NpcTradeAnalyzeIron;
        config[BotOptionPayloadKeys.NpcTradeAnalyzeCrop] = NpcTradeAnalyzeCrop;
        config["allow_gold_spending"] = AllowGoldSpending;
        config["gold_limit"] = GoldLimit;
    }

    /// <summary>
    /// Refreshes each row's <c>TroopOptions</c> dropdown based on the
    /// player's tribe. Returns <c>true</c> when the previously selected
    /// troop is no longer available for any row and a fallback was applied —
    /// the caller should persist in that case so the new selection survives
    /// across restarts.
    /// </summary>
    public bool UpdateTroopOptions(string? tribe)
    {
        var configChanged = false;
        _isConfigSuppressed = true;
        try
        {
            foreach (var option in Buildings)
            {
                var resolvedTroops = TroopCatalog.ResolveTroopTypesForTribe(tribe, option.BuildingType);
                var currentSelection = option.SelectedTroop;
                option.TroopOptions.Clear();
                foreach (var troop in resolvedTroops)
                {
                    option.TroopOptions.Add(troop);
                }

                if (resolvedTroops.Contains(currentSelection, StringComparer.OrdinalIgnoreCase))
                {
                    option.SelectedTroop = resolvedTroops.First(item => string.Equals(item, currentSelection, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    var fallbackTroop = resolvedTroops.FirstOrDefault() ?? string.Empty;
                    if (!string.Equals(option.SelectedTroop, fallbackTroop, StringComparison.Ordinal))
                    {
                        configChanged = true;
                    }

                    option.SelectedTroop = fallbackTroop;
                }
            }
        }
        finally
        {
            _isConfigSuppressed = false;
        }

        return configChanged;
    }

    public bool UpdateAutoCelebrationAvailability(string? tribe)
    {
        var isTeutons = string.Equals(tribe?.Trim(), "Teutons", StringComparison.OrdinalIgnoreCase);
        var configChanged = false;

        _isConfigSuppressed = true;
        try
        {
            IsAutoCelebrationAvailableForCurrentTribe = isTeutons;
            if (isTeutons)
            {
                if (!_autoCelebrationExplicitlyConfigured && !AutoCelebrationEnabled)
                {
                    AutoCelebrationEnabled = true;
                    configChanged = true;
                }

                if (!AutoCelebrationEnabled)
                {
                    AutoCelebrationStatusText = "Disabled.";
                }
                else if (string.Equals(AutoCelebrationStatusText, "Teutons only.", StringComparison.Ordinal))
                {
                    AutoCelebrationStatusText = "Status not loaded.";
                }
            }
            else
            {
                if (AutoCelebrationEnabled)
                {
                    AutoCelebrationEnabled = false;
                    configChanged = true;
                }

                AutoCelebrationCanStart = false;
                AutoCelebrationRemainingSeconds = null;
                AutoCelebrationStatusText = "Teutons only.";
            }
        }
        finally
        {
            _isConfigSuppressed = false;
        }

        return configChanged;
    }

    /// <summary>
    /// Applies queue / building-exists status onto the rows from a fresh
    /// <see cref="VillageStatus"/>. <paramref name="fallbackQueues"/> is used
    /// when <paramref name="status"/> doesn't carry its own
    /// <c>TroopTrainingQueues</c> (e.g. when a building snapshot is loaded
    /// without a queue read).
    /// </summary>
    public void ApplyStatus(VillageStatus status, IReadOnlyList<TroopTrainingQueueStatus>? fallbackQueues)
    {
        var queueStatuses = status.TroopTrainingQueues ?? fallbackQueues;
        foreach (var option in Buildings)
        {
            var queueStatus = queueStatuses?.FirstOrDefault(item => item.BuildingType == option.BuildingType);
            if (queueStatus is not null)
            {
                option.Exists = queueStatus.Exists;
                option.QueueRemainingSeconds = queueStatus.RemainingSeconds;
                option.QueueStatusText = queueStatus.Exists
                    ? $"Queue: {queueStatus.RemainingText}"
                    : "Building not found";
                continue;
            }

            if (option.Exists)
            {
                if (string.IsNullOrWhiteSpace(option.QueueStatusText))
                {
                    option.QueueStatusText = "Queue not loaded.";
                }

                continue;
            }

            var buildingExists = status.Buildings.Any(item =>
                item.SlotId is > 0
                && ((option.BuildingType == TroopTrainingBuildingType.Barracks && (item.Gid ?? 0) == 19)
                    || (option.BuildingType == TroopTrainingBuildingType.Stable && (item.Gid ?? 0) == 20)
                    || (option.BuildingType == TroopTrainingBuildingType.Workshop && (item.Gid ?? 0) == 21)
                    || string.Equals(item.Name, option.Title, StringComparison.OrdinalIgnoreCase)));
            option.Exists = buildingExists;
            if (!buildingExists)
            {
                option.QueueRemainingSeconds = null;
                option.QueueStatusText = "Building not found";
            }
            else if (string.IsNullOrWhiteSpace(option.QueueStatusText))
            {
                option.QueueStatusText = "Queue not loaded.";
            }
        }
    }

    /// <summary>Resets every row to a fresh "queue not loaded" state.</summary>
    public void ResetQueueStatus()
    {
        foreach (var option in Buildings)
        {
            option.Exists = false;
            option.QueueRemainingSeconds = null;
            option.QueueStatusText = "Queue not loaded.";
        }
    }

    public void ApplyBreweryCelebrationStatus(BreweryCelebrationStatus status)
    {
        AutoCelebrationCanStart = status.IsAvailableForTribe
            && status.IsCapital == true
            && status.BreweryExists
            && !status.CelebrationRunning;
        AutoCelebrationRemainingSeconds = status.CelebrationRunning ? status.RemainingSeconds : null;
        AutoCelebrationStatusText = string.IsNullOrWhiteSpace(status.StatusText)
            ? "Status unavailable."
            : status.StatusText;
    }

    public void ResetBreweryCelebrationStatus(string statusText = "Status not loaded.")
    {
        AutoCelebrationCanStart = false;
        AutoCelebrationRemainingSeconds = null;
        AutoCelebrationStatusText = statusText;
    }

    /// <summary>
    /// Returns the smallest positive remaining-seconds across all enabled
    /// rows, or <c>null</c> if any enabled row has no current queue (which
    /// means the group is ready to run again).
    /// </summary>
    public int? ResolveGroupRemainingSeconds()
    {
        var enabled = Buildings
            .Where(item => item.IsEnabled && item.Exists && !string.IsNullOrWhiteSpace(item.SelectedTroop))
            .ToList();
        if (enabled.Count <= 0)
        {
            return null;
        }

        if (enabled.Any(item => (item.QueueRemainingSeconds ?? 0) <= 0))
        {
            return null;
        }

        return enabled
            .Select(item => item.QueueRemainingSeconds ?? 0)
            .Where(seconds => seconds > 0)
            .DefaultIfEmpty(0)
            .Min();
    }

    public int? ResolveBreweryCelebrationGroupRemainingSeconds()
    {
        if (!AutoCelebrationEnabled || !IsAutoCelebrationAvailableForCurrentTribe)
        {
            return null;
        }

        return AutoCelebrationRemainingSeconds is > 0
            ? AutoCelebrationRemainingSeconds
            : null;
    }

    /// <summary>Decrements every row's countdown by one second. Called by the clock timer.</summary>
    public void TickCountdowns()
    {
        if (Buildings.Count <= 0)
        {
            return;
        }

        foreach (var option in Buildings)
        {
            option.TickOneSecond();
        }

        if (AutoCelebrationRemainingSeconds is > 0)
        {
            AutoCelebrationRemainingSeconds = Math.Max(0, AutoCelebrationRemainingSeconds.Value - 1);
            if (AutoCelebrationRemainingSeconds == 0)
            {
                AutoCelebrationCanStart = true;
                AutoCelebrationStatusText = "Ready.";
                AutoCelebrationRemainingSeconds = null;
            }
        }
    }

    public bool AutoCelebrationEnabled
    {
        get => _autoCelebrationEnabled;
        set
        {
            var normalized = IsAutoCelebrationAvailableForCurrentTribe && value;
            if (!SetProperty(ref _autoCelebrationEnabled, normalized))
            {
                return;
            }

            _autoCelebrationExplicitlyConfigured = true;
            if (!normalized)
            {
                AutoCelebrationCanStart = false;
                if (IsAutoCelebrationAvailableForCurrentTribe)
                {
                    AutoCelebrationStatusText = "Disabled.";
                }
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool IsAutoCelebrationAvailableForCurrentTribe
    {
        get => _isAutoCelebrationAvailableForCurrentTribe;
        private set
        {
            if (!SetProperty(ref _isAutoCelebrationAvailableForCurrentTribe, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAutoCelebrationCheckboxEnabled));
            OnPropertyChanged(nameof(AutoCelebrationTimerText));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBackground));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBorderBrush));
            OnPropertyChanged(nameof(AutoCelebrationBadgeForeground));
        }
    }

    public bool IsAutoCelebrationCheckboxEnabled => IsAutoCelebrationAvailableForCurrentTribe;

    public bool AutoCelebrationCanStart
    {
        get => _autoCelebrationCanStart;
        private set
        {
            if (!SetProperty(ref _autoCelebrationCanStart, value))
            {
                return;
            }

            OnPropertyChanged(nameof(AutoCelebrationTimerText));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBackground));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBorderBrush));
            OnPropertyChanged(nameof(AutoCelebrationBadgeForeground));
        }
    }

    public int? AutoCelebrationRemainingSeconds
    {
        get => _autoCelebrationRemainingSeconds;
        private set
        {
            var normalized = value.HasValue ? Math.Max(0, value.Value) : (int?)null;
            if (!SetProperty(ref _autoCelebrationRemainingSeconds, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(AutoCelebrationTimerText));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBackground));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBorderBrush));
            OnPropertyChanged(nameof(AutoCelebrationBadgeForeground));
        }
    }

    public string AutoCelebrationStatusText
    {
        get => _autoCelebrationStatusText;
        private set => SetProperty(ref _autoCelebrationStatusText, string.IsNullOrWhiteSpace(value) ? "Status unavailable." : value.Trim());
    }

    public string AutoCelebrationTimerText
    {
        get
        {
            if (!IsAutoCelebrationAvailableForCurrentTribe)
            {
                return "N/A";
            }

            if (AutoCelebrationRemainingSeconds is > 0)
            {
                var time = TimeSpan.FromSeconds(AutoCelebrationRemainingSeconds.Value);
                var totalHours = (int)Math.Floor(time.TotalHours);
                return $"{totalHours:00}:{time.Minutes:00}";
            }

            return AutoCelebrationCanStart ? "Ready" : "N/A";
        }
    }

    public string AutoCelebrationBadgeBackground => !IsAutoCelebrationAvailableForCurrentTribe
        ? "#E5E7EB"
        : AutoCelebrationRemainingSeconds is > 0
            ? "#FEF3C7"
            : AutoCelebrationCanStart
                ? "#DCFCE7"
                : "#E5E7EB";

    public string AutoCelebrationBadgeBorderBrush => !IsAutoCelebrationAvailableForCurrentTribe
        ? "#9CA3AF"
        : AutoCelebrationRemainingSeconds is > 0
            ? "#F59E0B"
            : AutoCelebrationCanStart
                ? "#22C55E"
                : "#9CA3AF";

    public string AutoCelebrationBadgeForeground => !IsAutoCelebrationAvailableForCurrentTribe
        ? "#4B5563"
        : AutoCelebrationRemainingSeconds is > 0
            ? "#B45309"
            : AutoCelebrationCanStart
                ? "#15803D"
                : "#4B5563";

    private void OnOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isConfigSuppressed)
        {
            return;
        }

        if (Array.IndexOf(RelevantOptionProperties, e.PropertyName) < 0)
        {
            return;
        }

        ConfigChanged?.Invoke();
    }
}
