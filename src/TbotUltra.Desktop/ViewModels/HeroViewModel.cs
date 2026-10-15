using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Hero / Adventures panel. It owns the panel state and
/// command enablement; <see cref="HeroPanelService"/> owns its persistence and Worker calls.
///
/// Owns:
///   - <see cref="AttributePriorityItems"/> — arrow-orderable hero attributes.
///   - The three status text fields shown on the Hero card.
///   - Manual-panel command enablement and request signals.
///   - Pure helpers for parsing and serializing the priority list and for
///     applying a stats snapshot to the items collection.
///
/// Host-only state remains on MainWindow:
///   - The blocked-reason key + IsHeroGroupBlocked() helper
///   - Lifecycle, dialog, cancellation, and dashboard integration bridges.
/// </summary>
public sealed class HeroViewModel : BaseViewModel
{
    private readonly RelayCommand _refreshAdventuresCommand;
    private readonly RelayCommand _refreshHpCommand;
    private readonly RelayCommand _refreshStatsCommand;
    private readonly RelayCommand _refreshInventoryCommand;
    private readonly RelayCommand<HeroAttributePriorityItem> _moveAttributeUpCommand;
    private readonly RelayCommand<HeroAttributePriorityItem> _moveAttributeDownCommand;
    private bool _isManualOperationRunning;
    private static readonly string[] DefaultPriorityOrder =
        ["resources", "fighting_strength", "offence_bonus", "defence_bonus"];

    private string _attributesStatusText = "Hero stats not loaded.";
    private string _adventureCountText = "?";
    private string _heroHpText = "?";
    private string _adventureStatusText = "Adventures not loaded.";
    private string _heroStatusText = "Unknown";
    private LoopTaskOption? _heroLoopTask;

    private string _heroInventoryWood = "-";
    private string _heroInventoryClay = "-";
    private string _heroInventoryIron = "-";
    private string _heroInventoryCrop = "-";
    private string _heroInventoryStatusText = "Hero inventory not loaded.";
    private HeroInventoryResources? _lastObservedInventory;
    private string? _lastObservedInventoryAccountName;
    private string? _lastObservedInventoryServerUrl;

    private bool _heroResourceMaxUseEnabled = true;
    private int _heroResourceMaxUsePerResource = 5000;
    private bool _heroResourceUseConstruction = true;
    private bool _heroResourceUseSmithy;
    private bool _heroResourceUseBrewery;
    private bool _heroResourceUseTownHall;

    private int _minHpForAdventure = 60;
    private bool _autoRevive = true;
    private bool _autoAssignPoints = true;
    private bool _autoUseOintments;
    private int _ointmentTargetHpPercent = 100;
    private bool _isAdventurePickTop;
    private bool _isAdventurePickShortest = true;
    private bool _continuousAdventures;
    private bool _increaseAdventuresToHard;
    private bool _reduceAdventureTime;
    private int _adventureVideoChancePercent = 70;
    private bool _heroRaidingEnabled;
    private int _heroRaidingMinimumHpPercent = 40;
    private int _heroRaidingMaximumDistance = 20;
    private int _heroRaidingObservationMinutes = 60;
    private int _heroRaidingMinimumCandidates = 3;
    private string _heroRaidingPriority = "raiding";
    private bool _raidWood = true;
    private bool _raidClay = true;
    private bool _raidIron = true;
    private bool _raidCrop25;
    private bool _raidCrop50;
    private bool _raidWoodCrop = true;
    private bool _raidClayCrop = true;
    private bool _raidIronCrop = true;
    private string _heroRaidingStatusText = "No oasis scan yet.";
    private bool _isHeroRaidingScanRunning;

