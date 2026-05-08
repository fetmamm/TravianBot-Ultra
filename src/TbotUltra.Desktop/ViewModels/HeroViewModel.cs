using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Hero / Adventures panel. First MVVM-migrated panel
/// in the desktop app — see <c>MainViewModel</c> doc-comment for context.
///
/// Currently owns:
///   - <see cref="AttributePriorityItems"/> — the drag-orderable list of
///     hero attributes (Fighting / Offence / Defence / Resources). Bound
///     to <c>HeroAttributePriorityItemsControl.ItemsSource</c>.
///
/// Other hero state (free-points text, adventure count, countdown,
/// blocked-reason, hide-mode flag, etc.) still lives on MainWindow and will
/// migrate here in subsequent commits (3b2/3b3) before the panel is
/// extracted as a UserControl.
/// </summary>
public sealed class HeroViewModel : BaseViewModel
{
    /// <summary>
    /// Drag-orderable list of hero attributes shown in the priority list.
    /// The collection is created once and re-populated in place so XAML
    /// bindings to <c>ItemsSource</c> stay stable across config reloads.
    /// </summary>
    public ObservableCollection<HeroAttributePriorityItem> AttributePriorityItems { get; } = [];
}
