using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Reinforcements panel. MVVM slice: it owns the village
/// and troop-rule collections the panel renders (target combo, source list, and
/// the troop-rule list edited in <see cref="ReinforcementTroopSelectionWindow"/>).
/// The scan/persist/payload logic still lives in MainWindow code-behind and
/// mutates these collections in place; it will migrate here in later steps.
/// </summary>
public sealed class ReinforcementViewModel : BaseViewModel
{
    /// <summary>
    /// Villages offered as the reinforcement target. Created once and mutated in
    /// place so the panel's ItemsSource assignments stay stable.
    /// </summary>
    public ObservableCollection<ReinforcementVillageItem> Villages { get; } = [];

    /// <summary>Villages offered as reinforcement sources (source list).</summary>
    public ObservableCollection<ReinforcementVillageItem> SourceVillages { get; } = [];

    /// <summary>Per-troop send rules configured for the panel.</summary>
    public ObservableCollection<ReinforcementTroopRuleItem> TroopRules { get; } = [];
}