    public HeroViewModel()
    {
        _refreshAdventuresCommand = new RelayCommand(() => RefreshAdventuresRequested?.Invoke(), CanRunManualCommand);
        _refreshHpCommand = new RelayCommand(() => RefreshHpRequested?.Invoke(), CanRunManualCommand);
        _refreshStatsCommand = new RelayCommand(() => RefreshStatsRequested?.Invoke(), CanRunManualCommand);
        _refreshInventoryCommand = new RelayCommand(() => RefreshInventoryRequested?.Invoke(), CanRunManualCommand);
        _moveAttributeUpCommand = new RelayCommand<HeroAttributePriorityItem>(
            item => MoveAttributePriority(item, -1),
            item => AttributePriorityItems.IndexOf(item) > 0);
        _moveAttributeDownCommand = new RelayCommand<HeroAttributePriorityItem>(
            item => MoveAttributePriority(item, 1),
            item => AttributePriorityItems.IndexOf(item) >= 0
                && AttributePriorityItems.IndexOf(item) < AttributePriorityItems.Count - 1);
    }

    /// <summary>
    /// Optional sink for [ui-apply] trace lines. Defaulted to a no-op so
    /// the VM can be unit-tested without a logger; MainWindow assigns this
    /// to AppendLog after construction.
    /// </summary>
    public Action<string> Logger { get; set; } = static _ => { };

    /// <summary>
    /// Drag-orderable list of hero attributes shown in the priority list.
    /// The collection is created once and re-populated in place so XAML
    /// bindings to <c>ItemsSource</c> stay stable across config reloads.
    /// </summary>
    public ObservableCollection<HeroAttributePriorityItem> AttributePriorityItems { get; } = [];
    public ObservableCollection<HeroRaidingCandidateItem> HeroRaidingCandidates { get; } = [];
    public event Action<int, int, bool>? HeroRaidingExclusionChanged;

    public string HeroRaidingStatusText
    {
        get => _heroRaidingStatusText;
        set => SetProperty(ref _heroRaidingStatusText, value);
    }

    public bool IsHeroRaidingScanRunning
    {
        get => _isHeroRaidingScanRunning;
        set
        {
            if (SetProperty(ref _isHeroRaidingScanRunning, value))
                OnPropertyChanged(nameof(CanScanHeroRaiding));
        }
    }

    public bool CanScanHeroRaiding => !IsHeroRaidingScanRunning;

    public void ApplyHeroRaidingScan(HeroRaidingScanResult scan)
    {
        foreach (var old in HeroRaidingCandidates)
            old.PropertyChanged -= HeroRaidingCandidate_PropertyChanged;
        HeroRaidingCandidates.Clear();
        foreach (var candidate in scan.Candidates)
        {
            var item = new HeroRaidingCandidateItem(candidate, HeroRaidingMinimumHpPercent);
            item.PropertyChanged += HeroRaidingCandidate_PropertyChanged;
            HeroRaidingCandidates.Add(item);
        }
        HeroRaidingStatusText = scan.Message;
    }

