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

    private int _minHpForAdventure = 60;
    private bool _autoRevive = true;
    private bool _autoAssignPoints = true;
    private bool _autoUseOintments;
    private bool _isAdventurePickTop;
    private bool _isAdventurePickShortest = true;
    private bool _isHideModeFight = true;
    private bool _isHideModeHide;
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
        AutoRevive = options.HeroAutoRevive;
        AutoAssignPoints = options.HeroAutoAssignPoints;
        AutoUseOintments = options.HeroAutoUseOintments;
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
