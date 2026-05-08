using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TbotUltra.Desktop.Models;

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
                    _terminalEntries.Insert(0, line);
                    logLinesForSessionLog.Add(line);
                    TryApplyInlineResourceLevelUpdateFromLog(part);
                    TryApplyPlusStatusFromLog(part);
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

                    if (IsAlarmMessage(part))
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
                        ShowCaptchaAutoSolvePopup();
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
                        ShowManualVerificationPopup(_browserSessionLikelyOpen);
                    }
                }
            }

            TrimToMaxEntries(_terminalEntries, 1000);
            TrimToMaxEntries(_alarmEntries, 200);
            UpdateCaptchaStatsUi();
            TryAppendSessionLogLines(logLinesForSessionLog, alarmLinesForSessionLog);

            if (lastRawMessage is not null)
            {
                StatusTextBlock.Text = lastRawMessage;
                StatusMiniLogTextBlock.Text = string.IsNullOrWhiteSpace(lastPrimaryPart)
                    ? lastRawMessage
                    : lastPrimaryPart;
            }

            UpdateTerminalAlarmUi();
            UpdateExecutionStateIndicator();

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
        if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
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
        ResourcesInfoTextBlock.Text = $"Resource slot {slotId} updated to level {nextLevel}.";
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

    private void ShowCaptchaAutoSolvePopup()
    {
        if (_captchaAutoSolvePopup is { IsVisible: true })
        {
            return;
        }

        _captchaAutoSolvePopup = AppDialog.ShowModeless(
            this,
            "Captcha detected. Tbot Ultra is trying to solve it automatically. This can take up to about one minute.",
            "Solving captcha",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        _captchaAutoSolvePopup.Closed += (_, _) => _captchaAutoSolvePopup = null;
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

    private void UpdateCaptchaStatsUi()
    {
        CaptchaStatsTextBlock.Text = $"Captchas solved: {_captchaSessionSolvedCount}/{_captchaSessionSeenCount} |";
    }

    private void CloseTerminalAlarmPopupButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 0;
    }

    private void AcknowledgeAlarmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_unacknowledgedAlarmCount == 0)
        {
            return;
        }

        AcknowledgeAllAlarmEntries();
        StatusTextBlock.Text = "Alerts acknowledged.";
        UpdateTerminalAlarmUi();
    }

    private void ClearCurrentLogButton_Click(object sender, RoutedEventArgs e)
    {
        var alarmsSelected = TerminalAlarmTabControl.SelectedIndex == 1;
        if (alarmsSelected)
        {
            _alarmEntries.Clear();
            _unacknowledgedAlarmCount = 0;
        }
        else
        {
            _terminalEntries.Clear();
        }

        UpdateTerminalAlarmUi();
    }

    private void CopyCurrentTabButton_Click(object sender, RoutedEventArgs e)
    {
        var alertsTabSelected = TerminalAlarmTabControl.SelectedIndex == 1;
        var list = alertsTabSelected ? AlarmListBox : TerminalListBox;
        var selectedLines = alertsTabSelected
            ? list.SelectedItems.Cast<AlarmEntryRow>().Select(item => item.Text).ToList()
            : list.SelectedItems.Cast<string>().ToList();
        var source = alertsTabSelected ? _alarmEntries.Select(item => item.Text).ToList() : _terminalEntries.ToList();
        var linesToCopy = selectedLines.Count > 0 ? selectedLines : source;
        if (linesToCopy.Count == 0)
        {
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, linesToCopy));
        StatusTextBlock.Text = alertsTabSelected
            ? "Alerts copied to clipboard."
            : "Terminal log copied to clipboard.";

        CopyFeedbackTextBlock.Text = "Copied";
        CopyFeedbackTextBlock.Visibility = Visibility.Visible;
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    private void UpdateTerminalAlarmUi()
    {
        var hasAlarms = _unacknowledgedAlarmCount > 0;
        var hasAlarmEntries = _alarmEntries.Count > 0;
        var alarmTabSelected = TerminalAlarmTabControl.SelectedIndex == 1;
        var activeList = alarmTabSelected ? AlarmListBox : TerminalListBox;
        var hasSelection = activeList.SelectedItems.Count > 0;
        AcknowledgeAlarmButton.IsEnabled = hasAlarms;
        CopyCurrentTabButton.IsEnabled = alarmTabSelected ? hasAlarmEntries : _terminalEntries.Count > 0;
        CopyCurrentTabButton.ToolTip = alarmTabSelected ? "Copy alerts" : "Copy terminal";
        ClearCurrentLogButton.IsEnabled = alarmTabSelected ? hasAlarmEntries : _terminalEntries.Count > 0;

        if (hasAlarms)
        {
            LogsNavButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            LogsNavButton.Foreground = Brushes.White;
            LogsNavButton.ToolTip = $"Logs ({_unacknowledgedAlarmCount} alarms)";
        }
        else
        {
            LogsNavButton.Background = new SolidColorBrush(Color.FromRgb(243, 244, 246));
            LogsNavButton.Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39));
            LogsNavButton.ToolTip = "Logs";
        }

        if (hasSelection)
        {
            CopyCurrentTabButton.Content = "Copy selected";
        }
        else
        {
            CopyCurrentTabButton.Content = "Copy";
        }
    }

    private void AcknowledgeAllAlarmEntries()
    {
        foreach (var entry in _alarmEntries)
        {
            entry.IsAcknowledged = true;
        }

        _unacknowledgedAlarmCount = 0;
        AlarmListBox.Items.Refresh();
        _logsPopupAlarmList?.Items.Refresh();
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // Keep current log selection when clicking log action buttons.
        if (IsDescendantOf(source, CopyCurrentTabButton)
            || IsDescendantOf(source, PopoutLogsButton)
            || IsDescendantOf(source, AcknowledgeAlarmButton)
            || IsDescendantOf(source, ClearCurrentLogButton))
        {
            return;
        }

        if (!IsDescendantOf(source, TerminalListBox))
        {
            TerminalListBox.UnselectAll();
        }

        if (!IsDescendantOf(source, AlarmListBox))
        {
            AlarmListBox.UnselectAll();
        }
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void LogListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        var item = GetListBoxItemAt(list, e.GetPosition(list));
        if (item is null)
        {
            return;
        }

        var index = list.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0 || index >= list.Items.Count)
        {
            return;
        }

        _logDragSelecting = true;
        _logDragSourceList = list;
        _logDragAnchorIndex = index;
        SelectListBoxRange(list, index, index);
        list.Focus();
        list.CaptureMouse();
    }

    private void LogListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_logDragSelecting || _logDragSourceList is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (!ReferenceEquals(sender, _logDragSourceList))
        {
            return;
        }

        var mousePosition = e.GetPosition(_logDragSourceList);
        var item = GetListBoxItemAt(_logDragSourceList, mousePosition);
        int index;
        if (item is not null)
        {
            index = _logDragSourceList.ItemContainerGenerator.IndexFromContainer(item);
        }
        else if (_logDragSourceList.Items.Count > 0 && mousePosition.Y < 0)
        {
            index = 0;
        }
        else if (_logDragSourceList.Items.Count > 0 && mousePosition.Y > _logDragSourceList.ActualHeight)
        {
            index = _logDragSourceList.Items.Count - 1;
        }
        else
        {
            return;
        }

        if (index < 0 || index >= _logDragSourceList.Items.Count || _logDragAnchorIndex < 0)
        {
            return;
        }

        SelectListBoxRange(_logDragSourceList, _logDragAnchorIndex, index);
    }

    private void LogListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_logDragSelecting)
        {
            return;
        }

        _logDragSelecting = false;
        _logDragAnchorIndex = -1;
        if (_logDragSourceList is not null && _logDragSourceList.IsMouseCaptured)
        {
            _logDragSourceList.ReleaseMouseCapture();
        }

        _logDragSourceList = null;
        UpdateTerminalAlarmUi();
    }

    private static void SelectListBoxRange(ListBox list, int startIndex, int endIndex)
    {
        if (list.Items.Count == 0)
        {
            return;
        }

        var start = Math.Clamp(Math.Min(startIndex, endIndex), 0, list.Items.Count - 1);
        var end = Math.Clamp(Math.Max(startIndex, endIndex), 0, list.Items.Count - 1);
        list.SelectedItems.Clear();
        for (var i = start; i <= end; i++)
        {
            list.SelectedItems.Add(list.Items[i]);
        }

        list.ScrollIntoView(list.Items[end]);
    }

    private static ListBoxItem? GetListBoxItemAt(ListBox list, Point point)
    {
        var hit = list.InputHitTest(point) as DependencyObject;
        var direct = FindAncestor<ListBoxItem>(hit);
        if (direct is not null)
        {
            return direct;
        }

        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item)
            {
                continue;
            }

            var topLeft = item.TranslatePoint(new Point(0, 0), list);
            var bounds = new Rect(topLeft, new Size(item.ActualWidth, item.ActualHeight));
            if (bounds.Contains(point))
            {
                return item;
            }
        }

        return null;
    }

    private void PopoutLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_logsPopupWindow is not null)
        {
            _logsPopupWindow.Activate();
            return;
        }

        var popupTab = new TabControl();
        var popupLogList = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(2, 6, 23)),
            Foreground = new SolidColorBrush(Color.FromRgb(147, 197, 253)),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            SelectionMode = SelectionMode.Extended,
            ItemsSource = _terminalEntries,
        };
        popupLogList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        popupLogList.ItemTemplate = new DataTemplate
        {
            VisualTree = new FrameworkElementFactory(typeof(TextBlock)),
        };
        popupLogList.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new Binding("."));
        popupLogList.ItemTemplate.VisualTree.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var popupAlarmList = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(17, 13, 13)),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            SelectionMode = SelectionMode.Extended,
            ItemsSource = _alarmEntries,
        };
        _logsPopupLogList = popupLogList;
        _logsPopupAlarmList = popupAlarmList;
        popupAlarmList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        popupAlarmList.ItemTemplate = new DataTemplate
        {
            VisualTree = new FrameworkElementFactory(typeof(TextBlock)),
        };
        popupAlarmList.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new Binding(nameof(AlarmEntryRow.Text)));
        popupAlarmList.ItemTemplate.VisualTree.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var popupAlarmStyle = new Style(typeof(TextBlock));
        popupAlarmStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(252, 165, 165))));
        popupAlarmStyle.Triggers.Add(new DataTrigger
        {
            Binding = new Binding(nameof(AlarmEntryRow.IsAcknowledged)),
            Value = true,
            Setters =
            {
                new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(147, 197, 253))),
            }
        });
        popupAlarmList.ItemTemplate.VisualTree.SetValue(TextBlock.StyleProperty, popupAlarmStyle);

        popupTab.Items.Add(new TabItem { Header = "Log", Content = popupLogList });
        popupTab.Items.Add(new TabItem { Header = "Alarms", Content = popupAlarmList });

        var clearButton = new Button { Content = "Clear", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), Height = 30 };
        clearButton.Click += (_, _) =>
        {
            if (popupTab.SelectedIndex == 1)
            {
                _alarmEntries.Clear();
            }
            else
            {
                _terminalEntries.Clear();
            }

            UpdateTerminalAlarmUi();
        };

        var acknowledgeButton = new Button { Content = "Acknowledge alarms", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), Height = 30 };
        acknowledgeButton.Click += (_, _) =>
        {
            AcknowledgeAllAlarmEntries();
            UpdateTerminalAlarmUi();
        };

        var copyButton = new Button { Content = "Copy", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), Height = 30 };
        copyButton.Click += (_, _) =>
        {
            var selected = popupTab.SelectedIndex == 1
                ? popupAlarmList.SelectedItems.Cast<AlarmEntryRow>().Select(item => item.Text).ToList()
                : popupLogList.SelectedItems.Cast<string>().ToList();
            var lines = selected.Count > 0
                ? selected
                : (popupTab.SelectedIndex == 1 ? _alarmEntries.Select(item => item.Text).ToList() : _terminalEntries.ToList());
            if (lines.Count == 0)
            {
                return;
            }

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        };

        var closeButton = new Button { Content = "Close", Padding = new Thickness(10, 4, 10, 4), Height = 30 };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        footer.Children.Add(acknowledgeButton);
        footer.Children.Add(copyButton);
        footer.Children.Add(clearButton);
        footer.Children.Add(closeButton);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(popupTab);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        root.PreviewMouseDown += (_, args) =>
        {
            if (args.OriginalSource is not DependencyObject src)
            {
                return;
            }

            if (!IsDescendantOf(src, popupLogList))
            {
                popupLogList.UnselectAll();
            }

            if (!IsDescendantOf(src, popupAlarmList))
            {
                popupAlarmList.UnselectAll();
            }
        };

        _logsPopupWindow = new Window
        {
            Title = "Logs",
            Width = 700,
            Height = 400,
            MinWidth = 580,
            MinHeight = 320,
            Content = root,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left + Width + 10,
            Top = Top + 30,
        };

        _logsPopupWindow.Closed += (_, _) =>
        {
            _logsPopupWindow = null;
            _logsPopupLogList = null;
            _logsPopupAlarmList = null;
        };
        closeButton.Click += (_, _) => _logsPopupWindow?.Close();
        _logsPopupWindow.Show();
    }
}
