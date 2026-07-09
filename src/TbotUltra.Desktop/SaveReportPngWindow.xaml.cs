using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace TbotUltra.Desktop;

public partial class SaveReportPngWindow : Window
{
    private readonly string _reportsDirectory;

    public event EventHandler<SaveReportPngRequest>? SaveRequested;

    public SaveReportPngWindow(string reportsDirectory)
    {
        _reportsDirectory = reportsDirectory;
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveRequested?.Invoke(
            this,
            new SaveReportPngRequest(
                HideAttackerCheckBox.IsChecked == true,
                HideDefenderCheckBox.IsChecked == true));
    }

    private void OpenReportsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_reportsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _reportsDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, $"Could not open reports folder: {ex.Message}", "Save report as PNG", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    public void SetSaveInProgress(bool isSaving)
    {
        SaveButton.IsEnabled = !isSaving;
        OpenReportsFolderButton.IsEnabled = !isSaving;
        HideAttackerCheckBox.IsEnabled = !isSaving;
        HideDefenderCheckBox.IsEnabled = !isSaving;
        if (isSaving)
        {
            StatusTextBlock.Text = "Saving report...";
        }
    }

    public void SetSaveResult(string message)
    {
        StatusTextBlock.Text = message;
        SetSaveInProgress(false);
    }
}

public sealed record SaveReportPngRequest(bool HideAttacker, bool HideDefender);
