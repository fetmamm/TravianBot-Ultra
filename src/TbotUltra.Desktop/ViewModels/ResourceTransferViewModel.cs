using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Resource Transfer panel. First MVVM slice for that
/// panel: it owns the village collection the panel renders (target combo +
/// source list). The scan/persist/payload logic still lives in MainWindow
/// code-behind and mutates this collection in place; it will migrate here in
/// later steps.
/// </summary>
public sealed class ResourceTransferViewModel : BaseViewModel
{
    /// <summary>
    /// Villages shown on the Resource Transfer tab. Created once and mutated in
    /// place so the panel's ItemsSource assignments stay stable.
    /// </summary>
    public ObservableCollection<ResourceTransferVillageItem> Villages { get; } = [];
}
