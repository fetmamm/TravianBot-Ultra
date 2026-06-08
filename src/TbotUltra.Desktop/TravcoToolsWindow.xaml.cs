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
    private readonly CancellationTokenSource _windowCts = new();
    private readonly TravcoToolsViewModel _viewModel = new();
    private Guid? _openedSavedListId;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _busy;

    public Func<CancellationToken, Task<TravcoScrapeResult>>? SearchRequested { get; init; }
    public Func<Task>? CloseRequested { get; init; }

    public TravcoToolsWindow(TravcoListStore store)
    {
        _store = store;
        InitializeComponent();
        DataContext = _viewModel;
        Closing += TravcoToolsWindow_Closing;
        ReloadSavedLists();
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
        BusyOverlay.Show("Travco inactive search", "Opening Travco and loading inactive villages...");
        try
        {
            var result = await SearchRequested(_windowCts.Token);
            _openedSavedListId = null;
            ApplyRows(result.Rows.Select(TravcoListRow.FromWorker));
            _viewModel.ListName = $"Travco page {result.PageNumber}";
            _viewModel.SelectedSavedList = null;
            _viewModel.StatusText = result.Rows.Count == 0
                ? $"Travco search finished: {result.TotalPages} page(s) found, current page has no matching villages."
                : $"Travco search finished: {result.TotalPages} page(s) found, page {result.PageNumber} has {result.Rows.Count} village(s).";
        }
        catch (OperationCanceledException)
        {
            _viewModel.StatusText = "Travco search canceled.";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = ex.Message;
        }
        finally
        {
            BusyOverlay.Hide();
            SetBusy(false);
        }
    }

    private void SaveListButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Rows.Count == 0)
        {
            _viewModel.StatusText = "There are no Travco rows to save.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_viewModel.ListName))
        {
            _viewModel.StatusText = "Enter a list name.";
            ListNameTextBox.Focus();
            return;
        }

        var list = new TravcoListStore.TravcoSavedList
        {
            Name = _viewModel.ListName.Trim(),
            Rows = BuildSavedRows(),
        };
        _store.Save(list);
        _openedSavedListId = list.Id;
        ReloadSavedLists(list.Id);
        _viewModel.StatusText = $"Saved '{list.Name}' with {list.Rows.Count} row(s).";
    }

    private void OpenListButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedSavedList;
        if (selected is null)
        {
            _viewModel.StatusText = "Select a saved list first.";
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
        _viewModel.StatusText = $"Opened '{selected.Name}' with {selected.Rows.Count} row(s).";
    }

    private void DeleteListButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedSavedList;
        if (selected is null)
        {
            _viewModel.StatusText = "Select a saved list first.";
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
        _viewModel.StatusText = $"Deleted '{selected.Name}'.";
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
            _viewModel.StatusText = $"Could not close Travco tab: {ex.Message}";
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
        SaveListButton.IsEnabled = !busy;
        SavedListsListBox.IsEnabled = !busy;
        ResultsDataGrid.IsEnabled = !busy;
    }
}
