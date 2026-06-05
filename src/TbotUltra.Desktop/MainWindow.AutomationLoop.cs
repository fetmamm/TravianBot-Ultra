using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
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
        if (BlockIfSessionSleeping("Continuous loop"))
        {
            return;
        }

        var initialOptions = LoadBotOptions();
        _loopController.ClearLoopStopRequest();
        _loopController.ClearQueueStopRequest();
        _continuousLoopConstructionStatusNeedsSync = true;
        var token = _loopController.StartLoop("loop");

        StartLoopButton.Content = "Pause bot";
        StartLoopButton.IsEnabled = true;
        SetLoopIndicator(true);
        AppendLog($"Loop started. Interval={initialOptions.LoopIntervalSeconds}s");
        NotifySessionPacingAutomationStarted();

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
            item.Gid == 13
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

    private bool IsConstructionGroupReady(bool allowWorkerValidationForReadyItem = false)
    {
        // NOTE: the construction inline-wait is intentionally NOT a gate here anymore. With multi-village
        // rotation, blocking the whole Construction group on one village's resource wait would stall the
        // other villages. The deferred item already carries a future NextAttemptAt (set by
        // MarkQueueItemDeferred), so the per-village selection skips it and rotation moves on; the inline
        // wait remains only as the dashboard countdown (ResolveConstructionGroupRemainingSeconds).
        if (_buildQueueRemainingSeconds <= 0)
        {
            return true;
        }

        // With Travian Plus the official server allows a second active construction.
        // Do not block the whole Construction group just because the first timer is running;
        // the worker still performs the authoritative slot check before clicking.
        if (_travianPlusActive == true && _buildQueueActiveCount < 2)
        {
            return true;
        }

        // If a construction item is due now, do not let Desktop's cached build-queue snapshot be
        // the final authority. The Worker re-reads Travian Plus and the live construction slots
        // immediately before clicking, then defers with a real queue_wait_seconds value if full.
        // This prevents a stale/unknown Plus signal from blocking the possible second slot.
        if (allowWorkerValidationForReadyItem)
        {
            AppendLoopPickVerbose(
                $"[loop-pick:verbose] group=Construction allowing worker slot validation (active={_buildQueueActiveCount}, remaining={_buildQueueRemainingSeconds}, plus={_travianPlusActive?.ToString() ?? "unknown"})",
                "group:Construction:worker-slot-validation");
            return true;
        }

        return false;
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

    private bool ShouldKeepHeroAdventurePolling()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(ShouldKeepHeroAdventurePolling);
        }

        return _heroBlockedPreviouslyEnabled
            && string.Equals(_heroBlockedReasonKey, HeroBlockedReasonNoAdventures, StringComparison.OrdinalIgnoreCase);
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
}
