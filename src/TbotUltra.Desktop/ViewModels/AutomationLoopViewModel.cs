using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the automation-loop task list. MVVM slice: it owns the
/// loop-task source collection. The filtered <c>ICollectionView</c> and the
/// load/save/toggle logic still live in MainWindow code-behind and mutate this
/// collection in place; they will migrate here in later steps.
/// </summary>
public sealed class AutomationLoopViewModel : BaseViewModel
{
    /// <summary>
    /// Loop-task options. Created once and mutated in place so the default
    /// collection view bound to the list stays stable.
    /// </summary>
    public ObservableCollection<LoopTaskOption> Tasks { get; } = [];
}
