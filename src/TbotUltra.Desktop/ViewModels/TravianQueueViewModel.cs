using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the in-game build/smithy queue display. MVVM slice: it
/// owns the two ObservableCollections the queue DataGrids render. The refresh
/// logic still lives in MainWindow code-behind and mutates these collections in
/// place; it will migrate here in later steps.
/// </summary>
public sealed class TravianQueueViewModel : BaseViewModel
{
    /// <summary>Rows shown in the construction-queue DataGrid.</summary>
    public ObservableCollection<TravianBuildQueueRow> BuildQueueRows { get; } = [];

    /// <summary>Rows shown in the smithy-queue DataGrid.</summary>
    public ObservableCollection<TravianSmithyQueueRow> SmithyQueueRows { get; } = [];
}
