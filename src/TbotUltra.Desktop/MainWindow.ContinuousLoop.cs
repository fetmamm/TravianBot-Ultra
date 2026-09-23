using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static readonly TimeSpan VillageMembershipPreflightInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan VillageMembershipVerificationRetryDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan VillageMembershipConfirmationRefreshInterval = TimeSpan.FromMinutes(20);
    private string? _villageMembershipPreflightAccount;
    private DateTimeOffset _lastVillageMembershipPreflightAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _villageMembershipVerificationNotBeforeUtc = DateTimeOffset.MinValue;
    private string? _failedVillageMembershipSignature;
    private string? _confirmedVillageMembershipSignature;
    private DateTimeOffset _confirmedVillageMembershipAtUtc = DateTimeOffset.MinValue;

    private DateTimeOffset GetContinuousKeepAliveNextReloadUtc()
        => _automationSessionRuntime.NextKeepAliveAtUtc;

    private DateTimeOffset GetVillageStatusSweepNextScanUtc()
        => _villageStatusRoundRuntime.GetNextRoundUtc(_accountStore.ActiveAccountName());

    private void ResetVillageStatusSweepSchedule()
    {
        var cleared = _villageStatusRoundRuntime.Reset(_accountStore.ActiveAccountName());

        if (!cleared)
        {
            AppendLog("[village-scan] could not clear the remembered next-scan deadline; the current session is ready immediately.");
        }
    }

    private async Task RunVillageStatusSweepNowFromSettingsAsync()
    {
        ResetVillageStatusSweepSchedule();

        if (IsContinuousLoopRunning())
        {
            _villageStatusRoundRuntime.RequestForce();
            RequestContinuousAutomationWake();
            AppendLog("[village-scan] Scan now requested; the active loop will start it at the next safe boundary.");
            return;
        }

        if (!_isLoggedIn || !_browserSessionLikelyOpen || IsSessionSleeping || IsFreezeActive)
        {
            AppendLog("[village-scan] Scan now could not start because the browser session is not active.");
            return;
        }

        if (_autoQueueRunning || _loopController.HasActiveOperation)
        {
            AppendLog("[village-scan] Scan now skipped because another manual operation is active.");
            return;
        }

        if (!_villageStatusRoundRuntime.TryBeginManualRun())
        {
            return;
        }

        var cancellationToken = _loopController.StartOperation("village-scan-now");
        try
        {
            var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
            AppendLog("[village-scan] Scan now starting a manual round.");
            await _continuousVillageStatusRound.RunIfDueAsync(
                options,
                cancellationToken,
                force: true);
        }
        catch (OperationCanceledException)
        {
            AppendLog("[village-scan] Scan now canceled.");
        }
        catch (Exception ex)
        {
            AppendLog($"[village-scan] Scan now failed: {FormatExceptionForLog(ex)}");
        }
        finally
        {
            _loopController.DisposeOperation();
            _villageStatusRoundRuntime.EndManualRun();
        }
    }

    private Task<VillageStatus> ReadVillageStatusSweepBaseStatusAsync(
        BotOptions options,
        VillageSelectionItem village,
        CancellationToken cancellationToken) =>
        (options.VillageStatusSweepDorf2Enabled || _continuousVillageStatusRound.LoginRoundPending)
            ? options.VillageStatusSweepSmithyEnabled
                ? _botService.ReadVillageStatusWithSmithyAsync(
                    options,
                    AppendLog,
                    village.Name,
                    village.Url,
                    cancellationToken)
                : _botService.ReadVillageStatusAsync(
                    options,
                    AppendLog,
                    village.Name,
                    village.Url,
                    cancellationToken)
            : _botService.ReadVillageResourceStatusAsync(
                options,
                AppendLog,
                village.Name,
                village.Url,
                cancellationToken);

    private async Task<(VillageStatus Status, bool ShouldContinue, int Attempts)> CollectVillageStatusSweepRewardsAsync(
        BotOptions options,
        VillageSelectionItem village,
        VillageStatus status,
        CancellationToken cancellationToken)
    {
        await TryQueueAutoCollectTasksAsync(options, status);
        await TryQueueAutoCollectDailyQuestsAsync(options, status);

        var villageKey = _villageSettingsStore.ResolveCanonicalKey(GetVillageKey(village));
        if (string.IsNullOrWhiteSpace(villageKey))
        {
            return (status, true, 0);
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = _botService.GetQueueItemsForDisplay()
            .Select(item => new ContinuousLoopSelectionCandidate(
                item,
                GetQueueItemVillageKey(item),
                IsQueueItemAllowedByAutomationSettings(item),
                IsAutoCollectUtilityTaskEnabledNow(item.TaskName, options)))
            .Where(candidate => ContinuousLoopSelector.IsVillageStatusSweepCollectionCandidate(
                candidate,
                villageKey,
                now))
            .ToList();
        var readyItems = ContinuousLoopSelector.SelectUtility(new ContinuousLoopUtilitySelectionInput(
                candidates,
                villageKey,
                now))
            .ReadyItems;
        if (readyItems.Count == 0)
        {
            return (status, true, 0);
        }

        var attempts = 0;
        foreach (var item in readyItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendLog(
                $"[village-scan] collecting detected rewards in '{village.Name}': "
                + $"task={item.TaskName}.");
            await ActionPacer.FromOptions(options, AppendLog).DelayAsync(
                options.ActionPacingTaskMinSeconds,
                options.ActionPacingTaskMaxSeconds,
                cancellationToken,
                "Village scan: before reward collection");
            RecordVillageBatchAttempt(item, "village-scan");
            attempts++;
            if (!await _automationQueueItemLifecycle.ExecuteAsync(
                    item,
                    options,
                    "[village-scan]",
                    AutomationRunMode.ContinuousLoop,
                    cancellationToken))
            {
                return (status, false, attempts);
            }

            MarkContinuousBrowserActivity(options);
            await ApplyPostTaskCooldownAsync(item, options, cancellationToken);
        }

        // Reward collection may change resources and leaves the browser on a task/dialog page.
        // Return to this village and refresh its selected sweep scope before making more decisions.
        status = await ReadVillageStatusSweepBaseStatusAsync(options, village, cancellationToken);
        await Dispatcher.InvokeAsync(() =>
        {
            CacheVillageStatus(status, village.Name, triggerDeferredWaitRefresh: false);
            SetActiveWorkingVillageFromStatus(status);
            ReconcilePendingBuildingQueueWithLiveStatus(status);
            SyncDashboardVillageUiFromVillages(
                status.Villages,
                status.ActiveVillage,
                activeVillageCoordX: status.ActiveVillageCoordX,
                activeVillageCoordY: status.ActiveVillageCoordY);
            if (IsStatusForSelectedVillage(status))
            {
                ApplyVillageStatusToUi(status);
            }
        });

        return (status, true, attempts);
    }

    private async Task<VillageStatus> RefreshVillageStatusSweepOptionalStatusesAsync(
        BotOptions options,
        VillageSelectionItem village,
        VillageStatus status,
        CancellationToken cancellationToken,
        string logPrefix = "[village-scan]")
    {
        var readsTroopTraining = options.VillageStatusSweepDorf2Enabled
            && (options.VillageStatusSweepBarracksEnabled
                || options.VillageStatusSweepStableEnabled
                || options.VillageStatusSweepWorkshopEnabled);
        if (readsTroopTraining)
        {
            try
            {
                var trainingOptions = options with
                {
                    TargetVillageName = village.Name,
                    TargetVillageUrl = village.Url,
                    TroopTrainingBarracksEnabled = options.VillageStatusSweepBarracksEnabled,
                    TroopTrainingStableEnabled = options.VillageStatusSweepStableEnabled,
                    TroopTrainingWorkshopEnabled = options.VillageStatusSweepWorkshopEnabled,
                };
                var queues = await _botService.ReadTroopTrainingQueuesAsync(
                    trainingOptions,
                    AppendLog,
                    status.Buildings,
                    cancellationToken);
                status = status with { TroopTrainingQueues = queues };
                await Dispatcher.InvokeAsync(() =>
                {
                    CacheVillageStatus(status, village.Name, triggerDeferredWaitRefresh: false);
                    if (IsStatusForSelectedVillage(status))
                    {
                        ApplyVillageStatusToUi(status);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog(
                    $"{logPrefix} troop-training status read failed for "
                    + $"'{village.Name}': {FormatExceptionForLog(ex)}");
            }
        }

        if (options.VillageStatusSweepDorf2Enabled && options.VillageStatusSweepBreweryEnabled)
        {
            try
            {
                var breweryStatus = await _botService.ReadBreweryCelebrationStatusAsync(
                    options with
                    {
                        TargetVillageName = village.Name,
                        TargetVillageUrl = village.Url,
                    },
                    AppendLog,
                    status.Buildings,
                    cancellationToken);
                status = status with { BreweryCelebrationStatus = breweryStatus };
                await Dispatcher.InvokeAsync(() =>
                {
                    CacheVillageStatus(status, village.Name, triggerDeferredWaitRefresh: false);
                    if (IsStatusForSelectedVillage(status))
                    {
                        ApplyVillageStatusToUi(status);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog(
                    $"{logPrefix} Brewery status read failed for "
                    + $"'{village.Name}': {FormatExceptionForLog(ex)}");
            }
        }

        return status;
    }

    private async Task RefreshVillageStatusSweepDeferredWaitsAsync(
        VillageStatus status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await RefreshDeferredConstructionWaitsAsync(status, "village_status_sweep");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog(
                $"[village-scan] construction wait refresh failed for "
                + $"'{status.ActiveVillage}': {FormatExceptionForLog(ex)}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await RefreshDeferredTroopTrainingWaitsAsync(status, "village_status_sweep");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog(
                $"[village-scan] troop-training wait refresh failed for "
                + $"'{status.ActiveVillage}': {FormatExceptionForLog(ex)}");
        }
    }

    private async Task<bool> ExecuteReadyVillageStatusSweepTasksAsync(
        BotOptions options,
        VillageSelectionItem village,
        int _,
        CancellationToken cancellationToken)
    {
        var villageKey = _villageSettingsStore.ResolveCanonicalKey(GetVillageKey(village));
        if (string.IsNullOrWhiteSpace(villageKey))
        {
            return true;
        }

        var attemptedItemIds = new HashSet<Guid>();
        var postLoginRound = _continuousVillageStatusRound.LoginRoundPending;
        var shortVillageHoldApplied = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var urgent = SelectUrgentQueueItemForVillageStatusSweep(options, attemptedItemIds,
                explicitPriorityOnly: postLoginRound);
            var next = urgent ?? SelectNextQueueItemForVillageStatusSweep(villageKey, attemptedItemIds);
            if (next is null)
            {
                if (postLoginRound && !shortVillageHoldApplied)
                {
                    var holdCandidates = _botService.GetQueueItemsForDisplay()
                        .Where(item => !attemptedItemIds.Contains(item.Id))
                        .Select(item => new ContinuousLoopSelectionCandidate(
                            item,
                            GetQueueItemVillageKey(item),
                            IsQueueItemAllowedByAutomationSettings(item),
                            ContinuousLoopSelector.IsUtilityTask(item.TaskName)
                                && IsAutoCollectUtilityTaskEnabledNow(item.TaskName, options)))
                        .ToList();
                    var hold = AutomationQueueSelector.Select(
                        new AutomationQueueSelectionInput(
                            holdCandidates,
                            GetContinuousLoopConsideredGroupsInOrder(),
                            _automationPassRuntime.SnapshotVillageBatch(_activeWorkingVillageKey),
                            villageKey,
                            DateTimeOffset.UtcNow,
                            options.ShortVillageDeferSeconds,
                            Preview: true),
                        SelectReadyConstructionForAutomationPass);
                    if (hold.Reason == AutomationQueueSelectionReason.ShortVillageHold
                        && hold.HoldUntil is { } holdUntil
                        && holdUntil > DateTimeOffset.UtcNow)
                    {
                        shortVillageHoldApplied = true;
                        AppendLog($"[village-round] waiting up to {Math.Ceiling((holdUntil - DateTimeOffset.UtcNow).TotalSeconds)}s "
                            + $"for a soon-ready task in '{village.Name}'.");
                        var holdDelay = holdUntil - DateTimeOffset.UtcNow;
                        if (holdDelay > TimeSpan.Zero)
                            await Task.Delay(holdDelay, cancellationToken);
                        continue;
                    }
                }
                if (postLoginRound)
                {
                    LogRomanLoginFillOutcome(villageKey, village.Name);
                }
                if (_automationPassRuntime.SnapshotVillageBatch(_activeWorkingVillageKey).HasUrgentPreemption)
                {
                    _automationPassRuntime.CompleteUrgentPreemption(_activeWorkingVillageKey);
                    AppendLog(
                        $"[village-scan] urgent work complete; '{village.Name}' has no more ready work.");
                }
                return true;
            }

            attemptedItemIds.Add(next.Id);
            if (urgent is not null
                && !string.Equals(
                    GetQueueItemVillageKey(urgent),
                    _activeWorkingVillageKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                _automationPassRuntime.RecordUrgentPreemption(
                    _activeWorkingVillageKey,
                    GetQueueItemVillageKey(urgent));
                AppendLog(
                    $"[village-scan] urgent preemption task='{urgent.TaskName}' "
                    + $"village='{GetQueueItemVillageName(urgent) ?? "-"}'; "
                    + $"'{village.Name}' will resume afterward.");
            }
            else
            {
                AppendLog(
                    $"[village-scan] reacting in '{village.Name}': "
                    + $"group={next.Group}, task={next.TaskName}.");
            }
            await ActionPacer.FromOptions(options, AppendLog).DelayAsync(
                options.ActionPacingTaskMinSeconds,
                options.ActionPacingTaskMaxSeconds,
                cancellationToken,
                "Village scan: before task");
            RecordVillageBatchAttempt(next, "village-scan");
            var shouldContinue = await _automationQueueItemLifecycle.ExecuteAsync(
                next,
                options,
                "[village-scan]",
                AutomationRunMode.ContinuousLoop,
                cancellationToken);
            MarkContinuousBrowserActivity(options);
            if (!shouldContinue)
            {
                return false;
            }

            await ApplyPostTaskCooldownAsync(next, options, cancellationToken);
        }

    }

    private void LogRomanLoginFillOutcome(string villageKey, string villageName)
    {
        if (!_villageStatusCache.TryGetByKey(villageKey, out var status))
        {
            return;
        }

        var pending = _botService.GetQueueItemsForDisplay()
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => IsConstructionQueueTask(item.TaskName))
            .Where(item => string.Equals(GetQueueItemVillageKey(item), villageKey, StringComparison.OrdinalIgnoreCase))
            .Where(IsQueueItemAllowedByAutomationSettings)
            .ToList();
        var decision = RomanLoginFillPolicy.Resolve(
            status,
            _travianPlusActive,
            pending.Any(item => ConstructionQueueState.IsResourceConstructionTask(item.TaskName)),
            pending.Any(item => !ConstructionQueueState.IsResourceConstructionTask(item.TaskName)));
        switch (decision.State)
        {
            case RomanLoginFillState.Complete:
                AppendLog(
                    $"[construction-login-fill] completed village='{villageName}': " +
                    $"live Roman queue is 3/3 ({decision.ResourceCount} resource, {decision.BuildingCount} building).");
                break;
            case RomanLoginFillState.NeedsComplementaryCategory:
                AppendLog(
                    $"[construction-login-fill] stopped village='{villageName}' at " +
                    $"{decision.ResourceCount + decision.BuildingCount}/3: complementary Roman category is queued but not runnable.");
                break;
            case RomanLoginFillState.Blocked:
                AppendLog(
                    $"[construction-login-fill] stopped village='{villageName}' at " +
                    $"{decision.ResourceCount + decision.BuildingCount}/3: {decision.Reason}.");
                break;
        }
    }

    private QueueItem? SelectNextQueueItemForVillageStatusSweep(
        string villageKey,
        IReadOnlySet<Guid> attemptedItemIds)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = _botService.GetQueueItemsForDisplay()
            .Select(item => new ContinuousLoopSelectionCandidate(
                item,
                GetQueueItemVillageKey(item),
                IsQueueItemAllowedByAutomationSettings(item),
                IsUtilityEnabled: false))
            .Where(candidate => ContinuousLoopSelector.IsVillageStatusSweepCandidate(candidate, villageKey))
            .ToList();
        var plan = ContinuousLoopSelector.CreatePlan(new ContinuousLoopSelectionInput(
            candidates,
            GetContinuousLoopConsideredGroupsInOrder()));
        var villageKeys = candidates.ToDictionary(candidate => candidate.Item.Id, candidate => candidate.VillageKey);

        foreach (var group in plan.OrderedGroups)
        {
            var villageItems = ContinuousLoopSelector.SelectVillageItems(
                plan.OrderedItemsByGroup[group],
                villageKeys,
                villageKey);
            if (villageItems.Count == 0)
            {
                continue;
            }

            var candidate = group == QueueGroup.Construction
                ? SelectNextConstructionQueueItem(villageItems, now, out _)
                : ContinuousLoopSelector.SelectReadyGroupHead(villageItems, now);
            if (candidate is not null && !attemptedItemIds.Contains(candidate.Id))
            {
                return candidate;
            }
        }

        return null;
    }

    private static readonly TimeSpan LoopPickVerboseThrottle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GoldClubInactiveRecheckInterval = TimeSpan.FromMinutes(10);
    private async Task TriggerQueueAutoRunAsync()
    {
        if (IsFreezeActive || IsSessionSleeping)
        {
            return;
        }

        if (_autoQueueRunning)
        {
            _automationDesk.Wake(AutomationWakeReason.QueueChanged);
            return;
        }

        if (IsContinuousLoopRunning())
        {
            return;
        }

        AutomationRunContext context;
        try
        {
            context = CreateAutomationRunContext();
        }
        catch (Exception ex)
        {
            AppendLog($"[automation] Auto Queue start failed: {FormatExceptionForLog(ex)}");
            return;
        }

        _automationPassRuntime.BeginAutoQueueRun(Interlocked.Increment(ref _operationCounter));
        LogConservativeAutomationWarnings(AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions()));
        AppendLog($"[AUTOQ {_automationPassRuntime.AutoQueueRunLogId}] START");
        var result = await _automationDesk.StartAsync(new AutomationStart(AutomationRunMode.AutoQueue, context));
        if (result is AutomationStartResult.Busy)
        {
            AppendLog("[automation] Auto Queue start skipped because another run owns the gate.");
        }
    }

    private QueueItem? SelectUrgentQueueItemForVillageStatusSweep(
        BotOptions options,
        IReadOnlySet<Guid> attemptedItemIds,
        bool explicitPriorityOnly = false)
    {
        var candidates = _botService.GetQueueItemsForDisplay()
            .Where(item => !attemptedItemIds.Contains(item.Id))
            .Where(item => !explicitPriorityOnly || item.Priority > 0)
            .Select(item => new ContinuousLoopSelectionCandidate(
                item,
                GetQueueItemVillageKey(item),
                IsQueueItemAllowedByAutomationSettings(item),
                ContinuousLoopSelector.IsUtilityTask(item.TaskName)
                    && IsAutoCollectUtilityTaskEnabledNow(item.TaskName, options)))
            .ToList();
        var batch = _automationPassRuntime.SnapshotVillageBatch(_activeWorkingVillageKey);
        var result = AutomationQueueSelector.Select(
            new AutomationQueueSelectionInput(
                candidates,
                GetContinuousLoopConsideredGroupsInOrder(),
                batch,
                _activeWorkingVillageKey,
                DateTimeOffset.UtcNow,
                options.ShortVillageDeferSeconds,
                Preview: true),
            SelectReadyConstructionForAutomationPass);
        return result.Reason == AutomationQueueSelectionReason.UrgentPreemption
            ? result.Selected
            : null;
    }

    private async ValueTask<VillageStatusRoundVisitResult> VisitVillageStatusRoundAsync(
        BotOptions options,
        VillageSelectionItem village,
        int villageNumber,
        int villageCount,
        bool inboxStatusChecked,
        CancellationToken cancellationToken)
    {
        var postLoginRound = _continuousVillageStatusRound.LoginRoundPending;
        using var villageActivity = _dashboardActivityTracker.Begin(
            $"{(postLoginRound ? "Village round" : "Village scan")} ({villageNumber}/{villageCount}): {village.Name}");
        cancellationToken.ThrowIfCancellationRequested();
        var inboxCheckedDuringVisit = false;
        try
        {
            var port = new DelegateVillageStatusReactionPort<VillageSelectionItem, VillageStatus>(
                (targetVillage, token) => new ValueTask<VillageStatus>(
                    ReadVillageStatusSweepBaseStatusAsync(options, targetVillage, token)),
                async (targetVillage, status, _) =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        CacheVillageStatus(status, targetVillage.Name, triggerDeferredWaitRefresh: false);
                        SetActiveWorkingVillageFromStatus(status);
                        ReconcilePendingBuildingQueueWithLiveStatus(status);
                        if (postLoginRound)
                        {
                            PrepareConstructionLoginFill(
                                "village-round",
                                targetVillage.Name,
                                GetVillageKey(targetVillage),
                                verifiedStatus: status);
                        }
                        SyncDashboardVillageUiFromVillages(
                            status.Villages,
                            status.ActiveVillage,
                            activeVillageCoordX: status.ActiveVillageCoordX,
                            activeVillageCoordY: status.ActiveVillageCoordY);
                        if (IsStatusForSelectedVillage(status))
                        {
                            ApplyVillageStatusToUi(status);
                        }
                    });
                },
                async token =>
                {
                    inboxCheckedDuringVisit = true;
                    await RefreshInboxIndicatorsForVillageStatusSweepAsync(options, token);
                },
                async (targetVillage, status, token) =>
                {
                    var result = await CollectVillageStatusSweepRewardsAsync(
                        options,
                        targetVillage,
                        status,
                        token);
                    return new VillageStatusCollectionResult<VillageStatus>(
                        result.Status,
                        result.ShouldContinue,
                        result.Attempts);
                },
                (targetVillage, status, token) => new ValueTask<VillageStatus>(
                    RefreshVillageStatusSweepOptionalStatusesAsync(options, targetVillage, status, token)),
                (status, token) => new ValueTask(RefreshVillageStatusSweepDeferredWaitsAsync(status, token)),
                async (targetVillage, token) =>
                {
                    AppendLog($"[village-scan] updated '{targetVillage.Name}'.");
                    await _continuousRuntimeItemPreparation.PrepareAsync(
                        options,
                        token,
                        new AutomationRuntimeVillage(
                            GetVillageKey(targetVillage),
                            targetVillage.Name,
                            targetVillage.Url,
                            targetVillage.IsCapital,
                            targetVillage.CoordX,
                            targetVillage.CoordY));
                },
                (targetVillage, attempts, token) => new ValueTask<bool>(
                    ExecuteReadyVillageStatusSweepTasksAsync(options, targetVillage, attempts, token)));
            return await _villageStatusReactionCoordinator.RunAsync(
                village,
                options.VillageStatusSweepDorf1Enabled || postLoginRound,
                inboxStatusChecked,
                port,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"[village-scan] skipped '{village.Name}': {FormatExceptionForLog(ex)}");
            return new VillageStatusRoundVisitResult(true, inboxCheckedDuringVisit);
        }
    }

    private AutomationRunContext CreateAutomationRunContext()
    {
        var accountKey = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            throw new InvalidOperationException("No active account is selected.");
        }

        var baseUrl = LoadBotOptions().BaseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var officialServerRoot))
        {
            throw new InvalidOperationException("The active account has no valid Official Travian server URL.");
        }

        return new AutomationRunContext(
            accountKey,
            officialServerRoot,
            _botService.BrowserGeneration);
    }

    private async Task<bool> EnsureVillageMembershipVerifiedBeforeAutomationAsync(
        BotOptions options,
        CancellationToken cancellationToken)
    {
        var accountName = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (!string.Equals(_villageMembershipPreflightAccount, accountName, StringComparison.OrdinalIgnoreCase))
        {
            _villageMembershipPreflightAccount = accountName;
            _lastVillageMembershipPreflightAtUtc = DateTimeOffset.MinValue;
            _villageMembershipVerificationNotBeforeUtc = DateTimeOffset.MinValue;
            _failedVillageMembershipSignature = null;
            _confirmedVillageMembershipSignature = null;
            _confirmedVillageMembershipAtUtc = DateTimeOffset.MinValue;
        }

        if (_failedVillageMembershipSignature is not null
            && now < _villageMembershipVerificationNotBeforeUtc)
        {
            return false;
        }

        if (now - _lastVillageMembershipPreflightAtUtc < VillageMembershipPreflightInterval)
        {
            return true;
        }

        var knownVillages = await Dispatcher.InvokeAsync(() =>
        {
            var source = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
                ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
                ?? Enumerable.Empty<VillageSelectionItem>();
            return source
                .Where(village => !string.IsNullOrWhiteSpace(village.Name)
                    && !string.Equals(village.Name, "-", StringComparison.Ordinal))
                .GroupBy(GetVillageKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        });
        if (knownVillages.Count == 0)
        {
            return true;
        }

        IReadOnlyList<Village> liveVillages;
        try
        {
            liveVillages = await _botService.ReadCurrentVillageMembershipAsync(
                options,
                AppendLog,
                cancellationToken);
            _lastVillageMembershipPreflightAtUtc = now;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _failedVillageMembershipSignature = "sidebar-read-failed";
            _villageMembershipVerificationNotBeforeUtc = now + VillageMembershipVerificationRetryDelay;
            AppendLog(
                "[village-membership] sidebar preflight failed; state-changing automation is paused "
                + $"until {_villageMembershipVerificationNotBeforeUtc:HH:mm}: {FormatExceptionForLog(ex)}");
            return false;
        }

        var knownKeys = knownVillages.Select(GetVillageKey).ToList();
        var liveKeys = liveVillages
            .Select(village => GetVillageKey(village.Url, village.CoordX, village.CoordY, village.Name))
            .ToList();
        if (!VillageListUpdatePolicy.HasPotentialMembershipMismatch(liveKeys, knownKeys, key => key))
        {
            _failedVillageMembershipSignature = null;
            _confirmedVillageMembershipSignature = null;
            _villageMembershipVerificationNotBeforeUtc = DateTimeOffset.MinValue;
            return true;
        }

        var mismatchSignature = string.Join(";", liveKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        if (string.Equals(
                mismatchSignature,
                _confirmedVillageMembershipSignature,
                StringComparison.OrdinalIgnoreCase)
            && now - _confirmedVillageMembershipAtUtc < VillageMembershipConfirmationRefreshInterval)
        {
            return true;
        }

        if (string.Equals(mismatchSignature, _failedVillageMembershipSignature, StringComparison.OrdinalIgnoreCase)
            && now < _villageMembershipVerificationNotBeforeUtc)
        {
            return false;
        }

        AppendLog(
            $"[village-membership] live sidebar differs from the verified UI list "
            + $"({liveVillages.Count}/{knownVillages.Count}); blocking automation until profile verification completes.");
        try
        {
            var snapshot = await _botService.VerifyVillageMembershipAsync(
                options,
                AppendLog,
                cancellationToken);
            if (snapshot.Villages.Count == 0)
            {
                throw new InvalidOperationException("The player profile returned no villages.");
            }

            await Dispatcher.InvokeAsync(() =>
            {
                SyncDashboardVillageUiFromVillages(
                    snapshot.Villages,
                    snapshot.ActiveVillage,
                    activeVillageCoordX: snapshot.ActiveVillageCoordX,
                    activeVillageCoordY: snapshot.ActiveVillageCoordY,
                    allowVillageRemoval: true);
                ReconcileConfirmedVillageList(snapshot.Villages, "automation_membership_preflight");
                QueueNewVillagesForFirstAnalysis(snapshot.Villages);
            });
            _failedVillageMembershipSignature = null;
            _villageMembershipVerificationNotBeforeUtc = DateTimeOffset.MinValue;
            _confirmedVillageMembershipSignature = mismatchSignature;
            _confirmedVillageMembershipAtUtc = DateTimeOffset.UtcNow;
            AppendLog(
                $"[village-membership] ownership verified; automation may resume with "
                + $"{snapshot.Villages.Count} confirmed village(s).");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _failedVillageMembershipSignature = mismatchSignature;
            _villageMembershipVerificationNotBeforeUtc = DateTimeOffset.UtcNow
                + VillageMembershipVerificationRetryDelay;
            AppendLog(
                "[village-membership] profile verification failed; state-changing automation remains paused "
                + $"until {_villageMembershipVerificationNotBeforeUtc:HH:mm}: {FormatExceptionForLog(ex)}");
            return false;
        }
    }

    private void AutomationDesk_Updated(object? sender, AutomationUpdate update)
    {
        if (update.Event is AutomationEvent.RunStarted)
        {
            UpdateExecutionStateIndicatorOnUiThread();
            return;
        }

        if (update.Event is AutomationEvent.RunDeferred deferredEvent)
        {
            AppendLog(
                $"[automation] Run {deferredEvent.RunId.Value} deferred after "
                + $"{deferredEvent.Failure.Kind}; retry at {deferredEvent.RetryAt:HH:mm:ss}.");
            return;
        }

        if (update.Event is AutomationEvent.WakeAccepted wakeEvent)
        {
            AppendLog($"[automation] Wake requested: reason={wakeEvent.Reason}.");
            return;
        }

        if (update.Event is not AutomationEvent.RunStopped
            && update.Event is not AutomationEvent.RunFaulted)
        {
            return;
        }

        var runMode = update.Event switch
        {
            AutomationEvent.RunStopped stopped => stopped.RunMode,
            AutomationEvent.RunFaulted runFaulted => runFaulted.RunMode,
            _ => (AutomationRunMode?)null,
        };
        if (update.Event is AutomationEvent.RunFaulted faultEvent)
        {
            AppendLog(
                $"[automation] Run {faultEvent.RunId.Value} faulted: "
                + $"kind={faultEvent.Failure.Kind}, code={faultEvent.Failure.DiagnosticCode}.");
        }

        if (runMode == AutomationRunMode.ContinuousLoop)
        {
            UpdateExecutionStateIndicatorOnUiThread();
            CompleteContinuousLoopPresentation();
            return;
        }

        UpdateExecutionStateIndicatorOnUiThread();
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_startContinuousLoopAfterQueueStop
                && _isLoggedIn
                && !_uiBusy
                && !_autoQueueRunning
                && !IsContinuousLoopRunning())
            {
                _startContinuousLoopAfterQueueStop = false;
                StartContinuousLoopRunner();
                return;
            }

            _startContinuousLoopAfterQueueStop = false;
            if ((_restartAutoQueueAfterLanguageGate || _restartAutoQueueAfterSettingsChange)
                && _isLoggedIn
                && !_uiBusy
                && !_autoQueueRunning
                && !IsContinuousLoopRunning())
            {
                _restartAutoQueueAfterLanguageGate = false;
                _restartAutoQueueAfterSettingsChange = false;
                _ = TriggerQueueAutoRunAsync();
                return;
            }

            _restartAutoQueueAfterLanguageGate = false;
            _restartAutoQueueAfterSettingsChange = false;
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

        // Queued items must not auto-start while the bot is idle. They may begin when the user
        // presses "Start bot", or be picked up by a runner that is already executing.
        var alreadyRunning = _autoQueueRunning || IsContinuousLoopRunning();
        if (!alreadyRunning)
        {
            return;
        }

        var hasEligibleWork = HasQueueAutoRunEligibleWork();
        if (!hasEligibleWork)
        {
            UpdateExecutionStateIndicator();
            return;
        }

        switch (AutomationEnqueuePolicy.Resolve(_automationDesk.Current, hasEligibleWork))
        {
            case AutomationEnqueueAction.WakeAutoQueue:
                _automationDesk.Wake(AutomationWakeReason.QueueChanged);
                return;
            case AutomationEnqueueAction.WakeContinuousLoop:
                RequestContinuousAutomationWake();
                return;
            default:
                return;
        }
    }

    private void RequestContinuousAutomationWake()
    {
        _automationPassRuntime.RequestImmediateWork();
        _automationDesk.Wake(AutomationWakeReason.QueueChanged);
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

    private IReadOnlyList<QueueGroup> GetContinuousLoopEnabledGroupsInOrder()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(GetContinuousLoopEnabledGroupsInOrder);
        }

        var groups = _automationLoopTasks
            .Where(item => item.IsEnabled)
            .Select(item => QueueGroupCatalog.TryParse(item.TaskName, out var group) ? group : (QueueGroup?)null)
            .Where(group => group.HasValue)
            .Select(group => group!.Value)
            .ToList();
        if (!groups.Contains(QueueGroup.Demolish))
        {
            groups.Add(QueueGroup.Demolish);
        }
        return groups;
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

        // Account is a queue category, not a user-configurable automation group. Keep it permanently
        // considered even when every village toggle group is disabled.
        var ordered = new List<QueueGroup> { QueueGroup.Account };
        ordered.AddRange(GetContinuousLoopEnabledGroupsInOrder());
        var seen = ordered.ToHashSet();

        foreach (var (_, enabledGroups) in _villageSettingsStore.GetEnabledVillagesGroups())
        {
            foreach (var key in enabledGroups ?? VillageSettingsStore.DefaultEnabledGroups)
            {
                if (QueueGroupCatalog.TryParse(key, out var group)
                    && group != QueueGroup.Account
                    && seen.Add(group))
                {
                    ordered.Add(group);
                }
            }
        }

        return ordered;
    }

    private Dictionary<string, string> BuildAutomaticReinforcementPayload(BotOptions options, IReadOnlyList<string> selectedSources)
    {
        var payload = new ReinforcementsPayload(
            Enabled: true,
            TargetVillageName: options.ReinforcementsTargetVillageName,
            SourceVillageNames: selectedSources,
            TroopRules: BuildReinforcementRulesForRun()).ToDictionary();
        payload[BotOptionPayloadKeys.ReinforcementsSendMinMinutes] = options.ReinforcementsSendMinMinutes.ToString();
        payload[BotOptionPayloadKeys.ReinforcementsSendMaxMinutes] = options.ReinforcementsSendMaxMinutes.ToString();
        return payload;
    }

    private TimeSpan CalculateNextReinforcementAutomaticSendDelay(BotOptions options)
    {
        return ReinforcementSendDefaults.CalculateSendDelay(
            options.ReinforcementsSendMinMinutes,
            options.ReinforcementsSendMaxMinutes);
    }

    private bool ScheduleAutomaticReinforcementSend(Dictionary<string, string> payload, TimeSpan delay, BotOptions options)
    {
        var item = _botService.EnqueueRuntime("send_reinforcements_between_villages", "Reinforcements", payload, priority: -50, maxRetries: 0);
        if (delay <= TimeSpan.Zero)
        {
            return true;
        }

        if (!_botService.UpdateDeferredQueueItem(item.Id, payload, delay))
        {
            _botService.RemoveQueueItem(item.Id);
            AppendLog("Reinforcements: failed to schedule next automatic send.");
            return false;
        }

        AppendLog(
            $"Reinforcements: next automatic send scheduled in {FormatCountdown((int)Math.Ceiling(delay.TotalSeconds))} "
            + $"(range={ReinforcementSendDefaults.NormalizeSendMinMinutes(options.ReinforcementsSendMinMinutes)}-"
            + $"{ReinforcementSendDefaults.NormalizeSendMaxMinutes(options.ReinforcementsSendMaxMinutes)}m).");
        RequestQueueUiRefresh();
        return true;
    }

    private void ScheduleNextReinforcementSendAfterSuccess(BotOptions options)
    {
        if (!GetContinuousLoopEnabledGroupsInOrder().Contains(QueueGroup.Reinforcements)
            || !CanRunReinforcements(options, out _))
        {
            return;
        }

        var hasActiveReinforcementItem = _botService.GetQueueItemsForDisplay()
            .Any(item =>
                string.Equals(item.TaskName, "send_reinforcements_between_villages", StringComparison.OrdinalIgnoreCase)
                && item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);
        if (hasActiveReinforcementItem)
        {
            return;
        }

        var selectedSources = options.ReinforcementsSourceVillageNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !string.Equals(name, options.ReinforcementsTargetVillageName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var payload = BuildAutomaticReinforcementPayload(options, selectedSources);
        ScheduleAutomaticReinforcementSend(payload, CalculateNextReinforcementAutomaticSendDelay(options), options);
    }

    // Villages discovered at runtime (e.g. the user just founded one) that still need a one-time dorf1/dorf2
    // analysis so automation knows their layout. Dispatcher-owned (same threading as _villageStatusCache):
    // filled on the UI thread from village reads, drained one-per-loop-pass via Dispatcher round-trips.
    private sealed record PendingVillageAnalysis(string Key, string Name, string? Url, int Attempts);
    private readonly Dictionary<string, PendingVillageAnalysis> _villagesPendingFirstAnalysis = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxFirstAnalysisAttempts = 3;

    // Queues any confirmed village we have no cached dorf1/dorf2 layout for. Gated by the same setting as the
    // login-time analysis so disabling startup analysis also disables this runtime variant (and avoids login
    // noise, since the login flow caches villages before the first periodic refresh runs).
    private void QueueNewVillagesForFirstAnalysis(IReadOnlyList<Village> villages)
    {
        if (!LoadBotOptions().PostLoginAnalyzeNewVillages)
        {
            return;
        }

        var missing = NewVillageStartupAnalyzer.FindVillagesWithoutKnownStatus(villages, _villageStatusCache.Snapshot);
        foreach (var village in missing)
        {
            var name = NormalizeVillageName(village.Name);
            var key = village.CoordX.HasValue && village.CoordY.HasValue
                ? VillageKey.FromCoords(village.CoordX.Value, village.CoordY.Value)
                : name;
            if (name is null || key is null || _villagesPendingFirstAnalysis.ContainsKey(key))
            {
                continue;
            }

            _villagesPendingFirstAnalysis[key] = new PendingVillageAnalysis(key, name, village.Url, 0);
            AppendLog($"[new-village-runtime] Discovered '{name}' ({key}) without cached dorf1/dorf2 status. Queued for one-time analysis.");
        }
    }

    private bool HasCachedDorf1Dorf2Status(string villageKey)
        => _villageStatusCache.TryGetByKey(villageKey, out var status)
           && status.ResourceFields is { Count: > 0 }
           && status.Buildings is { Count: > 0 };

    // Picks the next village still needing analysis (Dispatcher thread). Drops entries that became cached
    // meanwhile, or that exhausted their attempts, and counts the attempt for the returned one.
    private PendingVillageAnalysis? TakeNextVillagePendingFirstAnalysis()
    {
        foreach (var key in _villagesPendingFirstAnalysis.Keys.ToList())
        {
            var entry = _villagesPendingFirstAnalysis[key];
            if (HasCachedDorf1Dorf2Status(key))
            {
                _villagesPendingFirstAnalysis.Remove(key);
                continue;
            }

            if (entry.Attempts >= MaxFirstAnalysisAttempts)
            {
                _villagesPendingFirstAnalysis.Remove(key);
                AppendLog($"[new-village-runtime] Giving up first analysis for '{entry.Name}' after {entry.Attempts} attempt(s).");
                continue;
            }

            _villagesPendingFirstAnalysis[key] = entry with { Attempts = entry.Attempts + 1 };
            return entry with { Attempts = entry.Attempts + 1 };
        }

        return null;
    }

    // Reads dorf1/dorf2 once for a runtime-discovered village so automation knows its layout — the same
    // one-time analysis done for un-analyzed villages at login. Drains one village per loop pass and never
    // runs while sleeping (no browser activity during sleep). Each task switches to its own village anyway,
    // so leaving the browser on the analyzed village self-corrects on the next pick.
    private async Task MaybeAnalyzeNewVillageDuringContinuousLoopAsync(BotOptions options, CancellationToken token)
    {
        if (IsSessionSleeping)
        {
            return;
        }

        var pick = await Dispatcher.InvokeAsync(TakeNextVillagePendingFirstAnalysis);
        if (pick is null)
        {
            return;
        }

        var entry = pick;
        using var activity = _dashboardActivityTracker.Begin($"Analyzing new village: {entry.Name}");
        try
        {
            AppendLog($"[new-village-runtime] Analyzing '{entry.Name}' ({entry.Key}) (dorf1/dorf2) so automation knows its layout.");
            var status = await _botService.ReadVillageStatusAsync(options, AppendLog, entry.Name, entry.Url, token);
            await Dispatcher.InvokeAsync(() =>
            {
                CacheVillageStatus(status, entry.Name);
                ReconcilePendingBuildingQueueWithLiveStatus(status);
                _villagesPendingFirstAnalysis.Remove(entry.Key);
            });
            AppendLog($"[new-village-runtime] Cached '{entry.Name}' ({entry.Key}): fields={status.ResourceFields.Count}, buildings={status.Buildings.Count}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountAccessException ex)
        {
            await HoldAccountAutomationAsync(ex);
        }
        catch (Exception ex)
        {
            // Keep it queued (attempt already counted) for a later pass; a transient nav/read error must not
            // silently drop the analysis. TakeNext drops it once attempts are exhausted.
            AppendLog($"[new-village-runtime] Could not analyze '{entry.Name}' ({entry.Key}): {ex.Message}");
        }
    }

    private async Task EnsureContinuousLoopConstructionStatusAsync(BotOptions options, CancellationToken cancellationToken)
    {
        if (!_automationSessionRuntime.ConstructionStatusNeedsSync
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
            using var activity = _dashboardActivityTracker.Begin("Refreshing construction status");
            var status = await ReadVillageStatusWithRetryAsync(
                options,
                cancellationToken,
                resourceOnly: false,
                forceCurrentVillage: true);
            await Dispatcher.InvokeAsync(() =>
            {
                CacheVillageStatus(status);
                ReconcilePendingBuildingQueueWithLiveStatus(status);
                if (IsStatusForSelectedVillage(status))
                {
                    _lastBuildingStatus = status;
                    ApplyVillageStatusToUi(status);
                    PopulateBuildingsTab(status);
                }
            });
            _automationSessionRuntime.MarkConstructionStatusSynchronized();
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

    private async Task EnsureContinuousFarmListsReadyAsync(BotOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var farmingEnabled = GetContinuousLoopEnabledGroupsInOrder().Contains(QueueGroup.Farming);
        if (!farmingEnabled || _farmingOperationBusy)
        {
            return;
        }

        if (!_farmListsWorkflow.AutomationSnapshot.NeedsAnalysis)
        {
            return;
        }

        AppendLog("Continuous farming: analyzing farmlists before runtime send.");
        try
        {
            using var activity = _dashboardActivityTracker.Begin("Analyzing farm lists");
            await RefreshFarmListsFromServerAsync(options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"Continuous farming analyze failed: {ex.Message}");
        }
    }

    private QueueItem? SelectReadyConstructionForAutomationPass(
        IReadOnlyList<QueueItem> villageItems,
        DateTimeOffset now,
        bool preview)
    {
        var candidate = SelectNextConstructionQueueItem(villageItems, now, out _, preview);
        return candidate is not null
            && IsConstructionGroupReady(
                allowWorkerValidationForReadyItem: true,
                suppressLog: preview)
                ? candidate
                : null;
    }

    // Whether a queue item's automation group is enabled for ITS OWN village. Lets a group turned off on
    // village B block B's tasks even while another village is selected/worked. Village-less (global)
    // tasks and unknown villages fall back to the account default group set.
    private bool IsQueueItemGroupEnabledForItsVillage(QueueItem item)
    {
        if (item.Group == QueueGroup.Demolish)
        {
            return true;
        }

        if (item.Payload.TryGetValue(BotOptionPayloadKeys.AutoAddedBy, out var autoAddedBy)
            && string.Equals(
                autoAddedBy,
                BotOptionPayloadKeys.AutoAddedByHeroRallyPointRepair,
                StringComparison.OrdinalIgnoreCase))
        {
            return IsGroupEnabledForVillage(GetQueueItemVillageKey(item), QueueGroup.Hero);
        }

        return IsGroupEnabledForVillage(GetQueueItemVillageKey(item), item.Group);
    }

    // Village settings are authoritative for all automated queue execution: if the village Auto toggle
    // is off, or the task's group is off for that village, the item stays queued but is ignored.
    private bool IsQueueItemAllowedByAutomationSettings(QueueItem item)
    {
        if (string.Equals(item.TaskName, "send_farmlists", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(GetQueueItemVillageKey(item))
            && string.IsNullOrWhiteSpace(GetQueueItemVillageName(item)))
        {
            return false;
        }

        // Account is an always-on queue category. Its tasks are never gated by village Auto or by a
        // per-village automation group toggle.
        if (item.Group == QueueGroup.Account)
        {
            return true;
        }

        return IsQueueItemVillageEnabled(item) && IsQueueItemGroupEnabledForItsVillage(item);
    }

    // Whether an automation group is enabled for a specific village key (null/unknown villages fall back
    // to the account default group set). Shared by per-item gating and per-village runtime generation.
    private bool IsGroupEnabledForVillage(string? villageKey, QueueGroup group)
    {
        if (group == QueueGroup.Account)
        {
            return true;
        }

        if (group == QueueGroup.Farming && CurrentGoldClubAvailability != true)
        {
            return false;
        }

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
        if (group == QueueGroup.Account)
        {
            return true;
        }

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

        // Stable coordinate key (same reason as ApplySelectedVillageToPayload): keep per-village runtime
        // items bound to the right village even when names collide or change.
        var villageKey = GetVillageKey(village);
        if (!string.IsNullOrWhiteSpace(villageKey))
        {
            payload[BotOptionPayloadKeys.TargetVillageKey] = villageKey;
        }

        ApplyConstructFasterSettingsToPayload(payload, villageKey, village.Name);
        payload[BotOptionPayloadKeys.NpcTradeEnabled] = IsNpcTradeEnabledForVillageKey(villageKey) ? "true" : "false";
        return payload;
    }

    private static bool HasEnabledTroopTrainingBuilding(BotOptions options)
    {
        return options.TroopTrainingBarracksEnabled
            || options.TroopTrainingStableEnabled
            || options.TroopTrainingWorkshopEnabled;
    }

    internal static bool ShouldGateTroopTrainingEnqueueOnActiveQueue(BotOptions options)
    {
        return (options.TroopTrainingBarracksEnabled && HasTroopTrainingQueueLimit(options.TroopTrainingBarracksMaxQueueHours))
            || (options.TroopTrainingStableEnabled && HasTroopTrainingQueueLimit(options.TroopTrainingStableMaxQueueHours))
            || (options.TroopTrainingWorkshopEnabled && HasTroopTrainingQueueLimit(options.TroopTrainingWorkshopMaxQueueHours));
    }

    private static bool HasTroopTrainingQueueLimit(string? maxQueueHours)
    {
        if (string.IsNullOrWhiteSpace(maxQueueHours)
            || string.Equals(maxQueueHours.Trim(), "no_limit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(maxQueueHours.Trim(), out var hours) && hours > 0;
    }

    private int? ResolveActiveTroopTrainingQueueWaitSeconds(VillageSelectionItem village, BotOptions trainingOptions)
    {
        var enabledBuildingTypes = new HashSet<TroopTrainingBuildingType>();
        if (trainingOptions.TroopTrainingBarracksEnabled)
        {
            enabledBuildingTypes.Add(TroopTrainingBuildingType.Barracks);
        }

        if (trainingOptions.TroopTrainingStableEnabled)
        {
            enabledBuildingTypes.Add(TroopTrainingBuildingType.Stable);
        }

        if (trainingOptions.TroopTrainingWorkshopEnabled)
        {
            enabledBuildingTypes.Add(TroopTrainingBuildingType.Workshop);
        }

        if (enabledBuildingTypes.Count == 0)
        {
            return null;
        }

        _villageStatusCache.TryGetByKey(GetVillageKey(village), out var status);

        var relevantQueues = status?.TroopTrainingQueues?
            .Where(item => item.Exists && enabledBuildingTypes.Contains(item.BuildingType))
            .ToList();
        if (relevantQueues is null || relevantQueues.Count == 0)
        {
            return null;
        }

        var now = GetServerNow();
        var remainingSeconds = relevantQueues
            .Select(item => Math.Max(0, item.Finish?.RemainingSecondsAt(now) ?? item.RemainingSeconds ?? 0))
            .ToList();
        if (remainingSeconds.Any(seconds => seconds <= 0))
        {
            return null;
        }

        return remainingSeconds.Min();
    }

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
            villageItems => SelectNextConstructionQueueItem(villageItems, now, out _, preview: true),
            ref rotationKey) is not null;
    }

    // Per-village head selection for non-construction groups: returns this village's first item when it
    // is Pending and due, otherwise null (so rotation moves on to another village). Preserves the strict
    // in-order behavior within a village that the old single-head logic had.
    private QueueItem? SelectNextConstructionQueueItem(
        IReadOnlyList<QueueItem> orderedGroupItems,
        DateTimeOffset now,
        out string? skipReason,
        bool preview = false)
    {
        var firstItem = orderedGroupItems.FirstOrDefault();
        var availability = ResolveConstructionQueueAvailability(firstItem, now);
        var allowIndependentCategoryLookAhead = ConstructionQueueState.SupportsIndependentConstructionCategories(
            firstItem is null ? null : ResolveBuildingStatusForQueueItem(firstItem));
        var selection = ConstructionQueueSelector.SelectNext(
            orderedGroupItems,
            now,
            availability,
            index =>
            {
                var item = orderedGroupItems[index];
                return (IsBuildingUpgradeForSlot(item, out var upgradeSlotId)
                        && HasEarlierPendingConstructForSlot(orderedGroupItems, index, item, upgradeSlotId))
                    || HasEarlierStoragePreflightDependency(orderedGroupItems, index);
            },
            index => ResolveConstructionQueueAvailability(orderedGroupItems[index], now),
            allowIndependentCategoryLookAhead,
            firstItem is null
                ? RomanConstructionPriority.Auto
                : _villageSettingsStore.GetRomanConstructionPriority(GetQueueItemVillageKey(firstItem)));
        skipReason = selection.SkipReason;

        if (selection.QueueFullBlocker is not null && !preview)
        {
            TryPrepareConstructionFullQueueDelay(selection.QueueFullBlocker, now);
            var blockerIndex = orderedGroupItems
                .Select((item, index) => (item, index))
                .FirstOrDefault(entry => entry.item.Id == selection.QueueFullBlocker.Id)
                .index;
            var blockedItems = orderedGroupItems
                .Skip(blockerIndex + 1)
                .Count(candidate => candidate.Status == QueueStatus.Pending);
            LogConstructionQueueFullSummary(selection.QueueFullBlocker, blockedItems, now);
        }
        else if (selection.UsedIndependentCategoryLookAhead && !preview
            && firstItem is not null && availability == ConstructionQueueAvailability.Full)
        {
            // Romans may work in the other lane while this lane waits for a slot.
            TryPrepareConstructionFullQueueDelay(firstItem, now);
        }

        if (selection.Item is null)
        {
            return null;
        }

        if (selection.UsedIndependentCategoryLookAhead && !preview)
        {
            var villageName = NormalizeVillageName(GetQueueItemVillageName(selection.Item)) ?? "-";
            AppendLog(
                $"[construction-queue] Roman category look-ahead selected " +
                $"task='{selection.Item.TaskName}' village='{villageName}' because the earlier category is blocked.");
        }
        else if (selection.UsedRomanPriority && !preview)
        {
            var villageName = NormalizeVillageName(GetQueueItemVillageName(selection.Item)) ?? "-";
            var priority = _villageSettingsStore.GetRomanConstructionPriority(GetQueueItemVillageKey(selection.Item));
            AppendLog(
                $"[construction-queue] Roman priority='{priority}' selected " +
                $"task='{selection.Item.TaskName}' village='{villageName}'.");
        }

        if (TryDeferConstructUntilActivePrerequisiteFinishes(selection.Item, now, preview, out var dependencySkipReason))
        {
            skipReason = dependencySkipReason;
            return null;
        }

        if (!preview && TryPrepareConstructionStartDelay(selection.Item, now, out var humanizeSkipReason))
        {
            skipReason = humanizeSkipReason;
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

    private List<VillageSelectionItem> GetAllKnownVillages()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(GetAllKnownVillages);
        }

        var source = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? Enumerable.Empty<VillageSelectionItem>();
        return source
            .Where(v => !string.IsNullOrWhiteSpace(v.Name) && !string.Equals(v.Name, "-", StringComparison.Ordinal))
            .GroupBy(GetVillageKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private bool TryPrepareConstructionStartDelay(
        QueueItem item,
        DateTimeOffset now,
        out string skipReason)
    {
        skipReason = string.Empty;
        var decision = ConstructionStartDelayPlanner.Resolve(
            item,
            ResolveBuildingStatusForQueueItem(item),
            _travianPlusActive,
            LoadBotOptions(),
            now,
            (minimum, maximum) => minimum + Random.Shared.NextDouble() * (maximum - minimum));
        if (decision is null)
        {
            return false;
        }

        var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonHumanize,
            [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                ConstructionQueueState.CurrentDeferClassificationVersion,
            [BotOptionPayloadKeys.QueueHumanizeExtraSeconds] = decision.DelaySeconds.ToString(),
            [BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied] = "true",
        };
        if (!_botService.UpdateDeferredQueueItem(item.Id, payload, TimeSpan.FromSeconds(decision.DelaySeconds)))
        {
            AppendLog($"[construction-timing] could not persist pre-navigation delay id={item.Id} task='{item.TaskName}'.");
            return false;
        }

        item.Payload = payload;
        item.NextAttemptAt = decision.ReadyAtUtc;
        var villageName = NormalizeVillageName(GetQueueItemVillageName(item)) ?? "-";
        AppendLog(
            $"[construction-timing] village='{villageName}' task='{item.TaskName}' trigger=normal " +
            $"observedAt='{now:O}' referenceFinishAt='{decision.ReferenceFinishUtc:O}' " +
            $"humanDelaySeconds={decision.DelaySeconds} effectiveReadyAt='{decision.ReadyAtUtc:O}' " +
            $"navigation=pending reason='{decision.Reason}'.");
        skipReason =
            $"group=Construction task='{item.TaskName}' waiting for persisted pre-navigation human delay";
        RequestQueueUiRefresh(item.Id);
        return true;
    }

    private void TryPrepareConstructionFullQueueDelay(QueueItem item, DateTimeOffset now)
    {
        var decision = ConstructionStartDelayPlanner.ResolveAfterFullQueue(
            item,
            ResolveBuildingStatusForQueueItem(item),
            _travianPlusActive,
            LoadBotOptions(),
            now,
            (minimum, maximum) => minimum + Random.Shared.NextDouble() * (maximum - minimum));
        if (decision is null)
        {
            return;
        }

        var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonQueueFull,
            [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                ConstructionQueueState.CurrentDeferClassificationVersion,
            [BotOptionPayloadKeys.QueueHumanizeExtraSeconds] = decision.HumanizeExtraSeconds.ToString(),
            [BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied] = "true",
        };
        if (!_botService.UpdateDeferredQueueItem(
                item.Id, payload, TimeSpan.FromSeconds(decision.QueueRetrySeconds)))
        {
            AppendLog($"[construction-timing] could not persist full-queue pre-navigation delay id={item.Id} task='{item.TaskName}'.");
            return;
        }

        item.Payload = payload;
        item.NextAttemptAt = decision.ReadyAtUtc;
        var villageName = NormalizeVillageName(GetQueueItemVillageName(item)) ?? "-";
        AppendLog(
            $"[construction-timing] village='{villageName}' task='{item.TaskName}' trigger=normal-full-queue " +
            $"observedAt='{now:O}' referenceFinishAt='{decision.ReferenceFinishUtc:O}' " +
            $"humanDelaySeconds={decision.HumanizeExtraSeconds} effectiveReadyAt='{decision.ReadyAtUtc:O}' " +
            $"navigation=pending reason='{decision.Reason}'.");
        RequestQueueUiRefresh(item.Id);
    }

    private bool TryDeferConstructUntilActivePrerequisiteFinishes(
        QueueItem item,
        DateTimeOffset now,
        bool preview,
        out string skipReason)
    {
        skipReason = string.Empty;
        if (!TryResolveConstructActivePrerequisiteDelay(item, now, out var dependencyDelay))
        {
            return false;
        }

        skipReason =
            $"group=Construction task='{item.TaskName}' waiting for active prerequisite {dependencyDelay.Detail}";
        if (preview)
        {
            return true;
        }

        var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonRequirements,
            [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                ConstructionQueueState.CurrentDeferClassificationVersion,
        };
        payload.Remove(BotOptionPayloadKeys.RequirementDeferCount);

        if (_botService.UpdateDeferredQueueItem(item.Id, payload, dependencyDelay.Delay))
        {
            item.Payload = payload;
            var villageName = NormalizeVillageName(GetQueueItemVillageName(item)) ?? "-";
            AppendLoopPickVerbose(
                $"[construction-dependency:verbose] deferred construct until prerequisite finishes " +
                $"id={item.Id} village='{villageName}' waitSeconds={dependencyDelay.Delay.TotalSeconds:F0} " +
                $"requirements='{dependencyDelay.Detail}'",
                $"construction-dependency:{item.Id}:{dependencyDelay.Detail}");
            RequestQueueUiRefresh(item.Id);
        }
        else
        {
            AppendLoopPickVerbose(
                $"[construction-dependency:verbose] could not persist prerequisite defer id={item.Id}; " +
                "skipping this loop pass.",
                $"construction-dependency-persist:{item.Id}");
        }

        return true;
    }

    private bool TryResolveConstructActivePrerequisiteDelay(
        QueueItem item,
        DateTimeOffset now,
        out ConstructionDependencyDelay dependencyDelay)
    {
        dependencyDelay = null!;
        var status = ResolveBuildingStatusForQueueItem(item);
        if (status is null)
        {
            return false;
        }

        var result = ConstructionDependencyGate.ResolveConstructDelay(item, status, now);
        if (result is null)
        {
            return false;
        }

        dependencyDelay = result;
        return true;
    }

    private ConstructionQueueAvailability ResolveConstructionQueueAvailability(
        QueueItem? item,
        DateTimeOffset now)
    {
        var status = item is null
            ? ResolveSelectedVillageBuildingStatus()
            : ResolveBuildingStatusForQueueItem(item);
        return item is null
            ? ConstructionQueueState.ResolveAvailability(status, _travianPlusActive, now)
            : ConstructionQueueState.ResolveAvailabilityForItem(status, _travianPlusActive, item, now);
    }

    private void LogConstructionQueueFullSummary(QueueItem blocker, int blockedItems, DateTimeOffset now)
    {
        var villageName = NormalizeVillageName(GetQueueItemVillageName(blocker)) ?? "-";
        var villageKey = GetQueueItemVillageKey(blocker) ?? villageName;
        var waitSeconds = Math.Max(0, (blocker.NextAttemptAt - now).TotalSeconds);
        var state = $"{blocker.Id}:{blocker.NextAttemptAt.UtcTicks}:{blockedItems}";
        if (!_automationSessionRuntime.TrySetConstructionSummary(villageKey, state))
        {
            return;
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
        _automationSessionRuntime.ClearConstructionSummary(villageKey);
    }

    private static bool HasEarlierPendingConstructForSlot(
        IReadOnlyList<QueueItem> orderedItems,
        int beforeIndex,
        QueueItem upgrade,
        int slotId)
    {
        upgrade.Payload.TryGetValue(BotOptionPayloadKeys.BuildingTemplateStepId, out var upgradeStepId);
        for (var index = 0; index < beforeIndex; index++)
        {
            var earlier = orderedItems[index];
            if (earlier.Status == QueueStatus.Pending
                && IsBuildingConstructForSlot(earlier, out var constructSlotId))
            {
                if (constructSlotId == slotId)
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(upgradeStepId)
                    && earlier.Payload.TryGetValue(BotOptionPayloadKeys.BuildingTemplateStepId, out var constructStepId)
                    && string.Equals(constructStepId, upgradeStepId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void AppendLoopPickVerbose(string message, string key)
    {
        if (_automationSessionRuntime.ShouldPublishVerbose(key, LoopPickVerboseThrottle))
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

    private async Task MaybeCheckInboxDuringContinuousLoopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_inboxAutoEnabled)
        {
            return;
        }

        if (!_automationSessionRuntime.ShouldCheckInbox(
                _inboxAutoEnabled,
                TimeSpan.FromSeconds(ContinuousInboxCheckIntervalSeconds)))
        {
            return;
        }

        // Read the unread badge from the current page (cheap, no navigation) and refresh the
        // Messages/Reports indicators. force:true bypasses the busy guard in
        // RefreshInboxIndicatorsAsync — that guard exists to avoid touching the browser while a
        // task runs, but here the continuous loop owns the browser serially and calls this only
        // between task executions, so the access is safe.
        await RefreshInboxIndicatorsAsync(logErrors: false, force: true, cancellationToken);
    }

    private void MarkContinuousBrowserActivity(BotOptions options)
    {
        _automationSessionRuntime.RecordBrowserActivity(
            options.ContinuousKeepAliveEnabled,
            options.ContinuousKeepAliveMinMinutes,
            options.ContinuousKeepAliveMaxMinutes);
    }

    // Keep the Travian page from going stale while the loop is idle-waiting, but only when queued work is
    // due soon. Long idle periods should stay idle instead of refreshing on a fixed robotic cadence.
    private async Task MaybeKeepBrowserFreshDuringContinuousLoopAsync(BotOptions options, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var nextPendingAt = GetNextContinuousLoopPendingAt();
        var plan = _automationSessionRuntime.PlanKeepAlive(
            options.ContinuousKeepAliveEnabled,
            options.ContinuousKeepAliveMinMinutes,
            options.ContinuousKeepAliveMaxMinutes,
            IsSessionSleeping,
            _resourceSnapshotRefreshRunning,
            HasContinuousLoopWorkDueSoon(now),
            nextPendingAt);
        switch (plan)
        {
            case KeepAlivePlan.SkipSleeping:
                AppendLog("[keep-alive:verbose] skipped because the session is sleeping.");
                return;
            case KeepAlivePlan.SkipRefreshRunning:
                AppendLog("[keep-alive:verbose] skipped because resource refresh is already reading the browser.");
                return;
            case KeepAlivePlan.SkipNoWorkDueSoon:
                AppendLog("[keep-alive:verbose] skipped because no continuous-loop work is due soon.");
                return;
            case KeepAlivePlan.SkipImminentWork:
                AppendLog($"[keep-alive:verbose] skipped because queued work is due in {(nextPendingAt!.Value - now).TotalSeconds:F0}s; task navigation will refresh the page.");
                return;
            case KeepAlivePlan.Refresh:
                break;
            default:
                return;
        }

        try
        {
            using var activity = _dashboardActivityTracker.Begin("Refreshing current page");
            await _botService.RefreshCurrentPageAsync(options, _ => { }, token);
            MarkNetworkConnectionHealthy();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountAccessException ex)
        {
            await HoldAccountAutomationAsync(ex);
        }
        catch (Exception ex)
        {
            _automationSessionRuntime.MarkKeepAliveFailure();
            if (IsTransientKeepAliveFailure(ex))
            {
                var retryDelay = _automationNetworkBackoff.NextRetryDelay();
                _automationNetworkBackoff.MarkUnavailable(retryDelay);
                AppendLog($"[keep-alive:verbose] refresh skipped after transient failure: {ex.Message}");
                return;
            }

            AppendLog($"Continuous loop keep-alive refresh failed: {ex.Message}");
        }
    }

    private bool HasContinuousLoopWorkDueSoon(DateTimeOffset now)
    {
        try
        {
            var dueBefore = now.AddSeconds(ContinuousKeepAliveDueSoonSeconds);
            return GetNextContinuousLoopPendingAt() is DateTimeOffset nextPendingAt
                && nextPendingAt <= dueBefore;
        }
        catch
        {
            return true;
        }
    }

    private DateTimeOffset? GetNextContinuousLoopPendingAt()
    {
        return GetContinuousLoopRelevantQueueItems()
            .Where(item => item.Status == QueueStatus.Pending)
            .Select(item => (DateTimeOffset?)item.NextAttemptAt)
            .Min();
    }

    private static bool IsTransientKeepAliveFailure(Exception ex)
    {
        return ex.GetType().Name.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || IsExpectedWakeLoginRetry(ex)
            || IsTransientPageReadFailure(ex);
    }

    private async Task<bool> ResolveContinuousGoldClubStatusAsync(BotOptions options, CancellationToken cancellationToken)
    {
        var accountName = _accountStore.ActiveAccountName();
        var plan = _automationSessionRuntime.PlanGoldClubCheck(
            accountName,
            TryGetStoredGoldClubEnabled(accountName),
            GoldClubInactiveRecheckInterval);
        if (!plan.ShouldRefresh)
        {
            return plan.Enabled;
        }
        using (_dashboardActivityTracker.Begin("Checking Gold Club status"))
        {
            var enabled = await _farmListsWorkflow.IsGoldClubActiveAsync(options, cancellationToken);
            return _automationSessionRuntime.ApplyGoldClubStatus(enabled);
        }
    }

    // Occasional human-like "stepped away from the computer" pause. Called at the top of a loop
    // iteration (between tasks only, never mid-action). Somewhere within the interval range a random
    // pause of the duration range fires; when it ends the interval reschedules. Logged under Pacing.
    private static bool IsHeroLowHpCooldown(QueueItem item, Exception ex)
    {
        return string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase)
               && ex is TaskWaitException { ReasonCode: TaskWaitReasons.HeroHpTooLow };
    }

    private async Task ApplyHeroLowHpCooldownUiAsync(TimeSpan cooldown)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(cooldown.TotalSeconds));
        await Dispatcher.InvokeAsync(() =>
        {
            _heroViewModel.AdventureStatusText = $"Hero HP too low. Next adventure check in {seconds}s.";
        });
    }

    private Task ApplyPostTaskCooldownAsync(QueueItem item, BotOptions options, CancellationToken cancellationToken)
    {
        if (!IsStateChangingTask(item.TaskName))
        {
            return Task.CompletedTask;
        }

        return ActionPacer.FromOptions(options, AppendLog).DelayAsync(
            options.ActionPacingTaskMinSeconds,
            options.ActionPacingTaskMaxSeconds,
            cancellationToken,
            $"after state-changing task '{item.TaskName}'");
    }

    private void RecordVillageBatchAttempt(QueueItem item, string source)
    {
        var before = _automationPassRuntime.SnapshotVillageBatch(_activeWorkingVillageKey);
        var after = _automationPassRuntime.RecordVillageAttempt(
            GetQueueItemVillageKey(item),
            _activeWorkingVillageKey);
        if (string.IsNullOrWhiteSpace(after.VillageKey))
        {
            return;
        }

        if (!string.Equals(before.VillageKey, after.VillageKey, StringComparison.OrdinalIgnoreCase)
            || before.AttemptCount == 0)
        {
            var targetName = GetQueueItemVillageName(item) ?? _activeWorkingVillageName ?? "-";
            var targetKey = GetQueueItemVillageKey(item);
            if (!string.IsNullOrWhiteSpace(targetKey)
                && !string.Equals(targetKey, after.VillageKey, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog(
                    $"[village-batch] switch requested fromKey='{after.VillageKey}' "
                    + $"toVillage='{targetName}' targetKey='{targetKey}' source='{source}'.");
            }
            else
            {
                AppendLog(
                    $"[village-batch] start village='{targetName}' "
                    + $"key='{after.VillageKey}' source='{source}'.");
            }
        }

    }

    private static bool IsStateChangingTask(string taskName)
    {
        return !taskName.Equals("status", StringComparison.OrdinalIgnoreCase)
            && !taskName.Equals("scan_all_villages", StringComparison.OrdinalIgnoreCase)
            && !taskName.Equals("account_snapshot", StringComparison.OrdinalIgnoreCase)
            && !taskName.Equals("load_buildings_snapshot", StringComparison.OrdinalIgnoreCase);
    }

    private void LogConservativeAutomationWarnings(BotOptions options)
    {
        var warnings = new List<string>();
        try
        {
            var config = _botConfigStore.Load();
            var dailyMaxHours = ReadInt(
                config,
                BotOptionPayloadKeys.SessionPacingDailyMaxHours,
                PacingDefaults.SessionPacingDailyMaxHours,
                0,
                24);
            if (dailyMaxHours <= 0)
            {
                warnings.Add($"[conservative] session daily max is disabled; default is {PacingDefaults.SessionPacingDailyMaxHours}h.");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"[conservative] could not verify session daily max: {ex.Message}");
        }

        if (options.ContinuousFarmDispatchDelayMinMinutes < 10)
        {
            warnings.Add($"[conservative] farming dispatch delay is {options.ContinuousFarmDispatchDelayMinMinutes}m; recommended minimum is 10m.");
        }

        if (string.Equals(options.ContinuousFarmSendMode, FarmingDefaults.SendModeAllAtOnce, StringComparison.Ordinal))
        {
            warnings.Add("[conservative] farming send mode is all-at-once; list-per-list is the conservative default.");
        }

        var signature = string.Join("|", warnings);
        if (!_automationSessionRuntime.ShouldPublishWarnings(signature))
        {
            return;
        }
        foreach (var warning in warnings)
        {
            AppendLog(warning);
        }
    }
}
