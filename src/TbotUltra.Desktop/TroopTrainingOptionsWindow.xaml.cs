using System.Collections.ObjectModel;
using System.Windows;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.ViewModels;

namespace TbotUltra.Desktop;

public sealed record TroopTrainingQuickVillageResult(
    string VillageKey,
    string VillageName,
    TroopTrainingPayload Settings);

/// <summary>
/// Quick per-village troop-training popup. It only edits the building enable/troop choice for each
/// village; advanced settings stay in the row's base payload and are preserved on save.
/// </summary>
public partial class TroopTrainingOptionsWindow : Window
{
    public ObservableCollection<TroopTrainingQuickVillageRow> Rows { get; }

    public IReadOnlyList<TroopTrainingQuickVillageResult> Results { get; private set; } =
        Array.Empty<TroopTrainingQuickVillageResult>();

    public TroopTrainingOptionsWindow(IReadOnlyList<TroopTrainingQuickVillageRow> rows)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        Rows = new ObservableCollection<TroopTrainingQuickVillageRow>(rows);
        DataContext = this;
        SubtitleTextBlock.Text = $"{Rows.Count} village(s)";
    }

    private IReadOnlyList<TroopTrainingQuickVillageResult> BuildResults()
    {
        return Rows
            .Select(row => new TroopTrainingQuickVillageResult(
                row.VillageKey,
                row.VillageName,
                row.BuildPayload()))
            .ToList();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Results = BuildResults();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
