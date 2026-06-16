using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace TbotUltra.Desktop;

public partial class SavePageHtmlWindow : Window
{
    private readonly string _saveDirectory;

    public event EventHandler<SavePageHtmlRequest>? SaveRequested;
    public event EventHandler? BulkSaveRequested;

    public string FileName { get; private set; } = string.Empty;
    public string Notes { get; private set; } = string.Empty;

    public SavePageHtmlWindow(string saveDirectory)
    {
        _saveDirectory = saveDirectory;
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        FileNameTextBox.Text = $"page_{DateTime.Now:yyyyMMdd_HHmmss}";
        RefreshUiState();
        Loaded += (_, _) =>
        {
            FileNameTextBox.Focus();
            FileNameTextBox.SelectAll();
        };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var trimmed = (FileNameTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            AppDialog.Show(this, "Enter a file name.", "Save page HTML", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (trimmed.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
        {
            AppDialog.Show(this, "File name contains invalid characters.", "Save page HTML", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FileName = trimmed;
        Notes = (NotesTextBox.Text ?? string.Empty).Trim();
        SaveRequested?.Invoke(this, new SavePageHtmlRequest(FileName, Notes));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BulkSaveButton_Click(object sender, RoutedEventArgs e)
    {
        BulkSaveRequested?.Invoke(this, EventArgs.Empty);
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
            AppDialog.Show(this, $"Could not open folder: {ex.Message}", "Save page HTML", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_saveDirectory))
        {
            AppDialog.Show(this, "The folder does not exist yet, nothing to clear.", "Save page HTML", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = Directory.GetFiles(_saveDirectory);
        if (files.Length == 0)
        {
            AppDialog.Show(this, "The folder is already empty.", "Save page HTML", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void FileNameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RefreshUiState();
    }

    private void RefreshUiState()
    {
        if (SaveButton is null || FileNameTextBox is null)
        {
            return;
        }

        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(FileNameTextBox.Text);
    }

    public void SetSaveInProgress(bool isSaving)
    {
        SaveButton.IsEnabled = !isSaving && !string.IsNullOrWhiteSpace(FileNameTextBox.Text);
        BulkSaveButton.IsEnabled = !isSaving;
        FileNameTextBox.IsEnabled = !isSaving;
        NotesTextBox.IsEnabled = !isSaving;
        if (isSaving)
        {
            StatusTextBlock.Text = "Saving current browser page...";
        }
    }

    public void SetSaveResult(string message)
    {
        StatusTextBlock.Text = message;
        RefreshUiState();
    }
}

public sealed record SavePageHtmlRequest(string FileName, string Notes);
