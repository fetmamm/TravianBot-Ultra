using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Input;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
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
public sealed partial class TroopTrainingViewModel : BaseViewModel
{
    private readonly RelayCommand _upgradeOptionsCommand;
    private readonly RelayCommand _syncSettingsCommand;
    private readonly RelayCommand _buildNowCommand;
    private readonly RelayCommand _refreshQueuesCommand;
    private readonly RelayCommand _checkCelebrationCommand;
    private bool _isManualRefreshRunning;
    private static readonly string[] RelevantOptionProperties =
    [
        nameof(TroopTrainingBuildingOption.IsEnabled),
        nameof(TroopTrainingBuildingOption.SelectedTroop),
        nameof(TroopTrainingBuildingOption.MaxQueueMode),
        nameof(TroopTrainingBuildingOption.AmountMode),
        nameof(TroopTrainingBuildingOption.KeepResourcesPercent),
        nameof(TroopTrainingBuildingOption.RunMode),
        nameof(TroopTrainingBuildingOption.MinimumTroopsEnabled),
        nameof(TroopTrainingBuildingOption.MinimumTroops),
        nameof(TroopTrainingBuildingOption.MaximumMinimumTroops),
        nameof(TroopTrainingBuildingOption.MinimumResourcesPercent),
        nameof(TroopTrainingBuildingOption.TimedMinMinutes),
        nameof(TroopTrainingBuildingOption.TimedMaxMinutes),
    ];

    private bool _isConfigSuppressed;
    private string _infoText = string.Empty;
    private bool _checkWood = true;
    private bool _checkClay = true;
    private bool _checkIron = true;
    private bool _checkCrop;
    private bool _automaticResourceSelection;
    private int _fallbackCooldownSeconds = 300;
    private bool _autoCelebrationEnabled;
    private bool _autoCelebrationExplicitlyConfigured;
    private bool _isAutoCelebrationAvailableForCurrentTribe;
    private bool _autoCelebrationCanStart;
    private int? _autoCelebrationRemainingSeconds;
    // Display-only mirror of the dashboard automation-loop Brewery Celebration row timer, so the
    // troops-tab celebration badge shows the same countdown (e.g. a deferred "next try" wait).
    private int? _breweryLoopWaitSeconds;
    private string _autoCelebrationStatusText = "Teutons only.";
    private bool _breweryExists;
    private bool _npcTradeEnabled = true;
    private bool _npcTradeConstructionEnabled = true;
    private int _npcTradeThresholdPercent = 90;
    private bool _npcTradeAnalyzeWood = true;
    private bool _npcTradeAnalyzeClay = true;
    private bool _npcTradeAnalyzeIron = true;
    private bool _npcTradeAnalyzeCrop = true;
    private bool _npcTradeBuildTimeLimitEnabled = true;
    private int _npcTradeBuildTimeLimitSeconds = 300;
    private bool _allowGoldSpending = true;
    private int _goldLimit = 800;

    public TroopTrainingViewModel()
    {
        _upgradeOptionsCommand = new RelayCommand(() => UpgradeOptionsRequested?.Invoke());
        _syncSettingsCommand = new RelayCommand(() => SyncSettingsRequested?.Invoke());
        _buildNowCommand = new RelayCommand(() => BuildNowRequested?.Invoke());
        _refreshQueuesCommand = new RelayCommand(() => RefreshQueuesRequested?.Invoke(), CanRefreshManualStatus);
        _checkCelebrationCommand = new RelayCommand(() => CheckCelebrationRequested?.Invoke(), CanRefreshManualStatus);
    }

    /// <summary>The three building rules shown as rows on the panel.</summary>
    public ObservableCollection<TroopTrainingBuildingOption> Buildings { get; } = [];

    public ICommand UpgradeOptionsCommand => _upgradeOptionsCommand;
    public ICommand SyncSettingsCommand => _syncSettingsCommand;
    public ICommand BuildNowCommand => _buildNowCommand;
    public ICommand RefreshQueuesCommand => _refreshQueuesCommand;
    public ICommand CheckCelebrationCommand => _checkCelebrationCommand;

    public event Action? UpgradeOptionsRequested;
    public event Action? SyncSettingsRequested;
    public event Action? BuildNowRequested;
    public event Action? RefreshQueuesRequested;
    public event Action? CheckCelebrationRequested;

    public void SetManualRefreshRunning(bool isRunning)
    {
        if (!SetProperty(ref _isManualRefreshRunning, isRunning))
        {
            return;
        }

        _refreshQueuesCommand.RaiseCanExecuteChanged();
        _checkCelebrationCommand.RaiseCanExecuteChanged();
    }

    private bool CanRefreshManualStatus() => !_isManualRefreshRunning;

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

