using System;
using System.Windows;

namespace TbotUltra.Desktop;

// Toolbar and top-level window command handlers: start/stop/pause the bot,
// help/reload/settings buttons, window closing and popup cleanup.
public partial class MainWindow
{
    private bool _shutdownInProgress;
    private bool _shutdownCompleted;

    private void StartLoopButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockIfSessionSleeping("Start bot"))
        {
            return;
        }

        if (!_isLoggedIn)
        {
            return;
        }

        if (_travianLanguageGateActive)
        {
            AppendLog("Start bot blocked until Travian language is verified as English.");
            return;
        }

        if (_autoQueueRunning)
        {
            _startContinuousLoopAfterQueueStop = false;
            RequestImmediatePauseAutomation("Pause requested. Cancelling current task...");
            return;
        }

        if (_uiBusy && (_loopTask is null || _loopTask.IsCompleted))
        {
            RequestImmediatePauseAutomation("Pause requested. Cancelling current task...");
            return;
        }

        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            RequestImmediatePauseAutomation("Pause requested. Cancelling current task...");
            return;
        }

        StartContinuousLoopRunner();
    }

    private void RequestImmediatePauseAutomation(string message)
    {
        _loopController.RequestLoopStop();
        _loopController.RequestQueueStop();
        _loopController.CancelLoop();
        _loopController.CancelAutoQueueRun();
        _loopController.CancelOperation();
        _loopController.CancelSessionScope();

        EndInlineWait();
        UpdateExecutionStateIndicator();
        AppendLog(message);
    }

    private void StopBotButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockIfSessionSleeping("Stop bot"))
        {
            return;
        }

        // Confirm before the hard stop so an accidental click cannot wipe the active queue. The
        // message is explicit that stopping clears the queue for all villages.
        var choice = AppDialog.ShowCustom(
            this,
            "Stopping the bot will also clear the active queue for all villages. Are you sure you want to continue?",
            "Stop bot",
            new (string, MessageBoxResult)[]
            {
                ("Yes", MessageBoxResult.Yes),
                ("Cancel", MessageBoxResult.Cancel),
            },
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        ResetSessionPacing();

        // Hard stop: abort whatever is running right now (including waits) and clear state.
        _loopController.RequestQueueStop();
        _loopController.CancelOperation();
        _loopController.CancelAutoQueueRun();
        _loopController.RequestLoopStop();
        _loopController.CancelLoop();
        _loopController.CancelSessionScope();

        EndInlineWait();
        ClearPendingResourceLevelsFromUi();
        _buildingDemolishingSlots.Clear();
        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        _buildingClickCooldownBySlot.Clear();

        // Drop pending/deferred queue items, but keep the hero return timer across Stop.
        try
        {
            var preservedHeroTimers = ClearQueuePreservingDeferredHeroTimers();
            if (preservedHeroTimers > 0)
            {
                AppendLog($"Preserved {preservedHeroTimers} deferred hero timer(s) on stop.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not clear queue on stop: {ex.Message}");
        }

        SetActiveFunctionExecution(null);
        UpdateExecutionStateIndicator();
        AppendLog("Stop requested. Running actions and waits were stopped.");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var detectedResetHour = Services.ProductionBonusStateStore
            .LoadSettings(_projectRoot, _accountStore.ActiveAccountName())
            .DetectedResetHour;
        var window = new SettingsWindow(_botConfigStore, IsSessionSleeping, detectedResetHour)
        {
            Owner = this,
        };
        var saved = window.ShowDialog() == true;
        LoadConfigToUi();
        ConfigureSessionPacerFromConfig();
        RefreshUpdateNotificationState();
        if (saved)
        {
            LogConservativeAutomationWarnings(LoadBotOptions());
        }

        if (window.SleepNowRequested)
        {
            RequestManualSessionSleep();
        }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        => await GuardUiAsync(() => MainWindowClosingAsync(sender, e));

    private async Task MainWindowClosingAsync(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        try
        {
            _loopController.MarkClosing();
            _inboxAutoEnabled = false;
            _clockTimer.Stop();
            CloseCurrentProxyUsageInterval(DateTimeOffset.UtcNow);
            CloseCurrentSessionActivityInterval(DateTimeOffset.UtcNow);
            NotifySessionPacingOnlineStopped();
            ResetSessionPacing();
            _copyFeedbackTimer.Stop();
            _inboxRefreshTimer.Stop();
            _updateCheckTimer.Stop();
            _buildQueueCountdownTimer.Stop();
            _resourceSnapshotRefreshTimer.Stop();
            _troopTrainingDeferredRefreshDebounceTimer.Stop();
            _queueUiRefreshTimer.Stop();
            _loopController.RequestLoopStop();
            _loopController.RequestQueueStop();
            _loopController.CancelOperation();
            _loopController.CancelAutoQueueRun();
            _loopController.CancelLoop();
            _loopController.CancelVillageSwitch();
            _loopController.CancelQueueAutoRunRoot();
            _loopController.CancelSessionScope();
            ClosePopupWindows();

            var backgroundTasksStopped = await _backgroundTasks.StopAsync(TimeSpan.FromSeconds(10));
            if (!backgroundTasksStopped)
            {
                AppendLog("Shutdown timeout while waiting for background tasks. Continuing browser cleanup.");
            }

            try
            {
                await _botService.ShutdownAsync(AppendLog).WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException)
            {
                AppendLog("Shutdown timeout while closing browser session. Continuing app exit.");
            }

            if (ShouldClearQueueOnShutdown())
            {
                _botService.ClearQueue();
            }

            _loopController.DisposeOperation();
            _loopController.DisposeAutoQueueRun();
            _loopController.DisposeVillageSwitch();
        }
        catch (Exception ex)
        {
            AppendLog($"Shutdown cleanup failed: {ex.Message}");
        }
        finally
        {
            _backgroundTasks.Dispose();
            _shutdownCompleted = true;
            _shutdownInProgress = false;
            Application.Current.Shutdown();
        }
    }

    private void ClosePopupWindows()
    {
        try
        {
            _travcoSuppressRestart = true;
            _travcoToolsWindow?.CloseForShutdown();
            _logsPopupWindow?.Close();
            _queuePopupWindow?.Close();
            _resourceTestFunctionsWindow?.Close();
            _savePageHtmlWindow?.Close();
            _saveReportPngWindow?.Close();
            _bulkSavePageHtmlWindow?.Close();
            _bulkMessagesWindow?.Close();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not close popup windows: {ex.Message}");
        }
    }
}
