using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class TravcoToolsControl : UserControl
{
    private readonly TravcoListStore _store;
    private readonly AllVillagesImportSettingsStore _allVillagesSettingsStore;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly TravcoToolsViewModel _viewModel = new();
    private CancellationTokenSource? _activeOperationCts;
    private Guid? _openedSavedListId;
    private bool _sessionActive;
    private bool _browserTabOpen;
    private bool _busy;
    private bool _editMode;

    public Func<TravcoSearchRequest, IProgress<TravcoSearchProgress>, CancellationToken, Task<TravcoScrapeResult>>? SearchRequested { get; init; }
    public Func<IProgress<(int CurrentPage, int TotalPages)>, CancellationToken, Task<TravcoScrapeResult>>? ScrapeAllPagesRequested { get; init; }
    public Func<MapOasisScanRequest, IProgress<MapOasisScanProgress>, CancellationToken, Task<List<OasisInfo>>>? MapOasisScanRequested { get; init; }
    public Func<bool>? MapOasisScanExists { get; init; }
    public Func<MapSqlVillageImportRequest, IProgress<MapSqlVillageImportProgress>, CancellationToken, Task<MapSqlVillageImportResult>>? AddAllVillagesRequested { get; init; }
    public Func<Task>? CloseRequested { get; init; }
    public Func<IReadOnlyList<VillageSelectionItem>>? VillagesRequested { get; init; }

    public TravcoToolsControl(
        TravcoListStore store,
        AllVillagesImportSettingsStore allVillagesSettingsStore,
        IReadOnlyList<VillageSelectionItem> villages,
        Action<string>? log)
    {
        _store = store;
        _allVillagesSettingsStore = allVillagesSettingsStore;
        _log = log;
        InitializeComponent();
        DataContext = _viewModel;
        UpdateVillages(villages);
        ReloadSavedLists();
        SetSessionState(active: false, browserTabOpen: false);
        SetBusy(false);
    }

    public void StopForShutdown()
    {
        _lifetimeCts.Cancel();
    }

    public void UpdateVillages(IReadOnlyList<VillageSelectionItem> villages)
    {
        var selectedCoordinates = _viewModel.SelectedVillage is { CoordX: { } x, CoordY: { } y }
            ? (x, y)
            : ((int X, int Y)?)null;
        _viewModel.Villages.Clear();
        foreach (var village in villages)
        {
            _viewModel.Villages.Add(village);
        }

        _viewModel.SelectedVillage = selectedCoordinates is { } coordinates
            ? villages.FirstOrDefault(village => village.CoordX == coordinates.X && village.CoordY == coordinates.Y)
            : null;
        _viewModel.SelectedVillage ??= villages.FirstOrDefault(village => village.IsCapital) ?? villages.FirstOrDefault();
    }

    public void SetSessionState(bool active, bool browserTabOpen)
    {
        _sessionActive = active;
        _browserTabOpen = active && browserTabOpen;
        SessionStateTextBlock.Text = _browserTabOpen
            ? "Travco tab open · automation paused"
            : active
                ? "Analysis session active · automation paused"
                : "Browser session starts when needed";
        CloseTravcoTabButton.Content = _browserTabOpen ? "Close Travco tab" : "Finish session";
        CloseTravcoTabButton.IsEnabled = active && !_busy;
        CloseTravcoTabAttentionBorder.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    internal Task RequestCloseSessionAsync() => CloseTravcoSessionAsync();

    private bool RefreshVillages()
    {
        if (VillagesRequested is not null)
        {
            UpdateVillages(VillagesRequested());
        }

        if (_viewModel.Villages.Count > 0)
        {
            return true;
        }

        SetStatus("Village coordinates are unavailable. Scan villages or refresh village status first.");
        return false;
    }

    private void InactiveSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SearchRequested is null || ScrapeAllPagesRequested is null || !RefreshVillages())
        {
            return;
        }

        // The whole analyze + save-all workflow lives in its own popup. Open it empty; the user picks the
        // village/inactivity there, analyzes, and saves. On return, refresh the saved-list panel.
        var analyze = new TravcoAnalyzeWindow(
            _store,
            _viewModel.Villages.ToList(),
            _viewModel.SelectedVillage,
            SearchRequested,
            ScrapeAllPagesRequested,
            _log,
            _lifetimeCts.Token)
        {
            Owner = Window.GetWindow(this),
        };
        analyze.ShowDialog();
        if (analyze.ListSaved)
        {
            ReloadSavedLists();
            SetStatus("Saved list updated from Analyze Travco.");
        }
    }

    private void AddAllVillagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || AddAllVillagesRequested is null)
        {
            return;
        }

        var saved = _allVillagesSettingsStore.Load();
        var includePlayers = new CheckBox { Content = "Players", IsChecked = saved.IncludePlayers, Margin = new Thickness(0, 0, 0, 6) };
        var includeNatars = new CheckBox { Content = "Natars", IsChecked = saved.IncludeNatars, Margin = new Thickness(0, 0, 0, 14) };
        var listName = new TextBox { MinWidth = 360, ToolTip = "Leave empty to use All villages." };
        var ignoredPlayers = new TextBox { Text = saved.IgnoredPlayers, MinWidth = 360 };
        var ignoredAlliances = new TextBox { Text = saved.IgnoredAlliances, MinWidth = 360 };
        var skipOwnVillages = new CheckBox
        {
            Content = "Skip own villages",
            IsChecked = saved.SkipOwnVillages,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var content = new StackPanel();
        var savedListText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        void UpdateSavedListText()
        {
            var targetListName = string.IsNullOrWhiteSpace(listName.Text) ? "All villages" : listName.Text.Trim();
            savedListText.Text = "All villages and players from the active server will be downloaded and saved in the list "
                + $"'{targetListName}'. This list can be used to add to your farmlists.";
        }

        listName.TextChanged += (_, _) => UpdateSavedListText();
        UpdateSavedListText();
        content.Children.Add(savedListText);
        content.Children.Add(includePlayers);
        content.Children.Add(includeNatars);
        content.Children.Add(new TextBlock { Text = "List name (optional)", Margin = new Thickness(0, 0, 0, 4) });
        content.Children.Add(listName);
        content.Children.Add(new TextBlock { Text = "Ignore players (optional)", Margin = new Thickness(0, 0, 0, 4) });
        content.Children.Add(ignoredPlayers);
        content.Children.Add(new TextBlock { Text = "Ignore alliance (optional)", Margin = new Thickness(0, 12, 0, 4) });
        content.Children.Add(ignoredAlliances);
        content.Children.Add(skipOwnVillages);
        content.Children.Add(new TextBlock
        {
            Text = "Use commas to separate names. Multihunter is always ignored.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var result = AppDialog.ShowCustomContent(
            Window.GetWindow(this),
            content,
            "Add all villages",
            [("Add villages", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Information,
            MessageBoxResult.Yes,
            MessageBoxResult.Cancel,
            successResult: MessageBoxResult.Yes,
            width: 520);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var request = new MapSqlVillageImportRequest(
            includePlayers.IsChecked == true,
            includeNatars.IsChecked == true,
            ParseCommaSeparated(ignoredPlayers.Text),
            ParseCommaSeparated(ignoredAlliances.Text),
            skipOwnVillages.IsChecked == true);
        if (!request.IncludePlayers && !request.IncludeNatars)
        {
            SetStatus("Select Players or Natars before adding villages.");
            return;
        }

        _allVillagesSettingsStore.Save(new AllVillagesImportSettingsStore.Settings(
            request.IncludePlayers,
            request.IncludeNatars,
            ignoredPlayers.Text.Trim(),
            ignoredAlliances.Text.Trim(),
            request.SkipOwnVillages));
        _ = RunAllVillagesImportAsync(request, listName.Text);
    }

    private async Task RunAllVillagesImportAsync(MapSqlVillageImportRequest request, string? listName)
    {
        if (_busy || AddAllVillagesRequested is null)
        {
            return;
        }

        SetBusy(true);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _activeOperationCts = operationCts;
        BusyOverlay.ShowCancel = true;
        BusyOverlay.IsIndeterminate = true;
        BusyOverlay.Show("Add all villages", "Downloading map.sql...");
        try
        {
            var progress = new Progress<MapSqlVillageImportProgress>(value => BusyOverlay.Text = value.Status);
            var imported = await AddAllVillagesRequested(request, progress, operationCts.Token);
            var resolvedListName = string.IsNullOrWhiteSpace(listName) ? "All villages" : listName.Trim();
            _store.ReplaceAllVillages(resolvedListName, imported.Rows.Select(TravcoListRow.FromWorker).Select(row => new TravcoListStore.TravcoSavedRow
            {
                Account = row.Account,
                Village = row.Village,
                Pop = row.Pop,
                Coordinates = row.Coordinates,
                Selected = true,
            }));
            var savedList = _store.LoadAll().FirstOrDefault(list => string.Equals(list.Name, resolvedListName, StringComparison.OrdinalIgnoreCase));
            ReloadSavedLists(savedList?.Id);
            if (savedList is not null)
            {
                _openedSavedListId = savedList.Id;
                ApplySavedListRows(savedList);
            }

            SetStatus($"Saved '{resolvedListName}' with {imported.Rows.Count} village(s).");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Adding all villages was canceled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not add all villages: {ex.Message}");
        }
        finally
        {
            _activeOperationCts = null;
            BusyOverlay.ShowCancel = false;
            BusyOverlay.Hide();
            SetBusy(false);
        }
    }

    private static IReadOnlyList<string> ParseCommaSeparated(string? value)
    {
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private void AnalyzeMapOasisButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !RefreshVillages())
        {
            return;
        }

        var settings = new MapOasisSettingsWindow(
            _viewModel.Villages,
            _viewModel.SelectedVillage,
            MapOasisScanExists?.Invoke() == true)
        {
            Owner = Window.GetWindow(this),
        };
        if (settings.ShowDialog() != true || settings.Request is null)
        {
            return;
        }

        _ = RunMapOasisScanAndSaveAsync(settings.Request);
    }

    private void CalculateDistanceButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshVillages();
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

    // Scans the selected map area for oases (all types, occupied and free) and saves them as one list. The
    // list keeps each oasis's type/occupied/animals/owner so the farm-add dialog can filter on them.
    private async Task RunMapOasisScanAndSaveAsync(MapOasisScanRequest request)
    {
        if (_busy || MapOasisScanRequested is null)
        {
            return;
        }

        SetBusy(true);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _activeOperationCts = operationCts;
        BusyOverlay.ShowCancel = true;
        BusyOverlay.Show("Analyze map oasis", "0% - preparing map scan...");
        BusyOverlay.IsIndeterminate = false;
        BusyOverlay.ProgressValue = 0;
        try
        {
            var savedPartialResult = false;
            SetStatus(request.Scope == MapOasisScanScope.WholeMap
                ? "Scanning the whole Travian map for oases."
                : $"Scanning a radius of {request.Radius} around ({request.CenterX}|{request.CenterY}).");
            var progress = new Progress<MapOasisScanProgress>(value =>
            {
                savedPartialResult = value.IsPartialResult;
                var percent = value.TotalAreas == 0
                    ? 0
                    : (double)value.CompletedAreas / value.TotalAreas * 100;
                BusyOverlay.ProgressValue = percent;
                BusyOverlay.Text =
                    $"{percent:0}% complete\n" +
                    $"Map area {value.CompletedAreas}/{value.TotalAreas} - {value.TotalAreas - value.CompletedAreas} remaining\n" +
                    $"{value.OasisCount} oases found";
            });
            var oases = await MapOasisScanRequested(request, progress, operationCts.Token);
            if (oases.Count == 0)
            {
                SetStatus("Map oasis scan finished with no oases found. No list was created.");
                return;
            }

            SetEditMode(false);
            _openedSavedListId = null;
            ApplyRows(oases.Select(OasisToRow));
            var name = OasisListNaming.CreateName(
                OasisListNaming.TypeOrder,
                _store.LoadAll().Select(list => list.Name));
            _viewModel.ListName = name;
            SaveCurrentRows(name);
            SetStatus(savedPartialResult
                ? $"Scan canceled. Saved partial list '{name}' with {oases.Count} oasis/oases."
                : $"Saved '{name}' with {oases.Count} oasis/oases.");
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
            Window.GetWindow(this),
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

    private async void CloseTravcoTabButton_Click(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(CloseTravcoSessionAsync, SetStatus);

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

    private async Task CloseTravcoSessionAsync()
    {
        if (_busy || !_sessionActive)
        {
            return;
        }

        SetBusy(true);
        try
        {
            if (CloseRequested is not null)
            {
                await CloseRequested();
            }

            var completionMessage = _browserTabOpen
                ? "Travco browser tab closed. Paused automation resumed."
                : "Analysis session finished. Paused automation resumed.";
            SetSessionState(active: false, browserTabOpen: false);
            SetStatus(completionMessage);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not close Travco tab: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AddAllVillagesButton.IsEnabled = !busy;
        InactiveSearchButton.IsEnabled = !busy;
        AnalyzeMapOasisButton.IsEnabled = !busy;
        CalculateDistanceButton.IsEnabled = !busy;
        DistanceVillageComboBox.IsEnabled = !busy;
        SavedListsListBox.IsEnabled = !busy;
        ResultsDataGrid.IsEnabled = !busy;
        SaveEditedListButton.IsEnabled = !busy && _editMode;
        CloseTravcoTabButton.IsEnabled = !busy && _sessionActive;
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
        // The rename field only makes sense while editing a saved list; hide it otherwise.
        EditNamePanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
    }
}
