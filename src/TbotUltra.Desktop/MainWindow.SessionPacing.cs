using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private bool _sessionPacingSleepInProgress;
    private bool _sessionPacingWakeInProgress;
    private bool _sessionPacingSleepDeferredForManualOperation;
    private string _sessionPacingAccountName = string.Empty;

    // Visual state of the pacing box. Animated background pulse is (re)started only on state changes so
    // the 1s UI tick doesn't restart/flicker the animation.
    private enum PacingVisual { Idle, Running, Approaching, Sleeping }

    private static readonly TimeSpan PacingApproachingThreshold = TimeSpan.FromMinutes(5);
    private SolidColorBrush? _pacingBrush;
    private PacingVisual? _pacingVisualState;

    private sealed record DailyPacingHistoryEntry(
        DateOnly Date,
        double OnlineSeconds,
        double? LimitSeconds,
        int DailyMaxHours);

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

    private void ConfigureSessionPacerFromConfig(bool reloadRuntime = false)
    {
        JsonObject config;
        var accountName = _accountStore.ActiveAccountName();
        try
        {
            config = _botConfigStore.LoadForAccount(accountName);
        }
        catch (Exception ex)
        {
            AppendLog($"[pacing] could not load session pacing settings: {ex.Message}");
            config = [];
        }

        _sessionPacingAccountName = accountName;
        _sessionPacer.Configure(new SessionPacerSettings(
            ReadBool(config, BotOptionPayloadKeys.SessionPacingEnabled, PacingDefaults.SessionPacingEnabled),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingRunMinMinutes, PacingDefaults.SessionPacingRunMinMinutes, 1, 10080),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingRunMaxMinutes, PacingDefaults.SessionPacingRunMaxMinutes, 1, 10080),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingSleepMinMinutes, PacingDefaults.SessionPacingSleepMinMinutes, 5, 10080),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingSleepMaxMinutes, PacingDefaults.SessionPacingSleepMaxMinutes, 5, 10080),
            ReadAllowedHours(config),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingDailyMaxHours, PacingDefaults.SessionPacingDailyMaxHours, 0, 24),
            ReadRuntimeDate(config),
            ReadDouble(config, BotOptionPayloadKeys.SessionPacingRuntimeSeconds, 0, 0, 86400),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingDailyMaxVariationPercent, PacingDefaults.SessionPacingDailyMaxVariationPercent, 0, 50)),
            reloadRuntime);
    }

    private void PersistSessionPacingRuntimeState()
    {
        try
        {
            var progress = _sessionPacer.GetDailyProgress();
            var accountName = string.IsNullOrWhiteSpace(_sessionPacingAccountName)
                ? _accountStore.ActiveAccountName()
                : _sessionPacingAccountName;
            var config = _botConfigStore.LoadForAccount(accountName);
            config[BotOptionPayloadKeys.SessionPacingRuntimeDate] = progress.Date.ToString("yyyy-MM-dd");
            config[BotOptionPayloadKeys.SessionPacingRuntimeSeconds] = progress.OnlineToday.TotalSeconds;
            UpsertDailyPacingHistory(config, progress);
            _botConfigStore.SaveForAccount(accountName, config);
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
        UpdateSessionActivityState(forcePersist: true);
        UpdateSessionPacingUi();
    }

    private void NotifySessionPacingAutomationStopped()
    {
        _sessionPacer.NotifyAutomationStopped();
        UpdateSessionActivityState(forcePersist: true);
        UpdateSessionPacingUi();
    }

    private void NotifySessionPacingOnlineStarted()
    {
        ConfigureSessionPacerFromConfig();
        UpdateSessionActivityState(forcePersist: true);
        UpdateSessionPacingUi();
    }

    private void NotifySessionPacingOnlineStopped()
    {
        _sessionPacer.NotifyAutomationStopped();
        UpdateSessionActivityState(forcePersist: true);
        UpdateSessionPacingUi();
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

        if (!manual && _loopController.HasActiveOperation)
        {
            if (!_sessionPacingSleepDeferredForManualOperation)
            {
                AppendLog("[pacing] automatic sleep delayed until the active manual operation finishes.");
            }

            _sessionPacingSleepDeferredForManualOperation = true;
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
            // these stay true, the ~20s resource-refresh tick can slip onto the session gate during/after
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
            UpdateSessionActivityState(forcePersist: true);
        }
        finally
        {
            _sessionPacingSleepInProgress = false;
        }
    }

    private void TryStartDeferredSessionPacingSleepAfterOperation()
    {
        if (!_sessionPacingSleepDeferredForManualOperation)
        {
            return;
        }

        _sessionPacingSleepDeferredForManualOperation = false;
        if (IsSessionSleeping || _sessionPacingSleepInProgress || _loopController.HasActiveOperation)
        {
            return;
        }

        AppendLog("[pacing] delayed automatic sleep starting after manual operation completed.");
        _backgroundTasks.Track(SafeSessionPacingInvokeAsync(() => HandleSessionPacingSleepStartingAsync()));
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

            // Wake login must survive a transient failure (the root cause of the overnight stall: the
            // post-login snapshot navigation timed out, ExecuteLoginFlowAsync swallowed it, _isLoggedIn
            // stayed false, and the pacer — already Disabled with its timer stopped — never retried). Keep
            // re-logging-in on a backoff until it takes or the state says to stop.
            if (!await TryWakeLoginWithRetryAsync())
            {
                return;
            }

            var loopIdle = _loopTask is null || _loopTask.IsCompleted;
            if (_wasContinuousLoopRunningBeforeSleep && loopIdle)
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

    // Backoff between wake-login attempts. After the ramp it stays at the last value (30 min) with NO cap on
    // attempt count: an overnight transient (network/server timeout during the post-login snapshot) must never
    // leave the bot parked idle until morning — it keeps retrying, sparsely, until login takes.
    private static readonly int[] WakeLoginRetryBackoffMinutes = { 1, 2, 5, 10, 15, 30 };

    // Re-runs ExecuteLoginFlowAsync on a backoff until it logs in. ExecuteLoginFlowAsync swallows its own
    // exceptions and only leaves _isLoggedIn false on failure, so this loop just retries it. Returns true once
    // logged in, false if the retry was aborted (manual login, new sleep, account switch, or app shutdown).
    private async Task<bool> TryWakeLoginWithRetryAsync()
    {
        // Preserve what to resume after login. If a scheduled off-hours / daily-limit window opens mid-retry,
        // a login attempt converts into a planned sleep via TryEnterPlannedSleepInsteadOfLogin, which resets
        // these to "idle". Without restoring them, the wake after that window would log in but never resume
        // the automation that was running before the original sleep.
        var resumeContinuousLoop = _wasContinuousLoopRunningBeforeSleep;
        var resumeQueueAutoRun = _wasQueueAutoRunningBeforeSleep;

        for (var attempt = 1; ; attempt++)
        {
            await ExecuteLoginFlowAsync();
            if (_isLoggedIn)
            {
                if (attempt > 1)
                {
                    AppendLog($"[pacing] wake login succeeded on attempt {attempt}.");
                }

                return true;
            }

            // A planned sleep window (off-hours / daily limit) took over this attempt. Restore the resume
            // intent so the wake after that window continues automation instead of sitting idle logged in.
            if (IsSessionSleeping)
            {
                _wasLoggedInBeforeSleep = true;
                _wasContinuousLoopRunningBeforeSleep = resumeContinuousLoop;
                _wasQueueAutoRunningBeforeSleep = resumeQueueAutoRun;
                AppendLog("[pacing] wake retry: a planned sleep window took over; automation will resume after it.");
                return false;
            }

            if (ShouldAbortWakeRetry(out var reason))
            {
                AppendLog($"[pacing] wake login retry stopped: {reason}.");
                return false;
            }

            var index = Math.Min(attempt - 1, WakeLoginRetryBackoffMinutes.Length - 1);
            var waitMinutes = WakeLoginRetryBackoffMinutes[index];
            AppendLog($"[pacing] wake login failed (attempt {attempt}) — retrying in {waitMinutes} min.");

            if (!await DelayWhileWakeRetryAllowedAsync(TimeSpan.FromMinutes(waitMinutes)))
            {
                AppendLog("[pacing] wake login retry stopped during wait (state changed or app closing).");
                return false;
            }
        }
    }

    // Abort the wake-login retry when logging in no longer makes sense: the user logged in manually, a new
    // sleep window began, an account switch started, or the app is shutting down.
    private bool ShouldAbortWakeRetry(out string reason)
    {
        if (_isLoggedIn)
        {
            reason = "already logged in";
            return true;
        }

        if (IsSessionSleeping)
        {
            reason = "session is sleeping again";
            return true;
        }

        if (_accountSwitchInProgress)
        {
            reason = "account switch in progress";
            return true;
        }

        if (_shutdownInProgress || _shutdownCompleted || _loopController.IsClosing)
        {
            reason = "app closing";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    // Sleeps up to `total` in short slices so an abort (or app shutdown) is noticed within a couple of seconds
    // instead of blocking for the whole retry interval. Returns false as soon as the retry should stop.
    private async Task<bool> DelayWhileWakeRetryAllowedAsync(TimeSpan total)
    {
        var remaining = total;
        var slice = TimeSpan.FromSeconds(2);
        while (remaining > TimeSpan.Zero)
        {
            if (ShouldAbortWakeRetry(out _))
            {
                return false;
            }

            var wait = remaining < slice ? remaining : slice;
            await Task.Delay(wait);
            remaining -= wait;
        }

        return !ShouldAbortWakeRetry(out _);
    }

    // Login should respect planned off-hours / daily-limit windows, but manual in-session actions
    // must not start the normal run/sleep timer.
    private bool TryEnterPlannedSleepInsteadOfLogin()
    {
        if (_loginInProgress || _accountSwitchInProgress || _sessionPacingSleepInProgress)
        {
            return false;
        }

        ConfigureSessionPacerFromConfig();
        if (!_sessionPacer.ShouldSleepNow())
        {
            return false;
        }

        _wasLoggedInBeforeSleep = true;
        _wasContinuousLoopRunningBeforeSleep = false;
        _wasQueueAutoRunningBeforeSleep = false;

        if (!_sessionPacer.BeginScheduledSleepNow())
        {
            return false;
        }

        AppendLog("[login] planned sleep window is active — entering sleep instead of logging in. "
            + "Press the session pacing Run-now button to log in anyway.");
        UpdateSessionPacingUi();
        return true;
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
        UpdateDailyPacingUi();
        SessionPacingRunNowButton.Visibility = _sessionPacer.CanWakeNow
            ? Visibility.Visible
            : Visibility.Collapsed;
        SessionPacingRunNowButton.ToolTip = _sessionPacer.SleepReason == SessionSleepReason.Schedule
            ? "Run now (override the off-hours schedule)"
            : "Run now";
        UpdateSessionPacingTooltip();
        ApplySessionSleepingUiState();

        ApplyPacingVisual(ResolvePacingVisual());
    }

    private void UpdateDailyPacingUi()
    {
        if (DailyOnlineTextBlock is null)
        {
            return;
        }

        var progress = _sessionPacer.GetDailyProgress();
        var activityToday = GetSessionActivityDaySummary(progress.Date);
        DailyOnlineTextBlock.Text = FormatDailyProgressDuration(progress.OnlineToday);
        DailyLeftTextBlock.Text = progress.TimeLeft is null
            ? "-"
            : FormatDailyProgressDuration(progress.TimeLeft.Value);

        DailyPacingBorder.ToolTip = progress.Limit is null
            ? $"Daily max is disabled.\nWaiting today: {FormatDailyProgressDuration(activityToday.Waiting)}"
            : $"Configured daily max: {progress.ConfiguredDailyMaxHours}h\nActual limit today: {FormatDailyProgressDuration(progress.Limit.Value)}\nOnline today: {FormatDailyProgressDuration(progress.OnlineToday)}\nWaiting today: {FormatDailyProgressDuration(activityToday.Waiting)}";
    }

    private void DailyPacingDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSessionActivityState(forcePersist: true);
        var progress = _sessionPacer.GetDailyProgress();
        var activityToday = GetSessionActivityDaySummary(progress.Date);
        var dayRows = BuildDailyPacingDayRows(
            progress,
            out var weekTotalText,
            out var accountTotalText,
            out var chartPoints,
            out var firstTimelineDate);
        var taskRows = BuildDailyPacingTaskRows();
        var timelineSegments = BuildDailyPacingTimelineSegments(firstTimelineDate, progress.Date);
        var window = new DailyPacingDetailsWindow(
            FormatDailyDetailsDuration(progress.OnlineToday),
            FormatDailyDetailsDuration(activityToday.Waiting),
            progress.TimeLeft is null ? "Off" : FormatDailyDetailsDuration(progress.TimeLeft.Value),
            progress.Limit is null ? "Off" : FormatDailyDetailsDuration(progress.Limit.Value),
            weekTotalText,
            accountTotalText,
            dayRows,
            taskRows,
            timelineSegments,
            chartPoints)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    // Builds one row per recorded day (no day cap — covers the account's full history), plus the chart
    // series. Outputs both the last-7-day "Week total" and the all-time "Account total".
    private IReadOnlyList<DailyPacingDayRow> BuildDailyPacingDayRows(
        SessionPacerDailyProgress progress,
        out string weekTotalText,
        out string accountTotalText,
        out IReadOnlyList<DailyPacingChartPoint> chartPoints,
        out DateOnly firstTimelineDate)
    {
        var history = ReadDailyPacingHistory()
            .ToDictionary(entry => entry.Date);

        // Span from the earliest recorded day (or today if none) to today, filling gap days with zero so
        // the list and graph read continuously day by day.
        var earliest = history.Keys.Append(progress.Date).Min();
        var rows = new List<DailyPacingDayRow>();
        var points = new List<DailyPacingChartPoint>();
        var totalOnline = TimeSpan.Zero;
        var weekOnline = TimeSpan.Zero;
        var weekCutoff = progress.Date.AddDays(-6);
        var activitySummaries = BuildSessionActivityDaySummaries(earliest, progress.Date, DateTimeOffset.UtcNow);
        firstTimelineDate = earliest;

        for (var date = earliest; date <= progress.Date; date = date.AddDays(1))
        {
            var online = TimeSpan.Zero;
            TimeSpan? limit = null;
            if (history.TryGetValue(date, out var entry))
            {
                online = TimeSpan.FromSeconds(Math.Max(0, entry.OnlineSeconds));
                limit = entry.LimitSeconds is null
                    ? null
                    : TimeSpan.FromSeconds(Math.Max(0, entry.LimitSeconds.Value));
            }

            if (date == progress.Date)
            {
                online = progress.OnlineToday;
                limit = progress.Limit;
            }

            var activity = activitySummaries.TryGetValue(date, out var summary)
                ? summary
                : new SessionActivityDaySummary(date, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
            totalOnline += online;
            if (date >= weekCutoff)
            {
                weekOnline += online;
            }

            rows.Add(new DailyPacingDayRow(
                date.ToString("yyyy-MM-dd"),
                FormatDailyDetailsDuration(online),
                FormatDailyDetailsDuration(activity.Waiting),
                limit is null ? "Off" : FormatDailyDetailsDuration(limit.Value),
                FormatDailyUsage(online, limit)));

            points.Add(new DailyPacingChartPoint(
                date.ToString("MM-dd"),
                online.TotalHours,
                limit?.TotalHours));
        }

        weekTotalText = FormatDailyDetailsDuration(weekOnline);
        accountTotalText = FormatDailyDetailsDuration(totalOnline);
        chartPoints = points;
        return rows.OrderByDescending(row => row.Date).ToList();
    }

    private IReadOnlyList<DailyPacingTaskRow> BuildDailyPacingTaskRows()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var rows = _botService.GetQueueItemsForDisplay()
            .Where(item => item.UpdatedAt >= cutoff)
            .Where(item => item.Status is QueueStatus.Running or QueueStatus.Succeeded or QueueStatus.Failed or QueueStatus.Canceled)
            .GroupBy(item => HumanizeTaskNameForStats(item.TaskName), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(item => item.UpdatedAt).ToList();
                var peakHour = ordered
                    .GroupBy(item => item.UpdatedAt.ToLocalTime().Hour)
                    .OrderByDescending(hourGroup => hourGroup.Count())
                    .ThenBy(hourGroup => hourGroup.Key)
                    .FirstOrDefault();
                return new DailyPacingTaskRow(
                    group.Key,
                    ordered.Count,
                    ordered[0].UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    peakHour is null ? "-" : $"{peakHour.Key:00}:00-{(peakHour.Key + 1) % 24:00}:00");
            })
            .OrderByDescending(row => row.Runs)
            .ThenBy(row => row.Task, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        return rows.Count > 0
            ? rows
            : [new DailyPacingTaskRow("No task history", 0, "-", "-")];
    }

    private void UpsertDailyPacingHistory(JsonObject config, SessionPacerDailyProgress progress)
    {
        // Keep the FULL history (no day cap) so Account total and the day-by-day list/graph cover all time.
        // One entry per day is tiny, so the unbounded growth is negligible on disk.
        var entries = ReadDailyPacingHistory(config)
            .Where(entry => entry.Date <= progress.Date)
            .ToDictionary(entry => entry.Date);

        entries[progress.Date] = new DailyPacingHistoryEntry(
            progress.Date,
            progress.OnlineToday.TotalSeconds,
            progress.Limit?.TotalSeconds,
            progress.ConfiguredDailyMaxHours);

        var array = new JsonArray();
        foreach (var entry in entries.Values.OrderBy(entry => entry.Date))
        {
            var obj = new JsonObject
            {
                ["date"] = entry.Date.ToString("yyyy-MM-dd"),
                ["online_seconds"] = entry.OnlineSeconds,
                ["daily_max_hours"] = entry.DailyMaxHours,
            };
            if (entry.LimitSeconds is double limitSeconds)
            {
                obj["limit_seconds"] = limitSeconds;
            }
            else
            {
                obj["limit_seconds"] = null;
            }
            array.Add(obj);
        }

        config[BotOptionPayloadKeys.SessionPacingDailyHistory] = array;
    }

    private IReadOnlyList<DailyPacingHistoryEntry> ReadDailyPacingHistory()
    {
        try
        {
            return ReadDailyPacingHistory(_botConfigStore.Load());
        }
        catch (Exception ex)
        {
            AppendLog($"[pacing] could not load daily history: {ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<DailyPacingHistoryEntry> ReadDailyPacingHistory(JsonObject config)
    {
        if (config[BotOptionPayloadKeys.SessionPacingDailyHistory] is not JsonArray array)
        {
            return [];
        }

        var entries = new List<DailyPacingHistoryEntry>();
        foreach (var node in array.OfType<JsonObject>())
        {
            if (!DateOnly.TryParse(node["date"]?.GetValue<string>(), out var date))
            {
                continue;
            }

            entries.Add(new DailyPacingHistoryEntry(
                date,
                ReadJsonDouble(node, "online_seconds"),
                node["limit_seconds"] is null ? null : ReadJsonDouble(node, "limit_seconds"),
                (int)Math.Round(ReadJsonDouble(node, "daily_max_hours"))));
        }

        return entries;
    }

    private static double ReadJsonDouble(JsonObject obj, string key)
    {
        try
        {
            return obj[key]?.GetValue<double>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatDailyProgressDuration(TimeSpan value)
    {
        var totalHours = Math.Max(0, (int)value.TotalHours);
        return $"{totalHours:00}:{value.Minutes:00}";
    }

    private static string FormatDailyDetailsDuration(TimeSpan value)
    {
        var totalHours = Math.Max(0, (int)value.TotalHours);
        return $"{totalHours}h{value.Minutes:00}min";
    }

    private static string FormatDailyUsage(TimeSpan online, TimeSpan? limit)
    {
        if (limit is null || limit.Value <= TimeSpan.Zero)
        {
            return "Off";
        }

        var percent = Math.Clamp(online.TotalSeconds / limit.Value.TotalSeconds * 100, 0, 999);
        return $"{percent:0}%";
    }

    private static string HumanizeTaskNameForStats(string taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return "Task";
        }

        return string.Join(
            " ",
            taskName.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
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
        var automationActive = _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted);
        SetEnabled(
            AccountScanButton,
            !sleeping
            && _isLoggedIn
            && !_accountScanInProgress
            && (!_uiBusy || automationActive));
        // Selecting a village in the combo is a pure view/queue-context change (cached data only — it never
        // navigates the browser or wakes the bot; see VillageComboBox_SelectionChanged). Keep it usable while
        // sleeping so the user can browse villages and inspect queues. The actual "Switch village" move still
        // blocks during sleep (SwitchToActiveVillageAsync -> BlockIfSessionSleeping), so sleep stays unbroken.
        SetEnabled(VillageComboBox, !_uiBusy);
        SetEnabled(AnalyzeFarmListsButton, !sleeping && !_farmingOperationBusy);
        SetEnabled(FarmListSendAllNowButton, !sleeping && !_farmingOperationBusy && _farmingFeaturesAvailable && _farmLists.Any(IsRealFarmListRow));
        SetEnabled(StartManualFarmingButton, false);
        SetEnabled(StartCatapultWavesButton, !sleeping && !_farmingOperationBusy && _farmingFeaturesAvailable);
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

    private SolidColorBrush? _supportUpdatePulseBrush;

    // Slow gold "breathing" pulse on the Support (message) button while an update is available, mirroring
    // the session-sleep pulse so the user clearly notices a new release. Pass false to stop it and restore
    // the button's neutral look.
    private void ApplySupportButtonUpdatePulse(bool updateAvailable)
    {
        if (SupportButton is null)
        {
            return;
        }

        if (!updateAvailable)
        {
            _supportUpdatePulseBrush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
            SupportButton.ClearValue(Control.BackgroundProperty);
            SupportButton.ClearValue(Control.BorderBrushProperty);
            SupportButton.ClearValue(Control.ForegroundProperty);
            return;
        }

        SupportButton.BorderBrush = (Brush)FindResource("WarningBorderBrush");
        SupportButton.Foreground = (Brush)FindResource("WarningTextBrush");
        _supportUpdatePulseBrush ??= new SolidColorBrush(ThemeColors.Get("WarningBgBrush"));
        SupportButton.Background = _supportUpdatePulseBrush;
        StartGoldBreathePulse(_supportUpdatePulseBrush);
    }

    // Shared "update available" gold breathe: amber background fading to gold and back, slow and looping.
    // Used by both the dashboard Support button and the Support popup's Version button.
    internal static void StartGoldBreathePulse(SolidColorBrush brush)
    {
        var animation = new ColorAnimation
        {
            From = ThemeColors.Get("WarningBgBrush"),
            To = ThemeColors.Get("AmberPulseBrush"),
            Duration = TimeSpan.FromSeconds(1.6),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
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
