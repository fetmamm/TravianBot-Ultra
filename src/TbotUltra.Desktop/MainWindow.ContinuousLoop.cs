using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private async Task TriggerQueueAutoRunAsync()
    {
        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            return;
        }

        if (_loopController.QueueStopRequested)
        {
            return;
        }

        var gateLease = await _loopController.TryAcquireQueueAutoRunGateAsync(_queueAutoRunCts.Token);
        if (gateLease is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            _autoQueueRunCts = CancellationTokenSource.CreateLinkedTokenSource(_queueAutoRunCts.Token);
            var autoToken = _autoQueueRunCts.Token;
            try
            {
                _autoQueueRunning = true;
                UpdateExecutionStateIndicatorOnUiThread();
                await ExecuteQueuedItemsNowAsync(autoToken);
            }
            finally
            {
                _autoQueueRunning = false;
                _autoQueueRunCts?.Dispose();
                _autoQueueRunCts = null;
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
        if (enabledGroups.Count <= 0)
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

        if (enabledGroups.Contains(QueueGroup.Hero) && !IsHeroGroupBlocked() && !HasActiveTask("hero_manage"))
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
                var selectedFarmLists = Dispatcher.CheckAccess()
                    ? _farmLists
                        .Where(item => item.IsEnabled)
                        .Select(item => item.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : Dispatcher.Invoke(() => _farmLists
                        .Where(item => item.IsEnabled)
                        .Select(item => item.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());
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
                    var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [BotOptionPayloadKeys.ContinuousFarmListNames] = string.Join(",", selectedFarmLists),
                        [BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes] = options.ContinuousFarmDispatchDelayMinutes.ToString(),
                    };
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
                var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [BotOptionPayloadKeys.ResourceTransferEnabled] = "true",
                    [BotOptionPayloadKeys.ResourceTransferTargetVillageName] = options.ResourceTransferTargetVillageName,
                    [BotOptionPayloadKeys.ResourceTransferSourceVillageNames] = string.Join(",", selectedSources),
                    [BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent] = options.ResourceTransferSourceThresholdPercent.ToString(),
                    [BotOptionPayloadKeys.ResourceTransferSourceKeepPercent] = options.ResourceTransferSourceKeepPercent.ToString(),
                    [BotOptionPayloadKeys.ResourceTransferTargetFillPercent] = options.ResourceTransferTargetFillPercent.ToString(),
                    [BotOptionPayloadKeys.ResourceTransferSendWood] = options.ResourceTransferSendWood ? "true" : "false",
                    [BotOptionPayloadKeys.ResourceTransferSendClay] = options.ResourceTransferSendClay ? "true" : "false",
                    [BotOptionPayloadKeys.ResourceTransferSendIron] = options.ResourceTransferSendIron ? "true" : "false",
                    [BotOptionPayloadKeys.ResourceTransferSendCrop] = options.ResourceTransferSendCrop ? "true" : "false",
                };
                _botService.EnqueueRuntime("send_resources_between_villages", "Resource transfer", payload, priority: -50, maxRetries: 0);
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

        try
        {
            var status = await ReadVillageStatusWithRetryAsync(options, cancellationToken, resourceOnly: false);
            await Dispatcher.InvokeAsync(() =>
            {
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
        var orderedGroups = GetContinuousLoopEnabledGroupsInOrder().ToList();
        if (orderedGroups.Count <= 0)
        {
            return null;
        }

        var queueItems = _botService.GetQueueItemsForDisplay();
        var now = DateTimeOffset.UtcNow;
        foreach (var group in orderedGroups)
        {
            if (group == QueueGroup.Construction && !IsConstructionGroupReady())
            {
                continue;
            }

            var orderedGroupItems = OrderContinuousLoopGroupItems(
                queueItems.Where(item =>
                    item.Group == group &&
                    item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused));
            var head = orderedGroupItems.FirstOrDefault();
            if (head is null)
            {
                continue;
            }

            if (head.Status != QueueStatus.Pending || head.NextAttemptAt > now)
            {
                continue;
            }

            return head;
        }

        return null;
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

    private async Task RunContinuousLoopAsync(CancellationToken token)
    {
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
                await EnsureContinuousLoopConstructionStatusAsync(options, token);
                await EnsureContinuousLoopRuntimeItemsAsync(options);
                await MaybeCheckInboxDuringContinuousLoopAsync();

                var next = SelectNextQueueItemForContinuousLoop();
                if (next is not null)
                {
                    var terminalCountBefore = await Dispatcher.InvokeAsync(() => _terminalEntries.Count);
                    AppendLog($"[LOOP {tickId}] PICK group={next.Group}, task={next.TaskName}, retries={next.Retries}/{next.MaxRetries}");
                    SetActiveAutomationTask(next.TaskName);
                    SetActiveFunctionExecution(string.IsNullOrWhiteSpace(next.DisplayName) ? next.TaskName : next.DisplayName);
                    _botService.MarkQueueItemRunning(next.Id);
                    RefreshQueueUiOnUiThread(next.Id);

                    try
                    {
                        await _botService.ExecuteQueueItemAsync(options, next, AppendLog, token);
                        _botService.MarkQueueItemSucceeded(next.Id);
                        if (IsResourceUpgradeTask(next.TaskName))
                        {
                            var fastUpdated = await TryApplyFastResourceLevelUpdateAsync(next.TaskName, terminalCountBefore);
                            if (!fastUpdated)
                            {
                                await LoadResourcesAfterUpgradeAsync(token, resourceOnly: true);
                            }
                        }
                        if (NeedsConstructionStatusRefresh(next.TaskName))
                        {
                            await RefreshConstructionStatusAsync(token);
                        }
                        else if (string.Equals(next.TaskName, "load_buildings_snapshot", StringComparison.OrdinalIgnoreCase))
                        {
                            await LoadBuildingsSnapshotIntoUiAsync(token);
                        }
                        else if (string.Equals(next.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var snapshot = await _botService.ReadHeroAttributesAsync(options, AppendLog, token);
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    _heroViewModel.ApplyAttributeSnapshot(snapshot);
                                    _heroViewModel.AdventureStatusText = "Hero adventure check completed.";
                                });
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"Hero stats refresh after run failed: {ex.Message}");
                            }
                        }
                        else if (string.Equals(next.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                await RefreshTroopTrainingUiAfterBuildAsync(options, token);
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"Troop/resource refresh after run failed: {ex.Message}");
                            }
                        }
                        else if (string.Equals(next.TaskName, "run_brewery_celebration", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                await RefreshBreweryCelebrationStatusAsync(options, _lastBuildingStatus, token);
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"Brewery celebration refresh after run failed: {ex.Message}");
                            }
                        }

                        AppendLog($"[LOOP {tickId}] OK {tickSw.Elapsed.TotalSeconds:F1}s | queue:{next.TaskName}");
                        _ = Dispatcher.BeginInvoke(() => LastScanInfoTextBlock.Text = $"Last scan: {GetServerNow():HH:mm:ss}");
                    }
                    catch (OperationCanceledException)
                    {
                        _botService.MarkQueueItemDeferred(next.Id, TimeSpan.Zero);
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (await TryHandleTroopsBlockedExecutionAsync(next, ex, $"[LOOP {tickId}]"))
                        {
                            continue;
                        }

                        if (TryExtractQueueWaitDelay(ex.Message, out var queueWaitDelay))
                        {
                            if (IsConstructionQueueTask(next.TaskName))
                            {
                                await Dispatcher.InvokeAsync(() => ApplyConstructionInlineWait(queueWaitDelay));
                            }

                            if (IsHeroLowHpCooldown(next, ex))
                            {
                                await ApplyHeroLowHpCooldownUiAsync(queueWaitDelay);
                            }

                            var deferred = _botService.MarkQueueItemDeferred(next.Id, queueWaitDelay);
                            if (deferred)
                            {
                                var constructionSuffix = IsConstructionQueueTask(next.TaskName)
                                    ? " | construction wait timer updated; continuing with next enabled group; no Hero refresh was triggered by this defer"
                                    : string.Empty;
                                if (TryExtractDeferredUpgradePayload(ex.Message, next.Payload, out var updatedPayload))
                                {
                                    _botService.UpdateDeferredQueueItem(next.Id, updatedPayload);
                                    next.Payload = updatedPayload;
                                }
                                ScheduleDeferredBuildingsMidWaitRefresh(next, queueWaitDelay);
                                ScheduleDeferredResourcesMidWaitRefresh(next, queueWaitDelay);
                                AppendLog($"[LOOP {tickId}] DEFER {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName} | next try in {queueWaitDelay.TotalSeconds:F0}s{constructionSuffix}");
                            }
                            else
                            {
                                _botService.MarkQueueItemExecutionFailed(next.Id);
                                AppendLog($"[LOOP {tickId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s | {FormatExceptionForLog(ex)}");
                                RaiseAlarmIfQueueItemPermanentlyFailed(next, ex.Message);
                            }
                        }
                        else
                        {
                            _botService.MarkQueueItemExecutionFailed(next.Id);
                            AppendLog($"[LOOP {tickId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s | {FormatExceptionForLog(ex)}");
                            RaiseAlarmIfQueueItemPermanentlyFailed(next, ex.Message);
                        }
                    }
                    finally
                    {
                        SetActiveAutomationTask(null);
                        SetActiveFunctionExecution(null);
                        RefreshQueueUiOnUiThread(next.Id);
                    }
                }
                else
                {
                    var waitDelay = ResolveContinuousLoopWaitDelay(loopDelaySeconds);
                    await WaitForNextContinuousLoopPassAsync(tickId, waitDelay, token);
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

    private async Task WaitForNextContinuousLoopPassAsync(long tickId, TimeSpan waitDelay, CancellationToken token)
    {
        var totalSeconds = Math.Max(1, (int)Math.Ceiling(waitDelay.TotalSeconds));
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

            QueueItem? next;
            try
            {
                next = _botService.SelectNextQueueItem();
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

            var tickSw = Stopwatch.StartNew();
            try
            {
                var terminalCountBefore = await Dispatcher.InvokeAsync(() => _terminalEntries.Count);
                _botService.MarkQueueItemRunning(next.Id);
                RefreshQueueUiOnUiThread(next.Id);
                await EnsureChromiumInstalledAsync();
                var options = ApplySelectedVillageToOptions(LoadBotOptions());
                AppendLog($"[AUTOQ {runId}] RUN task={next.TaskName}, id={next.Id}");
                SetActiveAutomationTask(next.TaskName);
                SetActiveFunctionExecution(string.IsNullOrWhiteSpace(next.DisplayName) ? next.TaskName : next.DisplayName);
                await _botService.ExecuteQueueItemAsync(options, next, AppendLog, cancellationToken);
                _botService.MarkQueueItemSucceeded(next.Id);
                if (IsResourceUpgradeTask(next.TaskName))
                {
                    var fastUpdated = await TryApplyFastResourceLevelUpdateAsync(next.TaskName, terminalCountBefore);
                    if (!fastUpdated)
                    {
                        await LoadResourcesAfterUpgradeAsync(cancellationToken, resourceOnly: true);
                    }
                }
                if (NeedsConstructionStatusRefresh(next.TaskName))
                {
                    await RefreshConstructionStatusAsync(cancellationToken);
                }
                else if (string.Equals(next.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase))
                {
                    // Same post-run refresh the loop runner does — read the authoritative attributes-tab
                    // snapshot off the UI thread and marshal the UI write back via the dispatcher,
                    // so the Hero / Adventures card mirrors what Travian shows after the run.
                    try
                    {
                        var snapshot = await _botService.ReadHeroAttributesAsync(options, AppendLog, cancellationToken);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _heroViewModel.ApplyAttributeSnapshot(snapshot);
                            _heroViewModel.AdventureStatusText = "Hero adventure check completed.";
                        });
                    }
                    catch (Exception refreshEx)
                    {
                        AppendLog($"Hero stats refresh after run failed: {refreshEx.Message}");
                    }
                }
                else if (string.Equals(next.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await RefreshTroopTrainingUiAfterBuildAsync(options, cancellationToken);
                    }
                    catch (Exception refreshEx)
                    {
                        AppendLog($"Troop/resource refresh after run failed: {refreshEx.Message}");
                    }
                }
                else if (string.Equals(next.TaskName, "run_brewery_celebration", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await RefreshBreweryCelebrationStatusAsync(options, _lastBuildingStatus, cancellationToken);
                    }
                    catch (Exception refreshEx)
                    {
                        AppendLog($"Brewery celebration refresh after run failed: {refreshEx.Message}");
                    }
                }
                AppendLog($"[AUTOQ {runId}] OK {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName}");
            }
            catch (OperationCanceledException)
            {
                _botService.MarkQueueItemDeferred(next.Id, TimeSpan.Zero);
                return;
            }
            catch (Exception ex)
            {
                if (await TryHandleTroopsBlockedExecutionAsync(next, ex, $"[AUTOQ {runId}]"))
                {
                    continue;
                }

                if (ex is InvalidOperationException ioe
                    && ioe.Message.Contains("different thread owns it", StringComparison.OrdinalIgnoreCase))
                {
                    _botService.MarkQueueItemExecutionFailed(next.Id);
                    _loopController.RequestQueueStop();
                    AppendLog($"[AUTOQ {runId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName} | UI thread access error detected. Auto-queue paused to prevent spam.");
                    return;
                }

                if (TryExtractQueueWaitDelay(ex.Message, out var queueWaitDelay))
                {
                    if (IsConstructionQueueTask(next.TaskName))
                    {
                        await Dispatcher.InvokeAsync(() => ApplyConstructionInlineWait(queueWaitDelay));
                    }

                    if (IsHeroLowHpCooldown(next, ex))
                    {
                        await ApplyHeroLowHpCooldownUiAsync(queueWaitDelay);
                    }

                    var deferred = _botService.MarkQueueItemDeferred(next.Id, queueWaitDelay);
                    if (deferred)
                    {
                        var constructionSuffix = IsConstructionQueueTask(next.TaskName)
                            ? " | construction wait timer updated; continuing with other ready tasks; no Hero refresh was triggered by this defer"
                            : string.Empty;
                        if (TryExtractDeferredUpgradePayload(ex.Message, next.Payload, out var updatedPayload))
                        {
                            _botService.UpdateDeferredQueueItem(next.Id, updatedPayload);
                            next.Payload = updatedPayload;
                        }
                        ScheduleDeferredBuildingsMidWaitRefresh(next, queueWaitDelay);
                        ScheduleDeferredResourcesMidWaitRefresh(next, queueWaitDelay);
                        AppendLog($"[AUTOQ {runId}] DEFER {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName} | next try in {queueWaitDelay.TotalSeconds:F0}s{constructionSuffix}");
                    }
                    else
                    {
                        _botService.MarkQueueItemExecutionFailed(next.Id);
                        AppendLog($"[AUTOQ {runId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName} | {FormatExceptionForLog(ex)}");
                        RaiseAlarmIfQueueItemPermanentlyFailed(next, ex.Message);
                    }
                }
                else
                {
                    _botService.MarkQueueItemExecutionFailed(next.Id);
                    AppendLog($"[AUTOQ {runId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName} | {FormatExceptionForLog(ex)}");
                    RaiseAlarmIfQueueItemPermanentlyFailed(next, ex.Message);
                }
            }
            finally
            {
                SetActiveAutomationTask(null);
                SetActiveFunctionExecution(null);
                RefreshQueueUiOnUiThread(next.Id);
                if (IsBuildingMutationTask(next.TaskName))
                {
                    try
                    {
                        await LoadBuildingsSnapshotIntoUiAsync(cancellationToken);
                    }
                    catch
                    {
                        // Ignore snapshot reload errors in finally — the UI keeps the previous state.
                    }
                }
            }
        }
    }
}
