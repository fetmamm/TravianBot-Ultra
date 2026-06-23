using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class VersionWindow : Window
{
    private readonly string _currentVersion;
    private UpdateChecker.UpdateStatus? _status;
    private bool _downloading;

    public VersionWindow(string currentVersion, UpdateChecker.UpdateStatus? status)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "dev" : currentVersion;
        _status = status;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Render();

        // No release info yet (startup check failed/raced) → try a fresh check so the popup is useful.
        if (_status?.Release is null)
        {
            StatusText.Text = "Checking for updates…";
            try
            {
                _status = await UpdateChecker.CheckAsync(_currentVersion, CancellationToken.None);
            }
            catch
            {
                // Render() handles the unknown state below.
            }

            Render();
        }
    }

    private void Render()
    {
        CurrentVersionText.Text = $"Current version: v{_currentVersion}";

        var release = _status?.Release;
        if (release is null)
        {
            LatestVersionText.Text = "Latest version: unknown";
            StatusText.Text = "Could not check for the latest version (offline or rate-limited). "
                + "You can still open the GitHub releases page.";
            DownloadButton.IsEnabled = false;
            return;
        }

        LatestVersionText.Text = $"Latest version: v{release.LatestVersion}";
        if (_status!.UpdateAvailable)
        {
            StatusText.Text = release.PortableDownloadUrl is null
                ? $"A new version (v{release.LatestVersion}) is available. No portable asset was found on the "
                    + "release — use the GitHub releases page."
                : $"A new version (v{release.LatestVersion}) is available. Download the portable build and "
                    + "extract it to update.";
            DownloadButton.IsEnabled = !_downloading && release.PortableDownloadUrl is not null;
        }
        else
        {
            StatusText.Text = "You are running the latest version.";
            // Still allow re-downloading the current portable if the asset is known.
            DownloadButton.IsEnabled = !_downloading && release.PortableDownloadUrl is not null;
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var release = _status?.Release;
        if (release?.PortableDownloadUrl is null)
        {
            StatusText.Text = "No portable download is available — use the GitHub releases page.";
            return;
        }

        var suggestedName = string.IsNullOrWhiteSpace(release.PortableAssetName)
            ? $"tbot-ultra-win-x64-v{release.LatestVersion}-portable.zip"
            : release.PortableAssetName;

        var dialog = new SaveFileDialog
        {
            Title = "Save portable build",
            FileName = suggestedName,
            Filter = "Zip archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
        };

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
        {
            dialog.InitialDirectory = downloads;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var destination = dialog.FileName;
        _downloading = true;
        DownloadButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        StatusText.Text = $"Downloading v{release.LatestVersion}…";

        try
        {
            var progress = new Progress<double>(fraction =>
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = Math.Clamp(fraction * 100, 0, 100);
            });
            // No Content-Length → show an indeterminate bar so the user still sees activity.
            DownloadProgress.IsIndeterminate = true;

            await UpdateChecker.DownloadAsync(release.PortableDownloadUrl, destination, progress, CancellationToken.None);

            StatusText.Text = $"Downloaded to: {destination}";
            RevealInExplorer(destination);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Download failed: {ex.Message}";
            TryDeletePartialFile(destination);
        }
        finally
        {
            _downloading = false;
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Visibility = Visibility.Collapsed;
            Render();
        }
    }

    private void ReleasesButton_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(_status?.Release?.ReleaseUrl ?? UpdateChecker.ReleasesPageUrl);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Opening the browser is best-effort.
        }
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Revealing the file is best-effort.
        }
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Leave the partial file if it cannot be removed.
        }
    }
}