    public bool AutomaticResourceSelection
    {
        get => _automaticResourceSelection;
        set
        {
            if (!SetProperty(ref _automaticResourceSelection, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsManualResourceSelectionEnabled));
            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool IsManualResourceSelectionEnabled => !AutomaticResourceSelection;

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
                        option.MinimumTroopsEnabled = options.TroopTrainingBarracksMinimumTroopsEnabled;
                        option.MinimumTroops = options.TroopTrainingBarracksMinimumTroops;
                        option.MaximumMinimumTroops = options.TroopTrainingBarracksMaximumMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingBarracksMinimumResourcesPercent;
                        option.TimedMinMinutes = options.TroopTrainingBarracksTimedMinMinutes;
                        option.TimedMaxMinutes = options.TroopTrainingBarracksTimedMaxMinutes;
                        break;
                    case TroopTrainingBuildingType.Stable:
                        option.IsEnabled = options.TroopTrainingStableEnabled;
                        option.SelectedTroop = options.TroopTrainingStableTroopType;
                        option.MaxQueueMode = options.TroopTrainingStableMaxQueueHours;
                        option.AmountMode = options.TroopTrainingStableAmountMode;
                        option.KeepResourcesPercent = options.TroopTrainingStableKeepResourcesPercent;
                        option.RunMode = options.TroopTrainingStableRunMode;
                        option.MinimumTroopsEnabled = options.TroopTrainingStableMinimumTroopsEnabled;
                        option.MinimumTroops = options.TroopTrainingStableMinimumTroops;
                        option.MaximumMinimumTroops = options.TroopTrainingStableMaximumMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingStableMinimumResourcesPercent;
                        option.TimedMinMinutes = options.TroopTrainingStableTimedMinMinutes;
                        option.TimedMaxMinutes = options.TroopTrainingStableTimedMaxMinutes;
                        break;
                    case TroopTrainingBuildingType.Workshop:
                        option.IsEnabled = options.TroopTrainingWorkshopEnabled;
                        option.SelectedTroop = options.TroopTrainingWorkshopTroopType;
                        option.MaxQueueMode = options.TroopTrainingWorkshopMaxQueueHours;
                        option.AmountMode = options.TroopTrainingWorkshopAmountMode;
                        option.KeepResourcesPercent = options.TroopTrainingWorkshopKeepResourcesPercent;
                        option.RunMode = options.TroopTrainingWorkshopRunMode;
                        option.MinimumTroopsEnabled = options.TroopTrainingWorkshopMinimumTroopsEnabled;
                        option.MinimumTroops = options.TroopTrainingWorkshopMinimumTroops;
                        option.MaximumMinimumTroops = options.TroopTrainingWorkshopMaximumMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingWorkshopMinimumResourcesPercent;
                        option.TimedMinMinutes = options.TroopTrainingWorkshopTimedMinMinutes;
                        option.TimedMaxMinutes = options.TroopTrainingWorkshopTimedMaxMinutes;
                        break;
                }
            }

            CheckWood = options.TroopTrainingBarracksCheckWood;
            CheckClay = options.TroopTrainingBarracksCheckClay;
            CheckIron = options.TroopTrainingBarracksCheckIron;
            CheckCrop = options.TroopTrainingBarracksCheckCrop;
            AutomaticResourceSelection = options.TroopTrainingBarracksAutomaticResourceSelection;
            FallbackCooldownSeconds = options.TroopTrainingFallbackCooldownSeconds;
            NpcTradeEnabled = options.NpcTradeEnabled;
            NpcTradeConstructionEnabled = options.NpcTradeConstructionEnabled;
            NpcTradeThresholdPercent = options.NpcTradeThresholdPercent;
            NpcTradeAnalyzeWood = options.NpcTradeAnalyzeWood;
            NpcTradeAnalyzeClay = options.NpcTradeAnalyzeClay;
            NpcTradeAnalyzeIron = options.NpcTradeAnalyzeIron;
            NpcTradeAnalyzeCrop = options.NpcTradeAnalyzeCrop;
            NpcTradeBuildTimeLimitEnabled = options.NpcTradeBuildTimeLimitEnabled;
            NpcTradeBuildTimeLimitSeconds = options.NpcTradeBuildTimeLimitSeconds;
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
    /// Applies a per-village troop-training override onto the building rows (enable / troop / amount mode /
    /// run trigger / checks / fallback). Account-wide settings (NPC trade, gold, celebration) are left
    /// untouched. Suppresses change events so loading a village's settings never re-persists them. Used
    /// when the selected village changes or after the "Troop settings" popup saves.
    /// </summary>
    public void ApplyVillageTrainingPayload(TroopTrainingPayload payload)
    {
        _isConfigSuppressed = true;
        try
        {
            foreach (var option in Buildings)
            {
                var building = option.BuildingType switch
                {
                    TroopTrainingBuildingType.Barracks => payload.Barracks,
                    TroopTrainingBuildingType.Stable => payload.Stable,
                    _ => payload.Workshop,
                };

                option.IsEnabled = building.Enabled;
                option.SelectedTroop = building.TroopType;
                option.MaxQueueMode = building.MaxQueueHours;
                option.AmountMode = building.AmountMode;
                option.KeepResourcesPercent = building.KeepResourcesPercent;
                option.RunMode = building.RunMode;
                option.MinimumTroopsEnabled = building.MinimumTroopsEnabled;
                option.MinimumTroops = building.MinimumTroops;
                option.MaximumMinimumTroops = building.MaximumMinimumTroops;
                option.MinimumResourcesPercent = building.MinimumResourcesPercent;
                option.TimedMinMinutes = building.TimedMinMinutes;
                option.TimedMaxMinutes = building.TimedMaxMinutes;
            }

            // The Troops tab shows one Wood/Clay/Iron/Crop set shared by all buildings; the payload stores
            // the same flags per building, so read them from Barracks.
            CheckWood = payload.Barracks.CheckWood;
            CheckClay = payload.Barracks.CheckClay;
            CheckIron = payload.Barracks.CheckIron;
            CheckCrop = payload.Barracks.CheckCrop;
            AutomaticResourceSelection = payload.Barracks.AutomaticResourceSelection;
            FallbackCooldownSeconds = payload.FallbackCooldownSeconds;
        }
        finally
        {
            _isConfigSuppressed = false;
        }
    }

