using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TbotUltra.Desktop.Models;
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
                _pendingLogMessages.Enqueue(message ?? string.Empty);
                if (_logFlushQueued)
                {
                    return;
                }

                _logFlushQueued = true;
            }

            _ = Dispatcher.BeginInvoke((Action)FlushPendingLogsToUi, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppendLog dispatch failed: {ex}");
        }
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
                    messages.Add(_pendingLogMessages.Dequeue());
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
            foreach (var message in messages)
            {
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
                    var line = $"[{GetServerNow():yyyy-MM-dd HH:mm:ss}] {part}";
                    var isAlarm = IsAlarmMessage(part);
                    _terminalEntries.Insert(0, new TerminalEntryRow
                    {
                        Text = line,
                        Category = isAlarm ? LogCategory.Errors : LogClassifier.Classify(part),
                        IsVerbose = LogClassifier.IsVerbose(part),
                    });
                    logLinesForSessionLog.Add(line);
                    TryApplyInlineResourceLevelUpdateFromLog(part);
                    TryApplyInlineResourceProductionUpdateFromLog(part);
                    TryApplyPlusStatusFromLog(part);
                    if (TryExtractQueueWaitDelay(part, out var queueWaitDelay))
                    {
                        var waitUntilUtc = DateTimeOffset.UtcNow.Add(queueWaitDelay);
                        if (waitUntilUtc > _inlineWaitUntilUtc)
                        {
                            _inlineWaitUntilUtc = waitUntilUtc;
                        }

                        // Mirror smithy-side waits into the dashboard timer collection so the
                        // group card shows a countdown even when the wait is below the queue
                        // threshold and the task does not formally defer.
                        if (IsSmithyUpgradeWaitMessage(part))
                        {
                            PushSmithyUpgradeRemainingSeconds((int)Math.Ceiling(queueWaitDelay.TotalSeconds));
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
                        _alarmEntries.Insert(0, new AlarmEntryRow
                        {
                            Text = line,
                            IsAcknowledged = isAcknowledgedAlarm,
                        });
                        if (!isAcknowledgedAlarm)
                        {
                            _unacknowledgedAlarmCount += 1;
                        }

                        alarmLinesForSessionLog.Add(line);
                    }

                    if (IsCaptchaSessionStartMessage(part) && !_captchaSessionActive)
                    {
                        _captchaSessionSeenCount += 1;
                        _captchaSessionActive = true;
                    }

                    if (IsCaptchaAutoSolveAttemptMessage(part))
                    {
                        ShowCaptchaAutoSolvePopup(part);
                    }

                    if (IsManualVerificationAlarmMessage(part))
                    {
                        _manualVerificationAlarmActive = true;
                    }

                    if (_manualVerificationAlarmActive && IsManualVerificationResolvedMessage(part))
                    {
                        AcknowledgeAllAlarmEntries();
                        _manualVerificationAlarmActive = false;
                    }

                    if (_captchaSessionActive && IsCaptchaSolvedAutomaticallyMessage(part))
                    {
                        _captchaSessionSolvedCount += 1;
                        _captchaSessionActive = false;
                        AcknowledgeAllAlarmEntries();
                        CloseCaptchaAutoSolvePopup();
                    }
                    else if (_captchaSessionActive && IsManualVerificationResolvedMessage(part))
                    {
                        _captchaSessionActive = false;
                        CloseCaptchaAutoSolvePopup();
                    }

                    if (part.Contains("manual verification appeared", StringComparison.OrdinalIgnoreCase)
                        || part.Contains("captcha/manual", StringComparison.OrdinalIgnoreCase)
                        || IsCaptchaAutoSolveFailedMessage(part))
                    {
                        CloseCaptchaAutoSolvePopup();
                        // During a visible login the browser window is open even though
                        // _browserSessionLikelyOpen is still false (it flips only after post-login finishes).
                        ShowManualVerificationPopup(_browserSessionLikelyOpen || _visibleBrowserLoginInProgress);
                    }
                }
            }

            TrimToMaxEntries(_terminalEntries, 1000);
            TrimToMaxEntries(_alarmEntries, 200);
            UpdateCaptchaStatsUi();
            TryAppendSessionLogLines(logLinesForSessionLog, alarmLinesForSessionLog);

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
                _ = Dispatcher.BeginInvoke((Action)FlushPendingLogsToUi, DispatcherPriority.Background);
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

            var header = new[]
            {
                "=== Tbot Ultra Session Log ===",
                $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"ProjectRoot: {_projectRoot}",
                $"AppVersion: {ReadAppVersionForLog()}",
                $"MachineName: {Environment.MachineName}",
                $"UserName: {Environment.UserName}",
                $"OS: {RuntimeInformation.OSDescription}",
                $"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}",
                $"DotNet: {RuntimeInformation.FrameworkDescription}",
                $"CPU: {ReadCpuDescriptionForLog()}",
                $"LogicalProcessors: {Environment.ProcessorCount}",
                $"RAM: {ReadRamDescriptionForLog()}",
                $"Screen: {(int)SystemParameters.PrimaryScreenWidth}x{(int)SystemParameters.PrimaryScreenHeight}",
                string.Empty,
                "=== ALARMS ===",
                string.Empty,
                "=== LOGS ===",
                string.Empty,
            };
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

    private static string ReadCpuDescriptionForLog()
    {
        try
        {
            var identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                return identifier.Trim();
            }
        }
        catch
        {
        }

        return "unknown";
    }

    private static string ReadRamDescriptionForLog()
    {
        try
        {
            var memoryStatus = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };

            if (!GlobalMemoryStatusEx(ref memoryStatus) || memoryStatus.TotalPhys == 0)
            {
                return "unknown";
            }

            var totalBytes = memoryStatus.TotalPhys;
            if (totalBytes > 0)
            {
                var totalGb = totalBytes / (1024d * 1024d * 1024d);
                return $"{totalGb:F1} GB";
            }
        }
        catch
        {
        }

        return "unknown";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx memoryStatus);

    private void TryAppendSessionLogLines(IReadOnlyList<string> logLines, IReadOnlyList<string> alarmLines)
    {
        if (logLines.Count <= 0 && alarmLines.Count <= 0)
        {
            return;
        }

        try
        {
            lock (_sessionLogWriteSync)
            {
                if (alarmLines.Count > 0)
                {
                    _sessionAlarmLines.AddRange(alarmLines);
                }

                if (logLines.Count > 0)
                {
                    _sessionLogLines.AddRange(logLines);
                }

                var content = new List<string>(_sessionAlarmLines.Count + _sessionLogLines.Count + 8)
                {
                    "=== Tbot Ultra Session Log ===",
                    $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"ProjectRoot: {_projectRoot}",
                    string.Empty,
                    "=== ALARMS ===",
                };

                content.AddRange(_sessionAlarmLines);
                content.Add(string.Empty);
                content.Add("=== LOGS ===");
                content.AddRange(_sessionLogLines);
                content.Add(string.Empty);

                File.WriteAllLines(_sessionLogPath, content);
            }
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

    private void ShowManualVerificationPopup(bool browserAlreadyOpen)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastVerificationPopupAt).TotalSeconds < 10)
        {
            return;
        }

        _lastVerificationPopupAt = now;
        if (browserAlreadyOpen)
        {
            var solved = AppDialog.Show(
                this,
                "Manual verification detected. Solve it in the open browser window, then click Yes (Solved).",
                "Manual verification",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (solved == MessageBoxResult.Yes)
            {
                _manualVerificationAlarmActive = false;
                AcknowledgeAllAlarmEntries();
                AppendLog("Manual verification marked as solved by user.");
            }
            return;
        }

        var openBrowser = AppDialog.Show(
            this,
            "Manual verification is required. Browser is not open. Open/restart verification browser now?",
            "Manual verification",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (openBrowser == MessageBoxResult.Yes)
        {
            OpenVerificationBrowser();
        }
    }

    private void ShowCaptchaAutoSolvePopup(string attemptMessage)
    {
        var (attempt, attempts) = ParseCaptchaAutoSolveAttempt(attemptMessage);
        var timeoutSeconds = Math.Max(60, LoadBotOptions().CaptchaSolverTimeoutSeconds);
        var maxSeconds = timeoutSeconds * Math.Max(1, attempts);

        if (_captchaAutoSolvePopup is { IsVisible: true })
        {
            _captchaAutoSolveMaxSeconds = Math.Max(_captchaAutoSolveMaxSeconds, maxSeconds);
            UpdateCaptchaAutoSolveAttemptText(attempt, attempts);
            UpdateCaptchaAutoSolveElapsedText();
            return;
        }

        _captchaAutoSolveStartedAt = DateTimeOffset.UtcNow;
        _captchaAutoSolveMaxSeconds = maxSeconds;
        _captchaAutoSolveAttemptTextBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = System.Windows.Media.Brushes.Gray,
        };
        _captchaAutoSolveElapsedTextBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = System.Windows.Media.Brushes.Gray,
        };

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "Captcha detected. Tbot Ultra is trying to solve it automatically.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Black,
        });
        content.Children.Add(_captchaAutoSolveAttemptTextBlock);
        content.Children.Add(_captchaAutoSolveElapsedTextBlock);

        UpdateCaptchaAutoSolveAttemptText(attempt, attempts);
        UpdateCaptchaAutoSolveElapsedText();

        _captchaAutoSolvePopup = AppDialog.ShowModelessContent(
            this,
            content,
            "Solving captcha",
            MessageBoxButton.OK,
            MessageBoxImage.Information,
            MessageBoxResult.OK);
        _captchaAutoSolvePopup.Closed += (_, _) => ResetCaptchaAutoSolvePopupState();

        _captchaAutoSolveElapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _captchaAutoSolveElapsedTimer.Tick += (_, _) => UpdateCaptchaAutoSolveElapsedText();
        _captchaAutoSolveElapsedTimer.Start();
    }

    private void CloseCaptchaAutoSolvePopup()
    {
        if (_captchaAutoSolvePopup is null)
        {
            return;
        }

        var popup = _captchaAutoSolvePopup;
        _captchaAutoSolvePopup = null;
        popup.Close();
    }

    private void ResetCaptchaAutoSolvePopupState()
    {
        _captchaAutoSolveElapsedTimer?.Stop();
        _captchaAutoSolveElapsedTimer = null;
        _captchaAutoSolvePopup = null;
        _captchaAutoSolveAttemptTextBlock = null;
        _captchaAutoSolveElapsedTextBlock = null;
        _captchaAutoSolveStartedAt = DateTimeOffset.MinValue;
        _captchaAutoSolveMaxSeconds = 60;
    }

    private void UpdateCaptchaAutoSolveAttemptText(int attempt, int attempts)
    {
        if (_captchaAutoSolveAttemptTextBlock is null)
        {
            return;
        }

        _captchaAutoSolveAttemptTextBlock.Text = $"Attempt: {attempt}/{attempts}";
    }

    private void UpdateCaptchaAutoSolveElapsedText()
    {
        if (_captchaAutoSolveElapsedTextBlock is null || _captchaAutoSolveStartedAt == DateTimeOffset.MinValue)
        {
            return;
        }

        var elapsedSeconds = (int)Math.Floor((DateTimeOffset.UtcNow - _captchaAutoSolveStartedAt).TotalSeconds);
        elapsedSeconds = Math.Clamp(elapsedSeconds, 0, Math.Max(1, _captchaAutoSolveMaxSeconds));
        _captchaAutoSolveElapsedTextBlock.Text = $"Elapsed time: {FormatCountdown(elapsedSeconds)} / {FormatCountdown(_captchaAutoSolveMaxSeconds)}";
    }

    private static (int Attempt, int Attempts) ParseCaptchaAutoSolveAttempt(string message)
    {
        var match = Regex.Match(message ?? string.Empty, @"attempt\s+(?<attempt>\d+)\s*/\s*(?<attempts>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return (1, 1);
        }

        var attempt = int.TryParse(match.Groups["attempt"].Value, out var parsedAttempt)
            ? Math.Max(1, parsedAttempt)
            : 1;
        var attempts = int.TryParse(match.Groups["attempts"].Value, out var parsedAttempts)
            ? Math.Max(1, parsedAttempts)
            : attempt;
        return (Math.Min(attempt, attempts), attempts);
    }

    private static void TrimToMaxEntries<T>(ObservableCollection<T> entries, int max)
    {
        while (entries.Count > max)
        {
            entries.RemoveAt(entries.Count - 1);
        }
    }

    private static bool IsManualVerificationAlarmMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        return value.Contains("manual verification appeared")
            || value.Contains("captcha/manual")
            || value.Contains("captcha/manual step detected")
            || value.Contains("solve it in the browser window")
            || value.Contains("captured captcha screenshot")
            || value.Contains("captcha auto-solve attempt")
            || value.Contains("captcha solver result");
    }

    private static bool IsManualVerificationResolvedMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        return value.Contains("manual verification cleared")
            || value.Contains("captcha cleared automatically")
            || value.Contains("login completed")
            || value.Contains("login finished");
    }

    private static bool IsCaptchaSolvedAutomaticallyMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("Captcha cleared automatically", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCaptchaAutoSolveAttemptMessage(string message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && message.Contains("Captcha auto-solve attempt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCaptchaAutoSolveFailedMessage(string message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && message.Contains("Captcha auto-solve failed", StringComparison.OrdinalIgnoreCase)
            && message.Contains("manual verification", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCaptchaSessionStartMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        return value.Contains("captured captcha screenshot")
            || value.Contains("manual verification appeared")
            || value.Contains("captcha/manual step detected");
    }

    private static bool IsAlarmMessage(string message)
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

        // Normal "defer and retry later" signals are NOT errors. The worker raises a
        // TaskWaitException (and embeds queue_wait_seconds=) when a task simply needs to wait —
        // e.g. build queue full, hero already dispatched, resources still accumulating. These would
        // otherwise match the "failed"/"] fail" keywords below and flood the alarm panel even though
        // nothing is wrong, so they are explicitly classified as non-alarms.
        if (value.Contains("taskwaitexception") || value.Contains("queue_wait_seconds="))
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

        if (value.Contains("alarm:"))
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
            || (value.Contains("hero_adventure.php")
                && value.Contains("transient navigation context error")
                && value.Contains("retrying"))
            || value.Contains("the calling thread cannot access this object because a different thread owns it")
            || (value.Contains("ui sync snapshot failed")
                && value.Contains("execution context was destroyed"));
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

    private void UpdateCaptchaStatsUi()
    {
        CaptchaStatsTextBlock.Text = $"Captchas solved: {_captchaSessionSolvedCount}/{_captchaSessionSeenCount} |";
        var solved = _captchaSessionSolvedCount;
        var seen = _captchaSessionSeenCount;
        if (solved > 0 && solved == seen)
        {
            CaptchaStatsTextBlock.Foreground = GreenHighlightBrush;
        }
        else if (solved < seen)
        {
            CaptchaStatsTextBlock.Foreground = YellowHighlightBrush;
        }
        else
        {
            CaptchaStatsTextBlock.Foreground = NeutralStatsBrush;
        }
    }

    private void UpdateNpcTradeStatsUi()
    {
        var goldSpent = _npcTradeSessionCount * NpcTradeGoldCost;
        SetGoldHighlightedValueText(NpcTradeSessionStatsTextBlock, "Gold spent: ", goldSpent, neutralLabelBrush: NeutralStatsBrush);
        // The detail-panel TextBlock has its own dark base color (#111827) — preserve that for the
        // label and only swap the value color when it's worth highlighting.
        SetGoldHighlightedValueText(NpcTradeGoldSpentTextBlock, "Gold spent: ", goldSpent, neutralLabelBrush: null);
        NpcTradeTroopsTextBlock.Text = $"NPC Troops: {_npcTradeTroopSessionCount}";
        NpcTradeBuildingsTextBlock.Text = $"NPC Buildings: {_npcTradeBuildingSessionCount}";
    }

    // Travian-ish metallic gold. Reused for gold counts and non-zero gold-spent values.
    private static readonly SolidColorBrush GoldHighlightBrush = MakeFrozen(Color.FromRgb(0xD4, 0xAF, 0x37));
    private static readonly SolidColorBrush GreenHighlightBrush = MakeFrozen(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly SolidColorBrush YellowHighlightBrush = MakeFrozen(Color.FromRgb(0xEA, 0xB3, 0x08));
    private static readonly SolidColorBrush NeutralStatsBrush = MakeFrozen(Color.FromRgb(0x4B, 0x55, 0x63));

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
    internal static void SetGoldSilverStatusText(TextBlock target, string goldText, string silverText)
    {
        target.Inlines.Clear();
        target.Inlines.Add(new Run("Gold: ") { Foreground = NeutralStatsBrush });

        var goldValueRun = new Run(goldText);
        var goldIsMeaningful = goldText is not ("-" or "0") && !string.IsNullOrWhiteSpace(goldText);
        goldValueRun.Foreground = goldIsMeaningful ? GoldHighlightBrush : NeutralStatsBrush;
        if (goldIsMeaningful)
        {
            goldValueRun.FontWeight = FontWeights.SemiBold;
        }
        target.Inlines.Add(goldValueRun);

        target.Inlines.Add(new Run($" | Silver: {silverText}") { Foreground = NeutralStatsBrush });
    }
}
