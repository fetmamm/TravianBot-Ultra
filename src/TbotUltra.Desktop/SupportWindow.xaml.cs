using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop;

public partial class SupportWindow : Window
{
    private const string DiscordUrl = "https://discord.gg/qrge94p7TH";
    private const string GithubUrl = "https://github.com/fetmamm/Tbot_ultra_new";

    private readonly string _projectRoot;
    private readonly IReadOnlyList<string> _terminalEntries;

    public SupportWindow(string projectRoot, IReadOnlyList<string> terminalEntries)
    {
        InitializeComponent();
        _projectRoot = projectRoot;
        _terminalEntries = terminalEntries;
        InfoTextBlock.Text = "Use Send to open your mail client. If mail client is unavailable, message content is copied to clipboard.";
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

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        var email = EmailTextBox.Text.Trim();
        var message = MessageTextBox.Text.Trim();
        if (message.Length == 0)
        {
            InfoTextBlock.Text = "Message is required.";
            return;
        }

        var subject = Uri.EscapeDataString($"Tbot Ultra support from {name}");
        var body = Uri.EscapeDataString($"Name: {name}\nEmail: {email}\n\n{message}");
        var mailto = $"mailto:?subject={subject}&body={body}";

        try
        {
            var domain = email.Contains('@') ? email.Split('@').Last().Trim().ToLowerInvariant() : string.Empty;
            var webmailUrl = domain switch
            {
                "gmail.com" => $"https://mail.google.com/mail/?view=cm&fs=1&su={subject}&body={body}",
                "outlook.com" or "hotmail.com" or "live.com" or "msn.com" => $"https://outlook.live.com/mail/0/deeplink/compose?subject={subject}&body={body}",
                _ => string.Empty,
            };

            if (!string.IsNullOrWhiteSpace(webmailUrl))
            {
                OpenUrl(webmailUrl);
                InfoTextBlock.Text = "Webmail compose opened in browser.";
            }
            else
            {
                OpenUrl(mailto);
                InfoTextBlock.Text = "Mail client opened.";
            }
        }
        catch
        {
            var fallback = $"Name: {name}{Environment.NewLine}Email: {email}{Environment.NewLine}{Environment.NewLine}{message}";
            Clipboard.SetText(fallback);
            InfoTextBlock.Text = "No mail client detected. Message copied to clipboard.";
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
