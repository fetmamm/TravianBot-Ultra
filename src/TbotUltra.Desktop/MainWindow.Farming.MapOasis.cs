using System.Windows;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private MapOasisWindow? _mapOasisWindow;
    private bool _mapOasisResumeContinuous;
    private bool _mapOasisResumeQueue;

    private void AnalyzeMapOasisButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mapOasisWindow is { IsVisible: true })
        {
            _mapOasisWindow.Activate();
            return;
        }

        var options = LoadBotOptions();
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            AppendLog("[map-oasis-ui] No active Travian server URL is available.");
            return;
        }

        if (options.IsPrivateServer)
        {
            AppendLog("[map-oasis-ui] Map oasis analysis supports official Travian servers only.");
            return;
        }

        if (!_isLoggedIn)
        {
            AppendLog("[map-oasis-ui] Map oasis analysis requires an active Travian login.");
            return;
        }

        var window = new MapOasisWindow(_travcoListStore, AppendLog)
        {
            Owner = this,
            AnalyzeRequested = RunMapOasisScanAsync,
        };
        window.Closed += MapOasisWindow_Closed;
        _mapOasisWindow = window;
        window.Show();
    }

    private async Task<List<OasisInfo>> RunMapOasisScanAsync(
        bool includeOccupied,
        List<string> selectedTypes,
        IProgress<MapOasisScanProgress> progress,
        CancellationToken cancellationToken)
    {
        _mapOasisResumeContinuous = _loopTask is not null && !_loopTask.IsCompleted;
        _mapOasisResumeQueue = _autoQueueRunning && !_mapOasisResumeContinuous;

        try
        {
            await PauseAutomationForMapOasisAsync(cancellationToken);
            var options = LoadBotOptions();
            var entries = await _botService.ScanMapOasesAsync(
                options,
                includeOccupied,
                selectedTypes,
                AppendLog,
                progress,
                cancellationToken);
            return entries.Select(entry => new OasisInfo
            {
                X = entry.X,
                Y = entry.Y,
                Landscape = 0,
                IsOccupied = entry.IsOccupied,
                OasisType = entry.OasisType,
            }).ToList();
        }
        finally
        {
            await ResumeAutomationAfterMapOasisAsync();
        }
    }

    private async Task PauseAutomationForMapOasisAsync(CancellationToken cancellationToken)
    {
        if (!_mapOasisResumeContinuous && !_mapOasisResumeQueue)
        {
            return;
        }

        _loopController.RequestLoopStop();
        _loopController.RequestQueueStop();
        AppendLog("[map-oasis] pause requested; waiting for the current bot action to finish.");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_autoQueueRunning && (_loopTask is null || _loopTask.IsCompleted) && !_uiBusy)
            {
                AppendLog("[map-oasis] bot paused.");
                return;
            }

            await Task.Delay(120, cancellationToken);
        }

        AppendLog("[map-oasis] graceful pause timed out; canceling the active bot operation.");
        _loopController.CancelOperation();
        _loopController.CancelAutoQueueRun();
        _loopController.CancelLoop();
    }

    private async Task ResumeAutomationAfterMapOasisAsync()
    {
        if (_loopController.IsClosing || !_isLoggedIn)
        {
            _mapOasisResumeContinuous = false;
            _mapOasisResumeQueue = false;
            return;
        }

        if (_mapOasisResumeContinuous
            && ContinuousRunToggleButton.IsChecked == true
            && (_loopTask is null || _loopTask.IsCompleted))
        {
            AppendLog("[map-oasis] resuming continuous loop.");
            StartContinuousLoopRunner();
        }
        else if (_mapOasisResumeQueue && !_autoQueueRunning)
        {
            AppendLog("[map-oasis] resuming queue auto-run.");
            _loopController.ClearQueueStopRequest();
            await TriggerQueueAutoRunAsync();
        }

        _mapOasisResumeContinuous = false;
        _mapOasisResumeQueue = false;
    }

    private void MapOasisWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is MapOasisWindow window)
        {
            window.Closed -= MapOasisWindow_Closed;
        }

        _mapOasisWindow = null;
    }
}
