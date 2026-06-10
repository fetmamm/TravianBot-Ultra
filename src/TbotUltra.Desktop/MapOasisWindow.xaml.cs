using System.Windows;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MapOasisWindow : Window
{
    private readonly TravcoListStore _store;
    private readonly Action<string>? _log;
    private readonly MapOasisViewModel _viewModel = new();
    private bool _busy;
    private CancellationTokenSource? _runCts;

    public Func<bool, List<string>, IProgress<MapOasisScanProgress>, CancellationToken, Task<List<OasisInfo>>>? AnalyzeRequested { get; init; }

    public MapOasisWindow(TravcoListStore store, Action<string>? log)
    {
        _store = store;
        _log = log;
        InitializeComponent();
        DataContext = _viewModel;
        ReloadSavedLists();
    }

    public void CloseForShutdown() => Close();

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || AnalyzeRequested is null)
        {
            return;
        }

        var selectedTypes = GetSelectedTypes();
        if (selectedTypes.Count == 0)
        {
            SetStatus("Select at least one oasis type.");
            return;
        }

        SetBusy(true);
        _runCts = new CancellationTokenSource();
        BusyOverlay.Show("Analyze map oasis", "Scanning the Travian map...");
        try
        {
            var progress = new Progress<MapOasisScanProgress>(value =>
            {
                var text = $"Scanning map area {value.CompletedAreas}/{value.TotalAreas} - {value.OasisCount} matching oases found.";
                BusyOverlay.Text = text;
                _viewModel.StatusText = text;
            });
            var oases = await AnalyzeRequested(
                IncludeOccupiedCheckBox.IsChecked == true,
                selectedTypes,
                progress,
                _runCts.Token);
            if (oases.Count == 0)
            {
                SetStatus("No oases matched the selected filters. No list was created.");
                return;
            }

            var listName = OasisListNaming.CreateName(
                selectedTypes,
                _store.LoadAll().Select(list => list.Name));
            var list = new TravcoListStore.TravcoSavedList
            {
                Name = listName,
                CreatedUtc = DateTimeOffset.UtcNow,
                Rows = oases.Select(oasis => new TravcoListStore.TravcoSavedRow
                {
                    Village = oasis.OasisType,
                    Coordinates = $"{oasis.X}|{oasis.Y}",
                    Selected = true,
                    OasisType = oasis.OasisType,
                    IsOccupied = oasis.IsOccupied,
                }).ToList(),
            };
            _store.Save(list);
            ApplyRows(oases.Select(OasisListRow.FromOasis));
            ReloadSavedLists(list.Id);
            SetStatus($"Created '{listName}' with {oases.Count} oasis/oases.");
            _log?.Invoke($"[map-oasis] created oasis list '{listName}' with {oases.Count} row(s).");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Map oasis analysis was canceled.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            BusyOverlay.Hide();
            SetBusy(false);
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void OpenListButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedSavedList;
        if (selected is null)
        {
            SetStatus("Select a saved oasis list first.");
            return;
        }

        ApplyRows(selected.Rows.Select(row => new OasisListRow
        {
            Coordinates = row.Coordinates,
            OasisType = row.OasisType ?? row.Village,
            IsOccupied = row.IsOccupied == true,
        }));
        SetStatus($"Opened '{selected.Name}' with {selected.Rows.Count} oasis/oases.");
    }

    private void DeleteListButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedSavedList;
        if (selected is null)
        {
            SetStatus("Select a saved oasis list first.");
            return;
        }

        var result = AppDialog.ShowCustom(
            this,
            $"Delete the saved list '{selected.Name}'?",
            "Delete oasis list",
            [("Delete", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _store.Delete(selected.Id);
        _viewModel.Rows.Clear();
        ReloadSavedLists();
        SetStatus($"Deleted '{selected.Name}'.");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void BusyOverlay_Cancelled(object sender, EventArgs e)
    {
        _runCts?.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        _runCts?.Cancel();
        base.OnClosed(e);
    }

    private List<string> GetSelectedTypes()
    {
        var selected = new List<string>();
        AddIfChecked(WoodCheckBox, "Wood");
        AddIfChecked(ClayCheckBox, "Clay");
        AddIfChecked(IronCheckBox, "Iron");
        AddIfChecked(CropCheckBox, "Crop");
        AddIfChecked(WoodCropCheckBox, "Wood+Crop");
        AddIfChecked(ClayCropCheckBox, "Clay+Crop");
        AddIfChecked(IronCropCheckBox, "Iron+Crop");
        return selected;

        void AddIfChecked(System.Windows.Controls.CheckBox checkBox, string value)
        {
            if (checkBox.IsChecked == true)
            {
                selected.Add(value);
            }
        }
    }

    private void ReloadSavedLists(Guid? selectedId = null)
    {
        _viewModel.SavedLists.Clear();
        foreach (var list in _store.LoadAll().Where(list => list.Rows.Any(row => !string.IsNullOrWhiteSpace(row.OasisType))))
        {
            _viewModel.SavedLists.Add(list);
        }

        _viewModel.SelectedSavedList = selectedId is null
            ? null
            : _viewModel.SavedLists.FirstOrDefault(list => list.Id == selectedId);
    }

    private void ApplyRows(IEnumerable<OasisListRow> rows)
    {
        _viewModel.Rows.Clear();
        foreach (var row in rows)
        {
            _viewModel.Rows.Add(row);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AnalyzeButton.IsEnabled = !busy;
        SavedListsDataGrid.IsEnabled = !busy;
        ResultsDataGrid.IsEnabled = !busy;
    }

    private void SetStatus(string message)
    {
        _viewModel.StatusText = message;
        _log?.Invoke($"[map-oasis-ui] {message}");
    }
}