    /// <summary>
    /// Builds a per-village troop-training override from the current building rows, so the Troops tab's
    /// edits can be persisted as the selected village's override (mirrors the "Troop settings" popup).
    /// </summary>
    public TroopTrainingPayload BuildVillageTrainingPayload()
    {
        TroopTrainingBuildingPayload BuildFor(TroopTrainingBuildingType buildingType)
        {
            var option = Buildings.First(item => item.BuildingType == buildingType);
            return new TroopTrainingBuildingPayload(
                option.IsEnabled,
                option.SelectedTroop,
                option.MaxQueueMode,
                option.AmountMode,
                option.KeepResourcesPercent,
                option.RunMode,
                option.MinimumTroops,
                option.MinimumResourcesPercent,
                option.TimedMinMinutes,
                option.TimedMaxMinutes,
                CheckWood,
                CheckClay,
                CheckIron,
                CheckCrop)
            {
                MinimumTroopsEnabled = option.MinimumTroopsEnabled,
                MaximumMinimumTroops = option.MaximumMinimumTroops,
                AutomaticResourceSelection = AutomaticResourceSelection,
            };
        }

        return new TroopTrainingPayload(
            BuildFor(TroopTrainingBuildingType.Barracks),
            BuildFor(TroopTrainingBuildingType.Stable),
            BuildFor(TroopTrainingBuildingType.Workshop),
            FallbackCooldownSeconds);
    }

    public bool TryValidateMinimumTroopRanges(out string error)
    {
        var invalid = Buildings.FirstOrDefault(option => option.MinimumTroopsEnabled && !option.HasValidMinimumTroopRange);
        if (invalid is not null)
        {
            error = $"{invalid.Title}: minimum troops must use whole numbers from 1 to 10,000 and Max must be at least Min.";
            return false;
        }

        var requiresResourceSelection = Buildings.Any(option => option.IsEnabled && option.UsesResourcePercentMode);
        if (requiresResourceSelection && !AutomaticResourceSelection && !CheckWood && !CheckClay && !CheckIron && !CheckCrop)
        {
            error = "Select at least one resource when an enabled troop building uses % resources.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Writes the account-wide troop-related settings (NPC trade, gold, brewery celebration) into a freshly
    /// loaded config <see cref="JsonObject"/>. Per-village building rules are stored separately via
    /// <see cref="BuildVillageTrainingPayload"/>. Caller persists (e.g. <c>BotConfigStore.Save</c>).
    /// </summary>
    public void WriteToConfig(JsonObject config)
    {
        config[BotOptionPayloadKeys.BreweryAutoCelebrationEnabled] = AutoCelebrationEnabled;
        config[BotOptionPayloadKeys.NpcTradeEnabled] = NpcTradeEnabled;
        config[BotOptionPayloadKeys.NpcTradeConstructionEnabled] = NpcTradeConstructionEnabled;
        config[BotOptionPayloadKeys.NpcTradeThresholdPercent] = NpcTradeThresholdPercent;
        config[BotOptionPayloadKeys.NpcTradeAnalyzeWood] = NpcTradeAnalyzeWood;
        config[BotOptionPayloadKeys.NpcTradeAnalyzeClay] = NpcTradeAnalyzeClay;
        config[BotOptionPayloadKeys.NpcTradeAnalyzeIron] = NpcTradeAnalyzeIron;
        config[BotOptionPayloadKeys.NpcTradeAnalyzeCrop] = NpcTradeAnalyzeCrop;
        config[BotOptionPayloadKeys.NpcTradeBuildTimeLimitEnabled] = NpcTradeBuildTimeLimitEnabled;
        config[BotOptionPayloadKeys.NpcTradeBuildTimeLimitSeconds] = NpcTradeBuildTimeLimitSeconds;
        config[BotOptionPayloadKeys.AllowGoldSpending] = AllowGoldSpending;
        config[BotOptionPayloadKeys.GoldLimit] = GoldLimit;
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
