using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Buildings panel. First MVVM slice for that panel:
/// it owns the building-slot collection that the panel renders. The service-
/// and queue-bound mutation logic still lives in MainWindow code-behind and
/// mutates this collection in place; it will migrate here in later steps.
/// </summary>
public sealed class BuildingsViewModel : BaseViewModel
{
    /// <summary>
    /// Building slots shown on the Buildings tab. Created once and mutated in
    /// place so the panel's CollectionViewSource bindings stay stable.
    /// </summary>
    public ObservableCollection<BuildingSlotRow> BuildingSlots { get; } = [];
}
