using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

// Visual state for the automation loop / queue execution: the loop state badge,
// the Start/Pause button appearance, and the build-queue countdown text. Extracted
// verbatim from MainWindow.xaml.cs to keep that file focused; same class, so this
// is a pure relocation with no behavior change.
public partial class MainWindow
{
    private void SetLoopIndicator(bool running)
    {
        _ = running;
        UpdateExecutionStateIndicator();
    }

    private void ApplyStartLoopButtonVisual(string startButtonText)
    {
        StartLoopButton.Content = startButtonText;
        StartLoopButton.IsEnabled = !string.Equals(startButtonText, "Pausing...", StringComparison.Ordinal);

        var highlightPauseState = string.Equals(startButtonText, "Pause bot", StringComparison.Ordinal);
        if (highlightPauseState)
        {
            StartLoopButton.Background = new SolidColorBrush(ThemeColors.Get("AmberBg200Brush"));
            StartLoopButton.BorderBrush = new SolidColorBrush(ThemeColors.Get("WarningBorderBrush"));
            StartLoopButton.Foreground = new SolidColorBrush(ThemeColors.Get("WarningText2Brush"));
            return;
        }

        if (string.Equals(startButtonText, "Pausing...", StringComparison.Ordinal))
        {
            StartLoopButton.Background = new SolidColorBrush(ThemeColors.Get("WarningBgBrush"));
            StartLoopButton.BorderBrush = new SolidColorBrush(ThemeColors.Get("WarningBorderBrush"));
            StartLoopButton.Foreground = new SolidColorBrush(ThemeColors.Get("WarningText2Brush"));
            return;
        }

        // Soft/tinted green (light bg + green border + green text), matching the tinted "Pause bot"
        // look rather than a solid green fill.
        StartLoopButton.Background = new SolidColorBrush(ThemeColors.Get("SuccessBgBrush"));
        StartLoopButton.BorderBrush = new SolidColorBrush(ThemeColors.Get("SuccessBorderBrush"));
        StartLoopButton.Foreground = new SolidColorBrush(ThemeColors.Get("SuccessTextBrush"));
    }

    private void SetLoopStateBadge(string stateText, Color color, string startButtonText)
    {
        LoopStateTextBlock.Text = $"State: {stateText}";
        LoopStateBadge.Background = new SolidColorBrush(color);
        ApplyStartLoopButtonVisual(startButtonText);
    }

    private void UpdateExecutionStateIndicator(bool updateAutomationLoopCards = true)
    {
        if (updateAutomationLoopCards)
        {
            UpdateAutomationLoopRunningIndicators();
        }

        var loopRunning = _loopTask is not null && !_loopTask.IsCompleted;
        var hasPausedQueueItems = false;
        var hasRunningQueueItems = false;
        var hasFailedQueueItems = false;
        var hasDeferredQueueItems = false;
        var hasReadyQueueItems = false;
        DateTimeOffset? earliestNextAttemptUtc = null;
        var enabledGroups = GetContinuousLoopEnabledGroupsInOrder().ToHashSet();
        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var queueItems = GetQueueSnapshotForUi();
            var relevantQueueItems = enabledGroups.Count > 0
                ? queueItems.Where(item => enabledGroups.Contains(item.Group)).ToList()
                : [];
            hasPausedQueueItems = relevantQueueItems.Any(item => item.Status == QueueStatus.Paused);
            hasRunningQueueItems = relevantQueueItems.Any(item => item.Status == QueueStatus.Running);
            hasFailedQueueItems = relevantQueueItems.Any(item => item.Status == QueueStatus.Failed);
            hasReadyQueueItems = relevantQueueItems.Any(item =>
                item.Status == QueueStatus.Pending &&
                item.NextAttemptAt <= nowUtc);
            var deferredItems = relevantQueueItems
                .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > nowUtc)
                .ToList();
            hasDeferredQueueItems = deferredItems.Count > 0;
            if (hasDeferredQueueItems)
            {
                earliestNextAttemptUtc = deferredItems.Min(item => item.NextAttemptAt);
            }

