using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the farm-lists panel. MVVM slice: it owns the farm-list
/// status rows the panel renders. The send/refresh/scan logic still lives in
/// MainWindow code-behind and mutates this collection in place; it will migrate
/// here in later steps.
/// </summary>
public sealed class FarmListsViewModel : BaseViewModel
{
    /// <summary>
    /// Farm-list status rows shown on the farming tab. Created once and mutated
    /// in place so the panel's ItemsSource assignment stays stable.
    /// </summary>
    public ObservableCollection<FarmListStatusRow> FarmLists { get; } = [];
}
