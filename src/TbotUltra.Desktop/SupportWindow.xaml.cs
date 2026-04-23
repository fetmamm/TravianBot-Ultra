using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

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
            var diagnosticsRoot = Path.Combine(
                _projectRoot,
                "temp_build_out",
                "diagnostics-export",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(diagnosticsRoot);

            var txtPath = Path.Combine(diagnosticsRoot, "diagnostics.txt");
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

            var zipPath = $"{diagnosticsRoot}.zip";
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(diagnosticsRoot, zipPath);
            InfoTextBlock.Text = $"Diagnostics created: {zipPath}";
            OpenUrl(DiscordUrl);
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
            OpenUrl(mailto);
            InfoTextBlock.Text = "Mail client opened.";
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
}