            if (_inlineWaitUntilUtc <= nowUtc)
            {
                _inlineWaitUntilUtc = DateTimeOffset.MinValue;
            }
        }
        catch
        {
            // Ignore indicator read errors.
        }

        var nowForWait = DateTimeOffset.UtcNow;
        var hasInlineWait = _inlineWaitUntilUtc > nowForWait;
        var functionExecutionRunning = IsFunctionExecutionRunning(hasRunningQueueItems);
        var automationActiveOrWaiting = loopRunning || _autoQueueRunning || hasDeferredQueueItems || hasInlineWait || functionExecutionRunning;
        var pauseRequested = _loopController.LoopStopRequested || _loopController.QueueStopRequested;
        var activeWorkStopping = pauseRequested && (loopRunning || _autoQueueRunning || _uiBusy || functionExecutionRunning || hasRunningQueueItems);

        if (activeWorkStopping)
        {
            SetLoopStateBadge("pausing", ThemeColors.Get("AmberBrush"), "Pausing...");
            return;
        }

        if (pauseRequested)
        {
            SetLoopStateBadge("paused", ThemeColors.Get("AmberBrush"), "Start bot");
            return;
        }

        if (automationActiveOrWaiting
            && !hasReadyQueueItems
            && !functionExecutionRunning
            && !hasRunningQueueItems
            && !_uiBusy)
        {
            if (hasInlineWait || hasDeferredQueueItems)
            {
                int remainingSeconds;
                if (hasInlineWait)
                {
                    remainingSeconds = (int)Math.Ceiling((_inlineWaitUntilUtc - nowForWait).TotalSeconds);
                }
                else
                {
                    remainingSeconds = earliestNextAttemptUtc.HasValue
                        ? (int)Math.Ceiling((earliestNextAttemptUtc.Value - nowForWait).TotalSeconds)
                        : 0;
                }

                _ = Math.Max(0, remainingSeconds);
                SetLoopStateBadge("waiting", ThemeColors.Get("WaitingBrush"), (loopRunning || _autoQueueRunning) ? "Pause bot" : "Start bot");
                return;
            }

            if (automationActiveOrWaiting)
            {
                SetLoopStateBadge("idle", ThemeColors.Get("TextSubtleBrush"), "Pause bot");
                return;
            }
        }

        if (automationActiveOrWaiting)
        {
            // "Pause bot" must reflect actual execution. After a restart the persisted queue can have
            // ready/deferred items while the loop is not running; that is idle, not paused/running.
            var isExecuting = loopRunning || _autoQueueRunning || functionExecutionRunning || hasRunningQueueItems;
            SetLoopStateBadge(
                isExecuting ? "running" : "idle",
                ThemeColors.Get(isExecuting ? "SuccessBrush" : "TextSubtleBrush"),
                isExecuting ? "Pause bot" : "Start bot");
            return;
        }

        if (functionExecutionRunning)
        {
            SetLoopStateBadge("running", ThemeColors.Get("SuccessBrush"), "Pause bot");
            return;
        }

        if (loopRunning)
        {
            SetLoopStateBadge("running", ThemeColors.Get("SuccessBrush"), "Pause bot");
            return;
        }

        if (hasPausedQueueItems)
        {
            SetLoopStateBadge("paused", ThemeColors.Get("AmberBrush"), "Start bot");
            return;
        }

        SetLoopStateBadge("idle", ThemeColors.Get("TextSubtleBrush"), "Start bot");
    }

    private void UpdateExecutionStateIndicatorOnUiThread()
    {
        if (Dispatcher.CheckAccess())
        {
            UpdateExecutionStateIndicator();
            return;
        }

        _ = Dispatcher.BeginInvoke(() => UpdateExecutionStateIndicator());
    }

    private void UpdateBuildQueueStatusText()
    {
        // A pending humanized start delay for the selected village takes precedence: show when the next
        // construction attempt is due. Kept as a display override so it never overwrites the real
        // status-derived build-queue remaining (which stays correct for when the current build finishes).
        if (TryGetSelectedVillageConstructionHumanizeWaitSeconds(out var humanizeWaitSeconds))
        {
            BuildQueueStatusTextBlock.Text = $"Build queue: next attempt in {FormatCountdown(humanizeWaitSeconds)}";
            return;
        }

        if (_buildQueueActiveCount <= 0)
        {
            BuildQueueStatusTextBlock.Text = "Build queue: idle";
            return;
        }

        if (_buildQueueReachedZeroPendingCompletion)
        {
            BuildQueueStatusTextBlock.Text = $"Build queue: active={_buildQueueActiveCount}, checking Travian...";
            return;
        }

        if (_buildQueueRemainingSeconds >= 0)
        {
            BuildQueueStatusTextBlock.Text = $"Build queue: active={_buildQueueActiveCount}, remaining={FormatCountdown(_buildQueueRemainingSeconds)}";
            return;
        }

        BuildQueueStatusTextBlock.Text = $"Build queue: active={_buildQueueActiveCount}, remaining=-";
    }

    // True when the humanized construction start delay is still pending for the currently-selected
    // village. Clears itself once the wait elapses so the timer reverts to the real build-queue status.
    private bool TryGetSelectedVillageConstructionHumanizeWaitSeconds(out int seconds)
    {
        seconds = 0;
        if (_constructionHumanizeWaitUntilUtc <= DateTimeOffset.UtcNow)
        {
            _constructionHumanizeWaitUntilUtc = DateTimeOffset.MinValue;
            _constructionHumanizeWaitVillage = null;
            return false;
        }

        if (!string.Equals(
                _constructionHumanizeWaitVillage,
                GetSelectedVillageKey(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        seconds = (int)Math.Ceiling((_constructionHumanizeWaitUntilUtc - DateTimeOffset.UtcNow).TotalSeconds);
        return seconds > 0;
    }

    private void TickBuildQueueCountdown()
    {
        if (_buildQueueRemainingSeconds > 0)
        {
            _buildQueueRemainingSeconds -= 1;
        }

        if (_buildQueueRemainingSeconds == 0
            && _buildQueueActiveCount > 0
            && !_buildQueueReachedZeroPendingCompletion)
        {
            _buildQueueReachedZeroPendingCompletion = true;
            _continuousLoopConstructionStatusNeedsSync = true;
            Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
            AppendLoopPickVerbose(
                "[construction-queue:verbose] local construction timer reached zero; requesting confirmed Travian status.",
                "construction-queue:timer-zero");
        }

    }

    private static string FormatCountdown(int seconds)
    {
        var value = Math.Max(0, seconds);
        var time = TimeSpan.FromSeconds(value);
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
        }

        return $"{time.Minutes:00}:{time.Seconds:00}";
    }
}
