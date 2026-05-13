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
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private const string TroopsBlockedReasonSmithyMissing = "smithy_missing";
    private const string TroopsBlockedReasonAllDone = "all_done";
    private const string BreweryBlockedReasonMissing = "brewery_missing";
    private const string FarmingBlockedReasonNoGoldClub = "no_goldclub";
    private const string FarmingBlockedReasonNoFarmLists = "no_farmlists";
    private const string HeroBlockedReasonNoAdventures = "no_adventures";

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
            UpdateAutomationLoopSummaryText();
            UpdateAutomationLoopRunningIndicators();
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

    private void UpdateAutomationLoopSummaryText()
    {
        var enabledCount = _automationLoopTasks.Count(item => item.IsEnabled);
        var visibleCount = _automationLoopTasks.Count(item => item.IsVisible);
        AutomationLoopSummaryTextBlock.Text = enabledCount <= 0
            ? $"No group enabled. Visible on dashboard: {visibleCount}."
            : $"Continuous loop uses {enabledCount} enabled group(s). Visible on dashboard: {visibleCount}.";
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

    private void SetActiveAutomationTask(string? taskName)
    {
        void Apply()
        {
            _activeAutomationTaskName = string.IsNullOrWhiteSpace(taskName)
                ? null
                : taskName;
            UpdateAutomationLoopRunningIndicators();
        }

        if (Dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        _ = Dispatcher.BeginInvoke((Action)Apply);
    }

    private QueueGroup? GetActiveContinuousLoopGroup()
    {
        var taskName = _activeAutomationTaskName;
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return null;
        }

        return QueueGroupCatalog.ResolveGroup(taskName);
    }

    private bool HasEnabledContinuousLoopGroupsExcept(QueueGroup excludedGroup)
    {
        return GetContinuousLoopEnabledGroupsInOrder().Any(group => group != excludedGroup);
    }

    private void StartContinuousLoopRunner()
    {
        var initialOptions = LoadBotOptions();
        _loopController.ClearLoopStopRequest();
        _loopController.ClearQueueStopRequest();
        _continuousLoopConstructionStatusNeedsSync = true;
        _loopCts = _loopController.CreateCts("loop");
        var token = _loopCts.Token;

        StartLoopButton.Content = "Pause bot";
        StartLoopButton.IsEnabled = true;
        SetLoopIndicator(true);
        AppendLog($"Loop started. Interval={initialOptions.LoopIntervalSeconds}s");

        _loopTask = Task.Run(() => RunContinuousLoopAsync(token), token);
        _ = TrackLoopCompletionAsync(_loopTask);
    }

    private bool IsContinuousLoopGroupEnabled(QueueGroup group)
    {
        return GetContinuousLoopEnabledGroupsInOrder().Contains(group);
    }

    private IReadOnlyList<QueueItem> GetContinuousLoopRelevantQueueItems()
    {
        var enabledGroups = GetContinuousLoopEnabledGroupsInOrder().ToHashSet();
        if (enabledGroups.Count <= 0)
        {
            return [];
        }

        return _botService.GetQueueItemsForDisplay()
            .Where(item => enabledGroups.Contains(item.Group))
            .ToList();
    }

    private static IReadOnlyList<QueueItem> OrderContinuousLoopGroupItems(IEnumerable<QueueItem> items)
    {
        return items
            .OrderBy(item => item.IsRuntimeOnly)
            .ThenByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .ToList();
    }

    private static bool TryExtractTroopsBlockedReason(string? message, out string reasonKey, out string reasonText)
    {
        reasonKey = string.Empty;
        reasonText = string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.Trim();
        if (value.Contains("Smithy not found in this village", StringComparison.OrdinalIgnoreCase))
        {
            reasonKey = TroopsBlockedReasonSmithyMissing;
            reasonText = "Smithy missing";
            return true;
        }

        if (value.Contains("Smithy:", StringComparison.OrdinalIgnoreCase)
            && value.Contains("All done", StringComparison.OrdinalIgnoreCase))
        {
            reasonKey = TroopsBlockedReasonAllDone;
            reasonText = "All troops fully developed";
            return true;
        }

        return false;
    }

    private void SetTroopsBlockedState(string reasonKey, string reasonText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetTroopsBlockedState(reasonKey, reasonText));
            return;
        }

        var troopsOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Troops), StringComparison.OrdinalIgnoreCase));
        if (troopsOption is not null)
        {
            _troopsBlockedPreviouslyEnabled = troopsOption.IsEnabled;
            troopsOption.IsEnabled = false;
        }

        _troopsBlockedReasonKey = reasonKey;
        _troopsBlockedReasonText = reasonText;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void SetFarmingBlockedState(string reasonKey, string reasonText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetFarmingBlockedState(reasonKey, reasonText));
            return;
        }

        var farmingOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Farming), StringComparison.OrdinalIgnoreCase));
        if (farmingOption is not null)
        {
            _farmingBlockedPreviouslyEnabled = farmingOption.IsEnabled;
            farmingOption.IsEnabled = false;
        }

        _farmingBlockedReasonKey = reasonKey;
        _farmingBlockedReasonText = reasonText;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void SetHeroBlockedState(string reasonKey, string reasonText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetHeroBlockedState(reasonKey, reasonText));
            return;
        }

        var heroOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Hero), StringComparison.OrdinalIgnoreCase));
        if (heroOption is not null)
        {
            _heroBlockedPreviouslyEnabled = heroOption.IsEnabled;
            heroOption.IsEnabled = false;
        }

        _heroBlockedReasonKey = reasonKey;
        _heroBlockedReasonText = reasonText;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void SetBreweryBlockedState(string reasonKey, string reasonText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetBreweryBlockedState(reasonKey, reasonText));
            return;
        }

        var breweryOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.BreweryCelebration), StringComparison.OrdinalIgnoreCase));
        if (breweryOption is not null)
        {
            _breweryBlockedPreviouslyEnabled = breweryOption.IsEnabled;
            breweryOption.IsEnabled = false;
        }

        _breweryBlockedReasonKey = reasonKey;
        _breweryBlockedReasonText = reasonText;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void ClearTroopsBlockedState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ClearTroopsBlockedState);
            return;
        }

        if (string.IsNullOrWhiteSpace(_troopsBlockedReasonKey) && string.IsNullOrWhiteSpace(_troopsBlockedReasonText))
        {
            return;
        }

        _troopsBlockedReasonKey = null;
        _troopsBlockedReasonText = null;
        var troopsOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Troops), StringComparison.OrdinalIgnoreCase));
        if (troopsOption is not null && _troopsBlockedPreviouslyEnabled)
        {
            troopsOption.IsEnabled = true;
        }

        _troopsBlockedPreviouslyEnabled = false;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void ClearFarmingBlockedState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ClearFarmingBlockedState);
            return;
        }

        if (string.IsNullOrWhiteSpace(_farmingBlockedReasonKey) && string.IsNullOrWhiteSpace(_farmingBlockedReasonText))
        {
            return;
        }

        _farmingBlockedReasonKey = null;
        _farmingBlockedReasonText = null;
        var farmingOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Farming), StringComparison.OrdinalIgnoreCase));
        if (farmingOption is not null && _farmingBlockedPreviouslyEnabled)
        {
            farmingOption.IsEnabled = true;
        }

        _farmingBlockedPreviouslyEnabled = false;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void ClearHeroBlockedState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ClearHeroBlockedState);
            return;
        }

        if (string.IsNullOrWhiteSpace(_heroBlockedReasonKey) && string.IsNullOrWhiteSpace(_heroBlockedReasonText))
        {
            return;
        }

        _heroBlockedReasonKey = null;
        _heroBlockedReasonText = null;
        var heroOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Hero), StringComparison.OrdinalIgnoreCase));
        if (heroOption is not null && _heroBlockedPreviouslyEnabled)
        {
            heroOption.IsEnabled = true;
        }

        _heroBlockedPreviouslyEnabled = false;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void ClearBreweryBlockedState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ClearBreweryBlockedState);
            return;
        }

        if (string.IsNullOrWhiteSpace(_breweryBlockedReasonKey) && string.IsNullOrWhiteSpace(_breweryBlockedReasonText))
        {
            return;
        }

        _breweryBlockedReasonKey = null;
        _breweryBlockedReasonText = null;
        var breweryOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.BreweryCelebration), StringComparison.OrdinalIgnoreCase));
        if (breweryOption is not null && _breweryBlockedPreviouslyEnabled)
        {
            breweryOption.IsEnabled = true;
        }

        _breweryBlockedPreviouslyEnabled = false;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private static bool HasSmithyInVillageStatus(VillageStatus status)
    {
        return status.Buildings.Any(item =>
            item.Gid == 12
            || string.Equals(item.Name, "Smithy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Name, "Blacksmith", StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyTroopsAvailabilityFromVillageStatus(VillageStatus status)
    {
        var hasSmithy = HasSmithyInVillageStatus(status);
        if (hasSmithy)
        {
            if (string.Equals(_troopsBlockedReasonKey, TroopsBlockedReasonSmithyMissing, StringComparison.OrdinalIgnoreCase))
            {
                ClearTroopsBlockedState();
                AppendLog("Troops group re-enabled: Smithy detected after building refresh.");
            }

            return;
        }

        if (string.Equals(_troopsBlockedReasonKey, TroopsBlockedReasonAllDone, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(_troopsBlockedReasonKey, TroopsBlockedReasonSmithyMissing, StringComparison.OrdinalIgnoreCase))
        {
            SetTroopsBlockedState(TroopsBlockedReasonSmithyMissing, "Smithy missing");
        }
    }

    private int? ResolveConstructionGroupRemainingSeconds()
    {
        var remainingSeconds = _buildQueueRemainingSeconds > 0 ? _buildQueueRemainingSeconds : 0;
        if (_constructionInlineWaitUntilUtc > DateTimeOffset.UtcNow)
        {
            var inlineSeconds = (int)Math.Ceiling((_constructionInlineWaitUntilUtc - DateTimeOffset.UtcNow).TotalSeconds);
            remainingSeconds = Math.Max(remainingSeconds, Math.Max(0, inlineSeconds));
        }

        return remainingSeconds > 0 ? remainingSeconds : null;
    }

    private bool IsConstructionGroupReady()
    {
        return ResolveConstructionGroupRemainingSeconds() is not > 0;
    }

    private void ApplyConstructionInlineWait(TimeSpan waitDelay)
    {
        if (waitDelay <= TimeSpan.Zero)
        {
            return;
        }

        var waitUntilUtc = DateTimeOffset.UtcNow.Add(waitDelay);
        if (waitUntilUtc <= _constructionInlineWaitUntilUtc)
        {
            return;
        }

        _constructionInlineWaitUntilUtc = waitUntilUtc;
        UpdateAutomationLoopRunningIndicators();
    }

    private bool IsTroopsGroupBlocked()
    {
        return !string.IsNullOrWhiteSpace(_troopsBlockedReasonKey);
    }

    private bool IsFarmingGroupBlocked()
    {
        return !string.IsNullOrWhiteSpace(_farmingBlockedReasonKey);
    }

    private bool IsHeroGroupBlocked()
    {
        return !string.IsNullOrWhiteSpace(_heroBlockedReasonKey);
    }

    private bool IsBreweryGroupBlocked()
    {
        return !string.IsNullOrWhiteSpace(_breweryBlockedReasonKey);
    }

    private bool IsFunctionExecutionRunning(bool hasRunningQueueItems)
    {
        return hasRunningQueueItems
            || _autoQueueRunning
            || _uiBusy
            || !string.IsNullOrWhiteSpace(_activeFunctionDisplayName);
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
            AutomationLoopRunStateDot.Fill = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            AutomationLoopRunStateTextBlock.Text = "Running";
            AutomationLoopRunStateTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            return;
        }

        if (hasPausedQueueItems)
        {
            AutomationLoopRunStateDot.Fill = new SolidColorBrush(Color.FromRgb(217, 119, 6));
            AutomationLoopRunStateTextBlock.Text = "Paused";
            AutomationLoopRunStateTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(217, 119, 6));
            return;
        }

        AutomationLoopRunStateDot.Fill = new SolidColorBrush(Color.FromRgb(156, 163, 175));
        AutomationLoopRunStateTextBlock.Text = "Idle";
        AutomationLoopRunStateTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
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

        UpdateAutomationLoopSummaryText();
        UpdateAutomationLoopRunningIndicators();
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

        UpdateAutomationLoopSummaryText();
        UpdateAutomationLoopRunningIndicators();
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
        UpdateAutomationLoopSummaryText();
        UpdateAutomationLoopRunningIndicators();
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
