using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace TbotUltra.Desktop;

public partial class BulkSavePageHtmlWindow : Window
{
    private readonly string _saveDirectory;

    public event EventHandler<IReadOnlyList<BulkSavePageRequest>>? SaveRequested;
    public event EventHandler? CancelRequested;

    public ObservableCollection<BulkSavePageItem> Pages { get; } =
    [
        new("dorf1.php", "Village - Resources"),
        new("dorf2.php", "Village - Buildings"),
        new("karte.php", "Map"),
        new("statistiken.php", "Statistics"),
        new("berichte.php", "Reports"),
        new("nachrichten.php", "Messages"),
        new("spieler.php", "Player profile"),
        new("dorf3.php", "Village - Overview"),
        new("hero_inventory.php", "Hero - Inventory"),
        new("hero.php", "Hero - Attributes"),
        new("hero_adventure.php", "Hero - Adventures"),
        new("hero_auction.php", "Hero - Auctions"),
        new("build.php?id=39&fastUP=0", "RP - Overview"),
        new("build.php?t=2&id=39", "RP - Send troops"),
        new("build.php?id=39&t=99", "RP - Farm list"),
    ];

    public BulkSavePageHtmlWindow(string saveDirectory)
    {
        _saveDirectory = saveDirectory;
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        DataContext = this;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var item = new BulkSavePageItem("new_page.php", "Custom page");
        Pages.Add(item);
        PagesGrid.SelectedItem = item;
        PagesGrid.ScrollIntoView(item);
        RefreshUiState();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (PagesGrid.SelectedItem is not BulkSavePageItem item)
        {
            return;
        }

        Pages.Remove(item);
        RefreshUiState();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var pages = Pages
            .Where(item => item.IsSelected)
            .Select(item => new BulkSavePageRequest((item.Page ?? string.Empty).Trim(), (item.Alias ?? string.Empty).Trim()))
            .Where(item => !string.IsNullOrWhiteSpace(item.Page))
            .GroupBy(item => item.Page, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (pages.Count == 0)
        {
            AppDialog.Show(this, "Select at least one page.", "Bulk save HTML", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveRequested?.Invoke(this, pages.Select(item => item with { Prefix = (PrefixTextBox.Text ?? string.Empty).Trim() }).ToList());
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_saveDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _saveDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, $"Could not open folder: {ex.Message}", "Bulk save HTML", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_saveDirectory))
        {
            AppDialog.Show(this, "The folder does not exist yet, nothing to clear.", "Bulk save HTML", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = Directory.GetFiles(_saveDirectory);
        if (files.Length == 0)
        {
            AppDialog.Show(this, "The folder is already empty.", "Bulk save HTML", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = AppDialog.Show(
            this,
            $"Delete all {files.Length} file(s) in:\n{_saveDirectory}?\n\nThis cannot be undone.",
            "Clear folder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var deleted = 0;
        var failed = 0;
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch
            {
                failed++;
            }
        }

        var message = failed == 0
            ? $"Deleted {deleted} file(s)."
            : $"Deleted {deleted} file(s). {failed} file(s) could not be deleted (in use or locked).";
        AppDialog.Show(this, message, "Clear folder", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void SetSaveInProgress(bool isSaving, string? message = null)
    {
        PagesGrid.IsEnabled = !isSaving;
        SaveButton.IsEnabled = !isSaving && Pages.Any(item => item.IsSelected && !string.IsNullOrWhiteSpace(item.Page));
        OpenFolderButton.IsEnabled = !isSaving;
        ClearFolderButton.IsEnabled = !isSaving;
        StatusTextBlock.Text = message ?? (isSaving ? "Saving..." : string.Empty);
        if (isSaving)
        {
            LoadingOverlay.Show("Bulk saving HTML", message ?? "Saving HTML pages...");
        }
        else
        {
            LoadingOverlay.Hide();
        }
    }

    public void SetSaveResult(string message)
    {
        StatusTextBlock.Text = message;
        LoadingOverlay.Hide();
        RefreshUiState();
    }

    private void RefreshUiState()
    {
        SaveButton.IsEnabled = Pages.Any(item => item.IsSelected && !string.IsNullOrWhiteSpace(item.Page));
    }

    private void LoadingOverlay_Cancelled(object sender, EventArgs e)
    {
        // The overlay already disabled its button and showed "Cancelling…"; forward to the host.
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class BulkSavePageItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _page;
    private string _alias;

    public BulkSavePageItem(string page, string alias)
    {
        _page = page;
        _alias = alias;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Page
    {
        get => _page;
        set => SetProperty(ref _page, value);
    }

    public string Alias
    {
        get => _alias;
        set => SetProperty(ref _alias, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return;
        }

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record BulkSavePageRequest(string Page, string Alias)
{
    public string Prefix { get; init; } = string.Empty;
}
