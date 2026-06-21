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
        LogConservativeAutomationWarnings(initialOptions);
        NotifySessionPacingAutomationStarted();

        _loopTask = Task.Run(() => RunContinuousLoopAsync(token), token);
        _backgroundTasks.Track(_loopTask);
        _ = TrackLoopCompletionAsync(_loopTask);
    }

    private IReadOnlyList<QueueItem> GetContinuousLoopRelevantQueueItems()
    {
        var enabledGroups = GetContinuousLoopConsideredGroupsInOrder().ToHashSet();
        if (enabledGroups.Count <= 0)
        {
            return [];
        }

        return _botService.GetQueueItemsForDisplay()
            .Where(item => enabledGroups.Contains(item.Group))
            .Where(IsQueueItemAllowedByAutomationSettings)
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
        => SetAutomationGroupBlockedState(
            QueueGroup.Troops,
            reasonKey,
            reasonText,
            (key, text) =>
            {
                _troopsBlockedReasonKey = key;
                _troopsBlockedReasonText = text;
            },
            value => _troopsBlockedPreviouslyEnabled = value);

    private void SetFarmingBlockedState(string reasonKey, string reasonText)
        => SetAutomationGroupBlockedState(
            QueueGroup.Farming,
            reasonKey,
            reasonText,
            (key, text) =>
            {
                _farmingBlockedReasonKey = key;
                _farmingBlockedReasonText = text;
            },
            value => _farmingBlockedPreviouslyEnabled = value);

    private void SetHeroBlockedState(string reasonKey, string reasonText)
        => SetAutomationGroupBlockedState(
            QueueGroup.Hero,
            reasonKey,
            reasonText,
            (key, text) =>
            {
                _heroBlockedReasonKey = key;
                _heroBlockedReasonText = text;
            },
            value => _heroBlockedPreviouslyEnabled = value);

    private void SetBreweryBlockedState(string reasonKey, string reasonText)
        => SetAutomationGroupBlockedState(
            QueueGroup.BreweryCelebration,
            reasonKey,
            reasonText,
            (key, text) =>
            {
                _breweryBlockedReasonKey = key;
                _breweryBlockedReasonText = text;
            },
            value => _breweryBlockedPreviouslyEnabled = value);

    private void ClearTroopsBlockedState()
        => ClearAutomationGroupBlockedState(
            QueueGroup.Troops,
            () => _troopsBlockedReasonKey,
            () => _troopsBlockedReasonText,
            (key, text) =>
            {
                _troopsBlockedReasonKey = key;
                _troopsBlockedReasonText = text;
            },
            () => _troopsBlockedPreviouslyEnabled,
            value => _troopsBlockedPreviouslyEnabled = value);

    private void ClearFarmingBlockedState()
        => ClearAutomationGroupBlockedState(
            QueueGroup.Farming,
            () => _farmingBlockedReasonKey,
            () => _farmingBlockedReasonText,
            (key, text) =>
            {
                _farmingBlockedReasonKey = key;
                _farmingBlockedReasonText = text;
            },
            () => _farmingBlockedPreviouslyEnabled,
            value => _farmingBlockedPreviouslyEnabled = value);

    private void ClearHeroBlockedState()
        => ClearAutomationGroupBlockedState(
            QueueGroup.Hero,
            () => _heroBlockedReasonKey,
            () => _heroBlockedReasonText,
            (key, text) =>
            {
                _heroBlockedReasonKey = key;
                _heroBlockedReasonText = text;
            },
            () => _heroBlockedPreviouslyEnabled,
            value => _heroBlockedPreviouslyEnabled = value);

    private void ClearBreweryBlockedState()
        => ClearAutomationGroupBlockedState(
            QueueGroup.BreweryCelebration,
            () => _breweryBlockedReasonKey,
            () => _breweryBlockedReasonText,
            (key, text) =>
            {
                _breweryBlockedReasonKey = key;
                _breweryBlockedReasonText = text;
            },
            () => _breweryBlockedPreviouslyEnabled,
            value => _breweryBlockedPreviouslyEnabled = value);

    private void SetAutomationGroupBlockedState(
        QueueGroup group,
        string reasonKey,
        string reasonText,
        Action<string?, string?> setReason,
        Action<bool> setPreviouslyEnabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetAutomationGroupBlockedState(group, reasonKey, reasonText, setReason, setPreviouslyEnabled));
            return;
        }

        var option = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(group), StringComparison.OrdinalIgnoreCase));
        if (option is not null)
        {
            setPreviouslyEnabled(option.IsEnabled);
            option.IsEnabled = false;
        }

        setReason(reasonKey, reasonText);
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void ClearAutomationGroupBlockedState(
        QueueGroup group,
        Func<string?> getReasonKey,
        Func<string?> getReasonText,
        Action<string?, string?> setReason,
        Func<bool> wasPreviouslyEnabled,
        Action<bool> setPreviouslyEnabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ClearAutomationGroupBlockedState(
                group,
                getReasonKey,
                getReasonText,
                setReason,
                wasPreviouslyEnabled,
                setPreviouslyEnabled));
            return;
        }

        if (string.IsNullOrWhiteSpace(getReasonKey()) && string.IsNullOrWhiteSpace(getReasonText()))
        {
            return;
        }

        setReason(null, null);
        var option = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(group), StringComparison.OrdinalIgnoreCase));
        if (option is not null && wasPreviouslyEnabled())
        {
            option.IsEnabled = true;
        }

        setPreviouslyEnabled(false);
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

    private bool IsConstructionGroupReady(bool allowWorkerValidationForReadyItem = false, bool suppressLog = false)
    {
        // NOTE: the construction inline-wait is intentionally NOT a gate here anymore. With multi-village
        // rotation, blocking the whole Construction group on one village's resource wait would stall the
        // other villages. The deferred item already carries a future NextAttemptAt (set by
        // MarkQueueItemDeferred), so the per-village selection skips it and rotation moves on.
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
            if (!suppressLog)
            {
                AppendLoopPickVerbose(
                    $"[loop-pick:verbose] group=Construction allowing worker slot validation (active={_buildQueueActiveCount}, remaining={_buildQueueRemainingSeconds}, plus={_travianPlusActive?.ToString() ?? "unknown"})",
                    "group:Construction:worker-slot-validation");
            }

            return true;
        }

        return false;
    }

    // A construction task just deferred. Refresh the selected village's indicators; the card resolves
    // the Travian queue and the task's defer reason separately.
    private void ApplyConstructionInlineWait(TimeSpan waitDelay)
    {
        _ = waitDelay;
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
