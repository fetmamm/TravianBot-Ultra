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
        => await GuardUiAsync(TravcoInactiveSearchButtonClickAsync);

    private async Task TravcoInactiveSearchButtonClickAsync()
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
        var villages = GetTravcoVillages();
        if (villages.Count == 0)
        {
            AppendLog("Village coordinates are unavailable. Scan villages or refresh village status first.");
            return;
        }

        _travcoResumeContinuous = IsContinuousLoopRunning();
        _travcoResumeQueue = _autoQueueRunning && !_travcoResumeContinuous;
        _travcoSuppressRestart = false;
        await PauseAutomationForTravcoAsync();

        var window = new TravcoToolsWindow(_travcoListStore, villages, AppendLog)
        {
            Owner = this,
            SearchRequested = RunTravcoSearchAsync,
            ScrapeAllPagesRequested = (progress, cancellationToken) =>
                RunManualOperationAsync(
                    "Save All Travco Pages",
                    token => _botService.ScrapeAllTravcoPagesAsync(AppendLog, progress, token),
                    cancellationToken),
            MapOasisScanRequested = RunMapOasisScanAsync,
            CloseRequested = () => _botService.CloseTravcoTabAsync(AppendLog),
        };
        window.Closed += TravcoToolsWindow_Closed;
        _travcoToolsWindow = window;
        window.Show();
    }

    private async Task<TravcoScrapeResult> RunTravcoSearchAsync(
        TravcoSearchRequest request,
        IProgress<TravcoSearchProgress> progress,
        CancellationToken cancellationToken)
    {
        var options = LoadBotOptions();
        AppendLog(
            $"[travco-ui] analyze requested: coordinates=({request.X}|{request.Y}), days={request.DaysInactive}, order={request.OrderBy}.");
        if (IsContinuousLoopRunning() || _autoQueueRunning)
        {
            _travcoResumeContinuous = IsContinuousLoopRunning();
            _travcoResumeQueue = _autoQueueRunning && !_travcoResumeContinuous;
            _travcoSuppressRestart = false;
            await PauseAutomationForTravcoAsync();
        }

        return await RunManualOperationAsync(
            "Analyze Travco",
            async token =>
            {
                await _botService.OpenTravcoAndSearchAsync(options, request, AppendLog, progress, token);
                progress.Report(new TravcoSearchProgress(4, 5, "Reading inactive villages..."));
                var result = await _botService.ScrapeTravcoPageAsync(AppendLog, token);
                progress.Report(new TravcoSearchProgress(5, 5, "Travco analysis complete."));
                return result;
            },
            cancellationToken);
    }

    private async Task<T> RunManualOperationAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var operationId = BeginOperation(operationName);
        var operationSw = System.Diagnostics.Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken, cancellationToken);
        BeginManualFunctionPacingPause();
        try
        {
            var result = await action(linkedCts.Token);
            CompleteOperation(operationId, operationSw, $"{operationName} completed.");
            return result;
        }
        catch (OperationCanceledException)
        {
            SetManualExecutionOutcome(operationId, ManualExecutionOutcome.Canceled);
            _operationNamesById.Remove(operationId);
            AppendLog($"[{operationId}] [CANCELED] {operationSw.Elapsed.TotalSeconds:F1}s | {operationName} canceled.");
            throw;
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            throw;
        }
        finally
        {
            EndManualFunctionPacingPause();
            DisposeOperationCts();
        }
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

            await Task.Delay(Random.Shared.Next(150, 350)); // Random wait
        }

        AppendLog("[travco] graceful pause timed out; canceling the active bot operation.");
        _loopController.CancelOperation();
        _loopController.CancelAutoQueueRun();
        _loopController.CancelLoop();
    }

    private IReadOnlyList<VillageSelectionItem> GetTravcoVillages()
    {
        var source = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? [];
        var villages = source
            .Where(village => village.CoordX.HasValue && village.CoordY.HasValue)
            .ToList();
        if (villages.Count > 0)
        {
            return villages;
        }

        return _lastBuildingStatus?.Villages
            .Where(village => village.CoordX.HasValue && village.CoordY.HasValue)
            .Select(village => new VillageSelectionItem
            {
                Name = village.Name,
                Url = village.Url ?? string.Empty,
                IsCapital = village.IsCapital == true,
                CoordX = village.CoordX,
                CoordY = village.CoordY,
                Population = village.Population,
                CropFields = village.CropFields,
            })
            .ToList()
            ?? [];
    }

    private async void TravcoToolsWindow_Closed(object? sender, EventArgs e)
        => await GuardUiAsync(() => TravcoToolsWindowClosedAsync(sender, e));

    private async Task TravcoToolsWindowClosedAsync(object? sender, EventArgs e)
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

        if (_travcoResumeContinuous && !IsContinuousLoopRunning())
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
