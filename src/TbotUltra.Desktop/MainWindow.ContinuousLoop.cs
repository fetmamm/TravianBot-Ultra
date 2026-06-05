using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
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

    // The village the AutoQueue drain is currently working through. Rotation drains one village's ready
    // tasks before advancing to the next; reset at the start of each run so a fresh run starts cleanly.
    private string? _autoQueueRotationVillageKey;

    // The village the continuous loop is currently draining construction for. Same rotation rule as the
    // AutoQueue path: finish one village's construction (in strict order) before moving to the next; if
    // the current village's next construction is deferred (e.g. waiting for resources), rotate onward so
    // a stalled village never blocks the others. Reset when the loop starts.
    private string? _continuousConstructionRotationVillageKey;

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

        _ = Task.Run(async () =>
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

        _loopController.ClearQueueStopRequest();
        _ = TriggerQueueAutoRunAsync();
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

    private async Task EnsureContinuousLoopRuntimeItemsAsync(BotOptions options)
    {
        var enabledGroups = GetContinuousLoopEnabledGroupsInOrder();
        var heroPollingEnabled = enabledGroups.Contains(QueueGroup.Hero) || ShouldKeepHeroAdventurePolling();
        if (enabledGroups.Count <= 0 && !heroPollingEnabled)
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

        if (enabledGroups.Contains(QueueGroup.Troops) && !IsTroopsGroupBlocked() && !HasActiveTask("upgrade_troops_at_smithy"))
        {
            _botService.EnqueueRuntime("upgrade_troops_at_smithy", "Troop upgrades", null, priority: -50, maxRetries: 0);
        }

        if (enabledGroups.Contains(QueueGroup.TroopTraining) && !HasActiveTask("build_troops"))
        {
            _botService.EnqueueRuntime("build_troops", "Build troops", null, priority: -50, maxRetries: 0);
        }

        if (enabledGroups.Contains(QueueGroup.BreweryCelebration)
            && _troopTrainingViewModel.IsAutoCelebrationAvailableForCurrentTribe
            && _troopTrainingViewModel.AutoCelebrationEnabled
            && !HasActiveTask("run_brewery_celebration"))
        {
            _botService.EnqueueRuntime("run_brewery_celebration", "Auto celebration", null, priority: -50, maxRetries: 0);
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
                    var enabled = _farmLists.Where(item => item.IsEnabled).ToList();
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
                var availableFarmListCount = Dispatcher.CheckAccess()
                    ? _farmLists.Count
                    : Dispatcher.Invoke(() => _farmLists.Count);
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

                if (selectedFarmLists.Count > 0)
                {
                    var payload = new FarmingPayload(selectedFarmLists, selectedSnapshot.Ids).ToDictionary();
                    _botService.EnqueueRuntime("send_farmlists", "Send selected farmlists", payload, priority: -50, maxRetries: 0);
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
            var status = await ReadVillageStatusWithRetryAsync(options, cancellationToken, resourceOnly: false);
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
                TotalCount = _farmLists.Count,
                SelectedNames = _farmLists.Where(item => item.IsEnabled).Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
                AvailableNames = _farmLists.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
            }
            : await Dispatcher.InvokeAsync(() => new
            {
                TotalCount = _farmLists.Count,
                SelectedNames = _farmLists.Where(item => item.IsEnabled).Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
                AvailableNames = _farmLists.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
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

    private QueueItem? SelectNextQueueItemForContinuousLoop()
    {
        var options = LoadBotOptions();
        RemoveDisabledAutoCollectUtilityItems(options);
        var queueItems = _botService.GetQueueItemsForDisplay();
        var now = DateTimeOffset.UtcNow;
        var readyUtilityItem = OrderContinuousLoopGroupItems(
                queueItems.Where(item =>
                    IsAlwaysOnUtilityTask(item.TaskName) &&
                    IsAutoCollectUtilityTaskEnabledNow(item.TaskName, options) &&
                    IsQueueItemVillageEnabled(item) &&
                    item.Status == QueueStatus.Pending &&
                    item.NextAttemptAt <= now))
            .FirstOrDefault();
        if (readyUtilityItem is not null)
        {
            return readyUtilityItem;
        }

        var orderedGroups = GetContinuousLoopEnabledGroupsInOrder().ToList();
        if (orderedGroups.Count <= 0)
        {
            AppendLoopPickVerbose(
                "[loop-pick:verbose] no enabled groups — nothing to schedule",
                "no-enabled-groups");
            return null;
        }

        string? lastSkipReason = null;
        foreach (var group in orderedGroups)
        {
            var orderedGroupItems = OrderContinuousLoopGroupItems(
                queueItems.Where(item =>
                    item.Group == group &&
                    (!IsAlwaysOnUtilityTask(item.TaskName) || IsAutoCollectUtilityTaskEnabledNow(item.TaskName, options)) &&
                    IsQueueItemVillageEnabled(item) &&
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
                    villageItems => SelectNextConstructionQueueItem(villageItems, now, out _),
                    ref rotationKey);
                _continuousConstructionRotationVillageKey = rotationKey;
                if (constructionCandidate is not null)
                {
                    if (!IsConstructionGroupReady(allowWorkerValidationForReadyItem: true))
                    {
                        lastSkipReason = $"group={group} skipped (construction group not ready)";
                        AppendLoopPickVerbose(
                            $"[loop-pick:verbose] {lastSkipReason}",
                            $"group:{group}:construction-not-ready");
                        continue;
                    }

                    return constructionCandidate;
                }

                lastSkipReason = constructionSkipReason ?? $"group={group} skipped (no ready construction items)";
                AppendLoopPickVerbose(
                    $"[loop-pick:verbose] {lastSkipReason}",
                    $"group:{group}:{BuildLoopPickSkipKey(lastSkipReason)}");
                continue;
            }

            var head = orderedGroupItems.FirstOrDefault();
            if (head is null)
            {
                lastSkipReason = $"group={group} skipped (no pending/running/paused items)";
                AppendLoopPickVerbose(
                    $"[loop-pick:verbose] {lastSkipReason}",
                    $"group:{group}:empty");
                continue;
            }

            if (head.Status != QueueStatus.Pending)
            {
                lastSkipReason = $"group={group} head task='{head.TaskName}' is {head.Status} (not Pending)";
                AppendLoopPickVerbose(
                    $"[loop-pick:verbose] {lastSkipReason}",
                    $"group:{group}:task:{head.Id}:status:{head.Status}");
                continue;
            }

            if (head.NextAttemptAt > now)
            {
                var waitSec = (head.NextAttemptAt - now).TotalSeconds;
                lastSkipReason = $"group={group} head task='{head.TaskName}' waiting {waitSec:F0}s (NextAttemptAt in future)";
                AppendLoopPickVerbose(
                    $"[loop-pick:verbose] {lastSkipReason}",
                    $"group:{group}:task:{head.Id}:waiting");
                continue;
            }

            return head;
        }

        AppendLoopPickVerbose(
            $"[loop-pick:verbose] no item selected from {orderedGroups.Count} group(s)"
                + (lastSkipReason is null ? string.Empty : $" — last reason: {lastSkipReason}"),
            $"no-selected:{orderedGroups.Count}:{BuildLoopPickSkipKey(lastSkipReason)}");
        return null;
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
                    IsQueueItemVillageEnabled(item) &&
                    item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused));
        // Ready when any enabled village has a ready construction item (rotation key ignored here).
        string? rotationKey = null;
        return QueueVillageRotation.SelectByVillageRotation(
            items,
            GetQueueItemVillageKey,
            villageItems => SelectNextConstructionQueueItem(villageItems, now, out _),
            ref rotationKey) is not null;
    }

    private static QueueItem? SelectNextConstructionQueueItem(
        IReadOnlyList<QueueItem> orderedGroupItems,
        DateTimeOffset now,
        out string? skipReason)
    {
        skipReason = null;
        if (orderedGroupItems.Count <= 0)
        {
            skipReason = "group=Construction skipped (no pending/running/paused items)";
            return null;
        }

        for (var index = 0; index < orderedGroupItems.Count; index++)
        {
            var item = orderedGroupItems[index];
            if (item.Status != QueueStatus.Pending)
            {
                skipReason = $"group=Construction task='{item.TaskName}' is {item.Status} (not Pending)";
                return null;
            }

            if (item.NextAttemptAt > now)
            {
                // Strict queue order: if the next item in line is waiting (e.g. for resources),
                // hold the whole Construction group instead of skipping ahead to build something
                // queued later. The user wants the queue processed in order.
                var waitSec = (item.NextAttemptAt - now).TotalSeconds;
                skipReason = $"group=Construction task='{item.TaskName}' waiting {waitSec:F0}s (NextAttemptAt in future); holding queue order";
                return null;
            }

            if (IsBuildingUpgradeForSlot(item, out var upgradeSlotId)
                && HasEarlierPendingConstructForSlot(orderedGroupItems, index, upgradeSlotId))
            {
                skipReason = $"group=Construction task='{item.TaskName}' blocked by earlier construct for slot {upgradeSlotId}";
                continue;
            }

            return item;
        }

        return null;
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

    // Keep the Travian page from going stale while the loop is idle-waiting. If no task has
    // navigated the browser for ContinuousKeepAliveIntervalSeconds, do one real navigation so the
    // session stays live and the next status read happens on a fresh page. Throttled, so it never
    // fires more than once per interval — not spammy.
    private async Task MaybeKeepBrowserFreshDuringContinuousLoopAsync(BotOptions options, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastContinuousBrowserActivityUtc < TimeSpan.FromSeconds(ContinuousKeepAliveIntervalSeconds))
        {
            return;
        }

        MarkContinuousBrowserActivity();

        try
        {
            await _botService.NavigateToVillageResourceFieldsAsync(
                options,
                _ => { },
                GetSelectedVillageName(),
                GetSelectedVillageUrl(),
                token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"Continuous loop keep-alive refresh failed: {ex.Message}");
        }
    }

    private async Task RunContinuousLoopAsync(CancellationToken token)
    {
        // Start a fresh construction village rotation each time the loop starts.
        _continuousConstructionRotationVillageKey = null;
        while (!token.IsCancellationRequested)
        {
            if (_loopController.LoopStopRequested)
            {
                AppendLog("Loop stop requested. Exiting after current action.");
                break;
            }

            var options = ApplySelectedVillageToOptions(LoadBotOptions());
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
            if (GetContinuousLoopEnabledGroupsInOrder().Count <= 0)
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
                AppendLog($"[LOOP {tickId}] WAIT ended early: enabled groups changed.");
                return;
            }

            try
            {
                if (SelectNextQueueItemForContinuousLoop() is not null)
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
                next = QueueVillageRotation.SelectNext(
                    _botService.GetQueueItemsForDisplay(),
                    DateTimeOffset.UtcNow,
                    GetQueueItemVillageKey,
                    IsQueueItemVillageEnabled,
                    ref rotationKey);
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
                var nextDeferredItem = _botService
                    .GetQueueItemsForDisplay()
                    .Where(item => !item.IsRuntimeOnly && item.Status == QueueStatus.Pending)
                    .FirstOrDefault(item => item.NextAttemptAt > now);

                if (nextDeferredItem is null)
                {
                    nextDeferredItem = _botService
                        .GetQueueItemsForDisplay()
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
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
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
