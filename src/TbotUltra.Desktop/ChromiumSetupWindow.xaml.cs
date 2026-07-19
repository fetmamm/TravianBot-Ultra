using System.Windows;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

/// <summary>
/// Asks before downloading the bundled Chromium build, and shows progress while it runs.
///
/// This replaces throwing at the ~25 call sites that gate a browser operation: a released build has no
/// developer to read an exception, so a missing browser has to be something the user can fix from inside
/// the app. Shown only when the browser is actually needed, never as a startup nag.
/// </summary>
public partial class ChromiumSetupWindow : Window
{
    private readonly string _projectRoot;
    private readonly Action<string> _log;
    private CancellationTokenSource? _installCts;
    private bool _installing;

    public ChromiumSetupWindow(string projectRoot, Action<string> log)
    {
        InitializeComponent();
        _projectRoot = projectRoot;
        _log = log;

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

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installing)
        {
            return;
        }

        _installing = true;
        _installCts = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        NotNowButton.Content = "Cancel";
        StatusTextBlock.Visibility = Visibility.Visible;
        DownloadProgressBar.Visibility = Visibility.Visible;
        StatusTextBlock.Text = "Preparing download...";

        // Created on the UI thread, so the driver's background output marshals back here automatically.
        var progress = new Progress<ChromiumInstallProgress>(update =>
        {
            StatusTextBlock.Text = update.Status;
            if (update.PercentComplete is int percent)
            {
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressBar.Value = percent;
            }
            else
            {
                DownloadProgressBar.IsIndeterminate = true;
            }
        });

        try
        {
            await ChromiumInstaller.InstallChromiumAsync(_projectRoot, _log, progress, _installCts.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            DialogResult = false;
        }
        catch (Exception ex)
        {
            // Keep the window open so the user can retry without losing their place in the app.
            StatusTextBlock.Text = ex.Message;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            DownloadButton.Content = "Try again";
            DownloadButton.IsEnabled = true;
            NotNowButton.Content = "Close";
        }
        finally
        {
            _installing = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    private void NotNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installing)
        {
            _installCts?.Cancel();
            StatusTextBlock.Text = "Cancelling...";
            return;
        }

        DialogResult = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing mid-download would orphan the driver process; cancel it and let the click handler finish.
        if (_installing)
        {
            e.Cancel = true;
            _installCts?.Cancel();
            StatusTextBlock.Text = "Cancelling...";
            return;
        }

        base.OnClosing(e);
    }
}
