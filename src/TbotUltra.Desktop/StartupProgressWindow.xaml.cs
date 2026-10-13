using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TbotUltra.Desktop;

public partial class StartupProgressWindow : Window
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly DispatcherTimer _elapsedTimer;
    private bool _closingAfterReady;
    private string? _logPath;

    public event EventHandler? CancelRequested;

    public StartupProgressWindow()
    {
        InitializeComponent();
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();
        _elapsedTimer.Start();
        Closing += StartupProgressWindow_Closing;
        Closed += (_, _) => _elapsedTimer.Stop();
    }

    public void SetProgress(int percent, string message)
    {
        LaunchProgress.Value = Math.Clamp(percent, 0, 100);
        StatusText.Text = message;
        UpdateElapsed();
    }

    public void ShowFailure(string message, string details, string? logPath)
    {
        Width = 520;
        Height = 320;
        StepText.Text = "FAILED";
        StepText.Foreground = Brushes.IndianRed;
        StatusText.Text = message;
        StatusText.Foreground = Brushes.IndianRed;
        LaunchProgress.Foreground = Brushes.IndianRed;
        ErrorDetails.Text = details;
        ErrorPanel.Visibility = Visibility.Visible;
        _logPath = logPath;
        LogPathText.Text = string.IsNullOrWhiteSpace(logPath) ? "No log was written." : $"Log: {logPath}";
        OpenLogButton.IsEnabled = !string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath);
        OpenLogButton.Visibility = Visibility.Visible;
        CloseButton.ToolTip = "Close";
    }

    public void CloseAfterMainWindowReady()
    {
        _closingAfterReady = true;
        Close();
    }

    private void UpdateElapsed()
    {
        var elapsed = _elapsed.Elapsed;
        ElapsedText.Text = $"Elapsed: {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed
            && !MinimizeButton.IsMouseOver
            && !CloseButton.IsMouseOver)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void StartupProgressWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closingAfterReady)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_logPath) || !File.Exists(_logPath))
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
            start.ArgumentList.Add(_logPath);
            Process.Start(start);
        }
        catch
        {
            StatusText.Text = "Could not open the log. Its path is shown below.";
        }
    }
}
