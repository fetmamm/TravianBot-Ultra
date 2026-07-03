using System.Collections.ObjectModel;
using System.Windows;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.ViewModels;

namespace TbotUltra.Desktop;

public sealed record TroopTrainingQuickVillageResult(
    string VillageKey,
    string VillageName,
    bool IsBuildTroopsEnabled,
    TroopTrainingPayload Settings);

/// <summary>
/// Per-village troop-training popup. The compact row edits the building enable/troop choice;
/// expanding a row (chevron) exposes all settings for that village (max queue, amount mode,
/// run trigger, timed min/max, resource checks, fallback wait). One row is expanded at a time.
/// </summary>
public partial class TroopTrainingOptionsWindow : Window
{
    public ObservableCollection<TroopTrainingQuickVillageRow> Rows { get; }

    public IReadOnlyList<TroopTrainingQuickVillageResult> Results { get; private set; } =
        Array.Empty<TroopTrainingQuickVillageResult>();

    private bool _collapsingRows;

    public TroopTrainingOptionsWindow(IReadOnlyList<TroopTrainingQuickVillageRow> rows)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        Rows = new ObservableCollection<TroopTrainingQuickVillageRow>(rows);
        foreach (var row in Rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        DataContext = this;
        SubtitleTextBlock.Text = $"{Rows.Count} village(s)";
    }

    // Keep at most one village expanded so the list stays compact.
    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_collapsingRows
            || sender is not TroopTrainingQuickVillageRow expandedRow
            || !string.Equals(e.PropertyName, nameof(TroopTrainingQuickVillageRow.IsExpanded), StringComparison.Ordinal)
            || !expandedRow.IsExpanded)
        {
            return;
        }

        _collapsingRows = true;
        try
        {
            foreach (var row in Rows)
            {
                if (!ReferenceEquals(row, expandedRow))
                {
                    row.IsExpanded = false;
                }
            }
        }
        finally
        {
            _collapsingRows = false;
        }
    }

    private IReadOnlyList<TroopTrainingQuickVillageResult> BuildResults()
    {
        return Rows
            .Select(row => new TroopTrainingQuickVillageResult(
                row.VillageKey,
                row.VillageName,
                row.IsBuildTroopsEnabled,
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
