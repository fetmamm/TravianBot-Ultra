using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Hero / Adventures panel. First MVVM-migrated panel
/// in the desktop app — see <c>MainViewModel</c> doc-comment for context.
///
/// Owns:
///   - <see cref="AttributePriorityItems"/> — drag-orderable hero attributes.
///   - The three status text fields shown on the Hero card.
///   - Pure helpers for parsing and serializing the priority list and for
///     applying a stats snapshot to the items collection.
///
/// Hero state still on MainWindow (will migrate later):
///   - The drag-handler scratch state (anchor point, source item)
///   - The blocked-reason key + IsHeroGroupBlocked() helper
///   - The hide-mode suppression flag
///   - All the async / service-bound code (refresh stats, refresh adventures,
///     queue manage). That moves once the relevant services live in DI.
/// </summary>
public sealed class HeroViewModel : BaseViewModel
{
    private static readonly string[] DefaultPriorityOrder =
        ["fighting_strength", "offence_bonus", "defence_bonus", "resources"];

    private string _attributesStatusText = "Hero stats not loaded.";
    private string _adventureCountText = "?";
    private string _adventureStatusText = "Adventures not loaded.";
    private string _heroStatusText = "Hero status: Unknown";

    private string _heroInventoryWood = "-";
    private string _heroInventoryClay = "-";
    private string _heroInventoryIron = "-";
    private string _heroInventoryCrop = "-";
    private string _heroInventoryStatusText = "Hero inventory not loaded.";

    private bool _heroResourceMaxUseEnabled = true;
    private int _heroResourceMaxUsePerResource = 5000;
    private bool _heroResourceUseConstruction = true;
    private bool _heroResourceUseSmithy = true;
    private bool _heroResourceUseBrewery = true;

    private int _minHpForAdventure = 60;
    private int _heroHpRegenPerDayPercent = 40;
    private bool _autoRevive = true;
    private bool _autoAssignPoints = true;
    private bool _autoUseOintments;
    private bool _isAdventurePickTop;
    private bool _isAdventurePickShortest = true;
    private bool _hideModeControlEnabled;
    private bool _isHideModeFight;
    private bool _isHideModeHide = true;
    private bool _continuousAdventures;

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
        set => SetProperty(ref _adventureCountText, value);
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
        set => SetProperty(ref _heroStatusText, string.IsNullOrWhiteSpace(value) ? "Hero status: Unknown" : value);
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

    /// <summary>True to cap how much may be pulled from the hero inventory per resource for a single
    /// construction top-up (the "Max use limit" toggle in the hero inventory card).</summary>
    public bool HeroResourceMaxUseEnabled
    {
        get => _heroResourceMaxUseEnabled;
        set => SetProperty(ref _heroResourceMaxUseEnabled, value);
    }

    /// <summary>Per-resource cap (in units) used when <see cref="HeroResourceMaxUseEnabled"/> is on.
    /// Bound to the limit TextBox; persisted as an int.</summary>
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

    /// <summary>
    /// Minimum hero HP required before an adventure is queued (1-100).
    /// Bound to the min-HP TextBox in HeroPanel.xaml.
    /// </summary>
    public int MinHpForAdventure
    {
        get => _minHpForAdventure;
        set => SetProperty(ref _minHpForAdventure, Math.Clamp(value, 1, 100));
    }

    /// <summary>Selectable hero HP regen-per-day percentages for the dropdown (20–100).</summary>
    public IReadOnlyList<int> HeroHpRegenOptions { get; } = [20, 30, 40, 50, 60, 70, 80, 90, 100];

