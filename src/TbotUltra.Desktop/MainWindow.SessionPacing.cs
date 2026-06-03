using System.Text.Json.Nodes;
using System.Windows;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private bool _sessionPacingSleepInProgress;
    private bool _sessionPacingWakeInProgress;

    private void InitializeSessionPacing()
    {
        _sessionPacer.Logger = AppendLog;
        _sessionPacer.SleepStarting += (_, _) => _ = SafeSessionPacingInvokeAsync(HandleSessionPacingSleepStartingAsync);
        _sessionPacer.WakeRequested += (_, _) => _ = SafeSessionPacingInvokeAsync(HandleSessionPacingWakeRequestedAsync);
        ConfigureSessionPacerFromConfig();
        UpdateSessionPacingUi();
    }

    private void ConfigureSessionPacerFromConfig()
    {
        JsonObject config;
        try
        {
            config = _botConfigStore.Load();
        }
        catch (Exception ex)
        {
            AppendLog($"[pacing] could not load session pacing settings: {ex.Message}");
            config = [];
        }

        _sessionPacer.Configure(new SessionPacerSettings(
            ReadBool(config, BotOptionPayloadKeys.SessionPacingEnabled, PacingDefaults.SessionPacingEnabled),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingMaxRunMinutes, PacingDefaults.SessionPacingMaxRunMinutes, 1, 10080),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingSleepMinutes, PacingDefaults.SessionPacingSleepMinutes, 1, 10080),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingVariationPercent, PacingDefaults.SessionPacingVariationPercent, 0, 100)));
    }

    private void NotifySessionPacingAutomationStarted()
    {
        ConfigureSessionPacerFromConfig();
        _sessionPacer.NotifyAutomationStarted();
    }

    private void NotifySessionPacingAutomationStopped()
    {
        _sessionPacer.NotifyAutomationStopped();
    }

    private async Task HandleSessionPacingSleepStartingAsync()
    {
        if (_sessionPacingSleepInProgress)
        {
            return;
        }

        _sessionPacingSleepInProgress = true;
        try
        {
            AppendLog("[pacing] controlled session stop requested.");
            _loopController.RequestLoopStop();
            _loopController.RequestQueueStop();
            _operationCts?.Cancel();
            _autoQueueRunCts?.Cancel();
            _loopCts?.Cancel();
            await StopAllAutomationAndWaitAsync();

            var operationId = BeginOperation("Session sleep");
            var operationSw = System.Diagnostics.Stopwatch.StartNew();
            var operationToken = CancellationToken.None;
            ToggleUiBusy(true);
            try
            {
                await LogoutCoreAsync(operationId, operationToken, clearSavedSession: false);
                CompleteOperation(operationId, operationSw, "Session sleep logout completed.");
            }
            catch (Exception ex)
            {
                AppendLog($"[pacing] session logout failed: {ex.Message}");
            }
            finally
            {
                ToggleUiBusy(false);
            }

            _sessionPacer.BeginSleep();
        }
        finally
        {
            _sessionPacingSleepInProgress = false;
        }
    }

    private async Task HandleSessionPacingWakeRequestedAsync()
    {
        if (_sessionPacingWakeInProgress)
        {
            return;
        }

        _sessionPacingWakeInProgress = true;
        try
        {
            if (_loginInProgress || _accountSwitchInProgress)
            {
                AppendLog("[pacing] wake skipped: login or account switch already in progress.");
                return;
            }

            await ExecuteLoginFlowAsync();
            if (ContinuousRunToggleButton?.IsChecked == true
                && _isLoggedIn
                && (_loopTask is null || _loopTask.IsCompleted))
            {
                StartContinuousLoopRunner();
            }
        }
        finally
        {
            _sessionPacingWakeInProgress = false;
        }
    }

    private async Task SafeSessionPacingInvokeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppendLog($"[pacing] failed: {ex.Message}");
        }
    }

    private void SessionPacingRunNowButton_Click(object sender, RoutedEventArgs e)
    {
        _sessionPacer.WakeNow();
    }

    private void SessionPacingSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsButton_Click(sender, e);
    }

    private void UpdateSessionPacingUi()
    {
        if (SessionPacingStatusTextBlock is null)
        {
            return;
        }

        SessionPacingStatusTextBlock.Text = _sessionPacer.StatusText;
        SessionPacingRunNowButton.Visibility = _sessionPacer.Phase == SessionPacerPhase.Sleeping
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool ReadBool(JsonObject config, string key, bool defaultValue)
    {
        return config[key]?.GetValue<bool>() ?? defaultValue;
    }

    private static int ReadInt(JsonObject config, string key, int defaultValue, int min, int max)
    {
        var value = config[key]?.GetValue<int>() ?? defaultValue;
        return Math.Clamp(value, min, max);
    }
}
