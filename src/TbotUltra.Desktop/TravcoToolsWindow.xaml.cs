using System.ComponentModel;
using System.Windows;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class TravcoToolsWindow : Window
{
    private readonly TravcoListStore _store;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _windowCts = new();
    private readonly TravcoToolsViewModel _viewModel = new();
    private Guid? _openedSavedListId;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _busy;
    private bool _travcoReady;

    public Func<TravcoSearchRequest, CancellationToken, Task<TravcoScrapeResult>>? SearchRequested { get; init; }
    public Func<CancellationToken, Task<TravcoScrapeResult>>? ScrapePageRequested { get; init; }
    public Func<IProgress<(int CurrentPage, int TotalPages)>, CancellationToken, Task<TravcoScrapeResult>>? ScrapeAllPagesRequested { get; init; }
    public Func<Task>? CloseRequested { get; init; }

    public TravcoToolsWindow(
        TravcoListStore store,
        IReadOnlyList<VillageSelectionItem> villages,
        Action<string>? log)
    {
        _store = store;
        _log = log;
        InitializeComponent();
        DataContext = _viewModel;
        foreach (var village in villages)
        {
            _viewModel.Villages.Add(village);
        }

        _viewModel.SelectedVillage = villages.FirstOrDefault(village => village.IsCapital) ?? villages.FirstOrDefault();
        Closing += TravcoToolsWindow_Closing;
        ReloadSavedLists();
        SetBusy(false);
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        _windowCts.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        Closing -= TravcoToolsWindow_Closing;
        _windowCts.Cancel();
        _windowCts.Dispose();
        base.OnClosed(e);
    }

    private void InactiveSearchButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        if (_busy || SearchRequested is null)
        {
            return;
        }

        SetBusy(true);
        var village = _viewModel.SelectedVillage;
        if (village?.CoordX is null || village.CoordY is null)
        {
            SetStatus("Select a village with coordinates.");
            SetBusy(false);
            return;
        }

        if (!int.TryParse(_viewModel.DaysInactiveText, out var daysInactive)
            || daysInactive is < 1 or > 7)
        {
            SetStatus("Active days must be a whole number between 1 and 7.");
            SetBusy(false);
            return;
        }

        var request = new TravcoSearchRequest(
            village.CoordX.Value,
            village.CoordY.Value,
            daysInactive,
            _viewModel.SelectedOrderBy);
        SetStatus(
            $"Analyzing Travco for {village.NameWithCoords}, {daysInactive} active day(s), order {_viewModel.SelectedOrderBy}.");
        BusyOverlay.Show("Analyze Travco", "Opening Travco and loading inactive villages...");
        try
        {
            var result = await SearchRequested(request, _windowCts.Token);
            _travcoReady = true;
            _openedSavedListId = null;
            ApplyRows(result.Rows.Select(TravcoListRow.FromWorker));
            _viewModel.ListName = $"Travco page {result.PageNumber}";
            _viewModel.SelectedSavedList = null;
            SetStatus(result.Rows.Count == 0
                ? $"Travco search finished: {result.TotalPages} page(s) found, current page has no matching villages."
                : $"Travco search finished: {result.TotalPages} page(s) found, page {result.PageNumber} has {result.Rows.Count} village(s).");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Travco search canceled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Travco search failed: {ex.Message}");
        }
        finally
        {
            BusyOverlay.Hide();
            SetBusy(false);
        }
    }

    private void SaveListButton_Click(object sender, RoutedEventArgs e)
    {
        _ = SaveCurrentPageAsync();
    }

    private async Task SaveCurrentPageAsync()
    {
        if (_busy || !_travcoReady || ScrapePageRequested is null)
        {
            return;
        }

        SetBusy(true);
        BusyOverlay.Show("Save Travco page", "Reading the current Travco result page...");
        try
        {
            SetStatus("Reading the current Travco page.");
            var result = await ScrapePageRequested(_windowCts.Token);
            ApplyRows(result.Rows.Select(TravcoListRow.FromWorker));
            SaveCurrentRows(_viewModel.ListName);
            SetStatus($"Saved page {result.PageNumber}/{result.TotalPages}: {result.Rows.Count} row(s).");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Saving the Travco page was canceled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save the Travco page: {ex.Message}");
        }
        finally
        {
            BusyOverlay.Hide();
            SetBusy(false);
        }
    }

    private void SaveAllPagesButton_Click(object sender, RoutedEventArgs e)
    {
        _ = SaveAllPagesAsync();
    }

    private async Task SaveAllPagesAsync()
    {
        if (_busy || !_travcoReady || ScrapeAllPagesRequested is null)
        {
            return;
        }

        var confirm = AppDialog.ShowCustom(
            this,
            "Read every Travco result page and save all rows in one list?",
            "Save all Travco pages",
            [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No)],
            MessageBoxImage.Question,
            MessageBoxResult.No,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        BusyOverlay.Show("Save all Travco pages", "Preparing page collection...");
        try
        {
            SetStatus("Reading all Travco result pages.");
            var progress = new Progress<(int CurrentPage, int TotalPages)>(value =>
            {
                BusyOverlay.Text = $"Reading page {value.CurrentPage} of {value.TotalPages}...";
            });
            var result = await ScrapeAllPagesRequested(progress, _windowCts.Token);
            ApplyRows(result.Rows.Select(TravcoListRow.FromWorker));
            var name = _viewModel.ListName.StartsWith("Travco page ", StringComparison.OrdinalIgnoreCase)
                ? "Travco all pages"
                : _viewModel.ListName;
            _viewModel.ListName = name;
            SaveCurrentRows(name);
            SetStatus($"Saved all {result.TotalPages} page(s) as one list with {result.Rows.Count} row(s).");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Saving all Travco pages was canceled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save all Travco pages: {ex.Message}");
        }
        finally
        {
            BusyOverlay.Hide();
            SetBusy(false);
        }
    }

    private void OpenListButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedSavedList;
        if (selected is null)
        {
            SetStatus("Select a saved list first.");
            return;
        }

        _openedSavedListId = selected.Id;
        _viewModel.ListName = selected.Name;
        ApplyRows(selected.Rows.Select(row => new TravcoListRow
        {
            Distance = row.Distance,
            Account = row.Account,
            Village = row.Village,
            Pop = row.Pop,
            Coordinates = row.Coordinates,
            Selected = row.Selected,
        }));
        SetStatus($"Opened '{selected.Name}' with {selected.Rows.Count} row(s).");
    }

    private void DeleteListButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedSavedList;
        if (selected is null)
        {
            SetStatus("Select a saved list first.");
            return;
        }

        var result = AppDialog.ShowCustom(
            this,
            $"Delete the saved list '{selected.Name}'?",
            "Delete Travco list",
            [("Delete", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _store.Delete(selected.Id);
        if (_openedSavedListId == selected.Id)
        {
            _openedSavedListId = null;
            _viewModel.Rows.Clear();
        }

        ReloadSavedLists();
        SetStatus($"Deleted '{selected.Name}'.");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyRows(IEnumerable<TravcoListRow> rows)
    {
        foreach (var existing in _viewModel.Rows)
        {
            existing.PropertyChanged -= Row_PropertyChanged;
        }

        _viewModel.Rows.Clear();
        foreach (var row in rows)
        {
            row.PropertyChanged += Row_PropertyChanged;
            _viewModel.Rows.Add(row);
        }
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TravcoListRow.Selected) || _openedSavedListId is null)
        {
            return;
        }

        var existing = _store.LoadAll().FirstOrDefault(list => list.Id == _openedSavedListId.Value);
        if (existing is null)
        {
            return;
        }

        existing.Rows = BuildSavedRows();
        _store.Save(existing);
        ReloadSavedLists(existing.Id);
    }

    private List<TravcoListStore.TravcoSavedRow> BuildSavedRows()
    {
        return _viewModel.Rows.Select(row => new TravcoListStore.TravcoSavedRow
        {
            Distance = row.Distance,
            Account = row.Account,
            Village = row.Village,
            Pop = row.Pop,
            Coordinates = row.Coordinates,
            Selected = row.Selected,
        }).ToList();
    }

    private void SaveCurrentRows(string name)
    {
        if (_viewModel.Rows.Count == 0)
        {
            throw new InvalidOperationException("No Travco result rows were found on the current page.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Enter a list name.");
        }

        var list = new TravcoListStore.TravcoSavedList
        {
            Name = name.Trim(),
            CreatedUtc = DateTimeOffset.UtcNow,
            Rows = BuildSavedRows(),
        };
        _store.Save(list);
        _openedSavedListId = list.Id;
        ReloadSavedLists(list.Id);
    }

    private void ReloadSavedLists(Guid? selectedId = null)
    {
        var desiredId = selectedId ?? _viewModel.SelectedSavedList?.Id;
        _viewModel.SavedLists.Clear();
        foreach (var list in _store.LoadAll())
        {
            _viewModel.SavedLists.Add(list);
        }

        _viewModel.SelectedSavedList = desiredId is null
            ? null
            : _viewModel.SavedLists.FirstOrDefault(list => list.Id == desiredId.Value);
    }

    private async void TravcoToolsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        var result = AppDialog.ShowCustom(
            this,
            "Close Travco tools and its browser tab?",
            "Close Travco tools",
            [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No)],
            MessageBoxImage.Question,
            MessageBoxResult.No,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _closeInProgress = true;
        _windowCts.Cancel();
        try
        {
            if (CloseRequested is not null)
            {
                await CloseRequested();
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not close Travco tab: {ex.Message}");
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InactiveSearchButton.IsEnabled = !busy;
        SaveListButton.IsEnabled = !busy && _travcoReady;
        SaveAllPagesButton.IsEnabled = !busy && _travcoReady;
        SavedListsListBox.IsEnabled = !busy;
        ResultsDataGrid.IsEnabled = !busy;
    }

    private void SetStatus(string message)
    {
        _viewModel.StatusText = message;
        _log?.Invoke($"[travco-ui] {message}");
    }
}
