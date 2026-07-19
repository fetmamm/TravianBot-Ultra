using System.Windows;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

/// <summary>
/// Asks whether to download the missing bundled Chromium build. Consent only: the download runs behind
/// the shared busy overlay so it looks like every other long operation, and this window holds no
/// in-flight state that could block its own close.
///
/// This replaces throwing at the ~25 call sites that gate a browser operation — a released build has no
/// developer to read an exception, so a missing browser has to be fixable from inside the app.
/// </summary>
public partial class ChromiumSetupWindow : Window
{
    public ChromiumSetupWindow()
    {
        InitializeComponent();

        if (!ChromiumInstaller.DriverAvailable())
        {
            // Nothing the user can do from here: the installer itself is missing from the app folder.
            ExplanationTextBlock.Text =
                "A browser component is missing, and so is the installer needed to restore it. "
                + "Please download the latest release again.";
            DownloadButton.Visibility = Visibility.Collapsed;
            NotNowButton.Content = "Close";
        }
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void NotNowButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
