using System;
using System.Windows;

namespace TbotUltra.Desktop;

// Toolbar and top-level window command handlers: start/stop/pause the bot,
// continuous-run toggle, help/reload/settings buttons, window closing and popup
// cleanup. Extracted verbatim from MainWindow.xaml.cs to keep that file focused;
// same class, so this is a pure relocation with no behavior change.
public partial class MainWindow
{
    private bool _shutdownInProgress;
    private bool _shutdownCompleted;

    private void OpenVerificationBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (BlockIfSessionSleeping("Open verification browser"))
        {
            return;
        }

        OpenVerificationBrowser();
    }

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

        if (ContinuousRunToggleButton.IsChecked != true)
        {
            if (_autoQueueRunning || _uiBusy)
            {
                // Graceful pause: don't pick up new queue items. Let the currently running
                // task finish; the runner will exit at its next iteration check.
                _loopController.RequestQueueStop();
                UpdateExecutionStateIndicator();
                AppendLog("Pause requested. Letting current task finish before stopping.");
                return;
            }

            _loopController.ClearQueueStopRequest();
            ResumePausedQueueItems();
            _ = TriggerQueueAutoRunAsync();
            AppendLog("Function queue start requested.");
            return;
        }

        if (_autoQueueRunning)
        {
            _startContinuousLoopAfterQueueStop = true;
            _loopController.RequestQueueStop();
            UpdateExecutionStateIndicator();
            AppendLog("Continuous loop requested. Letting current queue task finish before switching.");
            return;
        }

        if (_uiBusy && (_loopTask is null || _loopTask.IsCompleted))
        {
            _loopController.RequestQueueStop();
            UpdateExecutionStateIndicator();
            AppendLog("Pause requested. Letting current function finish before stopping.");
            return;
        }

        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            // Pause the loop gracefully too — flag stop, let current iteration finish.
            _loopController.RequestLoopStop();
            UpdateExecutionStateIndicator();
            AppendLog("Pause requested. Loop will stop after the current iteration.");
            return;
        }

        StartContinuousLoopRunner();
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
        if (ContinuousRunToggleButton.IsChecked == true)
        {
            _loopController.RequestLoopStop();
            _loopController.CancelLoop();
        }

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

    private void ContinuousRunToggleButton_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_autoQueueRunning && !_uiBusy && (_loopTask is null || _loopTask.IsCompleted))
        {
            return;
        }

        _loopController.RequestQueueStop();
        _loopController.RequestLoopStop();
        _startContinuousLoopAfterQueueStop = false;
        _loopController.CancelOperation();
        _loopController.CancelAutoQueueRun();
        _loopController.CancelLoop();
        ClearPendingResourceLevelsFromUi();
        SetLoopIndicator(false);
        StartLoopButton.Content = "Start bot";
        StartLoopButton.IsEnabled = true;
        AppendLog("Continuous run disabled. Running actions were stopped.");
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        AppDialog.Show(this,
            "Use Login first.\nCheck village status and Scan all villages add tasks to queue.\nStart/Stop controls loop execution.",
            "Help",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        LoadConfigToUi();
        AppendLog("Config reloaded from config/bot.json.");
        _backgroundTasks.Track(RefreshInboxIndicatorsAsync(logErrors: false));
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_botConfigStore, IsSessionSleeping)
        {
            Owner = this,
        };
        window.ShowDialog();
        LoadConfigToUi();
        ConfigureSessionPacerFromConfig();
        if (window.SleepNowRequested)
        {
            RequestManualSessionSleep();
        }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
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
            ResetSessionPacing();
            _copyFeedbackTimer.Stop();
            _inboxRefreshTimer.Stop();
            _buildQueueCountdownTimer.Stop();
            _resourceSnapshotRefreshTimer.Stop();
            _troopTrainingDeferredRefreshDebounceTimer.Stop();
            _queueUiRefreshTimer.Stop();
            _captchaAutoSolveElapsedTimer?.Stop();
            _loopController.RequestLoopStop();
            _loopController.RequestQueueStop();
            _loopController.CancelOperation();
            _loopController.CancelAutoQueueRun();
            _loopController.CancelLoop();
            _loopController.CancelVillageSwitch();
            _loopController.CancelQueueAutoRunRoot();
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
            _mapOasisWindow?.CloseForShutdown();
            _logsPopupWindow?.Close();
            _queuePopupWindow?.Close();
            CloseCaptchaAutoSolvePopup();
            _resourceTestFunctionsWindow?.Close();
            _savePageHtmlWindow?.Close();
            _bulkSavePageHtmlWindow?.Close();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not close popup windows: {ex.Message}");
        }
    }
}
