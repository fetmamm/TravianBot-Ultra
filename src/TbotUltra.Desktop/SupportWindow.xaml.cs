using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class SupportWindow : Window
{
    private const string DiscordUrl = "https://discord.gg/7bgzKy9sHK";
    private const string GithubUrl = "https://github.com/fetmamm/TravianBot-Ultra";

    private readonly string _projectRoot;
    private readonly IReadOnlyList<string> _terminalEntries;
    private readonly string _currentVersion;
    private readonly UpdateChecker.UpdateStatus? _updateStatus;
    private readonly bool _muteUpdateNotifications;
    private readonly DiagnosticsExporter _diagnosticsExporter = new();
    private CancellationTokenSource? _diagnosticsCts;

    private static string DiagnosticsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Tbot Ultra",
        "Diagnostics");

    public SupportWindow(
        string projectRoot,
        IReadOnlyList<string> terminalEntries,
        string currentVersion,
        UpdateChecker.UpdateStatus? updateStatus,
        bool muteUpdateNotifications = false)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _projectRoot = projectRoot;
        _terminalEntries = terminalEntries;
        _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "dev" : currentVersion;
        _updateStatus = updateStatus;
        _muteUpdateNotifications = muteUpdateNotifications;
        ApplyVersionButtonState();
    }

    private SolidColorBrush? _versionPulseBrush;

    // Breathes gold (same slow pulse as the dashboard Support button / session sleep) when a newer release
    // exists, neutral grey otherwise — so the user clearly sees an update is available.
    private void ApplyVersionButtonState()
    {
        var updateAvailable = _updateStatus?.UpdateAvailable == true && !_muteUpdateNotifications;
        if (updateAvailable)
        {
            VersionButton.BorderBrush = (Brush)FindResource("WarningBorderBrush");
            VersionButton.Foreground = (Brush)FindResource("WarningTextBrush");
            _versionPulseBrush ??= new SolidColorBrush(ThemeColors.Get("WarningBgBrush"));
            VersionButton.Background = _versionPulseBrush;
            MainWindow.StartGoldBreathePulse(_versionPulseBrush);
            VersionButton.ToolTip = $"Update available: v{_updateStatus!.Release!.LatestVersion}";
            return;
        }

        _versionPulseBrush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
        VersionButton.Background = (Brush)FindResource("SurfaceBrush");
        VersionButton.BorderBrush = (Brush)FindResource("BorderMutedBrush");
        VersionButton.Foreground = (Brush)FindResource("TextMutedBrush");
        VersionButton.ToolTip = "Check the app version";
    }

    private void VersionButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new VersionWindow(_currentVersion, _updateStatus) { Owner = this };
        window.ShowDialog();
    }

    private void DiscordButton_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(DiscordUrl);
    }

    private void GithubButton_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(GithubUrl);
    }

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = AppDialog.Show(
            this,
            "The diagnostics ZIP contains sanitized settings, logs, and runtime diagnostics. "
            + "Screenshots may still show visible game data. Review the ZIP before sharing it.\n\nCreate the file now?",
            "Create diagnostics file",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        DiagnosticsButton.IsEnabled = false;
        OpenDiagnosticsFolderButton.IsEnabled = false;
        InfoTextBlock.Text = "Creating diagnostics file...";
        using var diagnosticsCts = new CancellationTokenSource();
        _diagnosticsCts = diagnosticsCts;
        DiagnosticsBusyOverlay.Show("Creating diagnostics file", "Collecting and sanitizing logs and settings...");
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            var result = await _diagnosticsExporter.CreateAsync(new DiagnosticsExportRequest(
                _projectRoot,
                AppContext.BaseDirectory,
                DiagnosticsDirectory,
                _currentVersion,
                _terminalEntries,
                DateTimeOffset.UtcNow),
                diagnosticsCts.Token);
            InfoTextBlock.Text = $"Diagnostics created: {result.ZipPath}";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{result.ZipPath}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                InfoTextBlock.Text = $"Diagnostics created: {result.ZipPath}\nCould not open File Explorer: {ex.Message}";
            }
        }
        catch (OperationCanceledException) when (diagnosticsCts.IsCancellationRequested)
        {
            InfoTextBlock.Text = "Diagnostics creation canceled.";
        }
        catch (Exception ex)
        {
            InfoTextBlock.Text = $"Could not create diagnostics: {ex.Message}";
        }
        finally
        {
            _diagnosticsCts = null;
            DiagnosticsBusyOverlay.Hide();
            DiagnosticsButton.IsEnabled = true;
            OpenDiagnosticsFolderButton.IsEnabled = true;
        }
    }

    private void DiagnosticsBusyOverlay_Cancelled(object sender, EventArgs e)
    {
        _diagnosticsCts?.Cancel();
    }

    private void OpenDiagnosticsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = DiagnosticsDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            InfoTextBlock.Text = $"Could not open diagnostics folder: {ex.Message}";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

}