    private void HeroRaidingCandidate_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HeroRaidingCandidateItem.IsExcluded)
            && sender is HeroRaidingCandidateItem candidate)
            HeroRaidingExclusionChanged?.Invoke(candidate.X, candidate.Y, candidate.IsExcluded);
    }

    public ICommand RefreshAdventuresCommand => _refreshAdventuresCommand;
    public ICommand RefreshHpCommand => _refreshHpCommand;
    public ICommand RefreshStatsCommand => _refreshStatsCommand;
    public ICommand RefreshInventoryCommand => _refreshInventoryCommand;
    public ICommand MoveAttributeUpCommand => _moveAttributeUpCommand;
    public ICommand MoveAttributeDownCommand => _moveAttributeDownCommand;

    public event Action? RefreshAdventuresRequested;
    public event Action? RefreshHpRequested;
    public event Action? RefreshStatsRequested;
    public event Action? RefreshInventoryRequested;
    public event Action? AttributePriorityChanged;

    public void SetManualOperationRunning(bool isRunning)
    {
        if (!SetProperty(ref _isManualOperationRunning, isRunning))
        {
            return;
        }

        _refreshAdventuresCommand.RaiseCanExecuteChanged();
        _refreshHpCommand.RaiseCanExecuteChanged();
        _refreshStatsCommand.RaiseCanExecuteChanged();
        _refreshInventoryCommand.RaiseCanExecuteChanged();
    }

    private bool CanRunManualCommand() => !_isManualOperationRunning;

    /// <summary>
    /// Caption under the priority list. Typically "Free points: N" after a
    /// successful stats read, or an error message after a failed refresh.
    /// </summary>
    public string AttributesStatusText
    {
        get => _attributesStatusText;
        set => SetProperty(ref _attributesStatusText, value);
    }

    /// <summary>
    /// Adventure-count badge on the Hero card. Shown as "?" until the first
    /// successful read, otherwise the integer count.
    /// </summary>
    public string AdventureCountText
    {
        get => _adventureCountText;
        set
        {
            if (SetProperty(ref _adventureCountText, value))
            {
                OnPropertyChanged(nameof(HeroReadyText));
            }
        }
    }

    /// <summary>
    /// Text for the Hero status box while the hero is home and idle (<see cref="HeroStatusText"/> ==
    /// "Ready"). Reads "No adventures" when none are available so an idle hero with nothing to do is
    /// distinguishable from one ready to depart; otherwise "Ready". Recomputed whenever the hero status
    /// or the adventure count changes, so a newly discovered adventure flips it back to "Ready".
    /// </summary>
    public string HeroReadyText =>
        string.Equals(_heroStatusText, "Ready", StringComparison.OrdinalIgnoreCase)
        && string.Equals(_adventureCountText?.Trim(), "0", StringComparison.Ordinal)
            ? "No adventures"
            : "Ready";

    /// <summary>Current hero HP read from the always-visible top-bar health arc.</summary>
    public string HeroHpText
    {
        get => _heroHpText;
        set => SetProperty(ref _heroHpText, string.IsNullOrWhiteSpace(value) ? "?" : value);
    }

    /// <summary>
    /// Status line under the adventure count. Shows the countdown when the
    /// hero is away, or refresh status / error messages otherwise.
    /// </summary>
    public string AdventureStatusText
    {
        get => _adventureStatusText;
        set => SetProperty(ref _adventureStatusText, value);
    }

    public string HeroStatusText
    {
        get => _heroStatusText;
        set
        {
            if (SetProperty(ref _heroStatusText, string.IsNullOrWhiteSpace(value) ? "Unknown" : value))
            {
                OnPropertyChanged(nameof(HeroReadyText));
            }
        }
    }

    /// <summary>The same Hero task instance displayed and ticked by the Automation Loop card.</summary>
    public LoopTaskOption? HeroLoopTask
    {
        get => _heroLoopTask;
        set => SetProperty(ref _heroLoopTask, value);
    }

    /// <summary>Hero inventory resource amounts. Always shown (default "-") so they can be
    /// refreshed in place by <see cref="ApplyInventory"/>.</summary>
    public string HeroInventoryWood
    {
        get => _heroInventoryWood;
        set => SetProperty(ref _heroInventoryWood, value);
    }

    public string HeroInventoryClay
    {
        get => _heroInventoryClay;
        set => SetProperty(ref _heroInventoryClay, value);
    }

    public string HeroInventoryIron
    {
        get => _heroInventoryIron;
        set => SetProperty(ref _heroInventoryIron, value);
    }

    public string HeroInventoryCrop
    {
        get => _heroInventoryCrop;
        set => SetProperty(ref _heroInventoryCrop, value);
    }

    public string HeroInventoryStatusText
    {
        get => _heroInventoryStatusText;
        set => SetProperty(ref _heroInventoryStatusText, value);
    }

    /// <summary>Legacy config flag. Max-use capping is now always active in the per-village popup.</summary>
    public bool HeroResourceMaxUseEnabled
    {
        get => _heroResourceMaxUseEnabled;
        set => SetProperty(ref _heroResourceMaxUseEnabled, value);
    }

    /// <summary>Per-resource cap (in units) for hero resource use.</summary>
    public int HeroResourceMaxUsePerResource
    {
        get => _heroResourceMaxUsePerResource;
        set => SetProperty(ref _heroResourceMaxUsePerResource, Math.Max(0, value));
    }

    /// <summary>Per-consumer gate: allow construction (buildings/resource fields) to top up from the hero.</summary>
    public bool HeroResourceUseConstruction
    {
        get => _heroResourceUseConstruction;
        set => SetProperty(ref _heroResourceUseConstruction, value);
    }

    /// <summary>Per-consumer gate: allow smithy troop upgrades to top up from the hero.</summary>
    public bool HeroResourceUseSmithy
    {
        get => _heroResourceUseSmithy;
        set => SetProperty(ref _heroResourceUseSmithy, value);
    }

    /// <summary>Per-consumer gate: allow brewery celebrations to top up from the hero.</summary>
    public bool HeroResourceUseBrewery
    {
        get => _heroResourceUseBrewery;
        set => SetProperty(ref _heroResourceUseBrewery, value);
    }

    /// <summary>Per-consumer gate: allow Town Hall celebrations to top up from the hero.</summary>
    public bool HeroResourceUseTownHall
    {
        get => _heroResourceUseTownHall;
        set => SetProperty(ref _heroResourceUseTownHall, value);
    }

    /// <summary>Applies a hero inventory read to the four resource fields.</summary>
    public void ApplyInventory(HeroInventoryResources resources)
    {
        if (resources is null)
        {
            return;
        }

        HeroInventoryWood = resources.Wood.ToString();
        HeroInventoryClay = resources.Clay.ToString();
        HeroInventoryIron = resources.Iron.ToString();
        HeroInventoryCrop = resources.Crop.ToString();
        // No "updated" status line — the values themselves show the state. Clear any prior message
        // (e.g. the initial "not loaded") so a stale line doesn't linger after a successful read.
        HeroInventoryStatusText = string.Empty;
    }

    public void SeedObservedInventory(string? accountName, string? serverUrl, HeroInventoryResources? resources)
    {
        _lastObservedInventoryAccountName = accountName;
        _lastObservedInventoryServerUrl = serverUrl;
        _lastObservedInventory = resources;
    }

    public bool ApplyObservedInventory(string? accountName, string? serverUrl, HeroInventoryResources resources)
    {
        var previous = string.Equals(_lastObservedInventoryAccountName, accountName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_lastObservedInventoryServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase)
                ? _lastObservedInventory
                : null;
        var increased = ConstructionQueueState.HasHeroInventoryIncreased(previous, resources);
        SeedObservedInventory(accountName, serverUrl, resources);
        ApplyInventory(resources);
        return increased;
    }

    public void ResetRuntimeState()
    {
        foreach (var candidate in HeroRaidingCandidates)
            candidate.PropertyChanged -= HeroRaidingCandidate_PropertyChanged;
        HeroRaidingCandidates.Clear();
        HeroRaidingStatusText = "No oasis scan yet.";
        IsHeroRaidingScanRunning = false;
        AttributesStatusText = "Hero stats not loaded.";
        AdventureCountText = "?";
        HeroHpText = "?";
        AdventureStatusText = "Adventures not loaded.";
        HeroStatusText = "Unknown";
        HeroInventoryWood = "-";
        HeroInventoryClay = "-";
        HeroInventoryIron = "-";
        HeroInventoryCrop = "-";
        HeroInventoryStatusText = "Hero inventory not loaded.";
        SeedObservedInventory(string.Empty, string.Empty, null);

        foreach (var item in AttributePriorityItems)
        {
            item.PointsText = "-";
        }
    }

    /// <summary>
    /// Minimum hero HP required before an adventure is queued (1-100).
    /// Bound to the min-HP TextBox in HeroPanel.xaml.
    /// </summary>
    public int MinHpForAdventure
    {
        get => _minHpForAdventure;
        set => SetProperty(ref _minHpForAdventure, Math.Clamp(value, 1, 100));
    }

    /// <summary>True to send the hero to revive when HP is below the threshold.</summary>
    public bool AutoRevive
    {
        get => _autoRevive;
        set => SetProperty(ref _autoRevive, value);
    }

    /// <summary>True to auto-assign attribute points based on the priority list.</summary>
    public bool AutoAssignPoints
    {
        get => _autoAssignPoints;
        set => SetProperty(ref _autoAssignPoints, value);
    }

    /// <summary>True to use hero ointments before adventures when HP is below the configured threshold.</summary>
    public bool AutoUseOintments
    {
        get => _autoUseOintments;
        set => SetProperty(ref _autoUseOintments, value);
    }

    /// <summary>HP target for automatic ointment use. Allowed values are 50-90 in tens, or 100 (Max).</summary>
    public int OintmentTargetHpPercent
    {
        get => _ointmentTargetHpPercent;
        set => SetProperty(ref _ointmentTargetHpPercent, NormalizeOintmentTarget(value));
    }

    private static int NormalizeOintmentTarget(int value)
        => value is 50 or 60 or 70 or 80 or 90 or 100 ? value : 100;

    public bool HeroRaidingEnabled
    {
        get => _heroRaidingEnabled;
        set => SetProperty(ref _heroRaidingEnabled, value);
    }

    public int HeroRaidingMinimumHpPercent
    {
        get => _heroRaidingMinimumHpPercent;
        set => SetProperty(ref _heroRaidingMinimumHpPercent, Math.Clamp(value, 1, 100));
    }

    public int HeroRaidingMaximumDistance
    {
        get => _heroRaidingMaximumDistance;
        set => SetProperty(ref _heroRaidingMaximumDistance, Math.Clamp(value, 1, 50));
    }

    public int HeroRaidingObservationMinutes
    {
        get => _heroRaidingObservationMinutes;
        set => SetProperty(ref _heroRaidingObservationMinutes, Math.Clamp(value, 1, 120));
    }

    public int HeroRaidingMinimumCandidates
    {
        get => _heroRaidingMinimumCandidates;
        set => SetProperty(ref _heroRaidingMinimumCandidates, Math.Clamp(value, 1, 20));
    }

    public string HeroRaidingPriority
    {
        get => _heroRaidingPriority;
        set => SetProperty(ref _heroRaidingPriority,
            string.Equals(value, "adventures", StringComparison.OrdinalIgnoreCase) ? "adventures" : "raiding");
    }

    public bool RaidWood { get => _raidWood; set => SetProperty(ref _raidWood, value); }
    public bool RaidClay { get => _raidClay; set => SetProperty(ref _raidClay, value); }
    public bool RaidIron { get => _raidIron; set => SetProperty(ref _raidIron, value); }
    public bool RaidCrop25 { get => _raidCrop25; set => SetProperty(ref _raidCrop25, value); }
    public bool RaidCrop50 { get => _raidCrop50; set => SetProperty(ref _raidCrop50, value); }
    public bool RaidWoodCrop { get => _raidWoodCrop; set => SetProperty(ref _raidWoodCrop, value); }
    public bool RaidClayCrop { get => _raidClayCrop; set => SetProperty(ref _raidClayCrop, value); }
    public bool RaidIronCrop { get => _raidIronCrop; set => SetProperty(ref _raidIronCrop, value); }

    public string HeroRaidingSelectedTypes => string.Join(',', new[]
    {
        RaidWood ? "Wood" : null,
        RaidClay ? "Clay" : null,
        RaidIron ? "Iron" : null,
        RaidWoodCrop ? "Wood+Crop" : null,
        RaidClayCrop ? "Clay+Crop" : null,
        RaidIronCrop ? "Iron+Crop" : null,
        RaidCrop25 ? "Crop25" : null,
        RaidCrop50 ? "Crop50" : null,
    }.Where(type => type is not null));

    /// <summary>
    /// Adventure pick order — top of the list. Mutually exclusive with
    /// <see cref="IsAdventurePickShortest"/>.
    /// </summary>
    public bool IsAdventurePickTop
    {
        get => _isAdventurePickTop;
        set
        {
            if (SetProperty(ref _isAdventurePickTop, value) && value)
            {
                IsAdventurePickShortest = false;
            }
        }
    }

    /// <summary>
    /// Adventure pick order — shortest distance. Mutually exclusive with
    /// <see cref="IsAdventurePickTop"/>.
    /// </summary>
    public bool IsAdventurePickShortest
    {
        get => _isAdventurePickShortest;
        set
        {
            if (SetProperty(ref _isAdventurePickShortest, value) && value)
            {
                IsAdventurePickTop = false;
            }
        }
    }

    /// <summary>True to keep re-queuing hero adventures while any remain.</summary>
    public bool ContinuousAdventures
    {
        get => _continuousAdventures;
        set => SetProperty(ref _continuousAdventures, value);
    }

    /// <summary>True to activate "Increased adventure danger to hard" (bonus video) before each
    /// adventure dispatch. Official Travian only.</summary>
    public bool IncreaseAdventuresToHard
    {
        get => _increaseAdventuresToHard;
        set => SetProperty(ref _increaseAdventuresToHard, value);
    }

    /// <summary>True to activate "Reduce adventure duration by 25%" (bonus video) before each
    /// adventure dispatch, after the increase-danger step. Official Travian only.</summary>
    public bool ReduceAdventureTime
    {
        get => _reduceAdventureTime;
        set => SetProperty(ref _reduceAdventureTime, value);
    }

    /// <summary>Independent chance to run each enabled Hard or -25% adventure video.</summary>
    public int AdventureVideoChancePercent
    {
        get => _adventureVideoChancePercent;
        set => SetProperty(ref _adventureVideoChancePercent, Math.Clamp(value, 0, 100));
    }

    /// <summary>String form of the adventure pick order, "top" or "shortest".</summary>
    public string AdventurePickOrder => IsAdventurePickTop ? "top" : "shortest";

    /// <summary>
    /// Loads all hero settings from a freshly read <see cref="BotOptions"/>.
    /// Also refreshes the priority list.
    /// </summary>
    public void LoadSettingsFromConfig(BotOptions options)
    {
        HeroRaidingEnabled = options.HeroRaidingEnabled;
        MinHpForAdventure = options.HeroMinHpForAdventure;
        AutoRevive = options.HeroAutoRevive;
        AutoAssignPoints = options.HeroAutoAssignPoints;
        AutoUseOintments = options.HeroAutoUseOintments;
        OintmentTargetHpPercent = options.HeroOintmentTargetHpPercent;
        HeroRaidingMinimumHpPercent = options.HeroRaidingMinimumHpPercent;
        HeroRaidingMaximumDistance = options.HeroRaidingMaximumDistance;
        HeroRaidingObservationMinutes = options.HeroRaidingObservationMinutes;
        HeroRaidingMinimumCandidates = options.HeroRaidingMinimumCandidates;
        HeroRaidingPriority = options.HeroRaidingPriority;
        var raidTypes = new HashSet<string>(
            (options.HeroRaidingSelectedTypes ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
        RaidWood = raidTypes.Contains("Wood");
        RaidClay = raidTypes.Contains("Clay");
        RaidIron = raidTypes.Contains("Iron");
        RaidCrop25 = raidTypes.Contains("Crop25") || raidTypes.Contains("Crop");
        RaidCrop50 = raidTypes.Contains("Crop50") || raidTypes.Contains("Crop");
        RaidWoodCrop = raidTypes.Contains("Wood+Crop");
        RaidClayCrop = raidTypes.Contains("Clay+Crop");
        RaidIronCrop = raidTypes.Contains("Iron+Crop");
        var topFirst = string.Equals(options.HeroAdventurePickOrder, "top", StringComparison.OrdinalIgnoreCase);
        if (topFirst)
        {
            IsAdventurePickTop = true;
        }
        else
        {
            IsAdventurePickShortest = true;
        }

        ContinuousAdventures = options.HeroContinuousAdventures;
        IncreaseAdventuresToHard = options.IncreaseAdventuresToHard;
        ReduceAdventureTime = options.ReduceAdventureTime;
        AdventureVideoChancePercent = options.HeroAdventureVideoChancePercent;
        HeroResourceMaxUseEnabled = options.HeroResourceMaxUseEnabled;
        HeroResourceMaxUsePerResource = options.HeroResourceMaxUsePerResource;
        HeroResourceUseConstruction = options.HeroResourceUseConstruction;
        HeroResourceUseSmithy = options.HeroResourceUseSmithy;
        HeroResourceUseBrewery = options.HeroResourceUseBrewery;
        HeroResourceUseTownHall = options.HeroResourceUseTownHall;
        LoadPriorityFromConfig(options.HeroStatPriority);
        LoadMaximumsFromConfig(options.HeroStatMaximums);
    }

    /// <summary>
    /// Re-populates <see cref="AttributePriorityItems"/> from a comma-separated
    /// priority string read from config. Preserves any existing
    /// <c>PointsText</c> values per-key so transient reloads don't blank the
    /// stats column.
    /// </summary>
    public void LoadPriorityFromConfig(string? configuredPriority)
    {
        var order = ParsePriorityForUi(configuredPriority);
        var existingPoints = AttributePriorityItems
            .ToDictionary(item => item.Key, item => item.PointsText, StringComparer.OrdinalIgnoreCase);
        var existingMaximums = AttributePriorityItems
            .ToDictionary(item => item.Key, item => item.MaxPoints, StringComparer.OrdinalIgnoreCase);
        AttributePriorityItems.Clear();

        for (var i = 0; i < order.Count; i++)
        {
            AttributePriorityItems.Add(new HeroAttributePriorityItem
            {
                Key = order[i],
                Title = GetAttributeTitle(order[i]),
                Order = i + 1,
                PointsText = existingPoints.GetValueOrDefault(order[i], "-"),
                MaxPoints = existingMaximums.GetValueOrDefault(order[i], HeroAttributeMaximums.DefaultMaximum),
            });
        }

        RaiseAttributePriorityCommandState();
    }

    public void LoadMaximumsFromConfig(string? configuredMaximums)
    {
        var maximums = HeroAttributeMaximums.Parse(configuredMaximums);
        foreach (var item in AttributePriorityItems)
        {
            item.MaxPoints = maximums.GetValueOrDefault(item.Key, HeroAttributeMaximums.DefaultMaximum);
        }
    }

    /// <summary>
    /// Re-numbers <see cref="HeroAttributePriorityItem.Order"/> for each item
    /// based on its current position in the collection. Call after a move that
    /// reordered the list.
    /// </summary>
    public void UpdateOrders()
    {
        for (var i = 0; i < AttributePriorityItems.Count; i++)
        {
            AttributePriorityItems[i].Order = i + 1;
        }
    }

    /// <summary>
    /// Serializes the current priority order to the comma-separated string
    /// expected by the worker config (<c>HeroStatPriority</c>).
    /// </summary>
    public string BuildPriorityPayload()
    {
        return string.Join(",", AttributePriorityItems.Select(item => item.Key));
    }

    /// <summary>
    /// Updates each item's points value and the free-points caption from a
    /// fresh snapshot read off the Hero attributes page.
    /// </summary>
    public void ApplyAttributeSnapshot(HeroAttributeSnapshot snapshot)
    {
        Logger.Invoke(
            $"[ui-apply] free={snapshot.FreePoints} fight={snapshot.FightingStrength} off={snapshot.OffenceBonus} def={snapshot.DefenceBonus} res={snapshot.Resources}, items={AttributePriorityItems.Count}");

        foreach (var item in AttributePriorityItems)
        {
            var points = item.Key switch
            {
                "fighting_strength" => snapshot.FightingStrength,
                "offence_bonus" => snapshot.OffenceBonus,
                "defence_bonus" => snapshot.DefenceBonus,
                "resources" => snapshot.Resources,
                _ => 0,
            };
            item.PointsText = points.ToString();
        }

        AttributesStatusText = $"Free points: {snapshot.FreePoints}";
        var terminalStatus = string.Equals(snapshot.HeroState, "Dead", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.HeroState, "Reviving", StringComparison.OrdinalIgnoreCase);
        HeroStatusText = snapshot.HomeVillageHeroAway && !terminalStatus
            ? string.IsNullOrWhiteSpace(snapshot.MovementState) ? "Away" : snapshot.MovementState.Trim()
            : FormatHeroStatus(snapshot.HeroState, snapshot.ReviveRemainingSeconds);
    }

    private void MoveAttributePriority(HeroAttributePriorityItem item, int offset)
    {
        var oldIndex = AttributePriorityItems.IndexOf(item);
        var newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= AttributePriorityItems.Count)
        {
            return;
        }

        AttributePriorityItems.Move(oldIndex, newIndex);
        UpdateOrders();
        RaiseAttributePriorityCommandState();
        Logger.Invoke(
            $"[hero-attributes-ui] moved '{item.Title}' from priority {oldIndex + 1} to {newIndex + 1}.");
        AttributePriorityChanged?.Invoke();
    }

    private void RaiseAttributePriorityCommandState()
    {
        _moveAttributeUpCommand.RaiseCanExecuteChanged();
        _moveAttributeDownCommand.RaiseCanExecuteChanged();
    }

    public string BuildMaximumsPayload()
    {
        return HeroAttributeMaximums.Serialize(
            AttributePriorityItems.Select(item => new KeyValuePair<string, int>(item.Key, item.MaxPoints)));
    }

    public bool AreAllAttributeMaximumsReached()
    {
        return AttributePriorityItems.Count > 0
            && AttributePriorityItems.All(item =>
                int.TryParse(item.PointsText, out var points) && points >= item.MaxPoints);
    }

    private static string FormatHeroStatus(string? state, int? reviveRemainingSeconds)
    {
        var normalized = string.IsNullOrWhiteSpace(state) ? "Unknown" : state.Trim();
        if (string.Equals(normalized, "Reviving", StringComparison.OrdinalIgnoreCase)
            && reviveRemainingSeconds is int seconds
            && seconds >= 0)
        {
            return $"Reviving ({FormatDuration(seconds)})";
        }

        return normalized;
    }

    private static string FormatDuration(int totalSeconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        var hours = (int)Math.Floor(time.TotalHours);
        return $"{hours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }

    /// <summary>
    /// Parses a stored priority string into a normalized, deduplicated list
    /// of attribute keys. Unknown / missing keys fall back to the default
    /// resources/fight/off/def order so the list always has all four entries.
    /// </summary>
    public static List<string> ParsePriorityForUi(string? value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fighting_strength"] = "fighting_strength",
            ["fighting strength"] = "fighting_strength",
            ["fight"] = "fighting_strength",
            ["strength"] = "fighting_strength",
            ["offence_bonus"] = "offence_bonus",
            ["offence bonus"] = "offence_bonus",
            ["offense_bonus"] = "offence_bonus",
            ["offense bonus"] = "offence_bonus",
            ["offence"] = "offence_bonus",
            ["offense"] = "offence_bonus",
            ["off"] = "offence_bonus",
            ["attack"] = "offence_bonus",
            ["defence_bonus"] = "defence_bonus",
            ["defence bonus"] = "defence_bonus",
            ["defense_bonus"] = "defence_bonus",
            ["defense bonus"] = "defence_bonus",
            ["defence"] = "defence_bonus",
            ["defense"] = "defence_bonus",
            ["def"] = "defence_bonus",
            ["resources"] = "resources",
            ["resource"] = "resources",
            ["production"] = "resources",
        };

        var parsed = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => map.GetValueOrDefault(item, string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var fallback in DefaultPriorityOrder)
        {
            if (!parsed.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                parsed.Add(fallback);
            }
        }

        return parsed;
    }

    /// <summary>
    /// Maps an attribute key to the display title shown in the priority list.
    /// </summary>
    public static string GetAttributeTitle(string key) => key switch
    {
        "fighting_strength" => "Fighting strength",
        "offence_bonus" => "Offence bonus",
        "defence_bonus" => "Defence bonus",
        "resources" => "Resources",
        _ => key,
    };
}
