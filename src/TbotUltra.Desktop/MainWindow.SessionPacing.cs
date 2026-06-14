using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private bool _sessionPacingSleepInProgress;
    private bool _sessionPacingWakeInProgress;

    // Visual state of the pacing box. Animated background pulse is (re)started only on state changes so
    // the 1s UI tick doesn't restart/flicker the animation.
    private enum PacingVisual { Idle, Running, Approaching, Sleeping }

    private static readonly TimeSpan PacingApproachingThreshold = TimeSpan.FromMinutes(5);
    private SolidColorBrush? _pacingBrush;
    private PacingVisual? _pacingVisualState;

    // Snapshot of what was actually running when sleep started, so wake restores the same state instead
    // of always starting the continuous loop (the toggle defaults to ON even when the bot was idle).
    private bool _wasLoggedInBeforeSleep;
    private bool _wasContinuousLoopRunningBeforeSleep;
    private bool _wasQueueAutoRunningBeforeSleep;
    private bool IsSessionSleeping => _sessionPacer.Phase == SessionPacerPhase.Sleeping;

    private void InitializeSessionPacing()
    {
        _sessionPacer.Logger = AppendLog;
        _sessionPacer.SleepStarting += (_, _) => _backgroundTasks.Track(
            SafeSessionPacingInvokeAsync(() => HandleSessionPacingSleepStartingAsync()));
        _sessionPacer.WakeRequested += (_, _) => _backgroundTasks.Track(
            SafeSessionPacingInvokeAsync(HandleSessionPacingWakeRequestedAsync));
        _sessionPacer.RuntimeStateChanged += (_, _) => PersistSessionPacingRuntimeState();

        // Use a mutable brush so the pacing box background can be animated (XAML's literal brush is frozen).
        if (SessionPacingBorder is not null)
        {
            _pacingBrush = new SolidColorBrush(ThemeColors.Get("SurfaceBrush"));
            SessionPacingBorder.Background = _pacingBrush;
            ToolTipService.SetInitialShowDelay(SessionPacingBorder, 700);
        }

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
            ReadInt(config, BotOptionPayloadKeys.SessionPacingSleepMinutes, PacingDefaults.SessionPacingSleepMinutes, 30, 10080),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingVariationPercent, PacingDefaults.SessionPacingVariationPercent, 0, 100),
            ReadAllowedHours(config),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingDailyMaxHours, PacingDefaults.SessionPacingDailyMaxHours, 0, 24),
            ReadRuntimeDate(config),
            ReadDouble(config, BotOptionPayloadKeys.SessionPacingRuntimeSeconds, 0, 0, 86400)));
    }

    private void PersistSessionPacingRuntimeState()
    {
        try
        {
            var state = _sessionPacer.RuntimeState;
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.SessionPacingRuntimeDate] = state.Date.ToString("yyyy-MM-dd");
            config[BotOptionPayloadKeys.SessionPacingRuntimeSeconds] = state.RuntimeSeconds;
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"[pacing] could not save daily runtime: {ex.Message}");
        }
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

    private void ResetSessionPacing()
    {
        _sessionPacer.Reset();
    }

    // Triggered from the Settings popup "Sleep now" button (after the user confirms). Reuses the normal
    // controlled-sleep flow but forces the sleep so it also works when session pacing is turned off.
    private void RequestManualSessionSleep()
    {
        if (IsSessionSleeping)
        {
            AppendLog("[pacing] manual sleep ignored: already sleeping.");
            return;
        }

        AppendLog("[pacing] manual sleep requested from settings.");
        _backgroundTasks.Track(SafeSessionPacingInvokeAsync(() => HandleSessionPacingSleepStartingAsync(manual: true)));
    }

    private async Task HandleSessionPacingSleepStartingAsync(bool manual = false)
    {
        if (_sessionPacingSleepInProgress)
        {
            return;
        }

        _sessionPacingSleepInProgress = true;
        try
        {
            // Capture the pre-sleep state BEFORE stopping anything, so wake can restore it.
            _wasLoggedInBeforeSleep = _isLoggedIn;
            _wasContinuousLoopRunningBeforeSleep = _loopTask is not null && !_loopTask.IsCompleted;
            _wasQueueAutoRunningBeforeSleep = _autoQueueRunning;
            AppendLog($"[pacing] pre-sleep state: loggedIn={_wasLoggedInBeforeSleep}, "
                + $"continuousLoop={_wasContinuousLoopRunningBeforeSleep}, queueAutoRun={_wasQueueAutoRunningBeforeSleep}.");

            AppendLog("[pacing] controlled session stop requested.");
            _loopController.RequestLoopStop();
            _loopController.RequestQueueStop();
            _loopController.CancelOperation();
            _loopController.CancelAutoQueueRun();
            _loopController.CancelLoop();

            // Disable background session work BEFORE logout (mirrors ResetForAccountSwitchAsync). While
            // these stay true, the ~16s resource-refresh tick can slip onto the session gate during/after
            // logout and silently log the account back in — especially if logout throws before
            // LogoutCoreAsync clears them. Flipping them here makes the background ticks bail immediately.
            _isLoggedIn = false;
            _browserSessionLikelyOpen = false;
            _inboxAutoEnabled = false;
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

            ConfigureSessionPacerFromConfig();
            _sessionPacer.BeginSleep(manual);
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

            // Restore the pre-sleep state. If the bot was idle/logged out before sleeping, stay that way.
            if (!_wasLoggedInBeforeSleep)
            {
                AppendLog("[pacing] wake: was logged out/idle before sleep — staying idle.");
                return;
            }

            await ExecuteLoginFlowAsync();
            if (!_isLoggedIn)
            {
                return;
            }

            var loopIdle = _loopTask is null || _loopTask.IsCompleted;
            if (_wasContinuousLoopRunningBeforeSleep && ContinuousRunToggleButton?.IsChecked == true && loopIdle)
            {
                AppendLog("[pacing] wake: resuming continuous loop (was running before sleep).");
                StartContinuousLoopRunner();
            }
            else if (_wasQueueAutoRunningBeforeSleep && !_autoQueueRunning && loopIdle)
            {
                AppendLog("[pacing] wake: resuming queue auto-run (was running before sleep).");
                _ = TriggerQueueAutoRunAsync();
            }
            else
            {
                AppendLog("[pacing] wake: logged in, staying idle (was not running before sleep).");
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

    private void UpdateSessionPacingUi()
    {
        if (SessionPacingStatusTextBlock is null)
        {
            return;
        }

        SessionPacingStatusTextBlock.Text = _sessionPacer.StatusText;
        SessionPacingRunNowButton.Visibility = _sessionPacer.CanWakeNow
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSessionPacingTooltip();
        ApplySessionSleepingUiState();

        ApplyPacingVisual(ResolvePacingVisual());
    }

    private void UpdateSessionPacingTooltip()
    {
        if (SessionPacingBorder is null)
        {
            return;
        }

        var runTime = SessionPacer.FormatDuration(_sessionPacer.ActiveRunDuration ?? _sessionPacer.TimeUntilSleep);
        var sleepTime = SessionPacer.FormatDuration(_sessionPacer.ActiveSleepDuration ?? _sessionPacer.TimeUntilWake);
        SessionPacingBorder.ToolTip = new ToolTip
        {
            Content = $"Run time: {runTime}\nSleep time: {sleepTime}",
        };
    }

    private bool BlockIfSessionSleeping(string actionName)
    {
        if (!IsSessionSleeping)
        {
            return false;
        }

        AppendLog(string.IsNullOrWhiteSpace(actionName)
            ? "Skipped: bot is sleeping."
            : $"Skipped: bot is sleeping. {actionName} will not run.");
        return true;
    }

    private void ApplySessionSleepingUiState()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(ApplySessionSleepingUiState);
            return;
        }

        var sleeping = IsSessionSleeping;
        SetEnabled(LoginButton, !sleeping && !_uiBusy);
        SetEnabled(LogoutButton, !sleeping && !_uiBusy);
        SetEnabled(StartLoopButton, !sleeping && !_uiBusy && _isLoggedIn);
        SetEnabled(StopBotButton, !sleeping);
        SetEnabled(LoadResourcesButton, !sleeping && !_uiBusy);
        SetEnabled(OpenResourceTestFunctionsButton, !sleeping && !_uiBusy);
        SetEnabled(StorageRefreshButton, !sleeping && !_uiBusy);
        SetEnabled(VillageComboBox, !sleeping && !_uiBusy);
        SetEnabled(AnalyzeFarmListsButton, !sleeping && !_farmingOperationBusy);
        SetEnabled(AnalyzeNatarsProfileButton, !sleeping && !_farmingOperationBusy && _farmingFeaturesAvailable);
        SetEnabled(ShowNatarsListButton, !sleeping && !_farmingOperationBusy && _farmingFeaturesAvailable && _natarsProfileAnalyzed);
        SetEnabled(StartManualFarmingButton, !sleeping && _farmingFeaturesAvailable);
        SetEnabled(StartCatapultWavesButton, !sleeping && !_farmingOperationBusy && _farmingFeaturesAvailable);
        SetEnabled(ReinforcementRefreshVillagesButton, !sleeping && !_uiBusy);
        SetEnabled(ResourceTransferScanVillagesButton, !sleeping && !_uiBusy && !_resourceTransferScanRunning);

        if (_resourceTestFunctionsWindow is not null)
        {
            _resourceTestFunctionsWindow.IsEnabled = !sleeping && !_uiBusy;
        }
    }

    private PacingVisual ResolvePacingVisual()
    {
        switch (_sessionPacer.Phase)
        {
            case SessionPacerPhase.Sleeping:
                return PacingVisual.Sleeping;
            case SessionPacerPhase.Running:
                var untilSleep = _sessionPacer.TimeUntilSleep;
                return untilSleep is not null && untilSleep.Value <= PacingApproachingThreshold
                    ? PacingVisual.Approaching
                    : PacingVisual.Running;
            default:
                return PacingVisual.Idle;
        }
    }

    // Applies the pacing-box background for a visual state. Pulsing states animate the brush color
    // (GPU-composited, negligible cost); only runs when the state actually changes to avoid restarts.
    private void ApplyPacingVisual(PacingVisual state)
    {
        if (_pacingBrush is null || _pacingVisualState == state)
        {
            return;
        }

        _pacingVisualState = state;
        switch (state)
        {
            case PacingVisual.Sleeping:
                StartPacingPulse(ThemeColors.Get("InfoBgBrush"), ThemeColors.Get("SlotSelectedBorderBrush"));
                break;
            case PacingVisual.Approaching:
                StartPacingPulse(ThemeColors.Get("WarningBgBrush"), ThemeColors.Get("AmberPulseBrush"));
                break;
            case PacingVisual.Running:
                SetPacingStaticColor(ThemeColors.Get("MintPulseBrush"));
                break;
            default:
                SetPacingStaticColor(ThemeColors.Get("SurfaceBrush"));
                break;
        }
    }

    private void StartPacingPulse(Color from, Color to)
    {
        var animation = new ColorAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromSeconds(1.2),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        _pacingBrush!.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void SetPacingStaticColor(Color color)
    {
        // Clear any running animation before setting a fixed color.
        _pacingBrush!.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _pacingBrush.Color = color;
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

    private static double ReadDouble(JsonObject config, string key, double defaultValue, double min, double max)
    {
        var value = config[key]?.GetValue<double>() ?? defaultValue;
        return Math.Clamp(value, min, max);
    }

    private static IReadOnlyList<int> ReadAllowedHours(JsonObject config)
    {
        if (config[BotOptionPayloadKeys.SessionPacingAllowedHours] is not JsonArray array)
        {
            return Enumerable.Range(0, 24).ToArray();
        }

        return array
            .Select(node => node?.GetValue<int>() ?? -1)
            .Where(hour => hour is >= 0 and <= 23)
            .Distinct()
            .ToArray();
    }

    private static DateOnly? ReadRuntimeDate(JsonObject config)
    {
        return DateOnly.TryParse(config[BotOptionPayloadKeys.SessionPacingRuntimeDate]?.GetValue<string>(), out var date)
            ? date
            : null;
    }
}
