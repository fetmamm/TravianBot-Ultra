using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Hero / Adventures panel. First MVVM-migrated panel
/// in the desktop app — see <c>MainViewModel</c> doc-comment for context.
///
/// Currently owns:
///   - <see cref="AttributePriorityItems"/> — drag-orderable hero attributes.
///   - The three status text fields shown on the Hero card
///     (attributes status, adventure count, adventure status / countdown).
///
/// Hero state still on MainWindow (will migrate later):
///   - The DispatcherTimer driving the countdown
///   - The blocked-reason key + IsHeroGroupBlocked() helper
///   - The drag-handler scratch state (anchor point, source item)
///   - The hide-mode suppression flag
/// </summary>
public sealed class HeroViewModel : BaseViewModel
{
    private string _attributesStatusText = "Hero stats not loaded.";
    private string _adventureCountText = "?";
    private string _adventureStatusText = "Adventures not loaded.";

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
}
