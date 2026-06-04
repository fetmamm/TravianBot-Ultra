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

        StartLoopButton.Background = new SolidColorBrush(ThemeColors.Get("AccentBrush"));
        StartLoopButton.BorderBrush = new SolidColorBrush(ThemeColors.Get("AccentBrush"));
        StartLoopButton.Foreground = Brushes.White;
    }

    private void SetLoopStateBadge(string stateText, Color color, string startButtonText)
    {
        LoopStateTextBlock.Text = $"State: {stateText}";
        LoopStateBadge.Background = new SolidColorBrush(color);
        ApplyStartLoopButtonVisual(startButtonText);
    }

    private void UpdateExecutionStateIndicator()
    {
        UpdateAutomationLoopRunningIndicators();

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
            var queueItems = _botService.GetQueueItemsForDisplay();
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
        var continuousModeEnabled = ContinuousRunToggleButton?.IsChecked == true;
        var continuousModeActive = continuousModeEnabled && (loopRunning || _autoQueueRunning || hasDeferredQueueItems || hasInlineWait || functionExecutionRunning);
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

        if (!continuousModeEnabled)
        {
            if ((hasDeferredQueueItems && !hasRunningQueueItems && !_uiBusy && !functionExecutionRunning) || hasInlineWait)
            {
                var remainingSeconds = hasInlineWait
                    ? (int)Math.Ceiling((_inlineWaitUntilUtc - nowForWait).TotalSeconds)
                    : earliestNextAttemptUtc.HasValue
                        ? (int)Math.Ceiling((earliestNextAttemptUtc.Value - nowForWait).TotalSeconds)
                        : 0;
                remainingSeconds = Math.Max(0, remainingSeconds);
                LoopStateTextBlock.Text = remainingSeconds > 0
                    ? $"State: waiting ({FormatCountdown(remainingSeconds)})"
                    : "State: waiting";
                LoopStateBadge.Background = new SolidColorBrush(ThemeColors.Get("WaitingBrush"));
                ApplyStartLoopButtonVisual((loopRunning || _autoQueueRunning) ? "Pause bot" : "Start bot");
                return;
            }

            if (functionExecutionRunning)
            {
                SetLoopStateBadge("function running", ThemeColors.Get("InfoBrush"), "Pause bot");
                return;
            }

            if (loopRunning)
            {
                SetLoopStateBadge("loop running", ThemeColors.Get("SuccessBrush"), "Pause bot");
                return;
            }

            if (hasPausedQueueItems)
            {
                SetLoopStateBadge("paused", ThemeColors.Get("AmberBrush"), "Start bot");
                return;
            }

            SetLoopStateBadge("idle", ThemeColors.Get("TextSubtleBrush"), "Start bot");
            return;
        }

        if ((continuousModeActive || hasInlineWait || hasDeferredQueueItems)
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

            if (continuousModeActive)
            {
                SetLoopStateBadge("idle", ThemeColors.Get("TextSubtleBrush"), "Pause bot");
                return;
            }
        }

        if (continuousModeActive)
        {
            SetLoopStateBadge("running", ThemeColors.Get("SuccessBrush"), "Pause bot");
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
        if (_buildQueueActiveCount <= 0)
        {
            BuildQueueStatusTextBlock.Text = "Build queue: idle";
            return;
        }

        if (_buildQueueRemainingSeconds >= 0)
        {
            BuildQueueStatusTextBlock.Text = $"Build queue: active={_buildQueueActiveCount}, remaining={FormatCountdown(_buildQueueRemainingSeconds)}";
            return;
        }

        BuildQueueStatusTextBlock.Text = $"Build queue: active={_buildQueueActiveCount}, remaining=-";
    }

    private void TickBuildQueueCountdown()
    {
        if (_buildQueueRemainingSeconds > 0)
        {
            _buildQueueRemainingSeconds -= 1;
            if (_buildQueueRemainingSeconds == 0)
            {
                _buildQueueReachedZeroPendingCompletion = true;
            }
        }

        if (_buildQueueRemainingSeconds == 0 && _buildQueueActiveCount > 0)
        {
            if (_buildQueueReachedZeroPendingCompletion)
            {
                _buildQueueReachedZeroPendingCompletion = false;
            }
            else
            {
                _buildQueueActiveCount = Math.Max(0, _buildQueueActiveCount - 1);
                if (_buildQueueActiveCount > 0)
                {
                    _buildQueueRemainingSeconds = -1;
                }
            }
        }

        UpdateBuildQueueStatusText();
        UpdateAutomationLoopRunningIndicators();
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
