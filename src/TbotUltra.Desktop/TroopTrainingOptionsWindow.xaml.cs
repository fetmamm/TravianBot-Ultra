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

    private void SyncSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TroopTrainingSettingsSyncWindow(Rows) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SourceRow is null)
        {
            return;
        }

        foreach (var target in dialog.TargetRows)
        {
            target.SyncSettingsFrom(dialog.SourceRow);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var invalid = Rows
            .SelectMany(row => row.BuildingCells.Select(building => (row, building)))
            .FirstOrDefault(item => item.building.UsesTroopRange && !item.building.HasValidMinimumTroopRange);
        if (invalid.building is not null)
        {
            AppDialog.Show(
                this,
                $"{invalid.row.VillageName} / {invalid.building.Title}: minimum troops must use whole numbers from 1 to 10,000 and Max must be at least Min.",
                "Invalid troop settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            invalid.row.IsExpanded = true;
            return;
        }

        var missingResourceSelection = Rows.FirstOrDefault(row => row.RequiresResourceSelection && !row.HasResourceSelection);
        if (missingResourceSelection is not null)
        {
            AppDialog.Show(
                this,
                $"{missingResourceSelection.VillageName}: select at least one resource when an enabled troop building uses % resources.",
                "Invalid troop settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            missingResourceSelection.IsExpanded = true;
            return;
        }

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
