using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Services;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

// Toolbar and top-level window command handlers: start/stop/pause the bot,
// help/reload/settings buttons, window closing and popup cleanup.
public partial class MainWindow
{
    private bool _shutdownInProgress;
    private bool _shutdownCompleted;

    private void StartLoopButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsSessionSleeping)
        {
            var changed = _sessionPacer.IsSleepPaused
                ? _sessionPacer.ResumeSleep()
                : _sessionPacer.PauseSleep();
            if (changed)
            {
                UpdateSessionPacingUi();
                UpdateExecutionStateIndicator();
            }
            else
            {
                AppendLog("[pacing] sleep controls are unavailable for this required sleep window.");
                ShowRequiredSleepWarning();
            }

            return;
        }

        if (BlockIfActiveAccountOnHold("Start bot"))
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

    private void ShowRequiredSleepWarning()
    {
        var isScheduledSleep = _sessionPacer.SleepReason == SessionSleepReason.Schedule;
        var title = isScheduledSleep ? "Scheduled sleep is active" : "Daily limit sleep is active";
        var reason = isScheduledSleep
            ? "Pause sleep is unavailable during the configured off-hours."
            : "Pause sleep is unavailable because the daily runtime limit has been reached.";
        var timing = _sessionPacer.PlannedWakeAt is { } wakeAt
            ? $"Next automatic wake: {wakeAt.LocalDateTime:HH:mm}."
            : "The next wake time is being calculated.";
        var content = new StackPanel();
        content.Children.Add(CreatePacingDialogCard(reason, "InfoBgBrush", "InfoBrush", "InfoTextBrush"));
        content.Children.Add(CreatePacingDialogCard(timing, "SurfaceAltBrush", "BorderSubtleBrush", "TextSecondaryBrush", new Thickness(0, 10, 0, 0)));

        var buttons = isScheduledSleep
            ? new (string Label, MessageBoxResult Result)[] { ("Cancel", MessageBoxResult.Cancel), ("Run program", MessageBoxResult.Yes) }
            : new (string Label, MessageBoxResult Result)[] { ("Close", MessageBoxResult.Cancel) };
        var result = AppDialog.ShowCustomContent(
            this,
            content,
            title,
            buttons,
            MessageBoxImage.Warning,
            isScheduledSleep ? MessageBoxResult.Yes : MessageBoxResult.Cancel,
            MessageBoxResult.Cancel,
            successResult: isScheduledSleep ? MessageBoxResult.Yes : null);
        if (isScheduledSleep && result == MessageBoxResult.Yes)
        {
            // Run program is an explicit login request, never an automation-resume request.
            _wasLoggedInBeforeSleep = true;
            _wasContinuousLoopRunningBeforeSleep = false;
            _wasQueueAutoRunningBeforeSleep = false;
            _sessionPacer.WakeNow();
        }
    }

    private Border CreatePacingDialogCard(string text, string backgroundResource, string borderResource, string foregroundResource, Thickness? margin = null)
    {
        var card = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource(backgroundResource),
            BorderBrush = (System.Windows.Media.Brush)FindResource(borderResource),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = margin ?? new Thickness(0),
        };
        card.Child = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource(foregroundResource),
        };
        return card;
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
        _ = sender;
        _ = e;
        OpenSettingsWindow(SettingsCategory.General, null);
    }

    private void OpenSettingsWindow(
        SettingsCategory initialCategory,
        IReadOnlyList<VillageSettingsRow>? villageSettingsRows)
    {
        var optionsBeforeSettings = LoadBotOptions();
        var detailedBrowserLoggingBefore = optionsBeforeSettings.DetailedBrowserLoggingEnabled;
        var detectedResetHour = Services.ProductionBonusStateStore
            .LoadSettings(_projectRoot, _accountStore.ActiveAccountName())
            .DetectedResetHour;
        var dailySpendingPath = AccountStoragePaths.DailySpendingStatePath(
            _projectRoot,
            _accountStore.ActiveAccountName(),
            optionsBeforeSettings.BaseUrl);
        var serverDate = DateOnly.FromDateTime(GetServerNow().Date);
        var dailySpendingState = DailySpendingState.Empty(serverDate);
        try
        {
            dailySpendingState = new DailySpendingStore(dailySpendingPath).Read(serverDate);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not read daily spending state for Settings: {ex.Message}");
        }
        var window = new SettingsWindow(
            _botConfigStore,
            IsSessionSleeping,
            detectedResetHour,
            ValidateActiveProxyPlanForSettings,
            initialCategory,
            BuildTownHallOverviewRows(villageSettingsRows),
            () => new DailySpendingStore(dailySpendingPath).ResetGold(),
            () => new DailySpendingStore(dailySpendingPath).ResetSilver(),
            dailySpendingState.GoldSpent,
            dailySpendingState.SilverSpent)
        {
            Owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(candidate => candidate.IsActive)
                ?? this,
        };
        var saved = window.ShowDialog() == true;
        LoadConfigToUi();
        ConfigureSessionPacerFromConfig();
        RefreshUpdateNotificationState();
        var optionsAfterSettings = LoadBotOptions();
        if (optionsAfterSettings.ConstructionHumanizeDelayEnabled != optionsBeforeSettings.ConstructionHumanizeDelayEnabled)
        {
            ApplyConstructionHumanizeToggleTransition(optionsAfterSettings.ConstructionHumanizeDelayEnabled);
        }
        if (optionsAfterSettings.DetailedBrowserLoggingEnabled != detailedBrowserLoggingBefore)
        {
            AppendLog(optionsAfterSettings.DetailedBrowserLoggingEnabled
                ? "Detailed browser logging ENABLED (development only). Detailed events start with the next browser operation."
                : "Detailed browser logging disabled.");
        }
        if (saved)
        {
            ResetChangedRestartDelayTasks(optionsBeforeSettings, optionsAfterSettings);
            if (window.TownHallSettingsChanged)
            {
                PersistTownHallSettings(window.TownHallResults, villageSettingsRows);
            }
            LogConservativeAutomationWarnings(optionsAfterSettings);
        }

        if (window.SleepNowRequested)
        {
            RequestManualSessionSleep();
        }
    }

    private void ResetChangedRestartDelayTasks(BotOptions before, BotOptions after)
    {
        var changedTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (before.HeroAdventureRestartDelayEnabled != after.HeroAdventureRestartDelayEnabled
            || before.HeroAdventureRestartDelayMinMinutes != after.HeroAdventureRestartDelayMinMinutes
            || before.HeroAdventureRestartDelayMaxMinutes != after.HeroAdventureRestartDelayMaxMinutes)
        {
            changedTasks.Add("hero_manage");
        }
        if (before.SmithyUpgradeRestartDelayEnabled != after.SmithyUpgradeRestartDelayEnabled
            || before.SmithyUpgradeRestartDelayMinMinutes != after.SmithyUpgradeRestartDelayMinMinutes
            || before.SmithyUpgradeRestartDelayMaxMinutes != after.SmithyUpgradeRestartDelayMaxMinutes)
        {
            changedTasks.Add("upgrade_troops_at_smithy");
        }
        if (before.TownHallCelebrationRestartDelayEnabled != after.TownHallCelebrationRestartDelayEnabled
            || before.TownHallCelebrationRestartDelayMinMinutes != after.TownHallCelebrationRestartDelayMinMinutes
            || before.TownHallCelebrationRestartDelayMaxMinutes != after.TownHallCelebrationRestartDelayMaxMinutes)
        {
            changedTasks.Add("run_town_hall_celebration");
        }
        if (before.BreweryCelebrationRestartDelayEnabled != after.BreweryCelebrationRestartDelayEnabled
            || before.BreweryCelebrationRestartDelayMinMinutes != after.BreweryCelebrationRestartDelayMinMinutes
            || before.BreweryCelebrationRestartDelayMaxMinutes != after.BreweryCelebrationRestartDelayMaxMinutes)
        {
            changedTasks.Add("run_brewery_celebration");
        }

        if (changedTasks.Count == 0)
        {
            return;
        }

        var resetCount = 0;
        foreach (var item in _botService.GetQueueItemsForDisplay()
                     .Where(item => changedTasks.Contains(item.TaskName)))
        {
            if (_botService.PatchDeferredQueueItem(item.Id, null, null, TimeSpan.Zero))
            {
                resetCount++;
            }
        }

        AppendLog($"Restart-delay settings changed. Released {resetCount} affected deferred task(s) for an immediate re-check.");
        RefreshQueueUi();
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

            // Sleep, proxy rotation and other in-app browser changes keep auth state so the session can
            // resume. A real process exit is the trust boundary: remove every account's saved cookies and
            // localStorage so a later program launch always starts with a fresh lobby authentication.
            BrowserStatePersistence.ClearAllSavedStates(_projectRoot, AppendLog);

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
            FlushPendingLogsBeforeClearingBrowserStatistics();
            try
            {
                await FlushBrowserStatisticsSavesAsync();
                await _sessionLogWriter.DisposeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not flush background UI persistence during shutdown: {ex}");
            }
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
