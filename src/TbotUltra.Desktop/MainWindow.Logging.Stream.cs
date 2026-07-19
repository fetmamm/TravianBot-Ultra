using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Logging;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void AppendLog(string message)
    {
        try
        {
            lock (_pendingLogSync)
            {
                _pendingLogMessages.AddLast(message ?? string.Empty);
                if (_logFlushQueued)
                {
                    return;
                }

                _logFlushQueued = true;
            }

            _ = Dispatcher.BeginInvoke((Action)FlushPendingLogsToUiMeasured, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppendLog dispatch failed: {ex}");
        }
    }

    private string? _lastSessionLogUiSyncPayload;
    private string? _lastSessionLogHeroHomePayload;

    // File-only dedup for the two load-bearing per-tick echoes. [ui-sync]/[herohome] must keep being
    // emitted (parsed above to drive the dashboard/hero icon, which still runs for every line), but
    // writing an identical consecutive payload to the .txt is pure noise. This skips ONLY the redundant
    // file append — it never affects parsing or any behavior.
    private bool ShouldWriteLineToSessionLog(string part)
    {
        var uiSync = UiSyncRegex.Match(part);
        if (uiSync.Success)
        {
            var payload = uiSync.Groups[1].Value;
            if (payload == _lastSessionLogUiSyncPayload)
            {
                return false;
            }

            _lastSessionLogUiSyncPayload = payload;
            return true;
        }

        var heroHome = HeroHomeRegex.Match(part);
        if (heroHome.Success)
        {
            if (heroHome.Value == _lastSessionLogHeroHomePayload)
            {
                return false;
            }

            _lastSessionLogHeroHomePayload = heroHome.Value;
            return true;
        }

        return true;
    }

    private void FlushPendingLogsToUi()
    {
        try
        {
            var terminalAnchor = CaptureLogListAnchor(TerminalListBox);
            var popupTerminalAnchor = CaptureLogListAnchor(_logsPopupLogList);
            var alarmAnchor = CaptureLogListAnchor(AlarmListBox);
            var popupAlarmAnchor = CaptureLogListAnchor(_logsPopupAlarmList);
            var messages = new List<string>(MaxLogLinesPerFlush);
            var logLinesForSessionLog = new List<string>(MaxLogLinesPerFlush * 2);
            var alarmLinesForSessionLog = new List<string>(MaxLogLinesPerFlush);
            var hasMore = false;
            lock (_pendingLogSync)
            {
                for (var i = 0; i < MaxLogLinesPerFlush && _pendingLogMessages.Count > 0; i++)
                {
                    messages.Add(_pendingLogMessages.First!.Value);
                    _pendingLogMessages.RemoveFirst();
                }

                hasMore = _pendingLogMessages.Count > 0;
                _logFlushQueued = hasMore;
            }

            if (messages.Count <= 0)
            {
                return;
            }

            string? lastRawMessage = null;
            string? lastPrimaryPart = null;
            var browserStatisticsChanged = false;
            var alarmEntriesChanged = false;
            var flushStopwatch = Stopwatch.StartNew();
            for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                if (messageIndex > 0 && flushStopwatch.Elapsed >= LogUiFlushBudget)
                {
                    lock (_pendingLogSync)
                    {
                        for (var index = messages.Count - 1; index >= messageIndex; index--)
                        {
                            _pendingLogMessages.AddFirst(messages[index]);
                        }

                        hasMore = true;
                        _logFlushQueued = true;
                    }

                    break;
                }

                var message = messages[messageIndex];
                lastRawMessage = message;
                var normalized = message.Replace("\r\n", "\n").Replace('\r', '\n');
                var parts = normalized
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (parts.Length == 0)
                {
                    parts = [string.Empty];
                }

                if (parts.Length > 0)
                {
                    lastPrimaryPart = parts[0];
                }

                foreach (var part in parts)
                {
                    browserStatisticsChanged |= TryRecordBrowserActivityStatistics(part);
                    var line = $"[{GetServerNow():yyyy-MM-dd HH:mm:ss}] {part}";
                    var isAlarm = IsAlarmMessage(part);
                    _terminalEntries.Insert(0, new TerminalEntryRow
                    {
                        Text = line,
                        Category = isAlarm ? LogCategory.Errors : LogClassifier.Classify(part),
                        IsVerbose = LogClassifier.IsVerbose(part),
                    });
                    if (!isAlarm && ShouldWriteLineToSessionLog(part))
                    {
                        logLinesForSessionLog.Add(line);
                    }
                    TryApplyInlineResourceLevelUpdateFromLog(part);
                    TryApplyInlineResourceProductionUpdateFromLog(part);
                    TryApplyPlusStatusFromLog(part);
                    // The real Smithy research queue drives the Queue page, dashboard timer and village icons.
                    // The task's defer wait is only its retry time and never becomes Smithy occupancy.
                    TryApplySmithyQueueFromLog(part);
                    if (TryExtractQueueWaitDelay(part, out var queueWaitDelay))
                    {
                        var waitUntilUtc = DateTimeOffset.UtcNow.Add(queueWaitDelay);
                        if (waitUntilUtc > _inlineWaitUntilUtc)
                        {
                            _inlineWaitUntilUtc = waitUntilUtc;
                        }
                    }

                    if (IsManualFarmingExecutionMessage(part))
                    {
                        _manualFarmSessionExecutionCount += 1;
                        UpdateManualFarmingExecutionCounter();
                    }

                    if (IsNpcTradeCompletedMessage(part))
                    {
                        _npcTradeSessionCount += 1;
                        if (IsNpcTradeTroopCompletedMessage(part))
                        {
                            _npcTradeTroopSessionCount += 1;
                        }
                        else
                        {
                            _npcTradeBuildingSessionCount += 1;
                        }

                        UpdateNpcTradeStatsUi();
                    }

                    if (isAlarm)
                    {
                        var isAcknowledgedAlarm = IsAutoAcknowledgedAlarmMessage(part);
                        var nowUtc = DateTimeOffset.UtcNow;
                        var accountKey = _accountStore.ActiveAccountName();
                        var signature = $"{accountKey}|{part}";
                        var existingAlarm = _alarmEntries.FirstOrDefault(entry =>
                            string.Equals(entry.Signature, signature, StringComparison.Ordinal)
                            && nowUtc - entry.LastSeenUtc <= TimeSpan.FromMinutes(30));
                        if (existingAlarm is not null)
                        {
                            alarmEntriesChanged = true;
                            existingAlarm.OccurrenceCount++;
                            existingAlarm.LastSeenUtc = nowUtc;
                            existingAlarm.Text =
                                $"{line} (x{existingAlarm.OccurrenceCount}, first {existingAlarm.FirstSeenUtc.ToLocalTime():HH:mm:ss})";
                            if (existingAlarm.IsAcknowledged && !isAcknowledgedAlarm)
                            {
                                existingAlarm.IsAcknowledged = false;
                                _unacknowledgedAlarmCount += 1;
                            }
                        }
                        else
                        {
                            alarmEntriesChanged = true;
                            _alarmEntries.Insert(0, new AlarmEntryRow
                            {
                                Text = line,
                                Signature = signature,
                                FirstSeenUtc = nowUtc,
                                LastSeenUtc = nowUtc,
                                IsAcknowledged = isAcknowledgedAlarm,
                            });
                            if (!isAcknowledgedAlarm)
                            {
                                _unacknowledgedAlarmCount += 1;
                            }

                            // Alarm rows are also normal terminal rows, but the session file must
                            // contain the event only once and under the alarm section. Repeated
                            // identical alarms inside the coalescing window update the UI count
                            // and are not appended again.
                            alarmLinesForSessionLog.Add(line);
                        }
                    }

                }
            }

            TrimToMaxEntries(_terminalEntries, 1000);
            TrimToMaxEntries(_alarmEntries, 200);
            if (browserStatisticsChanged)
            {
                PersistAndRefreshBrowserActivityStatistics();
            }
            TryAppendSessionLogLines(logLinesForSessionLog, alarmLinesForSessionLog);
            if (alarmEntriesChanged)
            {
                if (IsMainTabSelected(LogsTabItem))
                {
                    AlarmListBox.Items.Refresh();
                }

                _logsPopupAlarmList?.Items.Refresh();
            }

            if (lastRawMessage is not null)
            {
                UpdateStatusFromVisibleLog(lastRawMessage, lastPrimaryPart);
            }

            UpdateTerminalAlarmUi();
            UpdateExecutionStateIndicator();
            RestoreLogListAnchor(TerminalListBox, terminalAnchor);
            RestoreLogListAnchor(_logsPopupLogList, popupTerminalAnchor);
            RestoreLogListAnchor(AlarmListBox, alarmAnchor);
            RestoreLogListAnchor(_logsPopupAlarmList, popupAlarmAnchor);

            if (hasMore)
            {
                _ = Dispatcher.BeginInvoke((Action)FlushPendingLogsToUiMeasured, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppendLog UI update failed: {ex}");
            lock (_pendingLogSync)
            {
                _logFlushQueued = _pendingLogMessages.Count > 0;
            }
        }
    }

    private void FlushPendingLogsToUiMeasured()
        => MeasureUiWork("log UI flush", FlushPendingLogsToUi);

    private void UpdateStatusFromVisibleLog(string? fallbackRawMessage = null, string? fallbackPrimaryPart = null)
    {
        var latestVisible = VisibleTerminalEntries().FirstOrDefault();
        if (latestVisible is not null)
        {
            StatusTextBlock.Text = latestVisible.Text;
            StatusMiniLogTextBlock.Text = ExtractLogMessageBody(latestVisible.Text);
            return;
        }

        if (!string.IsNullOrWhiteSpace(fallbackRawMessage))
        {
            StatusTextBlock.Text = fallbackRawMessage;
            StatusMiniLogTextBlock.Text = string.IsNullOrWhiteSpace(fallbackPrimaryPart)
                ? fallbackRawMessage
                : fallbackPrimaryPart;
        }
    }

    private static string ExtractLogMessageBody(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var separatorIndex = line.IndexOf("] ", StringComparison.Ordinal);
        if (separatorIndex < 0 || separatorIndex + 2 >= line.Length)
        {
            return line;
        }

        return line[(separatorIndex + 2)..];
    }

    private void InitializeSessionLogFile()
    {
        try
        {
            var sessionLogDirectory = Path.GetDirectoryName(_sessionLogPath);
            if (!string.IsNullOrWhiteSpace(sessionLogDirectory))
            {
                Directory.CreateDirectory(sessionLogDirectory);
                TrimOldSessionLogFiles(sessionLogDirectory);
            }

            var header = new List<string>
            {
                "=== Tbot Ultra Session Log ===",
            };
            header.AddRange(SystemDiagnosticsInfo.BuildLines(ReadAppVersionForLog(), _projectRoot, DateTimeOffset.UtcNow));
            header.AddRange([string.Empty, "=== ALARMS ===", string.Empty, "=== LOGS ===", string.Empty]);
            lock (_sessionLogWriteSync)
            {
                File.WriteAllLines(_sessionLogPath, header);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not initialize session log file: {ex}");
        }
    }

    private void TrimOldSessionLogFiles(string sessionLogDirectory)
    {
        try
        {
            var oldFiles = new DirectoryInfo(sessionLogDirectory)
                .GetFiles("TbotUltra_Log_*.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.CreationTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Skip(MaxSessionLogFiles - 1)
                .ToList();

            foreach (var file in oldFiles)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Could not delete old session log '{file.FullName}': {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not trim old session logs: {ex}");
        }
    }

    private string ReadAppVersionForLog()
    {
        try
        {
            var version = File.Exists(_versionPath)
                ? File.ReadAllText(_versionPath).Trim()
                : "dev";
            return string.IsNullOrWhiteSpace(version) ? "dev" : version;
        }
        catch
        {
            return "unknown";
        }
    }

    private void TryAppendSessionLogLines(IReadOnlyList<string> logLines, IReadOnlyList<string> alarmLines)
    {
        if (logLines.Count <= 0 && alarmLines.Count <= 0)
        {
            return;
        }

        try
        {
            _sessionLogWriter.AppendSessionLines(logLines, alarmLines);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not append session logs: {ex}");
        }
    }

    private void TryApplyInlineResourceLevelUpdateFromLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var levelUp = Regex.Match(
            message,
            @"Resource slot\s+(?<slot>\d+)\s+level increased from\s+\d+\s+to\s+(?<to>\d+)",
            RegexOptions.IgnoreCase);
        if (!levelUp.Success)
        {
            return;
        }

        var slotId = int.Parse(levelUp.Groups["slot"].Value);
        var nextLevel = int.Parse(levelUp.Groups["to"].Value);
        var sourceRows = _resourcesViewModel.AllFields;
        if (sourceRows.Count == 0)
        {
            return;
        }

        var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
        var rows = sourceRows.ToList();
        var changed = false;
        var updatedRows = rows.Select(row =>
        {
            if (row.SlotId != slotId)
            {
                return row;
            }

            var existingLevel = row.Level ?? 0;
            if (existingLevel >= nextLevel)
            {
                return row;
            }

            changed = true;
            return new ResourceFieldRow
            {
                SlotId = row.SlotId,
                FieldType = row.FieldType,
                Name = row.Name,
                Level = nextLevel,
                Url = row.Url,
                PendingTargetLevel = ResolveQueuedResourceTarget(row.SlotId, nextLevel, queuedTargetsBySlot),
                IsMaxLevel = nextLevel >= _activeVillageResourceMaxLevel || row.IsMaxLevel,
            };
        }).ToList();

        if (!changed)
        {
            return;
        }

        SetResourceRows(updatedRows);
        _resourcesViewModel.InfoText = $"Resource slot {slotId} updated to level {nextLevel}.";
    }

    private void TryApplyInlineResourceProductionUpdateFromLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var match = Regex.Match(
            message,
            @"Resource production update:\s+wood=(?<wood>[-\d.]+)\s+clay=(?<clay>[-\d.]+)\s+iron=(?<iron>[-\d.]+)\s+crop=(?<crop>[-\d.]+)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return;
        }

        static double? ParseValue(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "-")
            {
                return null;
            }

            return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        var productionByHour = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = ParseValue(match.Groups["wood"].Value),
            ["clay"] = ParseValue(match.Groups["clay"].Value),
            ["iron"] = ParseValue(match.Groups["iron"].Value),
            ["crop"] = ParseValue(match.Groups["crop"].Value),
        };

        if (productionByHour.Values.All(value => value is null))
        {
            return;
        }

        ApplyResourceProductionOnlyToUi(productionByHour);
    }

    private static void TrimToMaxEntries<T>(ObservableCollection<T> entries, int max)
    {
        while (entries.Count > max)
        {
            entries.RemoveAt(entries.Count - 1);
        }
    }

    internal static bool IsAlarmMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        if (IsAutoAcknowledgedAlarmMessage(message))
        {
            return true;
        }

        if (value.Contains("alarm:"))
        {
            return true;
        }

        // Safe navigation failures are deferred without consuming task retries. The Worker emits its
        // FAILED/FAIL diagnostics before Desktop applies that defer, so those lines must not turn the
        // recoverable connection outage into a red alarm.
        if (LogClassifier.IsSafeTransientRetry(message))
        {
            return false;
        }

        // Verbose diagnostic lines (":verbose]" tag) are never alarms by definition. Their free-text
        // payload often contains words like "timeout", "failed" or "error" (e.g.
        // "[login:verbose] waiting for login confirmation (timeout=180s ...)") that would otherwise
        // trip the keyword check below.
        if (value.Contains(":verbose]"))
        {
            return false;
        }

        if ((value.Contains("[construct-faster]") && value.Contains("video unavailable"))
            || (value.Contains("[production-bonus]") && value.Contains("inspection unavailable")))
        {
            return false;
        }

        // Bonus-video ad-network diagnostics ([browser-video:network]) are sanitized telemetry: per-host
        // block reasons (e.g. ERR_BLOCKED_BY_ORB from our own ad-domain route block) and aggregate
        // ok/no-content/http-errors/request-failures counters. They are expected noise, never an actionable
        // failure — the real bonus-video failure classification and cooldowns are handled separately. Without
        // this the "failed"/"error" keywords below would flag every summary line as a red alarm.
        if (value.Contains("[browser-video:network]"))
        {
            return false;
        }

        if (LogClassifier.IsExpectedFarmListResult(message))
        {
            return false;
        }

        // Normal "defer and retry later" signals are NOT errors. The worker raises a
        // TaskWaitException (and embeds queue_wait_seconds=) when a task simply needs to wait —
        // e.g. build queue full, hero already dispatched, resources still accumulating. These would
        // otherwise match the "failed"/"] fail" keywords below and flood the alarm panel even though
        // nothing is wrong, so they are explicitly classified as non-alarms.
        if (value.Contains("taskwaitexception") || value.Contains("queue_wait_seconds="))
        {
            return false;
        }

        // Transient Playwright "Execution context was destroyed, most likely because of a navigation"
        // is a harmless navigation race (the page reloaded while a read was in flight). The worker
        // retries and continues, so don't raise it as a red alarm.
        if (value.Contains("execution context was destroyed"))
        {
            return false;
        }

        if (value.Contains("captcha warmup"))
        {
            return false;
        }

        if (value.Contains(" started]"))
        {
            return false;
        }

        if (value.Contains("[completed]"))
        {
            return false;
        }

        if (value.Contains("manual farming loop") && value.Contains(" restarting"))
        {
            return false;
        }

        if (value.Contains(" paused"))
        {
            return false;
        }

        if (value.Contains(" stopped"))
        {
            return false;
        }

        if (value.Contains(" canceled"))
        {
            return false;
        }

        if (value.Contains("] fail"))
        {
            return true;
        }

        return value.Contains("failed")
            || value.Contains("error")
            || value.Contains("exception")
            || value.Contains("timeout")
            || value.Contains("captcha")
            || value.Contains("verification")
            || value.Contains("invalid")
            || value.Contains("not logged in")
            || value.Contains("could not");
    }

    private static bool IsAutoAcknowledgedAlarmMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        return value.Contains("chromium warmup")
            || value.Contains("captcha warmup")
            || (value.Contains("not logged in")
                && value.Contains("current page state is 'unknown'"))
            || (value.Contains("[resource-refresh] fail")
                && (value.Contains("execution context was destroyed")
                    || value.Contains("timeout")))
            || (value.Contains("background resource refresh skipped:")
                && value.Contains("timeout"))
            || (value.Contains("hero_adventure.php")
                && value.Contains("transient navigation context error")
                && value.Contains("retrying"))
            || value.Contains("the calling thread cannot access this object because a different thread owns it")
            // A single browser-click candidate that times out is non-fatal: the helper falls back to a JS
            // click and the higher-level flow logs its own result. The "timeout" keyword makes IsAlarmMessage
            // flag it, so keep it in the alarm list but auto-acknowledged (not a red, unacknowledged alarm).
            || (value.Contains("[browser-click]") && value.Contains("skipped candidate"))
            || (value.Contains("ui sync snapshot failed")
                && value.Contains("execution context was destroyed"))
            // Best-effort logout before session sleep; a timeout here is harmless (the next login still
            // works) so it stays in the alarm list but is auto-acknowledged.
            || (value.Contains("session logout failed")
                && value.Contains("timeout"));
    }

    private static bool IsCleanModeHiddenAlarmMessage(string message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && message.Contains("UI sync snapshot failed:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManualFarmingExecutionMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        return value.Contains(" sent raid to (")
            || value.Contains(" sent normal attack to (");
    }

    private static bool IsNpcTradeCompletedMessage(string message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && message.Contains("NPC trade: completed at", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNpcTradeTroopCompletedMessage(string message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && message.Contains(" for unit t", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateNpcTradeStatsUi()
    {
        var goldSpent = _npcTradeSessionCount * NpcTradeGoldCost;
        // The detail-panel TextBlock has its own dark base color (#111827) — preserve that for the
        // label and only swap the value color when it's worth highlighting.
        SetGoldHighlightedValueText(NpcTradeGoldSpentTextBlock, "Gold spent: ", goldSpent, neutralLabelBrush: null);
        NpcTradeTroopsTextBlock.Text = $"NPC Troops: {_npcTradeTroopSessionCount}";
        NpcTradeBuildingsTextBlock.Text = $"NPC Buildings: {_npcTradeBuildingSessionCount}";
    }

    // Travian-ish metallic gold. Reused for gold counts and non-zero gold-spent values.
    private static readonly SolidColorBrush GoldHighlightBrush = MakeFrozen(ThemeColors.Get("GoldHighlightBrush"));
    private static readonly SolidColorBrush GreenHighlightBrush = MakeFrozen(ThemeColors.Get("SuccessBrush"));
    private static readonly SolidColorBrush YellowHighlightBrush = MakeFrozen(ThemeColors.Get("YellowHighlightBrush"));
    private static readonly SolidColorBrush NeutralStatsBrush = MakeFrozen(ThemeColors.Get("TextMutedBrush"));
    private static readonly SolidColorBrush PrimaryStatsBrush = MakeFrozen(ThemeColors.Get("TextPrimaryBrush"));

    private static SolidColorBrush MakeFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Renders a "label + value" TextBlock where the value turns gold when greater than zero.
    /// Pass <paramref name="neutralLabelBrush"/>=null to keep the TextBlock's own Foreground for
    /// the label (used by panel TextBlocks that already have a non-default base color).
    /// </summary>
    private static void SetGoldHighlightedValueText(TextBlock target, string label, int value, SolidColorBrush? neutralLabelBrush)
    {
        target.Inlines.Clear();
        var labelRun = new Run(label);
        if (neutralLabelBrush is not null)
        {
            labelRun.Foreground = neutralLabelBrush;
        }
        target.Inlines.Add(labelRun);

        var valueRun = new Run(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (value > 0)
        {
            valueRun.Foreground = GoldHighlightBrush;
        }
        else if (neutralLabelBrush is not null)
        {
            valueRun.Foreground = neutralLabelBrush;
        }
        target.Inlines.Add(valueRun);
    }

    /// <summary>
    /// Renders "Gold: {goldText} | Silver: {silverText}" with the gold number in gold color when
    /// it's a meaningful positive value.
    /// </summary>
    // Topbar Gold and Silver are now separate metric cards, so each value gets its own TextBlock.
    // Gold turns metallic gold when meaningful; silver uses the primary text color when meaningful.
    internal static void SetGoldSilverStatusText(TextBlock goldTarget, TextBlock silverTarget, string goldText, string silverText)
    {
        SetCurrencyValueText(goldTarget, goldText, GoldHighlightBrush);
        SetCurrencyValueText(silverTarget, silverText, PrimaryStatsBrush);
    }

    private static void SetCurrencyValueText(TextBlock target, string text, SolidColorBrush meaningfulBrush)
    {
        var isMeaningful = text is not ("-" or "0") && !string.IsNullOrWhiteSpace(text);

        // Don't clobber a previously shown real value with "-"/empty: partial status reads carry no gold/
        // silver and would otherwise blip the value to "-" between full reads.
        if (!isMeaningful)
        {
            var existing = (target.Inlines.FirstInline as Run)?.Text;
            var existingMeaningful = !string.IsNullOrWhiteSpace(existing) && existing is not ("-" or "0");
            if (existingMeaningful)
            {
                return;
            }
        }

        target.Inlines.Clear();
        var valueRun = new Run(string.IsNullOrWhiteSpace(text) ? "-" : text)
        {
            Foreground = isMeaningful ? meaningfulBrush : NeutralStatsBrush,
        };
        if (isMeaningful)
        {
            valueRun.FontWeight = FontWeights.SemiBold;
        }
        target.Inlines.Add(valueRun);
    }
}
