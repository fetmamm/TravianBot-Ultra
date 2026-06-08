using System.Windows;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private TravcoToolsWindow? _travcoToolsWindow;
    private bool _travcoResumeContinuous;
    private bool _travcoResumeQueue;
    private bool _travcoSuppressRestart;

    private async void TravcoInactiveSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockIfSessionSleeping("Travco inactive search"))
        {
            return;
        }

        if (_travcoToolsWindow is { IsVisible: true })
        {
            _travcoToolsWindow.Activate();
            return;
        }

        if (!_isLoggedIn)
        {
            AppendLog("Travco inactive search requires an active Travian login.");
            return;
        }

        var options = LoadBotOptions();
        if (options.IsPrivateServer)
        {
            AppendLog("Travco inactive search supports official Travian servers only.");
            return;
        }

        if (!TryGetCapitalCoordinates(out _, out _, out var coordinateError))
        {
            AppendLog(coordinateError);
            return;
        }

        _travcoResumeContinuous = _loopTask is not null && !_loopTask.IsCompleted;
        _travcoResumeQueue = _autoQueueRunning && !_travcoResumeContinuous;
        _travcoSuppressRestart = false;
        await PauseAutomationForTravcoAsync();

        var window = new TravcoToolsWindow(_travcoListStore)
        {
            Owner = this,
            SearchRequested = RunTravcoSearchAsync,
            CloseRequested = () => _botService.CloseTravcoTabAsync(AppendLog),
        };
        window.Closed += TravcoToolsWindow_Closed;
        _travcoToolsWindow = window;
        window.Show();
    }

    private async Task<TravcoScrapeResult> RunTravcoSearchAsync(CancellationToken cancellationToken)
    {
        var options = LoadBotOptions();
        if (options.IsPrivateServer)
        {
            throw new InvalidOperationException("Travco inactive search supports official Travian servers only.");
        }

        if (!TryGetCapitalCoordinates(out var x, out var y, out var error))
        {
            throw new InvalidOperationException(error);
        }

        await _botService.OpenTravcoAndSearchAsync(options, x, y, daysInactive: 2, AppendLog, cancellationToken);
        return await _botService.ScrapeTravcoPageAsync(AppendLog, cancellationToken);
    }

    private async Task PauseAutomationForTravcoAsync()
    {
        if (!_travcoResumeContinuous && !_travcoResumeQueue)
        {
            return;
        }

        _loopController.RequestLoopStop();
        _loopController.RequestQueueStop();
        AppendLog("[travco] pause requested; waiting for the current bot action to finish.");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!_autoQueueRunning && (_loopTask is null || _loopTask.IsCompleted) && !_uiBusy)
            {
                AppendLog("[travco] bot paused.");
                return;
            }

            await Task.Delay(120);
        }

        AppendLog("[travco] graceful pause timed out; canceling the active bot operation.");
        _loopController.CancelOperation();
        _loopController.CancelAutoQueueRun();
        _loopController.CancelLoop();
    }

    private bool TryGetCapitalCoordinates(out int x, out int y, out string error)
    {
        var source = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? [];
        var capital = source.FirstOrDefault(village =>
            village.IsCapital && village.CoordX.HasValue && village.CoordY.HasValue);

        if (capital is null)
        {
            var cachedCapital = _lastBuildingStatus?.Villages.FirstOrDefault(village =>
                village.IsCapital == true && village.CoordX.HasValue && village.CoordY.HasValue);
            if (cachedCapital is not null)
            {
                x = cachedCapital.CoordX!.Value;
                y = cachedCapital.CoordY!.Value;
                error = string.Empty;
                return true;
            }

            x = 0;
            y = 0;
            error = "Capital coordinates are unavailable. Scan villages or refresh village status first.";
            return false;
        }

        x = capital.CoordX!.Value;
        y = capital.CoordY!.Value;
        error = string.Empty;
        return true;
    }

    private async void TravcoToolsWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is TravcoToolsWindow window)
        {
            window.Closed -= TravcoToolsWindow_Closed;
        }

        _travcoToolsWindow = null;
        if (_travcoSuppressRestart || _loopController.IsClosing || !_isLoggedIn)
        {
            _travcoResumeContinuous = false;
            _travcoResumeQueue = false;
            return;
        }

        if (_travcoResumeContinuous
            && ContinuousRunToggleButton.IsChecked == true
            && (_loopTask is null || _loopTask.IsCompleted))
        {
            AppendLog("[travco] resuming continuous loop.");
            StartContinuousLoopRunner();
        }
        else if (_travcoResumeQueue && !_autoQueueRunning)
        {
            AppendLog("[travco] resuming queue auto-run.");
            _loopController.ClearQueueStopRequest();
            await TriggerQueueAutoRunAsync();
        }

        _travcoResumeContinuous = false;
        _travcoResumeQueue = false;
    }
}
