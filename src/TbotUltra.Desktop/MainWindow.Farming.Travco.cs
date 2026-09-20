using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private TravcoToolsControl? _travcoToolsControl;
    private bool _travcoResumeContinuous;
    private bool _travcoResumeQueue;
    private bool _travcoSuppressRestart;
    private bool _travcoSessionActive;
    private bool _travcoBrowserTabOpen;

    private void InitializeTravcoTools()
    {
        var control = new TravcoToolsControl(
            _travcoListStore,
            _allVillagesImportSettingsStore,
            GetTravcoVillages(),
            AppendLog)
        {
            VillagesRequested = GetTravcoVillages,
            SearchRequested = async (request, progress, cancellationToken) =>
            {
                await BeginTravcoSessionAsync();
                return await RunTravcoSearchAsync(request, progress, cancellationToken);
            },
            ScrapeAllPagesRequested = (progress, cancellationToken) =>
                RunManualOperationAsync(
                    "Save All Travco Pages",
                    token => _botService.ScrapeAllTravcoPagesAsync(AppendLog, progress, token),
                    cancellationToken),
            MapOasisScanRequested = async (request, progress, cancellationToken) =>
            {
                await BeginTravcoSessionAsync();
                return await RunMapOasisScanAsync(request, progress, cancellationToken);
            },
            MapOasisScanExists = HasMapOasisScanResult,
            AddAllVillagesRequested = RunAllVillagesImportAsync,
            CloseRequested = CloseTravcoSessionAsync,
        };
        _travcoToolsControl = control;
        FarmingPanelControl.TravcoWorkspaceHost.Content = control;
        UpdateTravcoSessionUi();
    }

    private async Task BeginTravcoSessionAsync()
    {
        if (_travcoSessionActive)
        {
            return;
        }

        if (BlockIfSessionSleeping("Travco analysis"))
        {
            throw new InvalidOperationException("Travco analysis is unavailable while the session is sleeping.");
        }

        if (!_isLoggedIn)
        {
            throw new InvalidOperationException("Travco analysis requires an active Travian login.");
        }

        _travcoResumeContinuous = IsContinuousLoopRunning();
        _travcoResumeQueue = _autoQueueRunning && !_travcoResumeContinuous;
        _travcoSuppressRestart = false;
        await PauseAutomationForTravcoAsync();
        _travcoSessionActive = true;
        _travcoBrowserTabOpen = false;
        UpdateTravcoSessionUi();
        AppendLog("[travco] analysis session started; automation is paused until the session is explicitly finished.");
    }

    private async Task<MapSqlVillageImportResult> RunAllVillagesImportAsync(
        MapSqlVillageImportRequest request,
        IProgress<MapSqlVillageImportProgress> progress,
        CancellationToken cancellationToken)
    {
        return await RunManualOperationAsync(
            "Add all villages",
            token => _botService.ImportAllVillagesAsync(LoadBotOptions(), request, AppendLog, progress, token),
            cancellationToken);
    }

    private async Task<TravcoScrapeResult> RunTravcoSearchAsync(
        TravcoSearchRequest request,
        IProgress<TravcoSearchProgress> progress,
        CancellationToken cancellationToken)
    {
        var options = LoadBotOptions();
        AppendLog(
            $"[travco-ui] analyze requested: coordinates=({request.X}|{request.Y}), days={request.DaysInactive}, order={request.OrderBy}.");
        return await RunManualOperationAsync(
            "Analyze Travco",
            async token =>
            {
                _travcoBrowserTabOpen = true;
                UpdateTravcoSessionUi();
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

        RequestAutomationStop(AutomationStopMode.AfterCurrentAction);
        AppendLog("[travco] pause requested; waiting for the current bot action to finish.");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!_autoQueueRunning && !IsContinuousLoopRunning() && !_uiBusy)
            {
                AppendLog("[travco] bot paused.");
                return;
            }

            await Task.Delay(Random.Shared.Next(150, 350)); // Random wait
        }

        AppendLog("[travco] graceful pause timed out; canceling the active bot operation.");
        _loopController.CancelOperation();
        RequestAutomationStop(AutomationStopMode.CancelCurrentAction);
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

    private async Task CloseTravcoSessionAsync()
    {
        if (!_travcoSessionActive)
        {
            return;
        }

        if (_travcoBrowserTabOpen)
        {
            await _botService.CloseTravcoTabAsync(AppendLog);
        }

        _travcoBrowserTabOpen = false;
        _travcoSessionActive = false;
        UpdateTravcoSessionUi();
        await ResumeAutomationAfterTravcoAsync();
    }

    private void UpdateTravcoSessionUi()
    {
        _travcoToolsControl?.SetSessionState(_travcoSessionActive, _travcoBrowserTabOpen);
        TravcoSessionAttentionBorder.Visibility = _travcoSessionActive ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        GlobalCloseTravcoTabButton.Content = _travcoBrowserTabOpen ? "Close Travco tab" : "Finish Travco session";
        GlobalCloseTravcoTabButton.ToolTip = _travcoBrowserTabOpen
            ? "Close the Travco browser tab and resume paused automation."
            : "Finish the Travco analysis session and resume paused automation.";
    }

    private async void GlobalCloseTravcoTabButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_travcoToolsControl is not null)
        {
            await _travcoToolsControl.RequestCloseSessionAsync();
        }
    }

    private async Task ResumeAutomationAfterTravcoAsync()
    {
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
            await TriggerQueueAutoRunAsync();
        }

        _travcoResumeContinuous = false;
        _travcoResumeQueue = false;
    }
}
