using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void LoadAutomationLoopTasks(BotOptions options)
    {
        var configured = (options.ContinuousLoopGroups ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (configured.Count <= 0)
        {
            configured = (options.LoopTasks ?? [])
                .Select(NormalizeLegacyLoopTaskName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(QueueGroupCatalog.ResolveGroup)
                .Select(QueueGroupCatalog.GetKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var orderedNames = LoadConfiguredContinuousLoopGroupOrder();
        var visibleGroups = LoadConfiguredDashboardVisibleGroups();

        _suppressAutomationLoopConfigWrite = true;
        try
        {
            _automationLoopTasks.Clear();
            foreach (var groupKey in orderedNames)
            {
                if (!QueueGroupCatalog.TryParse(groupKey, out var group))
                {
                    continue;
                }

                _automationLoopTasks.Add(new LoopTaskOption
                {
                    TaskName = groupKey,
                    Title = QueueGroupCatalog.GetTitle(group),
                    Description = QueueGroupCatalog.GetDescription(group),
                    IsEnabled = configured.Contains(groupKey, StringComparer.OrdinalIgnoreCase),
                    IsVisible = visibleGroups.Contains(groupKey, StringComparer.OrdinalIgnoreCase),
                    StateText = "Idle",
                    DetailText = "No queued task.",
                });
            }

            UpdateAutomationLoopOrders();
            RefreshAutomationLoopDashboardUi();
            SyncTeutonsOnlyAutomationGroups(ResolveStoredTroopTrainingTribe());
        }
        finally
        {
            _suppressAutomationLoopConfigWrite = false;
        }
    }

    private void UpdateAutomationLoopOrders()
    {
        for (var i = 0; i < _automationLoopTasks.Count; i++)
        {
            _automationLoopTasks[i].Order = i + 1;
        }
    }

    private void RefreshAutomationLoopDashboardUi()
    {
        UpdateAutomationLoopSummaryText();
        UpdateAutomationLoopRunningIndicators();
    }

    private void UpdateAutomationLoopSummaryText()
    {
        var enabledCount = _automationLoopTasks.Count(item => item.IsEnabled);
        var visibleCount = _automationLoopTasks.Count(item => item.IsVisible);
        var summaryText = enabledCount <= 0
            ? $"No group enabled. Visible on dashboard: {visibleCount}."
            : $"Continuous loop uses {enabledCount} enabled group(s). Visible on dashboard: {visibleCount}.";
        SetAutomationLoopSummaryText(summaryText);
    }

    private void SetAutomationLoopSummaryText(string summaryText)
    {
        AutomationLoopSummaryTextBlock.Text = summaryText;
        UpdateAutomationLoopColumns();
    }

    private void UpdateAutomationLoopColumns()
    {
        if (AutomationLoopListBox is null)
        {
            return;
        }

        var visibleCount = Math.Max(1, _automationLoopTasks.Count(item => item.IsVisible));
        var columns = visibleCount <= 4 ? 1 : 2;
        var factory = new FrameworkElementFactory(typeof(VerticalFirstUniformGrid));
        factory.SetValue(VerticalFirstUniformGrid.ColumnsProperty, columns);
        AutomationLoopListBox.ItemsPanel = new ItemsPanelTemplate(factory);
    }

    private void UpdateAutomationLoopRunningIndicators()
    {
        var isRunning = (_loopTask is not null && !_loopTask.IsCompleted) || _autoQueueRunning;
        var hasPausedQueueItems = false;
        string? queueRunningTaskName = null;
        IReadOnlyList<QueueItem> queueItems = [];
        try
        {
            queueItems = _botService.GetQueueItemsForDisplay();
            hasPausedQueueItems = queueItems.Any(item => item.Status == QueueStatus.Paused);
            queueRunningTaskName = queueItems.FirstOrDefault(item => item.Status == QueueStatus.Running)?.TaskName;
        }
        catch
        {
            // Ignore temporary queue read failures.
        }

        var runningTaskName = !string.IsNullOrWhiteSpace(queueRunningTaskName)
            ? queueRunningTaskName
            : isRunning
                ? _activeAutomationTaskName
                : null;

        var runningGroup = string.IsNullOrWhiteSpace(runningTaskName)
            ? (QueueGroup?)null
            : QueueGroupCatalog.ResolveGroup(runningTaskName);

        foreach (var item in _automationLoopTasks)
        {
            if (!QueueGroupCatalog.TryParse(item.TaskName, out var group))
            {
                item.IsRunning = false;
                item.StateText = "Idle";
                item.DetailText = "Unknown group.";
                item.QueuedCount = 0;
                item.RemainingSeconds = null;
                continue;
            }

            var groupItems = queueItems.Where(entry => entry.Group == group).ToList();
            var pendingCount = groupItems.Count(entry => entry.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);
            var deferred = groupItems
                .Where(entry => entry.Status == QueueStatus.Pending && entry.NextAttemptAt > DateTimeOffset.UtcNow)
                .OrderBy(entry => entry.NextAttemptAt)
                .FirstOrDefault();
            var runningItem = groupItems.FirstOrDefault(entry => entry.Status == QueueStatus.Running);
            var paused = groupItems.Any(entry => entry.Status == QueueStatus.Paused);
            var constructionWaitSeconds = group == QueueGroup.Construction
                ? ResolveConstructionGroupRemainingSeconds()
                : (int?)null;
            var troopTrainingWaitSeconds = group == QueueGroup.TroopTraining
                ? _troopTrainingViewModel.ResolveGroupRemainingSeconds()
                : (int?)null;
            var breweryCelebrationWaitSeconds = group == QueueGroup.BreweryCelebration
                ? _troopTrainingViewModel.ResolveBreweryCelebrationGroupRemainingSeconds()
                : (int?)null;

            item.QueuedCount = pendingCount;
            item.IsRunning = runningGroup.HasValue && runningGroup.Value == group;
            item.IsBlocked = false;
            item.BlockedText = "Blocked";
            if (runningItem is not null || item.IsRunning)
            {
                item.StateText = "Running";
                item.DetailText = runningItem is not null
                    ? BuildQueueDisplayName(runningItem)
                    : "Coordinator active.";
                item.RemainingSeconds = null;
            }
            else if (deferred is not null || constructionWaitSeconds is > 0 || troopTrainingWaitSeconds is > 0 || breweryCelebrationWaitSeconds is > 0)
            {
                item.StateText = "Waiting";
                if (deferred is not null)
                {
                    item.DetailText = $"Next try {FormatQueueServerTime(deferred.NextAttemptAt)}";
                    item.RemainingSeconds = Math.Max(0, (int)Math.Ceiling((deferred.NextAttemptAt - DateTimeOffset.UtcNow).TotalSeconds));
                }
                else if (troopTrainingWaitSeconds is > 0)
                {
                    item.DetailText = "Troop queue active.";
                    item.RemainingSeconds = troopTrainingWaitSeconds;
                }
                else if (breweryCelebrationWaitSeconds is > 0)
                {
                    item.DetailText = "Celebration running.";
                    item.RemainingSeconds = breweryCelebrationWaitSeconds;
                }
                else
                {
                    item.DetailText = "Build queue active.";
                    item.RemainingSeconds = constructionWaitSeconds;
                }
            }
            else if (group == QueueGroup.Troops && IsTroopsGroupBlocked())
            {
                item.StateText = "Blocked";
                item.DetailText = _troopsBlockedReasonText ?? "Troops group blocked.";
                item.RemainingSeconds = null;
                item.IsBlocked = true;
                item.BlockedText = _troopsBlockedReasonText ?? "Blocked";
            }
            else if (group == QueueGroup.Farming && IsFarmingGroupBlocked())
            {
                item.StateText = "Blocked";
                item.DetailText = _farmingBlockedReasonText ?? "Farming group blocked.";
                item.RemainingSeconds = null;
                item.IsBlocked = true;
                item.BlockedText = _farmingBlockedReasonText ?? "Blocked";
            }
            else if (group == QueueGroup.Hero && IsHeroGroupBlocked())
            {
                item.StateText = "Blocked";
                item.DetailText = _heroBlockedReasonText ?? "Hero group blocked.";
                item.RemainingSeconds = null;
                item.IsBlocked = true;
                item.BlockedText = _heroBlockedReasonText ?? "Blocked";
            }
            else if (group == QueueGroup.BreweryCelebration && IsBreweryGroupBlocked())
            {
                item.StateText = "Blocked";
                item.DetailText = _breweryBlockedReasonText ?? "Brewery group blocked.";
                item.RemainingSeconds = null;
                item.IsBlocked = true;
                item.BlockedText = _breweryBlockedReasonText ?? "Blocked";
            }
            else if (!item.IsEnabled)
            {
                item.StateText = "Disabled";
                item.DetailText = pendingCount > 0 ? $"{pendingCount} queued." : "No queued task.";
                item.RemainingSeconds = null;
            }
            else if (group == QueueGroup.BreweryCelebration
                && _troopTrainingViewModel.IsAutoCelebrationAvailableForCurrentTribe
                && !_troopTrainingViewModel.AutoCelebrationEnabled)
            {
                item.StateText = "Disabled";
                item.DetailText = "Auto celebration is off.";
                item.RemainingSeconds = null;
            }
            else if (group == QueueGroup.BreweryCelebration
                && _troopTrainingViewModel.IsAutoCelebrationAvailableForCurrentTribe
                && _troopTrainingViewModel.AutoCelebrationCanStart)
            {
                item.StateText = "Ready";
                item.DetailText = "Celebration can start.";
                item.RemainingSeconds = null;
            }
            else if (paused)
            {
                item.StateText = "Paused";
                item.DetailText = "Contains paused task.";
                item.RemainingSeconds = null;
            }
            else
            {
                item.StateText = item.IsEnabled ? "Idle" : "Disabled";
                item.DetailText = pendingCount > 0 ? $"{pendingCount} queued." : "No queued task.";
                item.RemainingSeconds = null;
            }
        }

        if (isRunning)
        {
            SetAutomationLoopRunState(
                Color.FromRgb(22, 163, 74),
                "Running",
                Color.FromRgb(22, 163, 74));
            return;
        }

        if (hasPausedQueueItems)
        {
            SetAutomationLoopRunState(
                Color.FromRgb(217, 119, 6),
                "Paused",
                Color.FromRgb(217, 119, 6));
            return;
        }

        SetAutomationLoopRunState(
            Color.FromRgb(156, 163, 175),
            "Idle",
            Color.FromRgb(107, 114, 128));
    }

    private void SetAutomationLoopRunState(Color dotColor, string stateText, Color textColor)
    {
        AutomationLoopRunStateDot.Fill = new SolidColorBrush(dotColor);
        AutomationLoopRunStateTextBlock.Text = stateText;
        AutomationLoopRunStateTextBlock.Foreground = new SolidColorBrush(textColor);
    }

    private void PersistAutomationLoopTasksToConfig()
    {
        if (_suppressAutomationLoopConfigWrite)
        {
            return;
        }

        try
        {
            var enabledGroupNames = _automationLoopTasks
                .Where(item => item.IsEnabled)
                .Select(item => item.TaskName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            var orderedGroupNames = _automationLoopTasks
                .Select(item => item.TaskName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            var visibleGroupNames = _automationLoopTasks
                .Where(item => item.IsVisible)
                .Select(item => item.TaskName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            var config = _botConfigStore.Load();
            config["continuous_loop_groups"] = new JsonArray(enabledGroupNames.Select(name => JsonValue.Create(name)!).ToArray());
            config[ContinuousLoopGroupOrderConfigKey] = new JsonArray(orderedGroupNames.Select(name => JsonValue.Create(name)!).ToArray());
            config[DashboardVisibleGroupsConfigKey] = new JsonArray(visibleGroupNames.Select(name => JsonValue.Create(name)!).ToArray());

            var existingLoopTasks = config["loop_tasks"] as JsonArray ?? new JsonArray();
            var normalizedLoopTasks = existingLoopTasks
                .Select(node => NormalizeLegacyLoopTaskName(node?.ToString()))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedLoopTasks.Count > 0)
            {
                config["loop_tasks"] = new JsonArray(normalizedLoopTasks.Select(name => JsonValue.Create(name)!).ToArray());
            }

            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save continuous loop groups: {ex.Message}");
        }
    }

    private List<string> LoadConfiguredDashboardVisibleGroups()
    {
        try
        {
            var config = _botConfigStore.Load();
            var configuredVisible = (config[DashboardVisibleGroupsConfigKey] as JsonArray ?? new JsonArray())
                .Select(node => node?.ToString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Where(name => QueueGroupCatalog.TryParse(name, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (configuredVisible.Count > 0)
            {
                foreach (var groupKey in QueueGroupCatalog.AllGroups.Select(QueueGroupCatalog.GetKey))
                {
                    if (!configuredVisible.Contains(groupKey, StringComparer.OrdinalIgnoreCase))
                    {
                        configuredVisible.Add(groupKey);
                    }
                }

                return configuredVisible;
            }
        }
        catch
        {
            // Ignore read errors and fall back to all visible.
        }

        return QueueGroupCatalog.AllGroups
            .Select(QueueGroupCatalog.GetKey)
            .ToList();
    }

    private List<string> LoadConfiguredContinuousLoopGroupOrder()
    {
        try
        {
            var config = _botConfigStore.Load();
            var configuredOrder = (config[ContinuousLoopGroupOrderConfigKey] as JsonArray ?? new JsonArray())
                .Select(node => node?.ToString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Where(name => QueueGroupCatalog.TryParse(name, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var groupKey in QueueGroupCatalog.AllGroups.Select(QueueGroupCatalog.GetKey))
            {
                if (!configuredOrder.Contains(groupKey, StringComparer.OrdinalIgnoreCase))
                {
                    configuredOrder.Add(groupKey);
                }
            }

            return configuredOrder;
        }
        catch
        {
            return QueueGroupCatalog.AllGroups
                .Select(QueueGroupCatalog.GetKey)
                .ToList();
        }
    }

    private static string NormalizeLegacyLoopTaskName(string? taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return string.Empty;
        }

        return string.Equals(taskName.Trim(), "hero_send_adventure", StringComparison.OrdinalIgnoreCase)
            ? "hero_manage"
            : taskName.Trim();
    }

    private static string HumanizeTaskName(string taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return "-";
        }

        return string.Join(
            " ",
            taskName
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Length == 1
                    ? char.ToUpperInvariant(part[0]).ToString()
                    : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private bool SyncTeutonsOnlyAutomationGroups(string? tribe, bool persistChanges = false)
    {
        var option = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.BreweryCelebration), StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            return false;
        }

        var isTeutons = IsTeutonsTribe(tribe);
        var changed = false;
        if (!isTeutons)
        {
            if (option.IsEnabled)
            {
                option.IsEnabled = false;
                changed = true;
            }

            if (option.IsVisible)
            {
                option.IsVisible = false;
                changed = true;
            }
        }
        else if (!option.IsVisible)
        {
            option.IsVisible = true;
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        RefreshAutomationLoopDashboardUi();
        if (persistChanges)
        {
            PersistAutomationLoopTasksToConfig();
        }

        return true;
    }

    private void AutomationLoopToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: LoopTaskOption option } toggle)
        {
            return;
        }

        option.IsEnabled = toggle.IsChecked == true;
        if (!option.IsEnabled
            && QueueGroupCatalog.TryParse(option.TaskName, out var disabledGroup)
            && ContinuousRunToggleButton?.IsChecked == true
            && GetActiveContinuousLoopGroup() == disabledGroup
            && _loopTask is not null
            && !_loopTask.IsCompleted)
        {
            _restartContinuousLoopAfterStop = HasEnabledContinuousLoopGroupsExcept(disabledGroup);
            _loopController.RequestLoopStop();
            _loopCts?.Cancel();
            AppendLog($"{QueueGroupCatalog.GetTitle(disabledGroup)} group disabled. Stopping current loop task.");
        }

        if (option.IsEnabled
            && string.Equals(option.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Troops), StringComparison.OrdinalIgnoreCase))
        {
            ClearTroopsBlockedState();
        }
        else if (option.IsEnabled
            && string.Equals(option.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Farming), StringComparison.OrdinalIgnoreCase))
        {
            _lastFarmListsAnalysisAt = DateTimeOffset.MinValue;
            ClearFarmingBlockedState();
        }
        else if (option.IsEnabled
            && string.Equals(option.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Hero), StringComparison.OrdinalIgnoreCase))
        {
            ClearHeroBlockedState();
        }
        else if (option.IsEnabled
            && string.Equals(option.TaskName, QueueGroupCatalog.GetKey(QueueGroup.BreweryCelebration), StringComparison.OrdinalIgnoreCase))
        {
            ClearBreweryBlockedState();
        }

        RefreshAutomationLoopDashboardUi();
        PersistAutomationLoopTasksToConfig();
    }

    private void AutomationLoopListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _automationLoopDragStart = e.GetPosition(AutomationLoopListBox);
        _automationLoopDragSource = FindAutomationLoopTask(e.OriginalSource as DependencyObject);
    }

    private void AutomationLoopListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _automationLoopDragSource is null)
        {
            return;
        }

        var position = e.GetPosition(AutomationLoopListBox);
        var delta = position - _automationLoopDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(AutomationLoopListBox, _automationLoopDragSource, DragDropEffects.Move);
    }

    private void AutomationLoopListBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(LoopTaskOption))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void AutomationLoopListBox_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LoopTaskOption)))
        {
            return;
        }

        if (e.Data.GetData(typeof(LoopTaskOption)) is not LoopTaskOption sourceOption)
        {
            return;
        }

        var targetOption = FindAutomationLoopTask(e.OriginalSource as DependencyObject);
        var fromIndex = _automationLoopTasks.IndexOf(sourceOption);
        if (fromIndex < 0)
        {
            return;
        }

        var toIndex = targetOption is null
            ? _automationLoopTasks.Count - 1
            : _automationLoopTasks.IndexOf(targetOption);
        if (toIndex < 0)
        {
            toIndex = _automationLoopTasks.Count - 1;
        }

        if (fromIndex == toIndex)
        {
            return;
        }

        _automationLoopTasks.Move(fromIndex, toIndex);
        UpdateAutomationLoopOrders();
        RefreshAutomationLoopDashboardUi();
        PersistAutomationLoopTasksToConfig();
    }

    private LoopTaskOption? FindAutomationLoopTask(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: LoopTaskOption option })
            {
                return option;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
