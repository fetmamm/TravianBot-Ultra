using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            await MaybeRunVillageStatusSweepAsync(options, cancellationToken, force: true);
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

    private DateTimeOffset? ScheduleNextVillageStatusSweep(int minMinutes, int maxMinutes, string? accountName)
    {
        var result = _villageStatusRoundRuntime.ScheduleNext(
            accountName,
            _accountStore.ActiveAccountName(),
            minMinutes,
            maxMinutes);
        if (result.AccountChanged)
        {
            return null;
        }

        if (!result.WasPersisted)
        {
            AppendLog("[village-scan] could not persist the next-scan deadline; it may run again after restart.");
        }

        return result.NextRoundUtc;
    }

    private async Task MaybeRunVillageStatusSweepAsync(
        BotOptions options,
        CancellationToken token,
        bool force = false)
    {
        if ((!options.VillageStatusSweepEnabled && !force)
            || (!force && DateTimeOffset.UtcNow < GetVillageStatusSweepNextScanUtc()))
        {
            return;
        }

        if (!await EnsureVillageMembershipVerifiedBeforeAutomationAsync(options, token))
        {
            AppendLog("[village-scan] round deferred until village ownership can be verified.");
            return;
        }

        var sweepAccountName = _accountStore.ActiveAccountName();

        var villages = await Dispatcher.InvokeAsync(() =>
        {
            var source = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
                ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
                ?? Enumerable.Empty<VillageSelectionItem>();
            return source.Where(v => !string.IsNullOrWhiteSpace(v.Name) && !string.Equals(v.Name, "-", StringComparison.Ordinal))
                .GroupBy(GetVillageKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        });

        if (villages.Count == 0)
        {
            return;
        }

        using var roundActivity = _dashboardActivityTracker.Begin($"Village scan (0/{villages.Count})");
        var villagesByKey = villages.ToDictionary(GetVillageKey, StringComparer.OrdinalIgnoreCase);
        var roundVillages = villages
            .Select(village => new VillageStatusRoundVillage(
                GetVillageKey(village),
                village.Name,
                village.Url))
            .ToList();
        var port = new DelegateVillageStatusRoundPort(
            async cancellationToken =>
            {
                try
                {
                    // Seed normal runtime work once before visiting the first village. Per-village
                    // generation runs after each fresh read, while account-level preparation must not
                    // navigate away during a village reaction.
                    await EnsureContinuousLoopRuntimeItemsAsync(options, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"[village-scan] runtime preparation failed; existing queued work will still run: "
                        + FormatExceptionForLog(ex));
                }

                AppendLog($"[village-scan] starting round for {villages.Count} village(s).");
            },
            (village, villageNumber, villageCount, inboxStatusChecked, cancellationToken) =>
                VisitVillageStatusRoundAsync(
                    options,
                    villagesByKey[village.Key],
                    villageNumber,
                    villageCount,
                    inboxStatusChecked,
                    cancellationToken),
            cancellationToken => new ValueTask(
                ActionPacer.FromOptions(options, AppendLog).DelayAsync(
                    options.VillageStatusSweepVillageMinSeconds,
                    options.VillageStatusSweepVillageMaxSeconds,
                    cancellationToken,
                    "Village scan: next village")));
        var roundResult = await _villageStatusRoundCoordinator.RunAsync(roundVillages, port, token);
        if (!roundResult.Completed)
        {
            return;
        }

        var min = Math.Min(options.VillageStatusSweepRoundMinMinutes, options.VillageStatusSweepRoundMaxMinutes);
        var max = Math.Max(options.VillageStatusSweepRoundMinMinutes, options.VillageStatusSweepRoundMaxMinutes);
        var nextScanUtc = ScheduleNextVillageStatusSweep(min, max, sweepAccountName);
        if (nextScanUtc is not null)
        {
            AppendLog($"[village-scan] round complete; next round after {nextScanUtc.Value:HH:mm}.");
        }
    }

    private Task<VillageStatus> ReadVillageStatusSweepBaseStatusAsync(
        BotOptions options,
        VillageSelectionItem village,
        CancellationToken cancellationToken) =>
        options.VillageStatusSweepDorf2Enabled
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
            if (!await ExecuteSingleQueueItemAsync(
                    item,
                    options,
                    "[village-scan]",
                    QueueExecutionMode.ContinuousLoop,
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
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var urgent = SelectUrgentQueueItemForVillageStatusSweep(options, attemptedItemIds);
            var next = urgent ?? SelectNextQueueItemForVillageStatusSweep(villageKey, attemptedItemIds);
            if (next is null)
            {
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
            var shouldContinue = await ExecuteSingleQueueItemAsync(
                next,
                options,
                "[village-scan]",
                QueueExecutionMode.ContinuousLoop,
                cancellationToken);
            MarkContinuousBrowserActivity(options);
            if (!shouldContinue)
            {
                return false;
            }

            await ApplyPostTaskCooldownAsync(next, options, cancellationToken);
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
    // Idle loop passes no longer log "[LOOP n] START" + "WAIT" every few seconds. Instead a single
    // "[LOOP n] idle" heartbeat is logged at most this often while nothing is ready, so the log shows the
    // loop is alive without the per-pass spine. Active passes (a PICK) and failures still log in full.
    private static readonly TimeSpan LoopIdleHeartbeatInterval = TimeSpan.FromMinutes(2);

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
        IReadOnlySet<Guid> attemptedItemIds)
    {
        var candidates = _botService.GetQueueItemsForDisplay()
            .Where(item => !attemptedItemIds.Contains(item.Id))
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
        using var villageActivity = _dashboardActivityTracker.Begin(
            $"Village scan ({villageNumber}/{villageCount}): {village.Name}");
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
                    await EnsureContinuousLoopRuntimeItemsAsync(options, token, targetVillage);
                },
                (targetVillage, attempts, token) => new ValueTask<bool>(
                    ExecuteReadyVillageStatusSweepTasksAsync(options, targetVillage, attempts, token)));
            return await _villageStatusReactionCoordinator.RunAsync(
                village,
                options.VillageStatusSweepDorf1Enabled,
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

    private async ValueTask<AutomationStateSnapshot> ReadContinuousAutomationStateAsync(
        CancellationToken cancellationToken)
    {
        var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
        var tickId = _automationPassRuntime.BeginContinuousPass();
        var tickStopwatch = Stopwatch.StartNew();
        try
        {
            if (TryScheduleAutomaticProxyRecovery(options))
            {
                return new AutomationStateSnapshot([], IsComplete: true);
            }

            var networkBackoffRemaining = _automationNetworkBackoff.Remaining;
            if (networkBackoffRemaining > TimeSpan.Zero)
            {
                AppendLog($"[LOOP {tickId}] WAIT {Math.Ceiling(networkBackoffRemaining.TotalSeconds):F0}s");
                return new AutomationStateSnapshot(
                    [],
                    NextWakeAt: DateTimeOffset.UtcNow.Add(networkBackoffRemaining));
            }

            await EnsureChromiumInstalledAsync();
            if (!await EnsureVillageMembershipVerifiedBeforeAutomationAsync(options, cancellationToken))
            {
                return new AutomationStateSnapshot(
                    [],
                    NextWakeAt: _villageMembershipVerificationNotBeforeUtc > DateTimeOffset.UtcNow
                        ? _villageMembershipVerificationNotBeforeUtc
                        : DateTimeOffset.UtcNow.AddSeconds(30));
            }

            var immediateWorkRequested = _automationPassRuntime.ConsumeImmediateWorkRequest();
            if (!immediateWorkRequested)
            {
                await MaybeTakeIdleBreakAsync(options, cancellationToken);
                immediateWorkRequested = _automationPassRuntime.ConsumeImmediateWorkRequest();
            }
            if (!immediateWorkRequested)
            {
                await MaybeDoIdleBrowseAsync(options, cancellationToken);
            }

            await HonorPendingVillageSwitchAsync(options, cancellationToken);
            var forceVillageStatusSweep = _villageStatusRoundRuntime.ConsumeForceRequest();
            await MaybeRunVillageStatusSweepAsync(options, cancellationToken, forceVillageStatusSweep);
            await EnsureContinuousLoopConstructionStatusAsync(options, cancellationToken);
            await MaybeAnalyzeNewVillageDuringContinuousLoopAsync(options, cancellationToken);
            await EnsureContinuousLoopRuntimeItemsAsync(options, cancellationToken);
            await MaybeCheckInboxDuringContinuousLoopAsync(cancellationToken);

            var next = SelectNextQueueItemForContinuousLoop();
            if (next is not null)
            {
                AppendLog(
                    $"[LOOP {tickId}] PICK group={next.Group}, task={next.TaskName}, "
                    + $"retries={next.Retries}/{next.MaxRetries}");
                _automationSessionRuntime.MarkActivePass();
                return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(next)]);
            }

            await MaybeKeepBrowserFreshDuringContinuousLoopAsync(options, cancellationToken);
            var waitDelay = ResolveContinuousLoopWaitDelay(options);
            var totalSeconds = AutomationDeadlinePolicy.ResolveWaitSeconds(
                waitDelay,
                options,
                networkBackoff: false);
            if (_automationSessionRuntime.ShouldPublishIdleHeartbeat(LoopIdleHeartbeatInterval))
            {
                AppendLog($"[LOOP {tickId}] idle — nothing ready, waiting {totalSeconds}s");
            }
            var nextWakeAt = DateTimeOffset.UtcNow.AddSeconds(totalSeconds);
            if (options.ContinuousKeepAliveEnabled
                && _automationSessionRuntime.NextKeepAliveAtUtc > DateTimeOffset.UtcNow
                && _automationSessionRuntime.NextKeepAliveAtUtc < nextWakeAt)
            {
                nextWakeAt = _automationSessionRuntime.NextKeepAliveAtUtc;
            }
            return new AutomationStateSnapshot(
                [],
                NextWakeAt: nextWakeAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountAccessException ex)
        {
            await HoldAccountAutomationAsync(ex);
            throw;
        }
        catch (Exception ex) when (AutomationNetworkBackoff.IsTransientConnectionFailure(ex))
        {
            if (TryScheduleAutomaticProxyRecovery(options))
            {
                return new AutomationStateSnapshot([], IsComplete: true);
            }
            throw;
        }
        catch (Exception ex)
        {
            AppendLog(
                $"[LOOP {tickId}] FAIL {tickStopwatch.Elapsed.TotalSeconds:F1}s | "
                + FormatExceptionForLog(ex));
            var retrySeconds = AutomationDeadlinePolicy.ResolveWaitSeconds(
                null,
                options,
                networkBackoff: false);
            return new AutomationStateSnapshot(
                [],
                NextWakeAt: DateTimeOffset.UtcNow.AddSeconds(retrySeconds));
        }
    }

    private async ValueTask<AutomationStateSnapshot> ReadAutoQueueAutomationStateAsync(
        CancellationToken cancellationToken)
    {
        await HonorPendingVillageSwitchAsync(
            ApplySelectedVillageToOptions(LoadBotOptions()),
            cancellationToken);

        var selected = SelectNextQueueItemForContinuousLoop();
        if (selected is not null)
        {
            return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(selected)]);
        }

        var now = DateTimeOffset.UtcNow;
        var eligibleItems = _botService
            .GetQueueItemsForDisplay()
            .Where(IsQueueItemAllowedByAutomationSettings)
            .ToList();
        var nextDeferredItem = eligibleItems
            .Where(item => !item.IsRuntimeOnly && item.Status == QueueStatus.Pending)
            .FirstOrDefault(item => item.NextAttemptAt > now)
            ?? eligibleItems
                .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                .OrderBy(item => item.NextAttemptAt)
                .FirstOrDefault();

        if (nextDeferredItem is null)
        {
            AppendLog($"[AUTOQ {_automationPassRuntime.AutoQueueRunLogId}] DONE (queue empty).");
            return new AutomationStateSnapshot([], IsComplete: true);
        }

        AppendLog(
            $"[AUTOQ {_automationPassRuntime.AutoQueueRunLogId}] WAIT "
            + $"{Math.Max(0, (nextDeferredItem.NextAttemptAt - now).TotalSeconds):F0}s "
            + $"for deferred task={nextDeferredItem.TaskName}");
        return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(nextDeferredItem)]);
    }

    private async ValueTask<AutomationActionOutcome> ExecuteContinuousAutomationActionAsync(
        AutomationCandidate action,
        CancellationToken cancellationToken)
    {
        var item = _botService
            .GetQueueItemsForDisplay()
            .FirstOrDefault(candidate => candidate.Id == action.Id);
        if (item is null)
        {
            AppendLog($"[LOOP {_automationPassRuntime.CurrentContinuousPassId}] SKIP missing queue item id={action.Id}");
            return AutomationActionOutcome.Skipped;
        }

        var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
        if (_loopController.LoopStopRequested)
        {
            return AutomationActionOutcome.Blocked;
        }

        await ActionPacer.FromOptions(options, AppendLog).DelayAsync(
            options.ActionPacingTaskMinSeconds,
            options.ActionPacingTaskMaxSeconds,
            cancellationToken,
            "before task");
        RecordVillageBatchAttempt(item, $"LOOP {_automationPassRuntime.CurrentContinuousPassId}");
        var shouldContinue = await ExecuteSingleQueueItemAsync(
            item,
            options,
            $"[LOOP {_automationPassRuntime.CurrentContinuousPassId}]",
            QueueExecutionMode.ContinuousLoop,
            cancellationToken);
        MarkContinuousBrowserActivity(options);
        if (!shouldContinue)
        {
            return AutomationActionOutcome.Blocked;
        }

        if (_loopController.LoopStopRequested)
        {
            return AutomationActionOutcome.Completed;
        }

        await ApplyPostTaskCooldownAsync(item, options, cancellationToken);
        return AutomationActionOutcome.Completed;
    }

    private async ValueTask<AutomationActionOutcome> ExecuteAutoQueueAutomationActionAsync(
        AutomationCandidate action,
        CancellationToken cancellationToken)
    {
        var item = _botService
            .GetQueueItemsForDisplay()
            .FirstOrDefault(candidate => candidate.Id == action.Id);
        if (item is null)
        {
            AppendLog($"[AUTOQ {_automationPassRuntime.AutoQueueRunLogId}] SKIP missing queue item id={action.Id}");
            return AutomationActionOutcome.Skipped;
        }

        await EnsureChromiumInstalledAsync();
        var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
        AppendLog($"[AUTOQ {_automationPassRuntime.AutoQueueRunLogId}] RUN task={item.TaskName}, id={item.Id}");
        RecordVillageBatchAttempt(item, $"AUTOQ {_automationPassRuntime.AutoQueueRunLogId}");
        var shouldContinue = await ExecuteSingleQueueItemAsync(
            item,
            options,
            $"[AUTOQ {_automationPassRuntime.AutoQueueRunLogId}]",
            QueueExecutionMode.AutoQueue,
            cancellationToken);
        if (!shouldContinue)
        {
            return AutomationActionOutcome.Blocked;
        }

        if (_loopController.QueueStopRequested)
        {
            return AutomationActionOutcome.Completed;
        }

        await ApplyPostTaskCooldownAsync(item, options, cancellationToken);
        return AutomationActionOutcome.Completed;
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

    private async Task EnsureContinuousLoopRuntimeItemsAsync(
        BotOptions options,
        CancellationToken cancellationToken,
        VillageSelectionItem? onlyVillage = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enabledGroups = GetContinuousLoopEnabledGroupsInOrder();
        // Troop-training, smithy, brewery and farming are generated PER VILLAGE (see below), so the loop
        // must keep running when only a non-selected village has those groups on. Hero/transfer/
        // reinforcements stay account-global and keep gating on the selected village's toggles via
        // `enabledGroups`.
        var consideredGroups = GetContinuousLoopConsideredGroupsInOrder();
        // Hero is global (one hero). Poll/queue adventures when Hero is on for ANY enabled village (the
        // considered/union set), not just the selected one — otherwise Hero never runs while a village that
        // has it OFF is selected even though the hero-home village has it ON.
        var heroPollingEnabled = consideredGroups.Contains(QueueGroup.Hero) || ShouldKeepHeroAdventurePolling();
        if (consideredGroups.Count <= 0
            && !heroPollingEnabled
            && !options.HeroCropAntiStarveEnabled
            && onlyVillage is null)
        {
            return;
        }

        var queueItems = _botService.GetQueueItemsForDisplay();
        var runtimeItems = new AutomationRuntimeItemReconciler(
            queueItems,
            key => _villageSettingsStore.ResolveCanonicalKey(key),
            new DelegateAutomationRuntimeQueuePort(
                spec => _botService.EnqueueRuntime(
                    spec.TaskName,
                    spec.DisplayName,
                    spec.Payload,
                    spec.Priority,
                    spec.MaxRetries),
                (id, payload) => _botService.UpdateDeferredQueueItem(id, payload),
                (id, priority) => _botService.UpdatePendingQueueItem(id, payload: null, priority: priority)));

        bool HasActiveTask(string taskName)
        {
            return runtimeItems.HasActive(taskName);
        }

        // Per-village variant: an item only counts as active for a village when its payload targets that
        // same village. Matched by the stable coordinate KEY, not the name — otherwise a renamed village
        // would fail to find its existing runtime task and enqueue a duplicate under the new name.
        bool HasActiveTaskForVillage(string taskName, VillageSelectionItem village)
        {
            return runtimeItems.HasActiveForVillage(taskName, GetVillageKey(village));
        }

        var automationVillages = onlyVillage is null
            ? GetEnabledAutomationVillages()
            : _villageSettingsStore.IsEnabledByKey(GetVillageKey(onlyVillage), defaultIfUnknown: false)
                ? [onlyVillage]
                : [];

        RemoveDisabledHeroCropAntiStarveTasks(options);
        SeedHeroCropAntiStarveObservations(options);
        ActivateDueHeroCropAntiStarveObservations(options);

        if (onlyVillage is null && heroPollingEnabled && !HasActiveTask("hero_manage"))
        {
            int? adventureCount;
            using (_dashboardActivityTracker.Begin("Checking Hero adventures"))
            {
                adventureCount = await _botService.RefreshAdventureCountAsync(options, AppendLog, cancellationToken);
            }
            await Dispatcher.InvokeAsync(() => ApplyHeroAdventureAvailability(adventureCount));
            if (adventureCount is > 0)
            {
                var payload = BuildHeroRuntimePayload();
                runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                    "hero_manage", "Hero adventure", payload, -50, 0));
                AppendLog($"Hero group: queued hero_manage because adventures available={adventureCount.Value}. priority={payload[BotOptionPayloadKeys.HeroStatPriority]}");
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
                    || HasActiveTaskForVillage("upgrade_troops_at_smithy", village))
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

                runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                    "upgrade_troops_at_smithy",
                    "Troop upgrades",
                    payload,
                    -50,
                    0,
                    villageKey));
            }
        }

        // Troop training (Barracks/Stable/Workshop) — generated per enabled village whose Build Troops
        // group is on. Per village by design (each village trains independently). When the village has a
        // saved per-village override it is snapshotted into the payload so the worker trains that village's
        // own troops; otherwise the task runs on the global troop-training config (backwards-compatible).
        var troopTrainingAccount = _accountStore.ActiveAccountName();
        foreach (var village in automationVillages)
        {
            if (!IsGroupEnabledForVillage(GetVillageKey(village), QueueGroup.TroopTraining))
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

            if (HasActiveTaskForVillage("build_troops", village))
            {
                // The runtime item can stay deferred for hours between runs. Keep its payload snapshot
                // in sync with the village's current troop settings — otherwise edits (e.g. a new timed
                // range) never take effect because the item is recreated only after it disappears.
                var refresh = runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                    "build_troops",
                    "Build troops",
                    trainingPayload,
                    -50,
                    0,
                    GetVillageKey(village),
                    RefreshPendingPayload: true));
                if (refresh.Change is AutomationRuntimeItemChange.PayloadUpdated
                    or AutomationRuntimeItemChange.PayloadAndPriorityUpdated)
                {
                    AppendLog($"[troops] refreshed deferred build_troops payload for '{village.Name}' with updated troop settings.");
                }

                continue;
            }

            var trainingOptions = villageTraining is null
                ? options
                : BotOptionsPayloadApplier.Apply(options, villageTraining.ToDictionary());
            if (!HasEnabledTroopTrainingBuilding(trainingOptions))
            {
                AppendLoopPickVerbose(
                    $"[troops:verbose] skipped build_troops enqueue for '{village.Name}' — no troop-training building is enabled.",
                    $"troops:no-enabled:{GetVillageKey(village)}");
                continue;
            }

            var activeQueueWaitSeconds = ShouldGateTroopTrainingEnqueueOnActiveQueue(trainingOptions)
                ? ResolveActiveTroopTrainingQueueWaitSeconds(village, trainingOptions)
                : null;
            if (activeQueueWaitSeconds is > 0)
            {
                AppendLoopPickVerbose(
                    $"[troops:verbose] skipped build_troops enqueue for '{village.Name}' — enabled training queue still active for {FormatSmithyDuration(activeQueueWaitSeconds.Value)}.",
                    $"troops:active-queue:{GetVillageKey(village)}");
                continue;
            }

            runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                "build_troops",
                "Build troops",
                trainingPayload,
                -50,
                0,
                GetVillageKey(village)));
        }

        // Brewery celebration — capital only (the brewery exists only in the capital). The capital's
        // Brewery Celebration group toggle is the authoritative switch — same as every other per-village
        // group above — and enabling it force-syncs the Troops-tab "Auto celebration" flag. Gate on the
        // persisted group + tribe support only; do NOT also require that VM flag here. At startup the
        // persisted group can be on while the flag has loaded off (it is only reconciled later), which
        // left the celebration un-enqueued — "no ready item across villages" — until the user re-toggled.
        if (_troopTrainingViewModel.IsAutoCelebrationAvailableForCurrentTribe)
        {
            var capital = automationVillages.FirstOrDefault(v => v.IsCapital);
            if (capital is not null
                && IsGroupEnabledForVillage(GetVillageKey(capital), QueueGroup.BreweryCelebration)
                && !HasActiveTaskForVillage("run_brewery_celebration", capital))
            {
                runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                    "run_brewery_celebration",
                    "Auto celebration",
                    BuildVillageRuntimePayload(capital),
                    -50,
                    0,
                    GetVillageKey(capital)));
            }
        }

        // Town Hall celebrations are generated per enabled village. A remembered running celebration is
        // restored as a deferred runtime item so the dashboard timer survives restart without navigating
        // back to the Town Hall until it ends.
        var townHallAccount = _accountStore.ActiveAccountName();
        foreach (var village in automationVillages)
        {
            var villageKey = GetVillageKey(village);
            if (!IsGroupEnabledForVillage(villageKey, QueueGroup.TownHallCelebration)
                || HasActiveTaskForVillage("run_town_hall_celebration", village))
            {
                continue;
            }

            var overrideMode = TownHallSettingsStore.LoadMode(_projectRoot, townHallAccount, villageKey);
            var mode = TownHallCelebrationDefaults.NormalizeMode(overrideMode ?? options.TownHallCelebrationMode);
            var nowUtc = DateTimeOffset.UtcNow;
            var remembered = TownHallCelebrationStateStore.LoadActive(_projectRoot, townHallAccount, villageKey, nowUtc);
            if (remembered is not null)
            {
                var restoredPayload = BuildVillageRuntimePayload(village);
                restoredPayload[BotOptionPayloadKeys.TownHallCelebrationMode] = remembered.Mode;
                var restoredItem = runtimeItems.EnqueueNew(new AutomationRuntimeItemSpec(
                    "run_town_hall_celebration",
                    "Town Hall celebration",
                    restoredPayload,
                    -50,
                    0,
                    villageKey));
                // The freshly enqueued item is Pending, so defer it via UpdateDeferred (Pending-based).
                // MarkQueueItemDeferred requires Running and failed silently here, which left the restored
                // item due immediately — the task then re-ran every loop pass (the "timer restarts at ~30s
                // over and over" bug) whenever a run ended without a persisted wait.
                var restoreDelay = remembered.EndsAtUtc > nowUtc
                    ? remembered.EndsAtUtc - nowUtc
                    : TimeSpan.Zero;
                if (_botService.UpdateDeferredQueueItem(restoredItem.Id, null, restoreDelay))
                {
                    AppendLog($"[town-hall] restored running celebration for '{village.Name}' until {FormatQueueServerTime(remembered.EndsAtUtc)}.");
                }
                else
                {
                    AppendLog($"[town-hall] restored celebration for '{village.Name}' but could not defer it to {FormatQueueServerTime(remembered.EndsAtUtc)}; it will re-check the Town Hall on the next pass.");
                }

                continue;
            }

            var payload = BuildVillageRuntimePayload(village);
            payload[BotOptionPayloadKeys.TownHallCelebrationMode] = mode;
            runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                "run_town_hall_celebration",
                "Town Hall celebration",
                payload,
                -50,
                0,
                villageKey));
        }

        var farmingBlockedForOtherReason = IsFarmingGroupBlocked()
            && !string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoGoldClub, StringComparison.OrdinalIgnoreCase)
            // Missing lists is recoverable: enabling Farming must re-enter the bootstrap path, analyze
            // the account's lists, restore their saved selections, and then enqueue send_farmlists.
            && !string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoFarmLists, StringComparison.OrdinalIgnoreCase);
        if (onlyVillage is null
            && consideredGroups.Contains(QueueGroup.Farming)
            && !farmingBlockedForOtherReason)
        {
            var goldClubEnabled = await ResolveContinuousGoldClubStatusAsync(options, cancellationToken);
            UpdateGoldClubInfo(goldClubEnabled);
            if (goldClubEnabled)
            {
                await EnsureContinuousFarmListsReadyAsync(options, cancellationToken);
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

                var selectedSnapshot = RunOnUi(GatherSelectedFarmLists);
                var selectedFarmLists = selectedSnapshot.Names;
                var farmSendMode = FarmingDefaults.NormalizeSendMode(options.ContinuousFarmSendMode);
                var sendsAllListsAtOnce = string.Equals(farmSendMode, FarmingDefaults.SendModeAllAtOnce, StringComparison.Ordinal);
                var availableFarmListCount = RunOnUi(() => _farmLists.Count(IsRealFarmListRow));
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
                    var farmingPayload = new FarmingPayload(
                        selectedFarmLists,
                        selectedSnapshot.Ids,
                        MoveRedLosses: options.ContinuousFarmMoveRedLosses,
                        RedLossDestinationListId: options.ContinuousFarmRedLossDestinationListId,
                        RedLossDestinationListName: options.ContinuousFarmRedLossDestinationListName,
                        RedLossDestinationBaseName: options.ContinuousFarmRedLossDestinationBaseName,
                        MoveYellowLosses: options.ContinuousFarmMoveYellowLosses,
                        YellowLossDestinationListId: options.ContinuousFarmYellowLossDestinationListId,
                        YellowLossDestinationListName: options.ContinuousFarmYellowLossDestinationListName,
                        YellowLossDestinationBaseName: options.ContinuousFarmYellowLossDestinationBaseName).ToDictionary();
                    foreach (var farmingVillage in automationVillages)
                    {
                        if (!IsGroupEnabledForVillage(GetVillageKey(farmingVillage), QueueGroup.Farming)
                            || HasActiveTaskForVillage("send_farmlists", farmingVillage))
                        {
                            continue;
                        }

                        var payload = new Dictionary<string, string>(farmingPayload, StringComparer.OrdinalIgnoreCase);
                        foreach (var pair in BuildVillageRuntimePayload(farmingVillage))
                        {
                            payload[pair.Key] = pair.Value;
                        }

                        var displayName = sendsAllListsAtOnce ? "Send all farmlists" : "Send selected farmlists";
                        runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                            "send_farmlists",
                            displayName,
                            payload,
                            -50,
                            0,
                            GetVillageKey(farmingVillage)));
                        AppendLog($"Continuous farming queued for village '{farmingVillage.Name}'.");
                    }
                }
            }
            else
            {
                SetFarmingBlockedState(FarmingBlockedReasonNoGoldClub, "No goldclub");
            }
        }

        if (onlyVillage is null
            && enabledGroups.Contains(QueueGroup.ResourceTransfer)
            && !HasActiveTask("send_resources_between_villages"))
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
                runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                    "send_resources_between_villages",
                    "Resource transfer",
                    payload,
                    -50,
                    0));
            }
        }

        if (onlyVillage is null
            && enabledGroups.Contains(QueueGroup.Reinforcements)
            && !HasActiveTask("send_reinforcements_between_villages"))
        {
            var selectedSources = options.ReinforcementsSourceVillageNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Where(name => !string.Equals(name, options.ReinforcementsTargetVillageName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (CanRunReinforcements(options, out _))
            {
                var payload = BuildAutomaticReinforcementPayload(options, selectedSources);
                var delay = ContinuousLoopSelector.ResolveReinforcementSendDelay(
                    options,
                    queueItems,
                    DateTimeOffset.UtcNow);
                ScheduleAutomaticReinforcementSend(payload, delay, options);
            }
        }
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

    // preview=true makes this a time-controlled, read-only prediction of the NEXT item the live loop would
    // pick, with ALL side effects suppressed (no queue mutation, rotation-key advance, logging, or dedup-state
    // writes). The dashboard and wake calculator use it so their forecast mirrors the live selector.
    private QueueItem? SelectNextQueueItemForContinuousLoop(
        bool preview = false,
        DateTimeOffset? evaluationTimeUtc = null,
        string? villageKeyFilter = null,
        IReadOnlyList<QueueItem>? queueItemsOverride = null)
    {
        var options = LoadBotOptions();
        if (!preview)
        {
            RemoveDisabledAutoCollectUtilityItems(options);
        }
        var queueItems = queueItemsOverride ?? _botService.GetQueueItemsForDisplay();
        var now = evaluationTimeUtc ?? DateTimeOffset.UtcNow;
        var selectionCandidates = queueItems
            .Select(item => new ContinuousLoopSelectionCandidate(
                item,
                GetQueueItemVillageKey(item),
                IsQueueItemAllowedByAutomationSettings(item),
                ContinuousLoopSelector.IsUtilityTask(item.TaskName)
                    && IsAutoCollectUtilityTaskEnabledNow(item.TaskName, options)))
            .Where(candidate => string.IsNullOrWhiteSpace(villageKeyFilter)
                || string.Equals(
                    candidate.VillageKey,
                    villageKeyFilter,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        var batch = _automationPassRuntime.SnapshotVillageBatch(_activeWorkingVillageKey);
        var result = AutomationQueueSelector.Select(
            new AutomationQueueSelectionInput(
                selectionCandidates,
                GetContinuousLoopConsideredGroupsInOrder(),
                batch,
                _activeWorkingVillageKey,
                now,
                options.ShortVillageDeferSeconds,
                preview),
            SelectReadyConstructionForAutomationPass);
        if (!preview)
        {
            if (result.CompleteUrgentPreemption)
            {
                _automationPassRuntime.CompleteUrgentPreemption(_activeWorkingVillageKey);
                AppendLog(
                    $"[village-batch] interrupted village key='{batch.VillageKey ?? "-"}' "
                    + "has no ready work; urgent preemption completed.");
            }
            else if (result.Reason == AutomationQueueSelectionReason.UrgentPreemption
                && result.Selected is not null
                && !string.Equals(
                    GetQueueItemVillageKey(result.Selected),
                    _activeWorkingVillageKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                _automationPassRuntime.RecordUrgentPreemption(
                    _activeWorkingVillageKey,
                    GetQueueItemVillageKey(result.Selected));
                AppendLog(
                    $"[village-batch] urgent preemption task='{result.Selected.TaskName}' "
                    + $"priority={result.Selected.Priority} village='{GetQueueItemVillageName(result.Selected) ?? "-"}'.");
            }
            else if (result.Reason == AutomationQueueSelectionReason.UrgentResume)
            {
                AppendLog(
                    $"[village-batch] urgent work complete; resuming "
                    + $"'{GetQueueItemVillageName(result.Selected!) ?? "-"}' "
                    + $"with task='{result.Selected!.TaskName}'.");
            }
            else if (result.Reason == AutomationQueueSelectionReason.VillageRotationNoReadyWork)
            {
                AppendLog(
                    $"[village-batch] complete '{_activeWorkingVillageName ?? "-"}' because it has no ready work; "
                    + $"next='{GetQueueItemVillageName(result.Selected!) ?? "-"}' "
                    + $"task='{result.Selected!.TaskName}'.");
            }
            else if (result.Reason == AutomationQueueSelectionReason.ShortVillageHold)
            {
                AppendLoopPickVerbose(
                    $"[loop-pick:verbose] holding current village for short defer until "
                    + $"'{FormatQueueServerTime(result.HoldUntil!.Value)}' "
                    + $"(limit={options.ShortVillageDeferSeconds}s)",
                    $"short-village-hold:{_activeWorkingVillageKey}:{result.HoldUntil.Value.UtcTicks}:{options.ShortVillageDeferSeconds}");
            }
            else if (result.Reason == AutomationQueueSelectionReason.NoEnabledGroups)
            {
                AppendLoopPickVerbose(
                    "[loop-pick:verbose] no enabled groups — nothing to schedule",
                    "no-enabled-groups");
            }
            else if (result.Reason == AutomationQueueSelectionReason.NoReadyWork)
            {
                AppendLoopPickVerbose(
                    $"[loop-pick:verbose] no ready item selected from {result.ConsideredGroupCount} group(s)",
                    $"no-selected:{result.ConsideredGroupCount}");
            }
        }

        return result.Selected;
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

    private ContinuousLoopForecast ResolveNextContinuousLoopForecast(
        DateTimeOffset now,
        string? villageKeyFilter = null,
        IReadOnlyList<QueueItem>? queueItemsOverride = null)
    {
        var queueItems = queueItemsOverride ?? GetQueueSnapshotForUi();
        var scopedItems = queueItems
            .Where(item => string.IsNullOrWhiteSpace(villageKeyFilter)
                || string.Equals(
                    GetQueueItemVillageKey(item),
                    villageKeyFilter,
                    StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Status == QueueStatus.Running
                || IsQueueItemAllowedByAutomationSettings(item))
            .ToList();
        var pending = scopedItems
            .Where(item => item.Status == QueueStatus.Pending)
            .ToList();
        var candidateDeadlines = pending
            .Where(item => item.NextAttemptAt > now)
            .Select(item => item.NextAttemptAt)
            .ToHashSet();
        foreach (var item in pending.Where(item => item.Group == QueueGroup.Construction))
        {
            var status = ResolveBuildingStatusForQueueItem(item);
            if (status is null)
            {
                continue;
            }

            var queueDelay = ConstructionQueueState.ResolveQueueFullRetryDelay(
                status,
                _travianPlusActive,
                item,
                now);
            if (queueDelay is { } delay && delay > TimeSpan.Zero)
            {
                candidateDeadlines.Add(now + delay);
            }
            if (TryResolveConstructActivePrerequisiteDelay(item, now, out var dependencyDelay)
                && dependencyDelay.Delay > TimeSpan.Zero)
            {
                candidateDeadlines.Add(now + dependencyDelay.Delay);
            }
        }

        return ContinuousLoopForecastPlanner.Resolve(
            scopedItems,
            now,
            candidateDeadlines,
            evaluationTime => SelectNextQueueItemForContinuousLoop(
                preview: true,
                evaluationTimeUtc: evaluationTime,
                villageKeyFilter: villageKeyFilter,
                queueItemsOverride: queueItems),
            selected => selected.Group != QueueGroup.Construction
                || ConstructionQueueState.ResolveAvailabilityForItem(
                    ResolveBuildingStatusForQueueItem(selected),
                    _travianPlusActive,
                    selected,
                    now) != ConstructionQueueAvailability.Unknown);
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
            allowIndependentCategoryLookAhead);
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

        if (selection.UsedIndependentCategoryLookAhead && !preview)
        {
            var villageName = NormalizeVillageName(GetQueueItemVillageName(selection.Item)) ?? "-";
            AppendLog(
                $"[construction-queue] Roman category look-ahead selected " +
                $"task='{selection.Item.TaskName}' village='{villageName}' because the earlier category is blocked.");
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
            || IsTransientPageReadFailure(ex);
    }

    private TimeSpan? ResolveContinuousLoopWaitDelay(BotOptions options)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var nextQueueDeadline = GetContinuousLoopConsideredGroupsInOrder().Count <= 0
                ? null
                : GetContinuousLoopRelevantQueueItems()
                    .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                    .Select(item => (DateTimeOffset?)item.NextAttemptAt)
                    .Min();
            DateTimeOffset? nextVillageScanUtc = options.VillageStatusSweepEnabled
                ? GetVillageStatusSweepNextScanUtc()
                : null;
            var forecast = ResolveNextContinuousLoopForecast(now);
            var nextConstructionAvailabilityUtc = forecast.Item?.Group == QueueGroup.Construction
                && forecast.State == ContinuousLoopForecastState.Waiting
                ? forecast.ReadyAtUtc
                : null;
            if (nextConstructionAvailabilityUtc is DateTimeOffset constructionDeadline
                && (nextQueueDeadline is null || constructionDeadline < nextQueueDeadline.Value))
            {
                AppendLoopPickVerbose(
                    $"[loop-pick:verbose] next wake follows humanized construction availability at "
                        + $"'{FormatQueueServerTime(constructionDeadline)}' "
                        + $"for {forecast.Item?.DisplayName ?? forecast.Item?.TaskName}",
                    $"construction-wake:{forecast.Item?.Id}:{constructionDeadline.UtcTicks}");
            }
            return AutomationDeadlinePolicy.ResolveNextDelay(
                now,
                nextQueueDeadline,
                nextConstructionAvailabilityUtc,
                nextVillageScanUtc);
        }
        catch
        {
            return null;
        }
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
            var enabled = await _botService.ReadAndPersistGoldClubStatusAsync(options, AppendLog, cancellationToken);
            return _automationSessionRuntime.ApplyGoldClubStatus(enabled);
        }
    }

    // Occasional human-like "stepped away from the computer" pause. Called at the top of a loop
    // iteration (between tasks only, never mid-action). Somewhere within the interval range a random
    // pause of the duration range fires; when it ends the interval reschedules. Logged under Pacing.
    private async Task MaybeTakeIdleBreakAsync(BotOptions options, CancellationToken token)
    {
        var plan = _automationIdlePacing.PlanBreak(
            options,
            sessionAvailable: !IsSessionSleeping && _isLoggedIn && _browserSessionLikelyOpen);
        if (!plan.ShouldTakeBreak)
        {
            return;
        }

        var totalSeconds = plan.DurationSeconds;
        AppendLog($"[pacing] idle break: stepping away for {totalSeconds}s.");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(totalSeconds);
        var stopped = false;
        var wakeRequested = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (_loopController.LoopStopRequested)
            {
                stopped = true;
                break;
            }

            if (_automationPassRuntime.IsImmediateWorkRequested)
            {
                wakeRequested = true;
                break;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var slice = remaining < TimeSpan.FromSeconds(ContinuousLoopMaxSleepSliceSeconds)
                ? remaining
                : TimeSpan.FromSeconds(ContinuousLoopMaxSleepSliceSeconds);
            await Task.Delay(slice, token);
        }

        if (stopped)
        {
            AppendLog("[pacing] idle break canceled by stop.");
            return;
        }

        if (wakeRequested)
        {
            AppendLog("[pacing] idle break ended early: queue state or settings changed.");
            _automationIdlePacing.CompleteBreak(options);
            return;
        }

        AppendLog("[pacing] idle break over; resuming.");
        _automationIdlePacing.CompleteBreak(options);
    }

    // Occasional "idle browse": open a random enabled non-functional page (map/statistics/reports/
    // messages) and read nothing, so the server-visible page mix looks like a real player browsing
    // instead of only build pages. Between loop passes only — mirrors MaybeTakeIdleBreakAsync.
    private async Task MaybeDoIdleBrowseAsync(BotOptions options, CancellationToken token)
    {
        var plan = _automationIdlePacing.PlanBrowse(
            options,
            sessionAvailable: !IsSessionSleeping && _isLoggedIn && _browserSessionLikelyOpen);
        if (plan.NoPageSelected)
        {
            AppendLog("[pacing:verbose] idle browse skipped: no pages selected.");
            return;
        }
        if (!plan.ShouldBrowse)
        {
            return;
        }

        var page = plan.Page!;
        AppendLog($"[pacing] idle browse: viewing {page}.");
        using var activity = _dashboardActivityTracker.Begin("Idle browsing");
        try
        {
            if (AutomationIdlePacing.RequiresStatisticsLandingPage(page))
            {
                AppendLog("[pacing:verbose] idle browse: opening the statistics overview before the selected statistics page.");
                await _botService.NavigateToPageAndReadHtmlAsync(
                    options, "/statistics", AppendLog, _loopController.AcquireSessionScopeToken());
            }

            await _botService.NavigateToPageAndReadHtmlAsync(
                options, page, AppendLog, _loopController.AcquireSessionScopeToken());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A transient nav/read failure here is cosmetic — log and move on, like the background refresh.
            AppendLog($"[pacing:verbose] idle browse skipped after page failure ({ex.Message}).");
        }

        _automationIdlePacing.CompleteBrowse(options);
    }

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
            AppendLog(
                $"[village-batch] start village='{GetQueueItemVillageName(item) ?? _activeWorkingVillageName ?? "-"}' "
                + $"key='{after.VillageKey}' source='{source}'.");
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
