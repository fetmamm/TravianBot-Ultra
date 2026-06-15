using System.Diagnostics;
using System.Windows;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private bool _accountScanInProgress;

    private async void AccountScanButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        if (_accountScanInProgress || BlockIfSessionSleeping("Account scan"))
        {
            return;
        }

        if (!_isLoggedIn)
        {
            AppendLog("[account-scan] Active Travian login required.");
            return;
        }

        var choice = AppDialog.ShowCustom(
            this,
            "Analyze every village now?\n\n"
            + "The scan reads all resource fields, buildings, construction queue and Smithy queue. "
            + "Active automation pauses after its current action and resumes when the scan returns to the starting village.",
            "Account scan",
            new (string, MessageBoxResult)[]
            {
                ("Start scan", MessageBoxResult.Yes),
                ("Cancel", MessageBoxResult.Cancel),
            },
            MessageBoxImage.Question,
            MessageBoxResult.Yes,
            MessageBoxResult.Cancel);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        var resumeContinuous = ContinuousRunToggleButton.IsChecked == true
            && ((_loopTask is not null && !_loopTask.IsCompleted)
                || _autoQueueRunning
                || _startContinuousLoopAfterQueueStop);
        var resumeQueue = !resumeContinuous && _autoQueueRunning;
        var safeToResume = true;
        _accountScanInProgress = true;
        AccountScanButton.IsEnabled = false;

        try
        {
            await PauseAutomationForAccountScanAsync(resumeContinuous || resumeQueue);
            safeToResume = await RunAccountScanAsync();
        }
        catch (Exception ex)
        {
            safeToResume = false;
            AppendLog($"[account-scan] Failed before scan could run: {ex.Message}");
            StatusTextBlock.Text = "Account scan failed.";
        }
        finally
        {
            _accountScanInProgress = false;
            AccountScanButton.IsEnabled = _isLoggedIn && !IsSessionSleeping;
            if (safeToResume)
            {
                await ResumeAutomationAfterAccountScanAsync(resumeContinuous, resumeQueue);
            }
            else if (resumeContinuous || resumeQueue)
            {
                AppendLog("[account-scan] Automation remains paused because browser restoration was not confirmed.");
            }
        }
    }

    private async Task PauseAutomationForAccountScanAsync(bool automationWasRunning)
    {
        if (!automationWasRunning)
        {
            return;
        }

        _startContinuousLoopAfterQueueStop = false;
        _restartContinuousLoopAfterStop = false;
        _loopController.RequestLoopStop();
        _loopController.RequestQueueStop();
        UpdateExecutionStateIndicator();
        AppendLog("[account-scan] Pause requested; waiting for current action to finish.");

        var gracefulDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < gracefulDeadline)
        {
            if (!_autoQueueRunning
                && (_loopTask is null || _loopTask.IsCompleted)
                && !_uiBusy)
            {
                AppendLog("[account-scan] Automation paused.");
                return;
            }

            await Task.Delay(120);
        }

        AppendLog("[account-scan] Graceful pause timed out; canceling current action.");
        _loopController.CancelOperation();
        _loopController.CancelAutoQueueRun();
        _loopController.CancelLoop();

        var cancelDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < cancelDeadline)
        {
            if (!_autoQueueRunning
                && (_loopTask is null || _loopTask.IsCompleted)
                && !_uiBusy)
            {
                AppendLog("[account-scan] Automation stopped after cancellation.");
                return;
            }

            await Task.Delay(120);
        }

        throw new InvalidOperationException("Could not pause active automation.");
    }

    private async Task<bool> RunAccountScanAsync()
    {
        var operationId = BeginOperation("AccountScan");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("account-scan");
        var selectedVillageName = GetSelectedVillageName();
        string? startingVillageName = NormalizeVillageName(_activeWorkingVillageName)
            ?? NormalizeVillageName(selectedVillageName);
        string? startingVillageUrl = null;
        BotOptions? options = null;
        var loaded = 0;
        var failed = 0;
        var restoreSucceeded = false;
        var scanCompleted = false;

        ToggleUiBusy(true);
        ShowBusyOverlay("Account scan", "Reading current village list...");
        try
        {
            options = LoadBotOptions();
            var snapshot = await _botService.ReadAccountSnapshotForScanAsync(
                options,
                AppendLog,
                operationToken);
            var villages = snapshot.Villages
                .Where(village => !string.IsNullOrWhiteSpace(village.Name))
                .GroupBy(village => village.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (villages.Count == 0)
            {
                throw new InvalidOperationException("No villages were found.");
            }

            startingVillageName = NormalizeVillageName(snapshot.ActiveVillage)
                ?? startingVillageName
                ?? NormalizeVillageName(villages[0].Name);
            startingVillageUrl = villages.FirstOrDefault(village =>
                string.Equals(
                    NormalizeVillageName(village.Name),
                    startingVillageName,
                    StringComparison.OrdinalIgnoreCase))?.Url;

            SyncDashboardVillageUiFromVillages(
                villages,
                startingVillageName,
                selectedVillageName);
            AppendLog(
                $"[account-scan] Started. villages={villages.Count}, "
                + $"returnVillage='{startingVillageName ?? "-"}'.");

            for (var index = 0; index < villages.Count; index++)
            {
                operationToken.ThrowIfCancellationRequested();
                var village = villages[index];
                BusyOverlay.Text = $"Analyzing village {index + 1}/{villages.Count}: {village.Name}";
                try
                {
                    var status = await ReadAccountScanVillageWithRetryAsync(
                        options,
                        village,
                        operationToken);
                    CacheVillageStatus(status, village.Name);
                    SetActiveWorkingVillageFromStatus(status);
                    SyncDashboardVillageUiFromVillages(
                        villages,
                        status.ActiveVillage,
                        selectedVillageName);
                    RefreshQueueUi();
                    loaded++;
                    AppendLog(
                        $"[account-scan] {index + 1}/{villages.Count} complete: "
                        + $"village='{village.Name}', fields={status.ResourceFields.Count}, "
                        + $"buildings={status.Buildings.Count}, construction={status.ActiveConstructions?.Count ?? status.BuildQueue.Count}, "
                        + $"smithy={status.SmithyUpgradeStatus?.ActiveUpgradeCount ?? 0}.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppendLog(
                        $"[account-scan] {index + 1}/{villages.Count} failed for "
                        + $"'{village.Name}': {ex.Message}");
                }
            }

            scanCompleted = true;
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{operationId}] INFO account scan canceled.");
            StatusTextBlock.Text = "Account scan canceled.";
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            StatusTextBlock.Text = "Account scan failed.";
        }
        finally
        {
            if (options is not null)
            {
                restoreSucceeded = await RestoreVillageAfterAccountScanAsync(
                    options,
                    startingVillageName,
                    startingVillageUrl);
            }

            if (scanCompleted && restoreSucceeded)
            {
                CompleteOperation(
                    operationId,
                    operationSw,
                    $"Account scan completed: {loaded} village(s), failed={failed}; starting village restored.");
                StatusTextBlock.Text = failed == 0
                    ? $"Account scan completed: {loaded} village(s)."
                    : $"Account scan completed with {failed} failure(s).";
            }
            else if (scanCompleted)
            {
                var restoreError = new InvalidOperationException(
                    $"Account scan read {loaded} village(s), but the starting village could not be restored.");
                FailOperation(operationId, operationSw, restoreError);
                StatusTextBlock.Text = "Account scan finished, but browser restoration failed.";
            }
            RefreshSelectedVillageAfterAccountScan();
            HideBusyOverlay();
            ToggleUiBusy(false);
            DisposeOperationCts();
        }

        return restoreSucceeded;
    }

    private async Task<VillageStatus> ReadAccountScanVillageWithRetryAsync(
        BotOptions options,
        Village village,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var status = await _botService.ReadVillageStatusWithSmithyAsync(
                    options,
                    AppendLog,
                    village.Name,
                    village.Url,
                    cancellationToken);
                if (status.ResourceFields.Count > 0 && status.Buildings.Count > 0)
                {
                    return status;
                }

                lastError = new InvalidOperationException(
                    $"Incomplete status: fields={status.ResourceFields.Count}, buildings={status.Buildings.Count}.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (attempt < maxAttempts)
            {
                AppendLog(
                    $"[account-scan] Retry {attempt + 1}/{maxAttempts} for "
                    + $"'{village.Name}': {lastError?.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(750 * attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Village '{village.Name}' did not return complete dorf1/dorf2 status after {maxAttempts} attempts.",
            lastError);
    }

    private async Task<bool> RestoreVillageAfterAccountScanAsync(
        BotOptions options,
        string? villageName,
        string? villageUrl)
    {
        if (_loopController.IsClosing || string.IsNullOrWhiteSpace(villageName))
        {
            return false;
        }

        try
        {
            BusyOverlay.Text = $"Returning to {villageName}...";
            await _botService.NavigateToVillageResourceFieldsAsync(
                options,
                AppendLog,
                villageName,
                villageUrl,
                CancellationToken.None);
            SetActiveWorkingVillage(
                ResolveVillageKeyByName(villageName),
                villageName);
            AppendLog($"[account-scan] Returned to starting village '{villageName}'.");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"[account-scan] Could not return to starting village '{villageName}': {ex.Message}");
            return false;
        }
    }

    private void RefreshSelectedVillageAfterAccountScan()
    {
        if (VillageComboBox.SelectedItem is VillageSelectionItem selected)
        {
            ShowSelectedVillageFromCache(selected);
        }
        else
        {
            RefreshVillageActivityIndicatorsOnDashboard();
            RefreshQueueUi();
        }
    }

    private async Task ResumeAutomationAfterAccountScanAsync(
        bool resumeContinuous,
        bool resumeQueue)
    {
        if (_loopController.IsClosing || !_isLoggedIn || IsSessionSleeping)
        {
            return;
        }

        if (resumeContinuous
            && ContinuousRunToggleButton.IsChecked == true
            && (_loopTask is null || _loopTask.IsCompleted))
        {
            AppendLog("[account-scan] Resuming continuous loop.");
            StartContinuousLoopRunner();
            return;
        }

        if (resumeQueue && !_autoQueueRunning)
        {
            AppendLog("[account-scan] Resuming queue auto-run.");
            _loopController.ClearQueueStopRequest();
            await TriggerQueueAutoRunAsync();
        }
    }
}
