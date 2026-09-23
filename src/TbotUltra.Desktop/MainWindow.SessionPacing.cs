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
    private int _sessionSleepMinMinutes = PacingDefaults.SessionPacingSleepMinMinutes;
    private int _sessionSleepMaxMinutes = PacingDefaults.SessionPacingSleepMaxMinutes;
    private SmartSleepSettings _smartSleepSettings = new(
        PacingDefaults.SmartSleepEnabled,
        PacingDefaults.SmartSleepMinimumOpportunityMinutes,
        PacingDefaults.SmartSleepWakeBeforeMinutes,
        PacingDefaults.SmartSleepWakeAfterMinutes,
        PacingDefaults.SmartSleepFallbackMinMinutes,
        PacingDefaults.SmartSleepFallbackMaxMinutes);
    private bool _smartSleepWakeWhenConstructionQueueClears = PacingDefaults.SmartSleepWakeWhenConstructionQueueClears;
    private string _sessionPacingAccountName = string.Empty;

    // >0 while a scope-limited manual function (Analyze farmlists / Add farms / Create farmlists / Travco)
    // is running. The pacer reads it (see IsManualOperationActive wiring) to freeze the run->sleep
    // countdown for the duration. Counter (not bool) so overlapping/sequential Travco operations balance.
    private int _pacingPauseRequestCount;

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

    private bool IsSessionSleeping => _sessionPacer.Phase == SessionPacerPhase.Sleeping;

    private void InitializeSessionPacing()
    {
        _sessionPacer.Logger = AppendLog;
        _sessionPacer.SleepStarting += (_, _) => _backgroundTasks.Track(
            SafeSessionPacingInvokeAsync(_sessionSleepLifecycle.StartAutomaticSleepAsync));
        _sessionPacer.WakeRequested += (_, _) => _backgroundTasks.Track(
            SafeSessionPacingInvokeAsync(_sessionSleepLifecycle.WakeAsync));
        _sessionPacer.RuntimeStateChanged += (_, _) => PersistSessionPacingRuntimeState();
        _sessionPacer.IsManualOperationActive = () => _pacingPauseRequestCount > 0;

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
        var sessionPacingEnabled = ReadBool(config, BotOptionPayloadKeys.SessionPacingEnabled, PacingDefaults.SessionPacingEnabled);
        var smartSleepEnabled = ReadBool(config, BotOptionPayloadKeys.SmartSleepEnabled, PacingDefaults.SmartSleepEnabled);
        if (sessionPacingEnabled && smartSleepEnabled)
        {
            sessionPacingEnabled = false;
        }
        _smartSleepSettings = new SmartSleepSettings(
            smartSleepEnabled,
            ReadInt(config, BotOptionPayloadKeys.SmartSleepMinimumOpportunityMinutes, PacingDefaults.SmartSleepMinimumOpportunityMinutes, 1, 1440),
            ReadInt(config, BotOptionPayloadKeys.SmartSleepWakeBeforeMinutes, PacingDefaults.SmartSleepWakeBeforeMinutes, 0, 1440),
            ReadInt(config, BotOptionPayloadKeys.SmartSleepWakeAfterMinutes, PacingDefaults.SmartSleepWakeAfterMinutes, 0, 1440),
            ReadInt(config, BotOptionPayloadKeys.SmartSleepFallbackMinMinutes, PacingDefaults.SmartSleepFallbackMinMinutes, 1, 10080),
            ReadInt(config, BotOptionPayloadKeys.SmartSleepFallbackMaxMinutes, PacingDefaults.SmartSleepFallbackMaxMinutes, 1, 10080));
        _smartSleepWakeWhenConstructionQueueClears = ReadBool(
            config,
            BotOptionPayloadKeys.SmartSleepWakeWhenConstructionQueueClears,
            PacingDefaults.SmartSleepWakeWhenConstructionQueueClears);
        _automationPassRuntime.SetSmartSleepDeadlineGroups(SmartSleepDeadlinePolicy.ReadGroups(
            config[BotOptionPayloadKeys.SmartSleepDeadlineGroups]));
        _sessionSleepMinMinutes = ReadInt(
            config,
            BotOptionPayloadKeys.SessionPacingSleepMinMinutes,
            PacingDefaults.SessionPacingSleepMinMinutes,
            5,
            10080);
        _sessionSleepMaxMinutes = Math.Max(
            _sessionSleepMinMinutes,
            ReadInt(
                config,
                BotOptionPayloadKeys.SessionPacingSleepMaxMinutes,
                PacingDefaults.SessionPacingSleepMaxMinutes,
                5,
                10080));
        _sessionPacer.Configure(new SessionPacerSettings(
            sessionPacingEnabled || smartSleepEnabled,
            ReadInt(config, BotOptionPayloadKeys.SessionPacingRunMinMinutes, PacingDefaults.SessionPacingRunMinMinutes, 1, 10080),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingRunMaxMinutes, PacingDefaults.SessionPacingRunMaxMinutes, 1, 10080),
            _sessionSleepMinMinutes,
            _sessionSleepMaxMinutes,
            ReadAllowedHours(config),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingDailyMaxHours, PacingDefaults.SessionPacingDailyMaxHours, 0, 24),
            ReadRuntimeDate(config),
            ReadDouble(config, BotOptionPayloadKeys.SessionPacingRuntimeSeconds, 0, 0, 86400),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingDailyMaxVariationPercent, PacingDefaults.SessionPacingDailyMaxVariationPercent, 0, 50),
            ReadInt(config, BotOptionPayloadKeys.SessionPacingHoursVariationPercent, PacingDefaults.SessionPacingHoursVariationPercent, 0, 49),
            RunTimerEnabled: sessionPacingEnabled),
            reloadRuntime);
        ConfigureProxyPlanTransition(accountName);
    }

    private IReadOnlyDictionary<Guid, DateTimeOffset> ResolveSmartSleepQueueDeadlineOverrides(
        IEnumerable<QueueItem> items,
        DateTimeOffset now)
    {
        var overrides = new Dictionary<Guid, DateTimeOffset>();
        var candidates = new List<(QueueItem Item, DateTimeOffset Deadline)>();
        if (!_smartSleepWakeWhenConstructionQueueClears)
        {
            return overrides;
        }

        foreach (var item in items.Where(item =>
                     item.Status == QueueStatus.Pending
                     && item.Group == QueueGroup.Construction))
        {
            var queueClearDelay = ConstructionQueueState.ResolveSmartSleepQueueClearDelay(
                ResolveBuildingStatusForQueueItem(item),
                _travianPlusActive,
                item,
                now);
            if (queueClearDelay is not { } delay || delay <= TimeSpan.Zero)
            {
                continue;
            }

            var queueClearDeadline = now.Add(delay);
            var effectiveDeadline = item.NextAttemptAt > queueClearDeadline
                ? item.NextAttemptAt
                : queueClearDeadline;
            overrides[item.Id] = effectiveDeadline;
            candidates.Add((item, effectiveDeadline));
        }

        if (candidates.Count > 0)
        {
            var selected = candidates
                .OrderBy(candidate => candidate.Deadline)
                .ThenBy(candidate => candidate.Item.Id)
                .First();
            var distinctDeadlineCount = candidates
                .Select(candidate => candidate.Deadline)
                .Distinct()
                .Count();
            var villageName = NormalizeVillageName(GetQueueItemVillageName(selected.Item)) ?? "-";
            AppendLoopPickVerbose(
                $"[smart-sleep] construction deadline summary: candidates={candidates.Count}, "
                + $"distinctDeadlines={distinctDeadlineCount}, selected='{FormatQueueServerTime(selected.Deadline)}', "
                + $"task='{selected.Item.DisplayName ?? selected.Item.TaskName}', village='{villageName}', "
                + $"itemId={selected.Item.Id}, mode=queue-clear.",
                $"smart-sleep:construction-summary:{selected.Item.Id}:{selected.Deadline.UtcTicks}:"
                    + $"{candidates.Count}:{distinctDeadlineCount}");
        }

        return overrides;
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
        _villageStatusRoundRuntime.SetForceOnWakeRequest(false);
        _automationPassRuntime.PrioritizeDeadlineWorkOnWake = false;
        _pacingPauseRequestCount = 0;
        _sessionSleepLifecycle.Reset();
    }

    // Freeze the pacing run->sleep countdown while a scope-limited manual function runs. Pair with
    // EndManualFunctionPacingPause in a finally so the count always balances. When the function finishes
    // the countdown resumes with its remaining time (no immediate sleep); if nothing was counting down
    // (idle / pacing off / already sleeping) nothing starts.
    private void BeginManualFunctionPacingPause()
    {
        _pacingPauseRequestCount++;
        _sessionPacer.SyncManualOperationPause();
    }

    private void EndManualFunctionPacingPause()
    {
        if (_pacingPauseRequestCount > 0)
        {
            _pacingPauseRequestCount--;
        }

        _sessionPacer.SyncManualOperationPause();
    }

    // Triggered from the Settings popup "Sleep now" button (after the user confirms). Reuses the normal
    // controlled-sleep flow but forces the sleep so it also works when session pacing is turned off.
    private void RequestManualSessionSleep()
    {
        _backgroundTasks.Track(SafeSessionPacingInvokeAsync(_sessionSleepLifecycle.RequestManualSleepAsync));
    }

    private async Task SafeSessionPacingInvokeAsync(Func<Task> action)
    {
        try
        {
            if (Dispatcher.CheckAccess())
            {
                await action();
            }
            else
            {
                await (await Dispatcher.InvokeAsync(action));
            }
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

    private void SmartSleepNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_smartSleepSettings.Enabled
            || _sessionSleepLifecycle.IsSleepInProgress
            || _sessionSleepLifecycle.IsManualSleepRequested
            || IsFreezeActive)
        {
            return;
        }

        if (IsSessionSleeping)
        {
            ShowSleepExtensionDialog(warnAboutDelayedTasks: true);
            return;
        }

        if (!_isLoggedIn || (!IsContinuousLoopRunning() && !_autoQueueRunning))
        {
            return;
        }

        var minimumMinutes = _sessionSleepMinMinutes;
        var maximumMinutes = _sessionSleepMaxMinutes;
        var durationText = minimumMinutes == maximumMinutes
            ? $"{minimumMinutes} minutes"
            : $"{minimumMinutes}–{maximumMinutes} minutes";
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "Put Tbot Ultra to sleep now?",
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"The current action will finish, no new work will start, and the browser will close. "
                + $"Tbot Ultra will wake automatically after {durationText} and resume the previous automation.",
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
        });

        var result = AppDialog.ShowCustomContent(
            this,
            content,
            "Sleep now",
            [("Cancel", MessageBoxResult.Cancel), ("Sleep now", MessageBoxResult.Yes)],
            MessageBoxImage.Question,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel,
            accentResult: MessageBoxResult.Yes,
            hideIcon: true);
        if (result == MessageBoxResult.Yes)
        {
            RequestManualSessionSleep();
        }
    }

    private void ShowSleepExtensionDialog(bool warnAboutDelayedTasks)
    {
        if (!IsSessionSleeping
            || _sessionPacer.SleepReason is SessionSleepReason.Schedule or SessionSleepReason.DailyLimit)
        {
            AppendLog("[pacing] sleep extension is unavailable for this sleep window.");
            return;
        }

        var selectedMinutes = 20;
        var description = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("InfoTextBrush"),
        };
        var descriptionCard = new Border
        {
            Background = (Brush)FindResource("InfoBgBrush"),
            BorderBrush = (Brush)FindResource("InfoBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 12, 0, 0),
            Child = description,
        };
        var picker = new ComboBox
        {
            Height = 30,
            Width = 360,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
            ItemsSource = new[] { "5 minutes", "10 minutes", "20 minutes", "30 minutes", "60 minutes" },
            SelectedItem = "20 minutes",
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "Extend sleep",
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Extend by",
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)FindResource("TextSubtleBrush"),
            FontSize = 12,
        });
        content.Children.Add(picker);
        content.Children.Add(descriptionCard);

        void UpdateDescription()
        {
            selectedMinutes = picker.SelectedItem is string value
                && int.TryParse(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var minutes)
                ? minutes
                : 20;
            var requested = TimeSpan.FromMinutes(selectedMinutes);
            var currentRemaining = _sessionPacer.IsSleepPaused
                ? _sessionPacer.PausedSleepRemaining ?? TimeSpan.Zero
                : _sessionPacer.TimeUntilWake ?? TimeSpan.Zero;
            var newWakeAt = DateTimeOffset.UtcNow.Add(currentRemaining).Add(requested);
            description.Text = $"Sleep will be extended by {selectedMinutes} minutes. "
                + $"New wake: {FormatQueueServerTime(newWakeAt)}."
                + (warnAboutDelayedTasks ? " Planned tasks may be delayed." : string.Empty);
        }

        picker.SelectionChanged += (_, _) => UpdateDescription();
        UpdateDescription();
        var result = AppDialog.ShowCustomContent(
            this,
            content,
            "Extend sleep",
            [("Cancel", MessageBoxResult.Cancel), ("Extend sleep", MessageBoxResult.Yes)],
            MessageBoxImage.Information,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel,
            successResult: MessageBoxResult.Yes,
            hideIcon: true);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var extended = _sessionPacer.ExtendSleep(TimeSpan.FromMinutes(selectedMinutes));
        if (extended > TimeSpan.Zero)
        {
            AppendLog($"[pacing] sleep extended by {SessionPacer.FormatDuration(extended)}; "
                + $"new wake {(_sessionPacer.PlannedWakeAt is { } wakeAt ? FormatQueueServerTime(wakeAt) : "paused")}.");
            UpdateSessionPacingUi();
        }
    }

    private void SessionPacingExtendButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsSessionSleeping)
        {
            ShowSleepExtensionDialog(warnAboutDelayedTasks: false);
            return;
        }

        var allowed = _sessionPacer.GetAllowedRunExtension(TimeSpan.FromMinutes(60));
        if (allowed <= TimeSpan.Zero)
        {
            AppendLog("[pacing] session extension is unavailable because the next restriction is due.");
            return;
        }

        var selectedMinutes = 20;
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("InfoTextBrush") };
        var descriptionCard = new Border
        {
            Background = (Brush)FindResource("InfoBgBrush"),
            BorderBrush = (Brush)FindResource("InfoBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 12, 0, 0),
            Child = description,
        };
        var picker = new ComboBox
        {
            Height = 30,
            Width = 360,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
            ItemsSource = new[] { "5 minutes", "10 minutes", "20 minutes", "30 minutes", "60 minutes" },
            SelectedItem = "20 minutes",
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "Extend active session",
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Extend by",
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)FindResource("TextSubtleBrush"),
            FontSize = 12,
        });
        content.Children.Add(picker);
        content.Children.Add(descriptionCard);

        void UpdateDescription()
        {
            selectedMinutes = picker.SelectedItem is string value
                && int.TryParse(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var minutes)
                ? minutes
                : 20;
            var requested = TimeSpan.FromMinutes(selectedMinutes);
            var actual = _sessionPacer.GetAllowedRunExtension(requested);
            description.Text = actual == requested
                ? $"You are about to extend the active session by {selectedMinutes} minutes."
                : $"You are about to extend the active session by {(int)Math.Floor(actual.TotalMinutes)} minutes (limited by the next restriction).";
        }

        picker.SelectionChanged += (_, _) => UpdateDescription();
        UpdateDescription();
        IReadOnlyList<(string Label, MessageBoxResult Result)> buttons =
        [
            ("Cancel", MessageBoxResult.Cancel),
            ("Sleep now", MessageBoxResult.No),
            ("Extend session", MessageBoxResult.Yes),
        ];
        var result = AppDialog.ShowCustomContent(
            this,
            content,
            "Extend active session",
            buttons,
            MessageBoxImage.Information,
            MessageBoxResult.Yes,
            MessageBoxResult.Cancel,
            accentResult: MessageBoxResult.No,
            successResult: MessageBoxResult.Yes,
            hideIcon: true);
        if (result == MessageBoxResult.No)
        {
            RequestManualSessionSleep();
            return;
        }

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var extended = _sessionPacer.ExtendRun(TimeSpan.FromMinutes(selectedMinutes));
        if (extended > TimeSpan.Zero)
        {
            AppendLog($"[pacing] active session extended by {SessionPacer.FormatDuration(extended)}.");
            UpdateSessionPacingUi();
        }
    }

    private void UpdateSessionPacingUi()
    {
        if (SessionPacingStatusTextBlock is null)
        {
            return;
        }

        if (_sessionSleepLifecycle.VillageRoundDeferredUntilUtc is not null)
        {
            _backgroundTasks.Track(SafeSessionPacingInvokeAsync(
                _sessionSleepLifecycle.PollVillageRoundDeferredSleepAsync));
        }

        SessionPacingStatusTextBlock.Text = _sessionSleepLifecycle.VillageRoundDeferredUntilUtc is { } deferredUntil
            ? $"Village round: sleep in {SessionPacer.FormatDuration(
                deferredUntil > DateTimeOffset.UtcNow
                    ? deferredUntil - DateTimeOffset.UtcNow
                    : TimeSpan.Zero)}"
            : _sessionPacer.StatusText;
        UpdateDailyPacingUi();
        SessionPacingRunNowButton.Visibility = _sessionPacer.CanWakeNow
            ? Visibility.Visible
            : Visibility.Collapsed;
        SessionPacingRunNowButton.ToolTip = _sessionPacer.SleepReason == SessionSleepReason.Schedule
            ? "Run now (override the off-hours schedule)"
            : "Run now";
        SmartSleepNowButton.Visibility = _smartSleepSettings.Enabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        var canExtendSmartSleep = IsSessionSleeping
            && _sessionPacer.SleepReason is not (SessionSleepReason.Schedule or SessionSleepReason.DailyLimit);
        SmartSleepNowButton.IsEnabled = !_sessionSleepLifecycle.IsSleepInProgress
            && !_sessionSleepLifecycle.IsManualSleepRequested
            && !IsFreezeActive
            && (canExtendSmartSleep
                || (_isLoggedIn && (IsContinuousLoopRunning() || _autoQueueRunning)));
        SmartSleepNowButton.ToolTip = IsSessionSleeping ? "Extend sleep" : "Sleep now";
        var canExtendSleep = IsSessionSleeping
            && !_smartSleepSettings.Enabled
            && _sessionPacer.SleepReason is SessionSleepReason.SessionPacing or SessionSleepReason.Manual;
        var canExtendRun = _sessionPacer.Phase == SessionPacerPhase.Running
            && _sessionPacer.IsRunTimerEnabled;
        SessionPacingExtendButton.Visibility = canExtendSleep || canExtendRun
            ? Visibility.Visible
            : Visibility.Collapsed;
        SessionPacingExtendButton.ToolTip = canExtendSleep ? "Extend sleep" : "Extend active session";
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
        var proxyUsageRows = BuildDailyProxyUsageRows(_accountStore.ActiveAccountName());
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
            chartPoints,
            proxyUsageRows)
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
        var rows = TaskActivityStatistics.Build(
                TaskActivityStore.Load(_projectRoot, _accountStore.ActiveAccountName()),
                cutoff)
            .Select(summary => new DailyPacingTaskRow(
                HumanizeTaskNameForStats(summary.TaskName),
                summary.Runs,
                summary.LastRunUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                $"{summary.PeakLocalHour:00}:00-{(summary.PeakLocalHour + 1) % 24:00}:00"))
            .ToList();

        return rows.Count > 0
            ? rows
            : [new DailyPacingTaskRow("No verified task activity yet", 0, "-", "-")];
    }

    private bool TryRequestSmartSleep(DateTimeOffset? trustedDeadlineUtc)
    {
        if (!_smartSleepSettings.Enabled || IsSessionSleeping || _sessionSleepLifecycle.IsSleepInProgress)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var plan = SmartSleepPlanner.Plan(now, trustedDeadlineUtc, _smartSleepSettings);
        if (!plan.ShouldSleep || plan.WakeAtUtc is not { } wakeAt)
        {
            if (plan.Decision is SmartSleepDecision.DeadlineCoalesced or SmartSleepDecision.OpportunityTooShort)
            {
                var reason = plan.Decision == SmartSleepDecision.DeadlineCoalesced
                    ? "deadline-coalesced"
                    : "opportunity-too-short";
                var candidate = plan.WakeAtUtc is { } candidateWake
                    ? FormatQueueServerTime(candidateWake)
                    : "-";
                var deadline = trustedDeadlineUtc is { } trustedDeadline
                    ? FormatQueueServerTime(trustedDeadline)
                    : "-";
                AppendLoopPickVerbose(
                    $"[smart-sleep] decision=stay-online reason={reason} "
                    + $"trustedDeadline='{deadline}' candidateWake='{candidate}' "
                    + $"minimum={_smartSleepSettings.MinimumOpportunityMinutes}m "
                    + $"coalescing={plan.CoalescingMinutes}m.",
                    $"smart-sleep:stay-online:{reason}:{trustedDeadlineUtc?.UtcTicks}:"
                        + $"{plan.CoalescingMinutes}");
            }
            return false;
        }

        var effectiveWakeAt = _sessionPacer.ResolveEffectiveSmartSleepWakeAt(wakeAt);
        var source = plan.UsesFallback ? "fallback" : "automation-deadline";
        var trusted = trustedDeadlineUtc is { } trustedDeadlineValue
            ? FormatQueueServerTime(trustedDeadlineValue)
            : "-";
        AppendLog(
            $"[smart-sleep] decision=sleep source={source} trustedDeadline='{trusted}' "
            + $"randomizedWake='{FormatQueueServerTime(wakeAt)}' "
            + $"effectiveWake='{FormatQueueServerTime(effectiveWakeAt)}' "
            + $"scheduleAdjusted={effectiveWakeAt > wakeAt} "
            + $"opportunity={FormatPositiveDuration(effectiveWakeAt - now)} "
            + $"minimum={_smartSleepSettings.MinimumOpportunityMinutes}m "
            + $"coalescing={plan.CoalescingMinutes}m.");
        var requested = _sessionPacer.RequestSmartSleep(effectiveWakeAt);
        if (requested)
        {
            _villageStatusRoundRuntime.SetForceOnWakeRequest(plan.UsesFallback);
            _automationPassRuntime.PrioritizeDeadlineWorkOnWake = !plan.UsesFallback;
        }
        return requested;
    }

    private static string FormatPositiveDuration(TimeSpan duration) =>
        duration <= TimeSpan.Zero
            ? "00:00:00"
            : $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private void OnTaskActivityRecorded(BotTaskActivity activity)
    {
        TaskActivityStore.Record(
            _projectRoot,
            activity.AccountName,
            activity.TaskName,
            activity.OccurredAtUtc);
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

        var sleepTime = SessionPacer.FormatDuration(_sessionPacer.ActiveSleepDuration ?? _sessionPacer.TimeUntilWake);
        SessionPacingBorder.ToolTip = new ToolTip
        {
            Content = _sessionPacer.Phase == SessionPacerPhase.Running
                && !_sessionPacer.IsRunTimerEnabled
                    ? "Smart sleep is active. The browser will close when there is a long enough idle window."
                    : $"Run time: {SessionPacer.FormatDuration(_sessionPacer.ActiveRunDuration ?? _sessionPacer.TimeUntilSleep)}\nSleep time: {sleepTime}",
        };
    }

    private bool BlockIfSessionSleeping(string actionName)
    {
        if (IsFreezeActive)
        {
            AppendLog(string.IsNullOrWhiteSpace(actionName)
                ? "Skipped: freeze is active."
                : $"Skipped: freeze is active. {actionName} will not run.");
            return true;
        }

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
        var frozen = IsFreezeActive;
        SetEnabled(LoginButton, !sleeping && !frozen && !_uiBusy);
        SetEnabled(LogoutButton, !sleeping && !frozen && !_uiBusy);
        SetEnabled(StartLoopButton, !frozen && (sleeping || (!sleeping && !_uiBusy && _isLoggedIn)));
        SetEnabled(FreezeButton, !sleeping && !frozen);
        SetEnabled(LoadResourcesButton, !sleeping && !frozen && !_uiBusy);
        SetEnabled(OpenResourceTestFunctionsButton, !sleeping && !frozen && !_uiBusy);
        SetEnabled(StorageRefreshButton, !sleeping && !frozen && !_uiBusy);
        var automationActive = _autoQueueRunning || IsContinuousLoopRunning();
        SetEnabled(
            AccountScanButton,
            !sleeping
            && !frozen
            && _isLoggedIn
            && !_accountScanInProgress
            && (!_uiBusy || automationActive));
        // Selecting a village in the combo is a pure view/queue-context change (cached data only — it never
        // navigates the browser or wakes the bot; see VillageComboBox_SelectionChanged). Keep it usable while
        // sleeping so the user can browse villages and inspect queues. The actual "Switch village" move still
        // blocks during sleep (SwitchToActiveVillageAsync -> BlockIfSessionSleeping), so sleep stays unbroken.
        SetEnabled(VillageComboBox, !frozen && !_uiBusy);
        SetEnabled(AnalyzeFarmListsButton, !sleeping && !frozen && !_farmingOperationBusy);
        SetEnabled(FarmListSendAllNowButton, !sleeping && !frozen && !_farmingOperationBusy && _farmingFeaturesAvailable && HasFarmListWithFarms());
        SetEnabled(StartCatapultWavesButton, !sleeping && !frozen && !_farmingOperationBusy);
        SetEnabled(ResourceTransferScanVillagesButton, !sleeping && !frozen && !_uiBusy && !_resourceTransferScanRunning);

        if (_resourceTestFunctionsWindow is not null)
        {
            _resourceTestFunctionsWindow.IsEnabled = !sleeping && !frozen && !_uiBusy;
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
