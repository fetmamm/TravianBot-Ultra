using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class SupportWindow : Window
{
    private const string DiscordUrl = "https://discord.gg/7bgzKy9sHK";
    private const string GithubUrl = "https://github.com/fetmamm/Tbot_ultra_new";

    private readonly string _projectRoot;
    private readonly IReadOnlyList<string> _terminalEntries;
    private readonly string _currentVersion;
    private readonly UpdateChecker.UpdateStatus? _updateStatus;
    private readonly bool _muteUpdateNotifications;

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

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var diagnosticsHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Tbot Ultra",
                "Diagnostics");
            Directory.CreateDirectory(diagnosticsHome);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var stagingPath = Path.Combine(diagnosticsHome, $"staging-{stamp}");
            Directory.CreateDirectory(stagingPath);

            var txtPath = Path.Combine(stagingPath, "diagnostics.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"GeneratedUtc={DateTime.UtcNow:O}");
            sb.AppendLine($"OS={RuntimeInformation.OSDescription}");
            sb.AppendLine($"Framework={RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Machine={Environment.MachineName}");
            sb.AppendLine();
            sb.AppendLine("Terminal:");
            foreach (var line in _terminalEntries)
            {
                sb.AppendLine(line);
            }

            File.WriteAllText(txtPath, sb.ToString());

            CopyIfExists(Path.Combine(_projectRoot, "config"), Path.Combine(stagingPath, "config"));
            CopyIfExists(Path.Combine(_projectRoot, ".env"), Path.Combine(stagingPath, ".env"));
            CopyIfExists(Path.Combine(_projectRoot, "temp_build_out", "diagnostics"), Path.Combine(stagingPath, "runtime-diagnostics"));

            var zipPath = Path.Combine(diagnosticsHome, $"TbotUltra-diagnostics-{stamp}.zip");
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(stagingPath, zipPath);
            Directory.Delete(stagingPath, recursive: true);
            InfoTextBlock.Text = $"Diagnostics created: {zipPath}";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{zipPath}\"",
                UseShellExecute = true,
            });

            if (ShowDiagnosticsReadyDialog(zipPath))
            {
                OpenUrl(DiscordUrl);
            }
        }
        catch (Exception ex)
        {
            InfoTextBlock.Text = $"Could not create diagnostics: {ex.Message}";
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

    private static void CopyIfExists(string sourcePath, string targetPath)
    {
        if (Directory.Exists(sourcePath))
        {
            CopyDirectory(sourcePath, targetPath);
            return;
        }

        if (File.Exists(sourcePath))
        {
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destination = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
        }

        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            var destination = Path.Combine(targetDir, Path.GetFileName(sub));
            CopyDirectory(sub, destination);
        }
    }

    private bool ShowDiagnosticsReadyDialog(string zipPath)
    {
        var dialog = new Window
        {
            Title = "Diagnostics ready",
            Owner = this,
            Width = 520,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };
        ThemeChrome.EnableEarlyDarkTitleBar(dialog);

        var text = new TextBlock
        {
            Text = $"Diagnostics ZIP created.\n\nLocation:\n{zipPath}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var openDiscord = new Button
        {
            Content = "Open Discord",
            Width = 120,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Width = 90,
        };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        footer.Children.Add(openDiscord);
        footer.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(text);

        var open = false;
        openDiscord.Click += (_, _) =>
        {
            open = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = root;
        dialog.ShowDialog();
        return open;
    }
}