    /// <summary>How much hero HP regenerates per day (%). Used to estimate the defer time when HP
    /// is below the adventure threshold. Bound to the regen dropdown in HeroPanel.xaml.</summary>
    public int HeroHpRegenPerDayPercent
    {
        get => _heroHpRegenPerDayPercent;
        set => SetProperty(ref _heroHpRegenPerDayPercent, Math.Clamp(value, 20, 100));
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

    /// <summary>
    /// Hero hide mode is set to "fight". Mutually exclusive with
    /// <see cref="IsHideModeHide"/>.
    /// </summary>
    public bool IsHideModeFight
    {
        get => _isHideModeFight;
        set
        {
            if (SetProperty(ref _isHideModeFight, value) && value)
            {
                IsHideModeHide = false;
            }
        }
    }

    /// <summary>True when the bot is allowed to change Travian's hero hide/fight switch.</summary>
    public bool HideModeControlEnabled
    {
        get => _hideModeControlEnabled;
        set => SetProperty(ref _hideModeControlEnabled, value);
    }

    /// <summary>
    /// Hero hide mode is set to "hide". Mutually exclusive with
    /// <see cref="IsHideModeFight"/>.
    /// </summary>
    public bool IsHideModeHide
    {
        get => _isHideModeHide;
        set
        {
            if (SetProperty(ref _isHideModeHide, value) && value)
            {
                IsHideModeFight = false;
            }
        }
    }

    /// <summary>True to keep re-queuing hero adventures while any remain.</summary>
    public bool ContinuousAdventures
    {
        get => _continuousAdventures;
        set => SetProperty(ref _continuousAdventures, value);
    }

    /// <summary>String form of the adventure pick order, "top" or "shortest".</summary>
    public string AdventurePickOrder => IsAdventurePickTop ? "top" : "shortest";

    /// <summary>String form of the hide mode, "fight" or "hide".</summary>
    public string HideMode => IsHideModeFight ? "fight" : "hide";

    /// <summary>
    /// Loads all hero settings (min HP, revive, auto-assign, pick order,
    /// hide mode) from a freshly read <see cref="BotOptions"/>. Also refreshes
    /// the priority list.
    /// </summary>
    public void LoadSettingsFromConfig(BotOptions options)
    {
        MinHpForAdventure = options.HeroMinHpForAdventure;
        HeroHpRegenPerDayPercent = options.HeroHpRegenPerDayPercent;
        AutoRevive = options.HeroAutoRevive;
        AutoAssignPoints = options.HeroAutoAssignPoints;
        AutoUseOintments = options.HeroAutoUseOintments;
        HideModeControlEnabled = options.HeroHideModeEnabled;
        var topFirst = string.Equals(options.HeroAdventurePickOrder, "top", StringComparison.OrdinalIgnoreCase);
        if (topFirst)
        {
            IsAdventurePickTop = true;
        }
        else
        {
            IsAdventurePickShortest = true;
        }

        var fightMode = string.Equals(options.HeroHideMode, "fight", StringComparison.OrdinalIgnoreCase);
        if (fightMode)
        {
            IsHideModeFight = true;
        }
        else
        {
            IsHideModeHide = true;
        }

        ContinuousAdventures = options.HeroContinuousAdventures;
        HeroResourceMaxUseEnabled = options.HeroResourceMaxUseEnabled;
        HeroResourceMaxUsePerResource = options.HeroResourceMaxUsePerResource;
        HeroResourceUseConstruction = options.HeroResourceUseConstruction;
        HeroResourceUseSmithy = options.HeroResourceUseSmithy;
        HeroResourceUseBrewery = options.HeroResourceUseBrewery;
        LoadPriorityFromConfig(options.HeroStatPriority);
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
        AttributePriorityItems.Clear();

        for (var i = 0; i < order.Count; i++)
        {
            AttributePriorityItems.Add(new HeroAttributePriorityItem
            {
                Key = order[i],
                Title = GetAttributeTitle(order[i]),
                Order = i + 1,
                PointsText = existingPoints.GetValueOrDefault(order[i], "-"),
            });
        }
    }

    /// <summary>
    /// Re-numbers <see cref="HeroAttributePriorityItem.Order"/> for each item
    /// based on its current position in the collection. Call after a drag /
    /// move that reordered the list.
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
        HeroStatusText = FormatHeroStatus(snapshot.HeroState, snapshot.ReviveRemainingSeconds);
    }

    private static string FormatHeroStatus(string? state, int? reviveRemainingSeconds)
    {
        var normalized = string.IsNullOrWhiteSpace(state) ? "Unknown" : state.Trim();
        if (string.Equals(normalized, "Reviving", StringComparison.OrdinalIgnoreCase)
            && reviveRemainingSeconds is int seconds
            && seconds >= 0)
        {
            return $"Hero status: Reviving ({FormatDuration(seconds)})";
        }

        return $"Hero status: {normalized}";
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
    /// fight/off/def/res order so the list always has all four entries.
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
