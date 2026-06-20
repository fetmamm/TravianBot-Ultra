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
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void LoadAutomationLoopTasks(BotOptions options)
    {
        var storedPreferences = LoadStoredAutomationLoopPreferencesForActiveAccount();
        var hasStoredEnabledGroups = storedPreferences.EnabledGroups is not null;
        var configured = hasStoredEnabledGroups
            ? storedPreferences.EnabledGroups!
            : NormalizeAutomationLoopGroupNames(options.ContinuousLoopGroups);
        if (!hasStoredEnabledGroups && configured.Count <= 0)
        {
            configured = NormalizeAutomationLoopGroupNames((options.LoopTasks ?? [])
                .Select(NormalizeLegacyLoopTaskName)
                .Select(QueueGroupCatalog.ResolveGroup)
                .Select(QueueGroupCatalog.GetKey));
        }

        var orderedNames = LoadConfiguredContinuousLoopGroupOrder();
        var visibleGroups = storedPreferences.VisibleGroups
            ?? LoadConfiguredDashboardVisibleGroups();
        BackfillTownHallVisibleGroupIfNew(visibleGroups);

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

                // NPC trade is controlled by the Auto settings master toggle + per-village choice, not as
                // an Automation Loop group — never show it in the loop list or Function list.
                if (group == QueueGroup.NpcTrade)
                {
                    continue;
                }

                _automationLoopTasks.Add(new LoopTaskOption
                {
                    TaskName = groupKey,
                    Title = QueueGroupCatalog.GetTitle(group),
                    Description = QueueGroupCatalog.GetDescription(group),
                    // Before login the dashboard must show nothing running: keep every group toggle OFF until
                    // the user logs in. The real per-village/account state is applied on login
                    // (ApplyAutomationLoopGroupsForSelectedVillage). The configured set still feeds
                    // _defaultEnabledGroupKeys below so that apply has the right default.
                    IsEnabled = _isLoggedIn && configured.Contains(groupKey, StringComparer.OrdinalIgnoreCase),
                    IsVisible = visibleGroups.Contains(groupKey, StringComparer.OrdinalIgnoreCase),
                    StateText = "Idle",
                    DetailText = "No queued task.",
                });
            }

            UpdateAutomationLoopOrders();
            // Snapshot the configured enabled set as the global default for villages with no per-village
            // override yet (so a new village inherits the account's configured groups). Derived from the
            // configured set (not the cards' IsEnabled) so the login gate above does not blank the default.
            _defaultEnabledGroupKeys = _automationLoopTasks
                .Where(item => configured.Contains(item.TaskName, StringComparer.OrdinalIgnoreCase))
                .Select(item => item.TaskName)
                .ToList();
            RefreshAutomationLoopDashboardUi();
            SyncTeutonsOnlyAutomationGroups(ResolveStoredTroopTrainingTribe());
        }
        finally
        {
            _suppressAutomationLoopConfigWrite = false;
        }
    }

    private (List<string>? EnabledGroups, List<string>? VisibleGroups) LoadStoredAutomationLoopPreferencesForActiveAccount()
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (string.IsNullOrWhiteSpace(accountName)
                || !_accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl())
                || analysis is null)
            {
                return (null, null);
            }

            return (
                analysis.AutomationLoopEnabledGroups is null ? null : NormalizeAutomationLoopGroupNames(analysis.AutomationLoopEnabledGroups),
                analysis.AutomationLoopVisibleGroups is null ? null : NormalizeAutomationLoopGroupNames(analysis.AutomationLoopVisibleGroups));
        }
        catch
        {
            return (null, null);
        }
    }

    private void PersistAutomationLoopPreferencesForActiveAccount(
        IReadOnlyList<string> enabledGroupNames,
        IReadOnlyList<string> visibleGroupNames)
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (string.IsNullOrWhiteSpace(accountName))
            {
                return;
            }

            var serverUrl = GetActiveAccountServerUrl();
            _accountAnalysisStore.TryLoad(accountName, out var existing, serverUrl);
            var snapshot = new AccountAnalysisSnapshot(
                SchemaVersion: AccountAnalysisConstants.CurrentSchemaVersion,
                AnalyzedAtUtc: DateTimeOffset.UtcNow,
                AccountName: string.IsNullOrWhiteSpace(existing?.AccountName) ? accountName : existing.AccountName,
                ServerUrl: string.IsNullOrWhiteSpace(existing?.ServerUrl) ? serverUrl ?? string.Empty : existing.ServerUrl,
                Tribe: string.IsNullOrWhiteSpace(existing?.Tribe) ? ResolveStoredTroopTrainingTribe() : existing.Tribe,
                GoldClubEnabled: existing?.GoldClubEnabled ?? false,
                BuildingCatalog: existing?.BuildingCatalog ?? [],
                AutoCelebrationEnabled: existing?.AutoCelebrationEnabled,
                AutomationLoopEnabledGroups: enabledGroupNames.ToList(),
                AutomationLoopVisibleGroups: visibleGroupNames.ToList());
            _accountAnalysisStore.Save(snapshot);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save automation loop preferences: {ex.Message}");
        }
    }

    private static List<string> NormalizeAutomationLoopGroupNames(IEnumerable<string>? names)
    {
        return (names ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Where(name => QueueGroupCatalog.TryParse(name, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void UpdateAutomationLoopOrders()
    {
        var visibleOrder = 1;
        foreach (var item in _automationLoopTasks)
        {
            if (!item.IsVisible)
            {
                continue;
            }

            item.Order = visibleOrder++;
        }
    }

    private void RefreshAutomationLoopDashboardUi()
    {
        UpdateAutomationLoopOrders();
        _automationLoopTasksView?.Refresh();
        UpdateAutomationLoopSummaryText();
        UpdateAutomationLoopRunningIndicators();
        UpdateNextTaskUi();
    }

    // Top-bar "Next task": shows what the bot is running, what it will pick next, or what it is waiting for
    // (with a countdown). Read-only — uses the live loop selector in preview mode so it mirrors the real
    // scheduling exactly without advancing rotation/keys or mutating the queue.
    private void UpdateNextTaskUi()
    {
        if (NextTaskTextBlock is null)
        {
            return;
        }

        NextTaskTextBlock.Text = ResolveNextTaskDisplayText();
    }

    private string ResolveNextTaskDisplayText()
    {
        if (!_isLoggedIn)
        {
            return "Idle";
        }

        IReadOnlyList<QueueItem> items;
        try
        {
            items = GetQueueSnapshotForUi();
        }
        catch
        {
            return "-";
        }

        var running = items.FirstOrDefault(item => item.Status == QueueStatus.Running);
        if (running is not null)
        {
            return $"Running: {DescribeNextTask(running)}";
        }

        // Exact: the item the live loop would pick right now (preview = no side effects).
        var next = SelectNextQueueItemForContinuousLoop(preview: true);
        if (next is not null)
        {
            return $"Next: {DescribeNextTask(next)}";
        }

        // Nothing ready now — surface the soonest eligible deferred item and its countdown so the user sees
        // what the loop is waiting for and when it retries.
        var now = DateTimeOffset.UtcNow;
        var soonest = items
            .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
            .Where(IsQueueItemVillageEnabled)
            .Where(IsQueueItemGroupEnabledForItsVillage)
            .OrderBy(item => item.NextAttemptAt)
            .FirstOrDefault();
        if (soonest is not null)
        {
            return $"Waiting {FormatNextTaskCountdown(soonest.NextAttemptAt - now)}: {DescribeNextTask(soonest)}";
        }

        return "Nothing queued";
    }

    private string DescribeNextTask(QueueItem item)
    {
        var name = BuildQueueDisplayName(item);
        var village = NormalizeVillageName(GetQueueItemVillageName(item));
        return village is null ? name : $"{name} ({village})";
    }

    private static string FormatNextTaskCountdown(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    private void TickAutomationLoopCountdowns()
    {
        var changed = false;
        var reachedZero = false;
        foreach (var item in _automationLoopTasks)
        {
            if (!item.TickOneSecond())
            {
                continue;
            }

            changed = true;
            if (item.RemainingSeconds == 0)
            {
                reachedZero = true;
            }
        }

        if (changed && reachedZero)
        {
            UpdateAutomationLoopRunningIndicators();
        }
    }

    private static bool AutomationLoopTaskFilter(object item)
    {
        return item is LoopTaskOption option && option.IsVisible;
    }

    private void UpdateAutomationLoopSummaryText()
    {
        // The summary text was removed from the Auto loop box; keep the column layout in sync
        // since callers still expect this to refresh the loop list after group changes.
        UpdateAutomationLoopColumns();
    }

    private void UpdateAutomationLoopColumns()
    {
        if (AutomationLoopListBox is null)
        {
            return;
        }

        // Always a single vertical column now that the Auto loop box fills the column height: groups
        // stack downward (and scroll if needed) instead of wrapping into a second column after 4.
        var factory = new FrameworkElementFactory(typeof(VerticalFirstUniformGrid));
        factory.SetValue(VerticalFirstUniformGrid.ColumnsProperty, 1);
        AutomationLoopListBox.ItemsPanel = new ItemsPanelTemplate(factory);
    }

    private void UpdateAutomationLoopRunningIndicators()
    {
        // Before login the dashboard shows nothing: no group runs and persisted (deferred) queue items must
        // not surface their countdown timers. Render every card idle with no timer until the user logs in.
        if (!_isLoggedIn)
        {
            foreach (var item in _automationLoopTasks)
            {
                item.IsRunning = false;
                item.IsBlocked = false;
                item.StateText = "Idle";
                item.DetailText = "No queued task.";
                item.QueuedCount = 0;
                item.RemainingSeconds = null;
            }

            return;
        }

        var isRunning = (_loopTask is not null && !_loopTask.IsCompleted) || _autoQueueRunning;
        var hasPausedQueueItems = false;
        string? queueRunningTaskName = null;
        IReadOnlyList<QueueItem> queueItems = [];
        try
        {
            queueItems = GetQueueSnapshotForUi();
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
        var now = DateTimeOffset.UtcNow;

        var disabledInvalidResourceTransfer = false;
        var disabledInvalidReinforcements = false;
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

            // Filter to the selected village (keeping village-less/global items) so each group card shows
            // THIS village's queued count, deferred timer and state — different villages have different
            // construction timers etc.
            var groupItems = queueItems
                .Where(entry => entry.Group == group && IsQueueItemForSelectedVillageOrGlobal(entry))
                .ToList();
            var pendingCount = groupItems.Count(entry => entry.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);
            var deferred = groupItems
                .Where(entry => entry.Status == QueueStatus.Pending && entry.NextAttemptAt > now)
                .OrderBy(entry => entry.NextAttemptAt)
                .FirstOrDefault();
            var runningItem = groupItems.FirstOrDefault(entry => entry.Status == QueueStatus.Running);
            var paused = groupItems.Any(entry => entry.Status == QueueStatus.Paused);
            var smithyUpgradeWaitSeconds = group == QueueGroup.Troops
                ? ResolveSmithyUpgradeGroupRemainingSeconds()
                : (int?)null;
            var troopTrainingWaitSeconds = group == QueueGroup.TroopTraining
                ? _troopTrainingViewModel.ResolveGroupRemainingSeconds()
                : (int?)null;
            var breweryCelebrationWaitSeconds = group == QueueGroup.BreweryCelebration
                ? _troopTrainingViewModel.ResolveBreweryCelebrationGroupRemainingSeconds()
                : (int?)null;
            var hasLiveSmithyWait = group == QueueGroup.Troops && smithyUpgradeWaitSeconds is > 0;

            item.QueuedCount = pendingCount;
            item.IsRunning = runningGroup.HasValue && runningGroup.Value == group;
            item.IsBlocked = false;
            item.BlockedText = "Blocked";
            if ((runningItem is not null || item.IsRunning) && !hasLiveSmithyWait)
            {
                item.StateText = "Running";
                item.DetailText = runningItem is not null
                    ? BuildQueueDisplayName(runningItem)
                    : "Coordinator active.";
                item.RemainingSeconds = null;
            }
            else if (group == QueueGroup.Construction && !item.IsEnabled)
            {
                item.StateText = "Disabled";
                item.DetailText = pendingCount > 0 ? $"{pendingCount} queued." : "No queued task.";
                item.RemainingSeconds = null;
            }
            else if (group == QueueGroup.Construction && paused)
            {
                item.StateText = "Paused";
                item.DetailText = "Contains paused task.";
                item.RemainingSeconds = null;
            }
            else if (group == QueueGroup.Construction)
            {
                ApplyConstructionLoopCardState(item, groupItems, pendingCount, now);
            }
            else if (deferred is not null || troopTrainingWaitSeconds is > 0 || breweryCelebrationWaitSeconds is > 0 || hasLiveSmithyWait)
            {
                item.StateText = hasLiveSmithyWait && (runningItem is not null || item.IsRunning)
                    ? "Running"
                    : "Waiting";
                if (deferred is not null)
                {
                    item.DetailText = $"Next try {FormatQueueServerTime(deferred.NextAttemptAt)}";
                    item.RemainingSeconds = Math.Max(0, (int)Math.Ceiling((deferred.NextAttemptAt - now).TotalSeconds));
                }
                else if (hasLiveSmithyWait)
                {
                    var smithyUpgradeCount = ResolveSmithyUpgradeActiveCount();
                    item.DetailText = smithyUpgradeCount > 1
                        ? $"Smithy upgrades active ({smithyUpgradeCount})."
                        : "Smithy upgrade active.";
                    item.RemainingSeconds = smithyUpgradeWaitSeconds;
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
            else if (group == QueueGroup.ResourceTransfer && !CanRunResourceTransfer(LoadBotOptions(), out var resourceTransferReason))
            {
                if (item.IsEnabled && _resourceTransferVillages.Count > 0)
                {
                    item.IsEnabled = false;
                    disabledInvalidResourceTransfer = true;
                }

                item.StateText = "Disabled";
                item.DetailText = resourceTransferReason;
                item.RemainingSeconds = null;
                item.IsBlocked = true;
                item.BlockedText = "Setup needed";
            }
            else if (group == QueueGroup.Reinforcements && !CanRunReinforcements(LoadBotOptions(), out var reinforcementsReason))
            {
                if (item.IsEnabled && _reinforcementVillages.Count > 0)
                {
                    item.IsEnabled = false;
                    disabledInvalidReinforcements = true;
                }

                item.StateText = "Disabled";
                item.DetailText = reinforcementsReason;
                item.RemainingSeconds = null;
                item.IsBlocked = true;
                item.BlockedText = "Setup needed";
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
            else if (group == QueueGroup.NpcTrade && !_troopTrainingViewModel.IsAnyNpcTradeEnabled)
            {
                item.StateText = "Disabled";
                item.DetailText = "NPC trade is off.";
                item.RemainingSeconds = null;
            }
            else if (group == QueueGroup.NpcTrade)
            {
                item.StateText = "Ready";
                item.DetailText = _troopTrainingViewModel.NpcTradeStatusText;
                item.RemainingSeconds = null;
            }
            else if (group == QueueGroup.ResourceTransfer)
            {
                item.StateText = "Ready";
                item.DetailText = "Resource transfer configured.";
                item.RemainingSeconds = null;
            }
            else if (group == QueueGroup.Reinforcements)
            {
                item.StateText = "Ready";
                item.DetailText = "Reinforcements configured.";
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

        if (disabledInvalidResourceTransfer || disabledInvalidReinforcements)
        {
            UpdateAutomationLoopSummaryText();
            PersistAutomationLoopTasksToConfig();
        }

        if (isRunning)
        {
            SetAutomationLoopRunState(
                ThemeColors.Get("SuccessBrush"),
                "Running",
                ThemeColors.Get("SuccessBrush"));
            return;
        }

        if (hasPausedQueueItems)
        {
            SetAutomationLoopRunState(
                ThemeColors.Get("AmberBrush"),
                "Paused",
                ThemeColors.Get("AmberBrush"));
            return;
        }

        SetAutomationLoopRunState(
            ThemeColors.Get("BorderMutedBrush"),
            "Idle",
            ThemeColors.Get("TextSubtleBrush"));
    }

    private void ApplyConstructionLoopCardState(
        LoopTaskOption item,
        IReadOnlyList<QueueItem> groupItems,
        int pendingCount,
        DateTimeOffset now)
    {
        var selectedVillage = NormalizeVillageName(GetSelectedVillageName());
        VillageStatus? status = null;
        if (selectedVillage is not null)
        {
            _villageStatusCacheByName.TryGetValue(selectedVillage, out status);
        }

        var snapshot = ConstructionQueueState.ResolveSnapshot(status, now);
        var availability = ConstructionQueueState.ResolveAvailability(status, _travianPlusActive, now);
        var readyItem = SelectNextConstructionQueueItem(groupItems, now, out _, preview: true);
        if (readyItem is not null)
        {
            item.StateText = "Ready";
            item.DetailText = snapshot.ActiveCount > 0
                ? $"Travian queue: {snapshot.ActiveCount} active; build slot available."
                : pendingCount == 1 ? "1 queued." : $"{pendingCount} queued.";
            item.RemainingSeconds = null;
            return;
        }

        if (availability == ConstructionQueueAvailability.Full)
        {
            item.StateText = "Waiting";
            item.DetailText = "Travian build queue full.";
            item.RemainingSeconds = snapshot.RemainingSeconds;
            return;
        }

        var deferred = groupItems
            .Where(entry => entry.Status == QueueStatus.Pending && entry.NextAttemptAt > now)
            .FirstOrDefault();
        if (deferred is not null)
        {
            var retryLabel = FormatQueueServerTime(deferred.NextAttemptAt);
            item.StateText = "Waiting";
            item.DetailText = ConstructionQueueState.ResolveDeferReason(deferred) switch
            {
                ConstructionDeferReason.QueueFull =>
                    $"Travian build queue status not confirmed. Next check {retryLabel}",
                ConstructionDeferReason.InProgress =>
                    $"Target already queued in Travian. Next check {retryLabel}",
                ConstructionDeferReason.Resources =>
                    $"Waiting for resources. Next try {retryLabel}",
                ConstructionDeferReason.Requirements =>
                    $"Waiting for building requirements. Next try {retryLabel}",
                _ => $"Waiting to retry. Next try {retryLabel}",
            };
            item.RemainingSeconds = Math.Max(
                0,
                (int)Math.Ceiling((deferred.NextAttemptAt - now).TotalSeconds));
            return;
        }

        if (snapshot.Knowledge == ConstructionQueueKnowledge.Active)
        {
            item.StateText = "Waiting";
            item.DetailText = "Travian build queue active.";
            item.RemainingSeconds = snapshot.RemainingSeconds;
            return;
        }

        item.StateText = pendingCount > 0 ? "Ready" : "Idle";
        item.DetailText = pendingCount > 0
            ? pendingCount == 1 ? "1 queued." : $"{pendingCount} queued."
            : "No queued task.";
        item.RemainingSeconds = null;
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
                .Where(item => item.IsEnabled || (
                    string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Hero), StringComparison.OrdinalIgnoreCase)
                    && ShouldKeepHeroAdventurePolling()))
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

            PersistAutomationLoopPreferencesForActiveAccount(enabledGroupNames, visibleGroupNames);

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
            if (!config.ContainsKey(DashboardVisibleGroupsConfigKey))
            {
                return QueueGroupCatalog.AllGroups
                    .Select(QueueGroupCatalog.GetKey)
                    .ToList();
            }

            var configuredVisible = (config[DashboardVisibleGroupsConfigKey] as JsonArray ?? new JsonArray())
                .Select(node => node?.ToString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Where(name => QueueGroupCatalog.TryParse(name, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return configuredVisible;
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

            foreach (var groupKey in GetDefaultContinuousLoopGroupOrder())
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
            return GetDefaultContinuousLoopGroupOrder();
        }
    }

    private static List<string> GetDefaultContinuousLoopGroupOrder()
    {
        // Explicit default priority order shown on the dashboard automation loop:
        // 1 Auto celebration, 2 Hero, then the remaining groups in their existing relative
        // order, with NPC Trade landing at position 8.
        var explicitOrder = new[]
        {
            QueueGroup.BreweryCelebration,
            QueueGroup.TownHallCelebration,
            QueueGroup.Hero,
            QueueGroup.Construction,
            QueueGroup.Troops,
            QueueGroup.Farming,
            QueueGroup.TroopTraining,
            QueueGroup.ResourceTransfer,
            QueueGroup.NpcTrade,
            QueueGroup.Reinforcements,
        };

        return explicitOrder
            .Select(QueueGroupCatalog.GetKey)
            // Append any group not covered above (defensive against future additions).
            .Concat(QueueGroupCatalog.AllGroups
                .Where(group => !explicitOrder.Contains(group))
                .Select(QueueGroupCatalog.GetKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private void BackfillTownHallVisibleGroupIfNew(List<string> visibleGroups)
    {
        var townHallKey = QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration);
        if (visibleGroups.Contains(townHallKey, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var config = _botConfigStore.Load();
            var configuredOrder = (config[ContinuousLoopGroupOrderConfigKey] as JsonArray ?? new JsonArray())
                .Select(node => node?.ToString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .ToList();
            if (configuredOrder.Contains(townHallKey, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
        }
        catch
        {
            // Missing/unreadable order means this is effectively a pre-Town-Hall preference set.
        }

        visibleGroups.Add(townHallKey);
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

        var wasEnabled = option.IsEnabled;
        option.IsEnabled = toggle.IsChecked == true;
        if (wasEnabled
            && !option.IsEnabled
            && string.Equals(option.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Hero), StringComparison.OrdinalIgnoreCase))
        {
            var removedHeroTimers = ClearHeroManageQueueItems();
            if (removedHeroTimers > 0)
            {
                AppendLog("Hero group disabled. Cleared cached hero timer so it will be read again when re-enabled.");
            }
        }

        if (!option.IsEnabled
            && QueueGroupCatalog.TryParse(option.TaskName, out var disabledGroup)
            && ContinuousRunToggleButton?.IsChecked == true
            && GetActiveContinuousLoopGroup() == disabledGroup
            && _loopTask is not null
            && !_loopTask.IsCompleted)
        {
            _restartContinuousLoopAfterStop = HasEnabledContinuousLoopGroupsExcept(disabledGroup);
            _loopController.RequestLoopStop();
            _loopController.CancelLoop();
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
        else if (!wasEnabled
            && option.IsEnabled
            && string.Equals(option.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Construction), StringComparison.OrdinalIgnoreCase))
        {
            // Toggling Construction off then on resets any resource-wait timer so the loop re-reads
            // and restarts the function instead of waiting out the old timer.
            ResetConstructionBuildQueueTimerForManualRefresh();
            ResetDeferredConstructionWaitsNow("construction group re-enabled");
        }

        var continuousLoopWillHandle = option.IsEnabled
            && ContinuousRunToggleButton?.IsChecked == true
            && _loopTask is not null
            && !_loopTask.IsCompleted;

        if (continuousLoopWillHandle)
        {
            Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
            AppendLog($"{option.Title} group enabled. Continuous loop will check it now.");
        }
        else if (option.IsEnabled
            && string.Equals(option.TaskName, QueueGroupCatalog.GetKey(QueueGroup.BreweryCelebration), StringComparison.OrdinalIgnoreCase))
        {
            // Group toggled on while the continuous loop isn't running. The cached
            // AutoCelebrationRemainingSeconds keeps ticking across toggle off/on (the
            // 1Hz clock timer ticks it regardless of group state), so we don't lose the
            // countdown. But we still want a fresh authoritative read from the server
            // to verify the cached value — the running celebration may have ended while
            // the group was off.
            TriggerBreweryCelebrationVerificationRefresh();
        }

        RefreshAutomationLoopDashboardUi();
        PersistAutomationLoopTasksToConfig();
        // Save these group toggles as the selected village's per-village override.
        SaveAutomationLoopGroupsForSelectedVillage();
    }

    private void TriggerBreweryCelebrationVerificationRefresh()
    {
        if (!_isLoggedIn || !_browserSessionLikelyOpen)
        {
            // No browser yet — nothing to read. The post-login flow already triggers a
            // refresh when the user signs in, so silently skipping here is correct.
            return;
        }

        AppendLog("Brewery celebration: group re-enabled, verifying status against server.");
        _backgroundTasks.Run(async cancellationToken =>
        {
            try
            {
                var options = ApplySelectedVillageToOptions(LoadBotOptions());
                await RefreshBreweryCelebrationStatusAsync(options, _lastBuildingStatus, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"Brewery celebration verification refresh failed: {ex.Message}");
            }
        });
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
