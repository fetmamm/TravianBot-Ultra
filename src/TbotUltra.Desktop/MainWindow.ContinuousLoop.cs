using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static readonly TimeSpan LoopPickVerboseThrottle = TimeSpan.FromSeconds(30);
    private readonly object _loopPickVerboseLogGate = new();
    private readonly Dictionary<string, DateTimeOffset> _loopPickVerboseLogAtByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _constructionQueueSummaryByVillage = new(StringComparer.OrdinalIgnoreCase);

    // The village the AutoQueue drain is currently working through. Rotation drains one village's ready
    // tasks before advancing to the next; reset at the start of each run so a fresh run starts cleanly.
    private string? _autoQueueRotationVillageKey;

    // The village the continuous loop is currently draining construction for. Same rotation rule as the
    // AutoQueue path: finish one village's construction (in strict order) before moving to the next; if
    // the current village's next construction is deferred (e.g. waiting for resources), rotate onward so
    // a stalled village never blocks the others. Reset when the loop starts.
    private string? _continuousConstructionRotationVillageKey;

    // Per-village rotation key for the OTHER continuous-loop groups (troop-training, smithy, and the
    // global groups). Same drain-then-rotate rule as construction, but kept per group so each group
    // rotates independently. Construction keeps its dedicated field above. Reset when the loop starts.
    private readonly Dictionary<QueueGroup, string?> _continuousGroupRotationVillageKeys = new();

    private string? GetContinuousGroupRotationVillageKey(QueueGroup group)
        => _continuousGroupRotationVillageKeys.TryGetValue(group, out var key) ? key : null;

    private void SetContinuousGroupRotationVillageKey(QueueGroup group, string? key)
        => _continuousGroupRotationVillageKeys[group] = key;

    private async Task TriggerQueueAutoRunAsync()
    {
        if (IsSessionSleeping)
        {
            return;
        }

        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            return;
        }

        if (_loopController.QueueStopRequested)
        {
            return;
        }

        var gateLease = await _loopController.TryAcquireQueueAutoRunGateAsync(_loopController.QueueAutoRunRootToken);
        if (gateLease is null)
        {
            return;
        }

        var autoQueueTask = Task.Run(async () =>
        {
            var autoToken = _loopController.StartAutoQueueRun();
            try
            {
                _autoQueueRunning = true;
                UpdateExecutionStateIndicatorOnUiThread();
                await ExecuteQueuedItemsNowAsync(autoToken);
            }
            finally
            {
                _autoQueueRunning = false;
                _loopController.DisposeAutoQueueRun();
                UpdateExecutionStateIndicatorOnUiThread();
                gateLease.Dispose();
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_startContinuousLoopAfterQueueStop
                        && ContinuousRunToggleButton?.IsChecked == true
                        && _isLoggedIn
                        && !_uiBusy
                        && !_autoQueueRunning
                        && (_loopTask is null || _loopTask.IsCompleted))
                    {
                        _startContinuousLoopAfterQueueStop = false;
                        StartContinuousLoopRunner();
                        return;
                    }

                    _startContinuousLoopAfterQueueStop = false;
                });
            }
        });
        _backgroundTasks.Track(autoQueueTask);
    }

    private void TriggerQueueAutoRunFromEnqueue()
    {
        if (IsSessionSleeping)
        {
            UpdateExecutionStateIndicator();
            AppendLog("Queued item will run after session sleep ends.");
            return;
        }

        // When continuous-run is toggled ON, queued items must NOT auto-start from an enqueue.
        // They may only begin when the user presses "Start bot", or be picked up by a runner
        // that is already executing (the existing ExecuteQueuedItemsNowAsync / loop will see
        // new items on its next iteration).
        if (ContinuousRunToggleButton?.IsChecked == true)
        {
            var alreadyRunning = _autoQueueRunning
                || (_loopTask is not null && !_loopTask.IsCompleted);

            if (!alreadyRunning)
            {
                return;
            }
        }

        if (!HasQueueAutoRunEligibleWork())
        {
            UpdateExecutionStateIndicator();
            return;
        }

        _loopController.ClearQueueStopRequest();
        _ = TriggerQueueAutoRunAsync();
    }

    private bool HasQueueAutoRunEligibleWork()
    {
        try
        {
            return _botService.GetQueueItemsForDisplay()
                .Any(item => item.Status == QueueStatus.Pending && IsQueueItemAllowedByAutomationSettings(item));
        }
        catch
        {
            return true;
        }
    }

    private void ResumePausedQueueItems()
    {
        try
        {
            var pausedItems = _botService
                .GetQueueItemsForDisplay()
                .Where(item => item.Status == QueueStatus.Paused)
                .ToList();

            foreach (var item in pausedItems)
            {
                _botService.ResumeQueueItem(item.Id);
            }

            if (pausedItems.Count > 0)
            {
                RefreshQueueUi();
                AppendLog($"Resumed {pausedItems.Count} paused queue item(s).");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not resume paused queue items: {ex.Message}");
        }
    }

    private IReadOnlyList<QueueGroup> GetContinuousLoopEnabledGroupsInOrder()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(GetContinuousLoopEnabledGroupsInOrder);
        }

        return _automationLoopTasks
            .Where(item => item.IsEnabled)
            .Select(item => QueueGroupCatalog.TryParse(item.TaskName, out var group) ? group : (QueueGroup?)null)
            .Where(group => group.HasValue)
            .Select(group => group!.Value)
            .ToList();
    }

    // Union of automation-loop groups enabled across the selected village (live UI toggles) plus every
    // other enabled village (persisted per-village overrides, falling back to the account default). Used
    // only to DECIDE WHICH ALREADY-QUEUED ITEMS to consider — never to GENERATE runtime items, which stay
    // scoped to the selected village. The per-item IsQueueItemGroupEnabledForItsVillage filter still gates
    // each item to its own village, so a group enabled only in village B won't run where it is off; this
    // just stops a group being skipped entirely because the *selected* village happens to have it off.
    private IReadOnlyList<QueueGroup> GetContinuousLoopConsideredGroupsInOrder()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(GetContinuousLoopConsideredGroupsInOrder);
        }

        var ordered = new List<QueueGroup>(GetContinuousLoopEnabledGroupsInOrder());
        var seen = ordered.ToHashSet();

        foreach (var (_, enabledGroups) in _villageSettingsStore.GetEnabledVillagesGroups())
        {
            foreach (var key in enabledGroups ?? VillageSettingsStore.DefaultEnabledGroups)
            {
                if (QueueGroupCatalog.TryParse(key, out var group) && seen.Add(group))
                {
                    ordered.Add(group);
                }
            }
        }

        return ordered;
    }

    private async Task EnsureContinuousLoopRuntimeItemsAsync(BotOptions options)
    {
        var enabledGroups = GetContinuousLoopEnabledGroupsInOrder();
        // Troop-training, smithy and brewery are generated PER VILLAGE (see below), so the loop must keep
        // running when only a non-selected village has those groups on. Hero/farming/transfer/reinforcements
        // stay account-global and keep gating on the selected village's toggles via `enabledGroups`.
        var consideredGroups = GetContinuousLoopConsideredGroupsInOrder();
        // Hero is global (one hero). Poll/queue adventures when Hero is on for ANY enabled village (the
        // considered/union set), not just the selected one — otherwise Hero never runs while a village that
        // has it OFF is selected even though the hero-home village has it ON.
        var heroPollingEnabled = consideredGroups.Contains(QueueGroup.Hero) || ShouldKeepHeroAdventurePolling();
        if (consideredGroups.Count <= 0 && !heroPollingEnabled)
        {
            return;
        }

        var activeItems = _botService.GetQueueItemsForDisplay()
            .Where(item => item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused)
            .ToList();

        bool HasActiveTask(string taskName)
        {
            return activeItems.Any(item =>
                string.Equals(item.TaskName, taskName, StringComparison.OrdinalIgnoreCase));
        }

        // Per-village variant: an item only counts as active for a village when its payload targets that
        // same village (by name). Lets each enabled village get its own troop-training/smithy task.
        bool HasActiveTaskForVillage(string taskName, string villageName)
        {
            return activeItems.Any(item =>
                string.Equals(item.TaskName, taskName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(GetQueueItemVillageName(item) ?? string.Empty, villageName, StringComparison.OrdinalIgnoreCase));
        }

        var automationVillages = GetEnabledAutomationVillages();

        if (heroPollingEnabled && !HasActiveTask("hero_manage"))
        {
            var adventureCount = await _botService.RefreshAdventureCountAsync(options, AppendLog, CancellationToken.None);
            await Dispatcher.InvokeAsync(() => ApplyHeroAdventureAvailability(adventureCount));
            if (adventureCount is > 0)
            {
                _botService.EnqueueRuntime("hero_manage", "Hero adventure", null, priority: -50, maxRetries: 0);
                AppendLog($"Hero group: queued hero_manage because adventures available={adventureCount.Value}.");
            }
        }

        // Smithy troop upgrades — generated per enabled village whose Troops group is on. Each item is
        // tagged with its village so the worker switches there before running (BotTaskRunner). The selected
        // troops + target levels are stored PER VILLAGE and snapshotted into the payload, so the loop-driven
        // task knows what to upgrade (without it the worker would no-op). A village with no selection is
        // skipped instead of queuing a task that does nothing.
        if (!IsTroopsGroupBlocked())
        {
            var account = _accountStore.ActiveAccountName();
            foreach (var village in automationVillages)
            {
                var villageKey = GetVillageKey(village);
                if (!IsGroupEnabledForVillage(villageKey, QueueGroup.Troops)
                    || HasActiveTaskForVillage("upgrade_troops_at_smithy", village.Name))
                {
                    continue;
                }

                var villageTargets = SmithyUpgradeTargetsStore.Load(_projectRoot, account, villageKey);
                if (villageTargets.Count == 0)
                {
                    continue;
                }

                var smithyPayloadFragment = new SmithyUpgradePayload(
                        villageTargets.Select(s => new SmithyTroopTarget(s.Key, s.TargetLevel, s.Name)).ToList())
                    .ToDictionary();

                var payload = BuildVillageRuntimePayload(village);
                foreach (var pair in smithyPayloadFragment)
                {
                    payload[pair.Key] = pair.Value;
                }

                _botService.EnqueueRuntime("upgrade_troops_at_smithy", "Troop upgrades", payload, priority: -50, maxRetries: 0);
            }
        }

        // Troop training (Barracks/Stable/Workshop) — generated per enabled village whose Build Troops
        // group is on. Per village by design (each village trains independently). When the village has a
        // saved per-village override it is snapshotted into the payload so the worker trains that village's
        // own troops; otherwise the task runs on the global troop-training config (backwards-compatible).
        var troopTrainingAccount = _accountStore.ActiveAccountName();
        foreach (var village in automationVillages)
        {
            if (!IsGroupEnabledForVillage(GetVillageKey(village), QueueGroup.TroopTraining)
                || HasActiveTaskForVillage("build_troops", village.Name))
            {
                continue;
            }

            var trainingPayload = BuildVillageRuntimePayload(village);
            var villageTraining = TroopTrainingSettingsStore.Load(_projectRoot, troopTrainingAccount, GetVillageKey(village));
            if (villageTraining is not null)
            {
                foreach (var pair in villageTraining.ToDictionary())
                {
                    trainingPayload[pair.Key] = pair.Value;
                }
            }

            _botService.EnqueueRuntime("build_troops", "Build troops", trainingPayload, priority: -50, maxRetries: 0);
        }

        // Brewery celebration — capital only (the brewery exists only in the capital). Generated for the
        // capital when it is enabled, its Auto Celebration group is on, and the tribe supports it.
        if (_troopTrainingViewModel.IsAutoCelebrationAvailableForCurrentTribe
            && _troopTrainingViewModel.AutoCelebrationEnabled)
        {
            var capital = automationVillages.FirstOrDefault(v => v.IsCapital);
            if (capital is not null
                && IsGroupEnabledForVillage(GetVillageKey(capital), QueueGroup.BreweryCelebration)
                && !HasActiveTaskForVillage("run_brewery_celebration", capital.Name))
            {
                _botService.EnqueueRuntime("run_brewery_celebration", "Auto celebration", BuildVillageRuntimePayload(capital), priority: -50, maxRetries: 0);
            }
        }

        if (enabledGroups.Contains(QueueGroup.Farming) && !IsFarmingGroupBlocked() && !HasActiveTask("send_farmlists"))
        {
            var goldClubEnabled = await _botService.ReadAndPersistGoldClubStatusAsync(options, AppendLog, CancellationToken.None);
            UpdateGoldClubInfo(goldClubEnabled);
            if (goldClubEnabled)
            {
                await EnsureContinuousFarmListsReadyAsync(options);
                (List<string> Names, List<string> Ids) GatherSelectedFarmLists()
                {
                    var enabled = _farmLists.Where(item => IsRealFarmListRow(item) && item.IsEnabled).ToList();
                    var names = enabled
                        .Select(item => item.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var ids = enabled
                        .Select(item => item.ListId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(id => id!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return (names, ids);
                }

                var selectedSnapshot = Dispatcher.CheckAccess()
                    ? GatherSelectedFarmLists()
                    : Dispatcher.Invoke(GatherSelectedFarmLists);
                var selectedFarmLists = selectedSnapshot.Names;
                var farmSendMode = FarmingDefaults.NormalizeSendMode(options.ContinuousFarmSendMode);
                var sendsAllListsAtOnce = string.Equals(farmSendMode, FarmingDefaults.SendModeAllAtOnce, StringComparison.Ordinal);
                var availableFarmListCount = Dispatcher.CheckAccess()
                    ? _farmLists.Count(IsRealFarmListRow)
                    : Dispatcher.Invoke(() => _farmLists.Count(IsRealFarmListRow));
                if (availableFarmListCount <= 0)
                {
                    SetFarmingBlockedState(FarmingBlockedReasonNoFarmLists, "No farmlists available");
                }
                else
                {
                    if (string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoFarmLists, StringComparison.OrdinalIgnoreCase))
                    {
                        ClearFarmingBlockedState();
                    }
                }

                if (selectedFarmLists.Count > 0 || (sendsAllListsAtOnce && availableFarmListCount > 0))
                {
                    var payload = new FarmingPayload(selectedFarmLists, selectedSnapshot.Ids).ToDictionary();
                    var displayName = sendsAllListsAtOnce ? "Send all farmlists" : "Send selected farmlists";
                    _botService.EnqueueRuntime("send_farmlists", displayName, payload, priority: -50, maxRetries: 0);
                }
            }
            else
            {
                SetFarmingBlockedState(FarmingBlockedReasonNoGoldClub, "No goldclub");
            }
        }

        if (enabledGroups.Contains(QueueGroup.ResourceTransfer) && !HasActiveTask("send_resources_between_villages"))
        {
            var selectedSources = options.ResourceTransferSourceVillageNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (CanRunResourceTransfer(options, out _))
            {
                var payload = new ResourceTransferPayload(
                    Enabled: true,
                    TargetVillageName: options.ResourceTransferTargetVillageName,
                    SourceVillageNames: selectedSources,
                    SourceThresholdPercent: options.ResourceTransferSourceThresholdPercent,
                    SourceKeepPercent: options.ResourceTransferSourceKeepPercent,
                    TargetFillPercent: options.ResourceTransferTargetFillPercent,
                    SendWood: options.ResourceTransferSendWood,
                    SendClay: options.ResourceTransferSendClay,
                    SendIron: options.ResourceTransferSendIron,
                    SendCrop: options.ResourceTransferSendCrop).ToDictionary();
                _botService.EnqueueRuntime("send_resources_between_villages", "Resource transfer", payload, priority: -50, maxRetries: 0);
            }
        }

        if (enabledGroups.Contains(QueueGroup.Reinforcements) && !HasActiveTask("send_reinforcements_between_villages"))
        {
            var selectedSources = options.ReinforcementsSourceVillageNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (CanRunReinforcements(options, out _))
            {
                var payload = new ReinforcementsPayload(
                    Enabled: true,
                    TargetVillageName: options.ReinforcementsTargetVillageName,
                    SourceVillageNames: selectedSources,
                    TroopRules: BuildReinforcementRulesForRun()).ToDictionary();
                _botService.EnqueueRuntime("send_reinforcements_between_villages", "Reinforcements", payload, priority: -50, maxRetries: 0);
            }
        }
    }

    private async Task EnsureContinuousLoopConstructionStatusAsync(BotOptions options, CancellationToken cancellationToken)
    {
        if (!_continuousLoopConstructionStatusNeedsSync
            || !GetContinuousLoopEnabledGroupsInOrder().Contains(QueueGroup.Construction))
        {
            return;
        }

        if (HasReadyContinuousConstructionItem())
        {
            return;
        }

        try
        {
            var status = await ReadVillageStatusWithRetryAsync(
                options,
                cancellationToken,
                resourceOnly: false,
                forceCurrentVillage: true);
            await Dispatcher.InvokeAsync(() =>
            {
                CacheVillageStatus(status);
                _lastBuildingStatus = status;
                ApplyVillageStatusToUi(status);
                PopulateBuildingsTab(status);
            });
            _continuousLoopConstructionStatusNeedsSync = false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"Continuous construction status sync failed: {ex.Message}");
        }
    }

    private async Task EnsureContinuousFarmListsReadyAsync(BotOptions options)
    {
        var farmingEnabled = GetContinuousLoopEnabledGroupsInOrder().Contains(QueueGroup.Farming);
        if (!farmingEnabled || _farmingOperationBusy)
        {
            return;
        }

        var farmSnapshot = Dispatcher.CheckAccess()
            ? new
            {
                TotalCount = _farmLists.Count(IsRealFarmListRow),
                SelectedNames = _farmLists.Where(item => IsRealFarmListRow(item) && item.IsEnabled).Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
                AvailableNames = _farmLists.Where(IsRealFarmListRow).Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
            }
            : await Dispatcher.InvokeAsync(() => new
            {
                TotalCount = _farmLists.Count(IsRealFarmListRow),
                SelectedNames = _farmLists.Where(item => IsRealFarmListRow(item) && item.IsEnabled).Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
                AvailableNames = _farmLists.Where(IsRealFarmListRow).Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
            });

        var needsAnalyze = farmSnapshot.TotalCount <= 0
            || farmSnapshot.SelectedNames.Count <= 0
            || farmSnapshot.SelectedNames.Any(name => !farmSnapshot.AvailableNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            || _lastFarmListsAnalysisAt == DateTimeOffset.MinValue;

        if (!needsAnalyze)
        {
            return;
        }

        AppendLog("Continuous farming: analyzing farmlists before runtime send.");
        try
        {
            await RefreshFarmListsFromServerAsync(options, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendLog($"Continuous farming analyze failed: {ex.Message}");
        }
    }

    // preview=true makes this a pure read-only prediction of the NEXT item the live loop would pick, with
    // ALL side effects suppressed (no queue mutation, no rotation-key advance, no logging, no dedup-state
    // writes). Used by the top-bar "Next task" display so it mirrors the real selector exactly without
    // affecting scheduling. preview=false is the live loop path and behaves exactly as before.
    private QueueItem? SelectNextQueueItemForContinuousLoop(bool preview = false)
    {
        var options = LoadBotOptions();
        if (!preview)
        {
            RemoveDisabledAutoCollectUtilityItems(options);
        }
        var queueItems = _botService.GetQueueItemsForDisplay();
        var now = DateTimeOffset.UtcNow;
        var readyUtilityItems = OrderContinuousLoopGroupItems(
                queueItems.Where(item =>
                    IsAlwaysOnUtilityTask(item.TaskName) &&
                    IsAutoCollectUtilityTaskEnabledNow(item.TaskName, options) &&
                    IsQueueItemAllowedByAutomationSettings(item) &&
                    item.Status == QueueStatus.Pending &&
                    item.NextAttemptAt <= now));
        var activeVillageKey = _activeWorkingVillageKey;
        var readyUtilityItem = readyUtilityItems.FirstOrDefault(item =>
            activeVillageKey is null
            || string.Equals(GetQueueItemVillageKey(item), activeVillageKey, StringComparison.OrdinalIgnoreCase));
        if (readyUtilityItem is not null)
        {
            return readyUtilityItem;
        }

        // Consider the union of enabled runtime groups across all active villages. Persistent Queue-page
        // work is appended below only to preserve group ordering; every item is still gated by its own
        // village Auto toggle and per-village group toggle before it can run.
        var orderedGroups = GetContinuousLoopConsideredGroupsInOrder().ToList();
        var seenGroups = orderedGroups.ToHashSet();
        foreach (var manualGroup in queueItems
            .Where(item => !item.IsRuntimeOnly
                && item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused)
            .Select(item => item.Group))
        {
            if (seenGroups.Add(manualGroup))
            {
                orderedGroups.Add(manualGroup);
            }
        }

        if (orderedGroups.Count <= 0)
        {
            if (!preview)
            {
                AppendLoopPickVerbose(
                    "[loop-pick:verbose] no enabled groups — nothing to schedule",
                    "no-enabled-groups");
            }

            return null;
        }

        string? lastSkipReason = null;
        foreach (var group in orderedGroups)
        {
            var orderedGroupItems = OrderContinuousLoopGroupItems(
                queueItems.Where(item =>
                    item.Group == group &&
                    !IsAlwaysOnUtilityTask(item.TaskName) &&
                    IsQueueItemAllowedByAutomationSettings(item) &&
                    item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused));
            if (group == QueueGroup.Construction)
            {
                // Rotate construction across enabled villages: drain the current village's construction in
                // strict order, but if its next item is deferred move on to another village rather than
                // holding the whole group (the user's per-village rotation rule).
                var rotationKey = _continuousConstructionRotationVillageKey;
                string? constructionSkipReason = null;
                var constructionCandidate = QueueVillageRotation.SelectByVillageRotation(
                    orderedGroupItems,
                    GetQueueItemVillageKey,
                    villageItems => SelectNextConstructionQueueItem(villageItems, now, out _, preview),
                    ref rotationKey);
                if (!preview)
                {
                    _continuousConstructionRotationVillageKey = rotationKey;
                }

                if (constructionCandidate is not null)
                {
                    if (!IsConstructionGroupReady(allowWorkerValidationForReadyItem: true, suppressLog: preview))
                    {
                        lastSkipReason = $"group={group} skipped (construction group not ready)";
                        if (!preview)
                        {
                            AppendLoopPickVerbose(
                                $"[loop-pick:verbose] {lastSkipReason}",
                                $"group:{group}:construction-not-ready");
                        }

                        continue;
                    }

                    return constructionCandidate;
                }

                lastSkipReason = constructionSkipReason ?? $"group={group} skipped (no ready construction items)";
                if (!preview)
                {
                    AppendLoopPickVerbose(
                        $"[loop-pick:verbose] {lastSkipReason}",
                        $"group:{group}:{BuildLoopPickSkipKey(lastSkipReason)}");
                }

                continue;
            }

            // Rotate non-construction groups across villages too: troop-training/smithy items are now
            // tagged per village, so a village whose head item is waiting must not block another village's
            // ready item. For global/village-less groups (hero, farming, …) all items share one village
            // key, so this collapses to the original strict in-order head selection.
            var groupRotationKey = GetContinuousGroupRotationVillageKey(group);
            var candidate = QueueVillageRotation.SelectByVillageRotation(
                orderedGroupItems,
                GetQueueItemVillageKey,
                villageItems => SelectNextReadyGroupHead(villageItems, now),
                ref groupRotationKey);
            if (!preview)
            {
                SetContinuousGroupRotationVillageKey(group, groupRotationKey);
            }

            if (candidate is not null)
            {
                return candidate;
            }

            lastSkipReason = $"group={group} skipped (no ready item across villages)";
            if (!preview)
            {
                AppendLoopPickVerbose(
                    $"[loop-pick:verbose] {lastSkipReason}",
                    $"group:{group}:{BuildLoopPickSkipKey(lastSkipReason)}");
            }
        }

        if (!preview)
        {
            AppendLoopPickVerbose(
                $"[loop-pick:verbose] no item selected from {orderedGroups.Count} group(s)"
                    + (lastSkipReason is null ? string.Empty : $" — last reason: {lastSkipReason}"),
                $"no-selected:{orderedGroups.Count}:{BuildLoopPickSkipKey(lastSkipReason)}");
        }
        // Collect rewards in the village where the signal was observed. If another village still had
        // ready work, the utility item waited above; switch only after the current work is exhausted.
        return readyUtilityItems.FirstOrDefault();
    }

    // Whether a queue item's automation group is enabled for ITS OWN village. Lets a group turned off on
    // village B block B's tasks even while another village is selected/worked. Village-less (global)
    // tasks and unknown villages fall back to the account default group set.
    private bool IsQueueItemGroupEnabledForItsVillage(QueueItem item)
    {
        return IsGroupEnabledForVillage(GetQueueItemVillageKey(item), item.Group);
    }

    // Village settings are authoritative for all automated queue execution: if the village Auto toggle
    // is off, or the task's group is off for that village, the item stays queued but is ignored.
    private bool IsQueueItemAllowedByAutomationSettings(QueueItem item)
    {
        return IsQueueItemVillageEnabled(item) && IsQueueItemGroupEnabledForItsVillage(item);
    }

    // Whether an automation group is enabled for a specific village key (null/unknown villages fall back
    // to the account default group set). Shared by per-item gating and per-village runtime generation.
    private bool IsGroupEnabledForVillage(string? villageKey, QueueGroup group)
    {
        // Village-less (global) tasks like hero_manage are enabled when the group is on for ANY enabled
        // village — so e.g. Hero runs while the hero-home village has it on even
        // though another village is currently selected. Resolve this from the settings store only (no UI
        // marshalling): this runs on the continuous-loop background thread during item selection, so it must
        // NOT call GetContinuousLoopConsideredGroupsInOrder (which Dispatcher.Invokes to the UI thread and
        // would stall the whole loop behind UI work).
        if (villageKey is null)
        {
            return IsGroupEnabledForAnyVillage(group);
        }

        var groups = _villageSettingsStore.GetEnabledGroups(villageKey)
            ?? VillageSettingsStore.DefaultEnabledGroups;
        return groups.Contains(QueueGroupCatalog.GetKey(group), StringComparer.OrdinalIgnoreCase);
    }

    // Whether an automation group is enabled for ANY enabled village. Store-only and thread-safe (no
    // Dispatcher), so it is safe to call from the continuous-loop background thread. Used to gate
    // village-less global tasks (e.g. hero_manage) without depending on the UI-selected village.
    private bool IsGroupEnabledForAnyVillage(QueueGroup group)
    {
        var key = QueueGroupCatalog.GetKey(group);
        foreach (var (_, enabledGroups) in _villageSettingsStore.GetEnabledVillagesGroups())
        {
            var effective = enabledGroups ?? VillageSettingsStore.DefaultEnabledGroups;
            if (effective.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Villages currently enabled for automation, deduplicated by village key. Read from the Dashboard
    // village list (falls back to the dropdown), filtered against the persisted enabled state so it
    // matches what the user sees and what the queue rotation honors. Marshals to the UI thread.
    private List<VillageSelectionItem> GetEnabledAutomationVillages()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(GetEnabledAutomationVillages);
        }

        var source = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? Enumerable.Empty<VillageSelectionItem>();

        return source
            .Where(v => !string.IsNullOrWhiteSpace(v.Name) && !string.Equals(v.Name, "-", StringComparison.Ordinal))
            .GroupBy(GetVillageKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(v => _villageSettingsStore.IsEnabledByKey(GetVillageKey(v), defaultIfUnknown: false))
            .ToList();
    }

    // Tags a runtime item with its target village so the worker switches there before executing, and
    // gates NPC trade per village (master AND per-village both on).
    private Dictionary<string, string> BuildVillageRuntimePayload(VillageSelectionItem village)
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(village.Name))
        {
            payload[BotOptionPayloadKeys.TargetVillageName] = village.Name;
        }

        if (!string.IsNullOrWhiteSpace(village.Url))
        {
            payload[BotOptionPayloadKeys.TargetVillageUrl] = village.Url;
        }

        payload[BotOptionPayloadKeys.NpcTradeEnabled] = IsNpcTradeEnabledForVillageKey(GetVillageKey(village)) ? "true" : "false";
        return payload;
    }

    private static bool IsAlwaysOnUtilityTask(string? taskName) =>
        string.Equals(taskName, "collect_tasks", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "collect_daily_quests", StringComparison.OrdinalIgnoreCase);

    private bool HasReadyContinuousConstructionItem()
    {
        var now = DateTimeOffset.UtcNow;
        var items = OrderContinuousLoopGroupItems(
            _botService.GetQueueItemsForDisplay()
                .Where(item =>
                    item.Group == QueueGroup.Construction &&
                    IsQueueItemAllowedByAutomationSettings(item) &&
                    item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused));
        // Ready when any enabled village has a ready construction item (rotation key ignored here).
        string? rotationKey = null;
        return QueueVillageRotation.SelectByVillageRotation(
            items,
            GetQueueItemVillageKey,
            villageItems => SelectNextConstructionQueueItem(villageItems, now, out _),
            ref rotationKey) is not null;
    }

    // Per-village head selection for non-construction groups: returns this village's first item when it
    // is Pending and due, otherwise null (so rotation moves on to another village). Preserves the strict
    // in-order behavior within a village that the old single-head logic had.
    private static QueueItem? SelectNextReadyGroupHead(IReadOnlyList<QueueItem> villageItems, DateTimeOffset now)
    {
        var head = villageItems.FirstOrDefault();
        if (head is null || head.Status != QueueStatus.Pending || head.NextAttemptAt > now)
        {
            return null;
        }

        return head;
    }

    private QueueItem? SelectNextConstructionQueueItem(
        IReadOnlyList<QueueItem> orderedGroupItems,
        DateTimeOffset now,
        out string? skipReason,
        bool preview = false)
    {
        var availability = ResolveConstructionQueueAvailability(orderedGroupItems, now);
        var selection = ConstructionQueueSelector.SelectNext(
            orderedGroupItems,
            now,
            availability,
            index =>
            {
                var item = orderedGroupItems[index];
                return IsBuildingUpgradeForSlot(item, out var upgradeSlotId)
                    && HasEarlierPendingConstructForSlot(orderedGroupItems, index, upgradeSlotId);
            });
        skipReason = selection.SkipReason;

        if (selection.QueueFullBlocker is not null && !preview)
        {
            var blockerIndex = orderedGroupItems
                .Select((item, index) => (item, index))
                .FirstOrDefault(entry => entry.item.Id == selection.QueueFullBlocker.Id)
                .index;
            var blockedItems = orderedGroupItems
                .Skip(blockerIndex + 1)
                .Count(candidate => candidate.Status == QueueStatus.Pending);
            LogConstructionQueueFullSummary(selection.QueueFullBlocker, blockedItems, now);
        }

        if (selection.Item is null)
        {
            return null;
        }

        if (selection.ForcedLiveValidation && !preview)
        {
            var villageName = NormalizeVillageName(GetQueueItemVillageName(selection.Item)) ?? "-";
            AppendLoopPickVerbose(
                $"[construction-queue:verbose] live queue state allows immediate validation " +
                $"id={selection.Item.Id} task='{selection.Item.TaskName}' village='{villageName}' " +
                $"availability={availability} scheduledAt='{FormatQueueServerTime(selection.Item.NextAttemptAt)}'",
                $"construction-queue-live-validation:{selection.Item.Id}:{availability}");
        }

        if (!preview)
        {
            ClearConstructionQueueFullSummary(selection.Item);
        }

        return selection.Item;
    }

    private ConstructionQueueAvailability ResolveConstructionQueueAvailability(
        IReadOnlyList<QueueItem> villageItems,
        DateTimeOffset now)
    {
        var villageName = villageItems
            .Select(GetQueueItemVillageName)
            .Select(NormalizeVillageName)
            .FirstOrDefault(name => name is not null)
            ?? NormalizeVillageName(GetSelectedVillageName());
        VillageStatus? status = null;
        if (villageName is not null)
        {
            _villageStatusCacheByName.TryGetValue(villageName, out status);
        }

        return ConstructionQueueState.ResolveAvailability(status, _travianPlusActive, now);
    }

    private QueueItem? SelectNextQueueItemForAutoQueue(
        DateTimeOffset now,
        ref string? rotationVillageKey)
    {
        var orderedItems = _botService.GetQueueItemsForDisplay()
            .Where(item => ConstructionQueueState.IsActiveQueueStatus(item.Status))
            .Where(IsQueueItemAllowedByAutomationSettings)
            .ToList();

        return QueueVillageRotation.SelectByVillageRotation(
            orderedItems,
            GetQueueItemVillageKey,
            villageItems =>
            {
                var constructionItems = villageItems
                    .Where(item => item.Group == QueueGroup.Construction)
                    .ToList();
                var constructionCandidate = SelectNextConstructionQueueItem(
                    constructionItems,
                    now,
                    out _);

                foreach (var item in villageItems)
                {
                    if (item.Group == QueueGroup.Construction)
                    {
                        if (constructionCandidate?.Id == item.Id)
                        {
                            return item;
                        }

                        continue;
                    }

                    if (item.Status == QueueStatus.Pending && item.NextAttemptAt <= now)
                    {
                        return item;
                    }
                }

                return null;
            },
            ref rotationVillageKey);
    }

    private void LogConstructionQueueFullSummary(QueueItem blocker, int blockedItems, DateTimeOffset now)
    {
        var villageName = NormalizeVillageName(GetQueueItemVillageName(blocker)) ?? "-";
        var villageKey = GetQueueItemVillageKey(blocker) ?? villageName;
        var waitSeconds = Math.Max(0, (blocker.NextAttemptAt - now).TotalSeconds);
        var state = $"{blocker.Id}:{blocker.NextAttemptAt.UtcTicks}:{blockedItems}";
        lock (_loopPickVerboseLogGate)
        {
            if (_constructionQueueSummaryByVillage.TryGetValue(villageKey, out var existing)
                && string.Equals(existing, state, StringComparison.Ordinal))
            {
                return;
            }

            _constructionQueueSummaryByVillage[villageKey] = state;
        }

        AppendLog(
            $"[construction-queue:verbose] village queue blocked " +
            $"id={blocker.Id} task='{blocker.TaskName}' village='{villageName}' " +
            $"blockedItems={blockedItems} waitSeconds={waitSeconds:F0} " +
            $"retryAt='{FormatQueueServerTime(blocker.NextAttemptAt)}' " +
            $"reason='{blocker.Payload.GetValueOrDefault(BotOptionPayloadKeys.UpgradeDeferReason, "-")}'");
    }

    private void ClearConstructionQueueFullSummary(QueueItem item)
    {
        var villageName = NormalizeVillageName(GetQueueItemVillageName(item)) ?? "-";
        var villageKey = GetQueueItemVillageKey(item) ?? villageName;
        lock (_loopPickVerboseLogGate)
        {
            _constructionQueueSummaryByVillage.Remove(villageKey);
        }
    }

    private static bool HasEarlierPendingConstructForSlot(IReadOnlyList<QueueItem> orderedItems, int beforeIndex, int slotId)
    {
        for (var index = 0; index < beforeIndex; index++)
        {
            var earlier = orderedItems[index];
            if (earlier.Status == QueueStatus.Pending
                && IsBuildingConstructForSlot(earlier, out var constructSlotId)
                && constructSlotId == slotId)
            {
                return true;
            }
        }

        return false;
    }

    private void AppendLoopPickVerbose(string message, string key)
    {
        var now = DateTimeOffset.UtcNow;
        var shouldLog = false;
        lock (_loopPickVerboseLogGate)
        {
            if (!_loopPickVerboseLogAtByKey.TryGetValue(key, out var lastLogAt)
                || now - lastLogAt >= LoopPickVerboseThrottle)
            {
                _loopPickVerboseLogAtByKey[key] = now;
                shouldLog = true;
            }
        }

        if (shouldLog)
        {
            AppendLog(message);
        }
    }

    private static string BuildLoopPickSkipKey(string? skipReason)
    {
        if (string.IsNullOrWhiteSpace(skipReason))
        {
            return "none";
        }

        var waitingIndex = skipReason.IndexOf(" waiting ", StringComparison.Ordinal);
        if (waitingIndex >= 0)
        {
            return skipReason[..waitingIndex] + " waiting";
        }

        return skipReason;
    }

    private async Task MaybeCheckInboxDuringContinuousLoopAsync()
    {
        if (!_inboxAutoEnabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastContinuousInboxCheckUtc < TimeSpan.FromSeconds(ContinuousInboxCheckIntervalSeconds))
        {
            return;
        }

        _lastContinuousInboxCheckUtc = now;

        // Read the unread badge from the current page (cheap, no navigation) and refresh the
        // Messages/Reports indicators. force:true bypasses the busy guard in
        // RefreshInboxIndicatorsAsync — that guard exists to avoid touching the browser while a
        // task runs, but here the continuous loop owns the browser serially and calls this only
        // between task executions, so the access is safe.
        await RefreshInboxIndicatorsAsync(logErrors: false, force: true);
    }

    private void MarkContinuousBrowserActivity()
    {
        _lastContinuousBrowserActivityUtc = DateTimeOffset.UtcNow;
    }

    // Keep the Travian page from going stale while the loop is idle-waiting. If no task has touched
    // the browser for ContinuousKeepAliveIntervalSeconds (~1 min), reload the page the program is
    // currently on so the session stays live and Travian never shows stale/wrong values. Throttled,
    // so it never fires more than once per interval — not spammy.
    private async Task MaybeKeepBrowserFreshDuringContinuousLoopAsync(BotOptions options, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastContinuousBrowserActivityUtc < TimeSpan.FromSeconds(ContinuousKeepAliveIntervalSeconds))
        {
            return;
        }

        if (now - _lastContinuousKeepAliveFailureUtc < TimeSpan.FromMinutes(2))
        {
            return;
        }

        if (_resourceSnapshotRefreshRunning)
        {
            MarkContinuousBrowserActivity();
            AppendLog("[keep-alive:verbose] skipped because resource refresh is already reading the browser.");
            return;
        }

        MarkContinuousBrowserActivity();

        try
        {
            await _botService.RefreshCurrentPageAsync(options, _ => { }, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastContinuousKeepAliveFailureUtc = DateTimeOffset.UtcNow;
            if (IsTransientKeepAliveFailure(ex))
            {
                AppendLog($"[keep-alive:verbose] refresh skipped after transient failure: {ex.Message}");
                return;
            }

            AppendLog($"Continuous loop keep-alive refresh failed: {ex.Message}");
        }
    }

    private static bool IsTransientKeepAliveFailure(Exception ex)
    {
        return ex.GetType().Name.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || IsTransientPageReadFailure(ex);
    }

    private async Task RunContinuousLoopAsync(CancellationToken token)
    {
        // Start a fresh construction village rotation each time the loop starts.
        _continuousConstructionRotationVillageKey = null;
        _continuousGroupRotationVillageKeys.Clear();
        while (!token.IsCancellationRequested)
        {
            if (_loopController.LoopStopRequested)
            {
                AppendLog("Loop stop requested. Exiting after current action.");
                break;
            }

            var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
            var loopDelaySeconds = Math.Max(5, options.LoopIntervalSeconds);
            var tickId = Interlocked.Increment(ref _loopTickCounter);
            var tickSw = Stopwatch.StartNew();
            try
            {
                AppendLog($"[LOOP {tickId}] START interval={loopDelaySeconds}s, headless={options.Headless}");
                await EnsureChromiumInstalledAsync();
                await HonorPendingVillageSwitchAsync(options, token);
                await EnsureContinuousLoopConstructionStatusAsync(options, token);
                await EnsureContinuousLoopRuntimeItemsAsync(options);
                await MaybeCheckInboxDuringContinuousLoopAsync();

                var next = SelectNextQueueItemForContinuousLoop();
                if (next is not null)
                {
                    AppendLog($"[LOOP {tickId}] PICK group={next.Group}, task={next.TaskName}, retries={next.Retries}/{next.MaxRetries}");
                    await ActionPacer.FromOptions(options, AppendLog).DelayAsync(
                        options.ActionPacingTaskMinSeconds,
                        options.ActionPacingTaskMaxSeconds,
                        token,
                        "before task");
                    var shouldContinue = await ExecuteSingleQueueItemAsync(
                        next,
                        options,
                        $"[LOOP {tickId}]",
                        QueueExecutionMode.ContinuousLoop,
                        token);
                    MarkContinuousBrowserActivity();
                    if (!shouldContinue)
                    {
                        break;
                    }
                }
                else
                {
                    var waitDelay = ResolveContinuousLoopWaitDelay(loopDelaySeconds);
                    await WaitForNextContinuousLoopPassAsync(tickId, waitDelay, options, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppendLog($"[LOOP {tickId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s | {FormatExceptionForLog(ex)}");
                await Task.Delay(TimeSpan.FromSeconds(loopDelaySeconds), token);
            }
        }
    }

    private TimeSpan ResolveContinuousLoopWaitDelay(int fallbackSeconds)
    {
        try
        {
            if (GetContinuousLoopConsideredGroupsInOrder().Count <= 0)
            {
                return TimeSpan.FromSeconds(Math.Max(5, fallbackSeconds));
            }

            var now = DateTimeOffset.UtcNow;
            var nextDeferred = GetContinuousLoopRelevantQueueItems()
                .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                .OrderBy(item => item.NextAttemptAt)
                .FirstOrDefault();
            if (nextDeferred is null)
            {
                return TimeSpan.FromSeconds(Math.Max(5, fallbackSeconds));
            }

            var delay = nextDeferred.NextAttemptAt - now;
            if (delay < TimeSpan.FromSeconds(1))
            {
                return TimeSpan.FromSeconds(1);
            }

            return delay <= TimeSpan.FromSeconds(fallbackSeconds)
                ? delay
                : TimeSpan.FromSeconds(Math.Max(5, fallbackSeconds));
        }
        catch
        {
            return TimeSpan.FromSeconds(Math.Max(5, fallbackSeconds));
        }
    }

    private async Task WaitForNextContinuousLoopPassAsync(long tickId, TimeSpan waitDelay, BotOptions options, CancellationToken token)
    {
        var totalSeconds = Math.Max(1, (int)Math.Ceiling(waitDelay.TotalSeconds));
        if (options.ActionPacingEnabled)
        {
            var minMs = (int)Math.Round(Math.Max(0, options.ActionPacingLoopMinSeconds) * 1000);
            var maxMs = (int)Math.Round(Math.Max(options.ActionPacingLoopMinSeconds, options.ActionPacingLoopMaxSeconds) * 1000);
            var pacingSeconds = Random.Shared.Next(minMs, maxMs + 1) / 1000.0;
            totalSeconds = Math.Max(totalSeconds, (int)Math.Ceiling(pacingSeconds));
        }

        AppendLog($"[LOOP {tickId}] WAIT {totalSeconds}s");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(totalSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (_loopController.LoopStopRequested)
            {
                AppendLog($"[LOOP {tickId}] WAIT canceled by stop.");
                return;
            }

            if (Interlocked.Exchange(ref _continuousLoopWakeRequested, 0) == 1)
            {
                AppendLog($"[LOOP {tickId}] WAIT ended early: queue state or settings changed.");
                return;
            }

            try
            {
                if (SelectNextQueueItemForContinuousLoop(preview: true) is not null)
                {
                    AppendLog($"[LOOP {tickId}] WAIT ended early: queue item ready.");
                    return;
                }
            }
            catch
            {
                // If checking readiness fails, keep the wait responsive and let the next pass log the real error.
            }

            await MaybeKeepBrowserFreshDuringContinuousLoopAsync(options, token);

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var slice = remaining < TimeSpan.FromSeconds(ContinuousLoopMaxSleepSliceSeconds)
                ? remaining
                : TimeSpan.FromSeconds(ContinuousLoopMaxSleepSliceSeconds);
            await Task.Delay(slice, token);
        }
    }

    private static bool IsHeroLowHpCooldown(QueueItem item, Exception ex)
    {
        return string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase)
               && ex.Message.Contains("adventure_skipped_hp_too_low", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ApplyHeroLowHpCooldownUiAsync(TimeSpan cooldown)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(cooldown.TotalSeconds));
        await Dispatcher.InvokeAsync(() =>
        {
            _heroViewModel.AdventureStatusText = $"Hero HP too low. Next adventure check in {seconds}s.";
        });
    }

    private async Task ExecuteQueuedItemsNowAsync(CancellationToken cancellationToken)
    {
        var runId = System.Threading.Interlocked.Increment(ref _operationCounter);
        // Each run starts a fresh village rotation so it does not resume mid-drain on a stale village.
        _autoQueueRotationVillageKey = null;
        AppendLog($"[AUTOQ {runId}] START");
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_loopController.QueueStopRequested)
            {
                AppendLog($"[AUTOQ {runId}] STOPPED (graceful stop requested).");
                return;
            }

            if (_loopTask is not null && !_loopTask.IsCompleted)
            {
                AppendLog($"[AUTOQ {runId}] STOPPED (loop is running).");
                return;
            }

            // Honor a Switch-village request made while the queue is running (between items, safe).
            await HonorPendingVillageSwitchAsync(ApplySelectedVillageToOptions(LoadBotOptions()), cancellationToken);

            QueueItem? next;
            try
            {
                // Drain one village's ready tasks before rotating to the next enabled village. Each task
                // still switches to its own village via BotTaskRunner; rotation just keeps the runner on
                // one village at a time instead of interleaving villages by global priority/FIFO.
                var previousRotationKey = _autoQueueRotationVillageKey;
                var rotationKey = _autoQueueRotationVillageKey;
                next = SelectNextQueueItemForAutoQueue(DateTimeOffset.UtcNow, ref rotationKey);
                _autoQueueRotationVillageKey = rotationKey;

                if (next is not null && !string.Equals(previousRotationKey, rotationKey, StringComparison.OrdinalIgnoreCase))
                {
                    var villageLabel = GetQueueItemVillageName(next);
                    AppendLog($"[AUTOQ {runId}] ROTATE to village '{(string.IsNullOrWhiteSpace(villageLabel) ? "-" : villageLabel)}'");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[AUTOQ {runId}] FAIL selecting queue item: {FormatExceptionForLog(ex)}");
                return;
            }

            if (next is null)
            {
                var now = DateTimeOffset.UtcNow;
                var eligibleItems = _botService
                    .GetQueueItemsForDisplay()
                    .Where(IsQueueItemAllowedByAutomationSettings)
                    .ToList();

                var nextDeferredItem = eligibleItems
                    .Where(item => !item.IsRuntimeOnly && item.Status == QueueStatus.Pending)
                    .FirstOrDefault(item => item.NextAttemptAt > now);

                if (nextDeferredItem is null)
                {
                    nextDeferredItem = eligibleItems
                        .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                        .OrderBy(item => item.NextAttemptAt)
                        .FirstOrDefault();
                }

                if (nextDeferredItem is null)
                {
                    AppendLog($"[AUTOQ {runId}] DONE (queue empty).");
                    return;
                }

                var waitDelay = nextDeferredItem.NextAttemptAt - now;
                if (waitDelay < TimeSpan.Zero)
                {
                    waitDelay = TimeSpan.Zero;
                }

                AppendLog($"[AUTOQ {runId}] WAIT {waitDelay.TotalSeconds:F0}s for deferred task={nextDeferredItem.TaskName}");
                try
                {
                    await Task.Delay(waitDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await EnsureChromiumInstalledAsync();
            var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
            AppendLog($"[AUTOQ {runId}] RUN task={next.TaskName}, id={next.Id}");
            var shouldContinue = await ExecuteSingleQueueItemAsync(
                next,
                options,
                $"[AUTOQ {runId}]",
                QueueExecutionMode.AutoQueue,
                cancellationToken);
            if (!shouldContinue)
            {
                return;
            }
        }
    }
}
