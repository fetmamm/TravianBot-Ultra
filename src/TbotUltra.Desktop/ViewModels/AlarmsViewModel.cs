using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the alarm log. MVVM slice: it owns the alarm-entry
/// collection. The filtered <c>ICollectionView</c> and the append/clear logic
/// still live in MainWindow code-behind and mutate this collection in place;
/// they will migrate here in later steps.
/// </summary>
public sealed class AlarmsViewModel : BaseViewModel
{
    /// <summary>
    /// Alarm entries. Created once and mutated in place so the default
    /// collection view bound to the alarm list stays stable.
    /// </summary>
    public ObservableCollection<AlarmEntryRow> Entries { get; } = [];
}
