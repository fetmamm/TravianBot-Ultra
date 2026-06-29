using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
    private bool _busy;

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
        RenderStatus();

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
                // RenderStatus() handles the unknown state below.
            }

            RenderStatus();
        }
    }

    // Sets the version lines, the status line and the button states. Called on load / after a check — not
    // during an in-progress download, so it never clobbers a transient "Downloading…/Downloaded to…" message.
    private void RenderStatus()
    {
        CurrentVersionText.Text = $"Current version: v{_currentVersion}";

        var release = _status?.Release;
        if (release is null)
        {
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
            LatestVersionText.Text = "Latest version: unknown";
            StatusText.Text = "Could not check for the latest version (offline or rate-limited). "
                + "You can still open the GitHub releases page.";
            UpdateButtonStates();
            return;
        }

        LatestVersionText.Text = $"Latest version: v{release.LatestVersion}";
        var hasAsset = release.PortableDownloadUrl is not null;
        if (_status!.UpdateAvailable)
        {
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SuccessTextBrush");
            StatusText.Text = hasAsset
                ? $"A new version (v{release.LatestVersion}) is available."
                : $"A new version (v{release.LatestVersion}) is available. No portable asset was found on the "
                    + "release — use the GitHub releases page.";
        }
        else
        {
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
            StatusText.Text = "You are running the latest version.";
        }

        UpdateButtonStates();
    }

    // Button enable/visibility only — safe to call mid-operation without overwriting the status text.
    private void UpdateButtonStates()
    {
        var release = _status?.Release;
        var hasAsset = release?.PortableDownloadUrl is not null;
        DownloadButton.IsEnabled = !_busy && hasAsset;

        // One-click update is only offered for a real portable build (not "dev") when a newer version exists.
        var canSelfUpdate = _status?.UpdateAvailable == true && hasAsset && SelfUpdater.IsSupported(_currentVersion);
        UpdateRestartButton.Visibility = canSelfUpdate ? Visibility.Visible : Visibility.Collapsed;
        UpdateRestartButton.IsEnabled = !_busy && canSelfUpdate;
    }

    private async void UpdateRestartButton_Click(object sender, RoutedEventArgs e)
    {
        var release = _status?.Release;
        if (release?.PortableDownloadUrl is null)
        {
            StatusText.Text = "No portable download is available — use the GitHub releases page.";
            return;
        }

        var choice = AppDialog.ShowCustom(
            this,
            $"Tbot Ultra will download v{release.LatestVersion}, close, install it, and restart.\n\n"
                + "Your accounts, queue, settings, caches and logged-in sessions are kept. Continue?",
            "Update & restart",
            new (string, MessageBoxResult)[]
            {
                ("Update & restart", MessageBoxResult.Yes),
                ("Cancel", MessageBoxResult.Cancel),
            },
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;

        try
        {
            var tempRoot = SelfUpdater.CreateUpdateWorkspace();
            var assetName = string.IsNullOrWhiteSpace(release.PortableAssetName)
                ? "update.zip"
                : release.PortableAssetName;
            var zipPath = Path.Combine(tempRoot, assetName);

            StatusText.Text = $"Downloading v{release.LatestVersion}…";
            DownloadProgress.IsIndeterminate = true;
            var progress = new Progress<double>(fraction =>
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = Math.Clamp(fraction * 100, 0, 100);
            });
            await UpdateChecker.DownloadAsync(release.PortableDownloadUrl, zipPath, progress, CancellationToken.None);

            StatusText.Text = "Extracting update…";
            DownloadProgress.IsIndeterminate = true;
            var extractDir = Path.Combine(tempRoot, "extract");
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir));

            var appDir = SelfUpdater.FindExtractedAppDir(extractDir);
            if (appDir is null)
            {
                StatusText.Text = "Update package looks invalid (app files not found). "
                    + "Use the GitHub releases page instead.";
                return;
            }

            StatusText.Text = "Closing to install the update…";
            SelfUpdater.LaunchUpdater(appDir, tempRoot);
            // The external updater now waits for this process to exit before swapping files.
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Update failed: {ex.Message}";
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Visibility = Visibility.Collapsed;
            SetBusy(false);
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
        SetBusy(true);
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        StatusText.Text = $"Downloading v{release.LatestVersion}…";

        try
        {
            DownloadProgress.IsIndeterminate = true;
            var progress = new Progress<double>(fraction =>
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = Math.Clamp(fraction * 100, 0, 100);
            });

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
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Visibility = Visibility.Collapsed;
            SetBusy(false);
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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtonStates();
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
