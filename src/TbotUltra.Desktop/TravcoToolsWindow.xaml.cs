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
    private CancellationTokenSource? _activeOperationCts;
    private Guid? _openedSavedListId;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _busy;
    private bool _travcoReady;
    private bool _editMode;

    public Func<TravcoSearchRequest, CancellationToken, Task<TravcoScrapeResult>>? SearchRequested { get; init; }
    public Func<IProgress<(int CurrentPage, int TotalPages)>, CancellationToken, Task<TravcoScrapeResult>>? ScrapeAllPagesRequested { get; init; }
    public Func<IProgress<MapOasisScanProgress>, CancellationToken, Task<List<OasisInfo>>>? MapOasisScanRequested { get; init; }
    public Func<Task>? CloseRequested { get; init; }

    public TravcoToolsWindow(
        TravcoListStore store,
        IReadOnlyList<VillageSelectionItem> villages,
        Action<string>? log)
    {
        _store = store;
        _log = log;
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
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
            SetEditMode(false);
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
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token);
        _activeOperationCts = operationCts;
        BusyOverlay.ShowCancel = true;
        BusyOverlay.Show("Save all Travco pages", "Preparing page collection...");
        try
        {
            SetStatus("Reading all Travco result pages.");
            var progress = new Progress<(int CurrentPage, int TotalPages)>(value =>
            {
                BusyOverlay.Text = $"Reading page {value.CurrentPage} of {value.TotalPages}...";
            });
            var result = await ScrapeAllPagesRequested(progress, operationCts.Token);
            SetEditMode(false);
            ApplyRows(result.Rows.Select(TravcoListRow.FromWorker));
            var name = _viewModel.ListName.StartsWith("Travco page ", StringComparison.OrdinalIgnoreCase)
                ? "Travco all pages"
                : _viewModel.ListName;
            // Never overwrite/duplicate an existing list: append " 1", " 2", ... until the name is free.
            name = MakeUniqueListName(name);
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
            _activeOperationCts = null;
            BusyOverlay.ShowCancel = false;
            BusyOverlay.Hide();
            SetBusy(false);
        }
    }

    private void AnalyzeMapOasisButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = AppDialog.Show(
            this,
            "Analyze map oasis scans the Travian map and can generate a high volume of requests.\n\n"
                + "Recommended use: run this once per account, save the result, and do not repeat it unless you have a specific reason.",
            "Analyze map oasis",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _ = RunMapOasisScanAndSaveAsync();
    }

    private void CalculateDistanceButton_Click(object sender, RoutedEventArgs e)
    {
        CalculateDistancesFromSelectedVillage();
    }

    private void CalculateDistancesFromSelectedVillage()
    {
        if (_busy)
        {
            return;
        }

        var village = _viewModel.SelectedVillage;
        if (village?.CoordX is null || village.CoordY is null)
        {
            SetStatus("Select a village with coordinates first.");
            return;
        }

        SetBusy(true);
        try
        {
            ResultsDataGrid.CommitEdit();
            ResultsDataGrid.CommitEdit();

            if (_openedSavedListId is not null)
            {
                var updatedRows = CalculateVisibleRowDistances(village.CoordX.Value, village.CoordY.Value);
                var existing = _store.LoadAll().FirstOrDefault(list => list.Id == _openedSavedListId.Value);
                if (existing is null)
                {
                    SetStatus("The opened list could not be found.");
                    return;
                }

                existing.Rows = BuildSavedRows();
                _store.Save(existing);
                ReloadSavedLists(existing.Id);
                SetStatus(
                    $"Calculated distance from {village.NameWithCoords} for {updatedRows}/{_viewModel.Rows.Count} row(s) in '{existing.Name}'.");
                return;
            }

            var lists = _store.LoadAll().ToList();
            if (lists.Count == 0)
            {
                SetStatus("No opened list and no saved lists were found.");
                return;
            }

            var updatedLists = 0;
            var updatedRowsTotal = 0;
            foreach (var list in lists)
            {
                var updatedRows = CalculateSavedRowDistances(list.Rows, village.CoordX.Value, village.CoordY.Value);
                if (updatedRows == 0)
                {
                    continue;
                }

                _store.Save(list);
                updatedLists++;
                updatedRowsTotal += updatedRows;
            }

            ReloadSavedLists();
            SetStatus(
                $"No list was open. Calculated distance from {village.NameWithCoords} for {updatedRowsTotal} row(s) in {updatedLists}/{lists.Count} saved list(s).");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not calculate distances: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Scans the whole map for oases (all types, occupied and free) and saves them as one list. The
    // list keeps each oasis's type/occupied/animals/owner so the farm-add dialog can filter on them.
    private async Task RunMapOasisScanAndSaveAsync()
    {
        if (_busy || MapOasisScanRequested is null)
        {
            return;
        }

        SetBusy(true);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token);
        _activeOperationCts = operationCts;
        BusyOverlay.ShowCancel = true;
        BusyOverlay.Show("Analyze map oasis", "Scanning the Travian map for oases...");
        try
        {
            SetStatus("Scanning the Travian map for oases.");
            var progress = new Progress<MapOasisScanProgress>(value =>
            {
                BusyOverlay.Text =
                    $"Scanning map area {value.CompletedAreas}/{value.TotalAreas} - {value.OasisCount} oases found.";
            });
            var oases = await MapOasisScanRequested(progress, operationCts.Token);
            if (oases.Count == 0)
            {
                SetStatus("Map oasis scan finished with no oases found. No list was created.");
                return;
            }

            SetEditMode(false);
            _openedSavedListId = null;
            _travcoReady = false;
            ApplyRows(oases.Select(OasisToRow));
            var name = OasisListNaming.CreateName(
                OasisListNaming.TypeOrder,
                _store.LoadAll().Select(list => list.Name));
            _viewModel.ListName = name;
            SaveCurrentRows(name);
            SetStatus($"Saved '{name}' with {oases.Count} oasis/oases.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Map oasis scan canceled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Map oasis scan failed: {ex.Message}");
        }
        finally
        {
            _activeOperationCts = null;
            BusyOverlay.ShowCancel = false;
            BusyOverlay.Hide();
            SetBusy(false);
        }
    }

    // Maps a scanned oasis to a list row. The Village column shows the oasis type and the Account
    // column shows the owner (occupied) or the animal garrison (free); the structured oasis fields
    // are kept for the farm-add filter. Distance is left empty — it is computed live at add time.
    private static TravcoListRow OasisToRow(OasisInfo oasis)
    {
        var ownerOrAnimals = oasis.IsOccupied
            ? string.IsNullOrWhiteSpace(oasis.OwnerAlliance)
                ? oasis.OwnerPlayer
                : $"{oasis.OwnerPlayer} ({oasis.OwnerAlliance})"
            : oasis.Animals;

        return new TravcoListRow
        {
            Coordinates = $"{oasis.X}|{oasis.Y}",
            Village = oasis.OasisType,
            Account = ownerOrAnimals,
            Selected = true,
            OasisType = oasis.OasisType,
            IsOccupied = oasis.IsOccupied,
            Animals = oasis.Animals,
            OwnerPlayer = oasis.OwnerPlayer,
            OwnerAlliance = oasis.OwnerAlliance,
        };
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
        SetEditMode(false);
        ApplySavedListRows(selected);
        SetStatus($"Opened '{selected.Name}' with {selected.Rows.Count} row(s).");
    }

    private void EditListButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var selected = _viewModel.SelectedSavedList;
        if (selected is null)
        {
            SetStatus("Select a saved list first.");
            return;
        }

        _openedSavedListId = selected.Id;
        _viewModel.ListName = selected.Name;
        ApplySavedListRows(selected);
        SetEditMode(true);
        SetStatus($"Editing '{selected.Name}'. Change the farms and click Save.");
    }

    private void SaveEditedListButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_editMode || _openedSavedListId is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_viewModel.ListName))
        {
            SetStatus("Enter a list name.");
            ListNameTextBox.Focus();
            return;
        }

        ResultsDataGrid.CommitEdit();
        ResultsDataGrid.CommitEdit();

        var existing = _store.LoadAll().FirstOrDefault(list => list.Id == _openedSavedListId.Value);
        if (existing is null)
        {
            SetStatus("The saved list could not be found.");
            return;
        }

        existing.Name = _viewModel.ListName.Trim();
        existing.CreatedUtc = DateTimeOffset.UtcNow;
        existing.Rows = BuildSavedRows();
        _store.Save(existing);
        ReloadSavedLists(existing.Id);
        SetEditMode(false);
        if (_viewModel.SelectedSavedList is not null)
        {
            ApplySavedListRows(_viewModel.SelectedSavedList);
        }

        var savedCount = _viewModel.SelectedSavedList?.Rows.Count ?? 0;
        SetStatus($"Saved changes to '{existing.Name}' with {savedCount} row(s).");
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
        if (_editMode
            || e.PropertyName != nameof(TravcoListRow.Selected)
            || _openedSavedListId is null)
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
            OasisType = row.OasisType,
            IsOccupied = row.IsOccupied,
            Animals = row.Animals,
            OwnerPlayer = row.OwnerPlayer,
            OwnerAlliance = row.OwnerAlliance,
        }).ToList();
    }

    private void ApplySavedListRows(TravcoListStore.TravcoSavedList list)
    {
        ApplyRows(list.Rows.Select(row => new TravcoListRow
        {
            Distance = row.Distance,
            Account = row.Account,
            Village = row.Village,
            Pop = row.Pop,
            Coordinates = row.Coordinates,
            Selected = row.Selected,
            OasisType = row.OasisType,
            IsOccupied = row.IsOccupied,
            Animals = row.Animals,
            OwnerPlayer = row.OwnerPlayer,
            OwnerAlliance = row.OwnerAlliance,
        }));
    }

    private int CalculateVisibleRowDistances(int fromX, int fromY)
    {
        var updated = 0;
        foreach (var row in _viewModel.Rows)
        {
            if (!TravianMapDistance.TryParseCoordinates(row.Coordinates, out var x, out var y))
            {
                continue;
            }

            row.Distance = TravianMapDistance.CalculateRounded(fromX, fromY, x, y);
            updated++;
        }

        return updated;
    }

    private static int CalculateSavedRowDistances(
        IEnumerable<TravcoListStore.TravcoSavedRow> rows,
        int fromX,
        int fromY)
    {
        var updated = 0;
        foreach (var row in rows)
        {
            if (!TravianMapDistance.TryParseCoordinates(row.Coordinates, out var x, out var y))
            {
                continue;
            }

            row.Distance = TravianMapDistance.CalculateRounded(fromX, fromY, x, y);
            updated++;
        }

        return updated;
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
        var saved = _viewModel.SelectedSavedList;
        if (saved is not null)
        {
            ApplySavedListRows(saved);
        }
    }

    // Returns baseName unchanged if no saved list already uses it, otherwise the first free
    // "baseName 1", "baseName 2", ... so a new "save all pages" list never collides with an existing one.
    private string MakeUniqueListName(string baseName)
    {
        var trimmed = baseName.Trim();
        var existing = _store.LoadAll()
            .Select(list => list.Name?.Trim())
            .Where(listName => !string.IsNullOrEmpty(listName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(trimmed))
        {
            return trimmed;
        }

        for (var suffix = 1; ; suffix++)
        {
            var candidate = $"{trimmed} {suffix}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
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
        => await AsyncUi.GuardAsync(() => TravcoToolsWindowClosingAsync(sender, e), SetStatus);

    private async Task TravcoToolsWindowClosingAsync(object? sender, CancelEventArgs e)
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
            await Dispatcher.InvokeAsync(Close, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InactiveSearchButton.IsEnabled = !busy;
        AnalyzeMapOasisButton.IsEnabled = !busy;
        CalculateDistanceButton.IsEnabled = !busy;
        SaveAllPagesButton.IsEnabled = !busy && _travcoReady;
        SavedListsListBox.IsEnabled = !busy;
        ResultsDataGrid.IsEnabled = !busy;
        SaveEditedListButton.IsEnabled = !busy && _editMode;
    }

    private void SetStatus(string message)
    {
        _viewModel.StatusText = message;
        _log?.Invoke($"[travco-ui] {message}");
    }

    private void BusyOverlay_Cancelled(object sender, EventArgs e)
    {
        if (_activeOperationCts is null || _activeOperationCts.IsCancellationRequested)
        {
            return;
        }

        SetStatus("Cancel requested. Returning Travco to the first page...");
        _activeOperationCts.Cancel();
    }

    private void SetEditMode(bool editing)
    {
        _editMode = editing;
        UseColumn.IsReadOnly = !editing;
        SaveEditedListButton.IsEnabled = !_busy && editing;
    }
}
