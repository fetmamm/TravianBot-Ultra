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
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static readonly TimeSpan LoopPickVerboseThrottle = TimeSpan.FromSeconds(30);
    // Idle loop passes no longer log "[LOOP n] START" + "WAIT" every few seconds. Instead a single
    // "[LOOP n] idle" heartbeat is logged at most this often while nothing is ready, so the log shows the
    // loop is alive without the per-pass spine. Active passes (a PICK) and failures still log in full.
    private static readonly TimeSpan LoopIdleHeartbeatInterval = TimeSpan.FromMinutes(2);
    private DateTimeOffset _lastLoopIdleHeartbeatUtc = DateTimeOffset.MinValue;
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

        // Queued items must not auto-start while the bot is idle. They may begin when the user
        // presses "Start bot", or be picked up by a runner that is already executing.
        var alreadyRunning = _autoQueueRunning || IsContinuousLoopRunning();
        if (!alreadyRunning)
        {
            return;
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

    private async Task EnsureContinuousLoopRuntimeItemsAsync(BotOptions options, CancellationToken cancellationToken)
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
        if (consideredGroups.Count <= 0 && !heroPollingEnabled)
        {
            return;
        }

        var queueItems = _botService.GetQueueItemsForDisplay();
        var activeItems = queueItems
            .Where(item => item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused)
            .ToList();

        bool HasActiveTask(string taskName)
        {
            return activeItems.Any(item =>
                string.Equals(item.TaskName, taskName, StringComparison.OrdinalIgnoreCase));
        }

        // Per-village variant: an item only counts as active for a village when its payload targets that
        // same village. Matched by the stable coordinate KEY, not the name — otherwise a renamed village
        // would fail to find its existing runtime task and enqueue a duplicate under the new name.
        bool HasActiveTaskForVillage(string taskName, VillageSelectionItem village)
        {
            var villageKey = _villageSettingsStore.ResolveCanonicalKey(GetVillageKey(village)) ?? string.Empty;
            return activeItems.Any(item =>
                string.Equals(item.TaskName, taskName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(GetQueueItemVillageKey(item) ?? string.Empty, villageKey, StringComparison.OrdinalIgnoreCase));
        }

        var automationVillages = GetEnabledAutomationVillages();

        if (heroPollingEnabled && !HasActiveTask("hero_manage"))
        {
            var adventureCount = await _botService.RefreshAdventureCountAsync(options, AppendLog, cancellationToken);
            await Dispatcher.InvokeAsync(() => ApplyHeroAdventureAvailability(adventureCount));
            if (adventureCount is > 0)
            {
                var payload = BuildHeroRuntimePayload();
                _botService.EnqueueRuntime("hero_manage", "Hero adventure", payload, priority: -50, maxRetries: 0);
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
                var buildTroopsVillageKey = _villageSettingsStore.ResolveCanonicalKey(GetVillageKey(village)) ?? string.Empty;
                var existingPending = activeItems.FirstOrDefault(existing =>
                    string.Equals(existing.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase)
                    && existing.Status == QueueStatus.Pending
                    && string.Equals(GetQueueItemVillageKey(existing) ?? string.Empty, buildTroopsVillageKey, StringComparison.OrdinalIgnoreCase));
                if (existingPending is not null
                    && !ContinuousLoopSelector.PayloadEquals(existingPending.Payload, trainingPayload)
                    && _botService.UpdateDeferredQueueItem(existingPending.Id, trainingPayload))
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

            _botService.EnqueueRuntime("build_troops", "Build troops", trainingPayload, priority: -50, maxRetries: 0);
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
                _botService.EnqueueRuntime("run_brewery_celebration", "Auto celebration", BuildVillageRuntimePayload(capital), priority: -50, maxRetries: 0);
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
                var restoredItem = _botService.EnqueueRuntime(
                    "run_town_hall_celebration",
                    "Town Hall celebration",
                    restoredPayload,
                    priority: -50,
                    maxRetries: 0);
                _botService.MarkQueueItemDeferred(restoredItem.Id, remembered.EndsAtUtc - nowUtc);
                AppendLog($"[town-hall] restored running celebration for '{village.Name}' until {FormatQueueServerTime(remembered.EndsAtUtc)}.");
                continue;
            }

            var payload = BuildVillageRuntimePayload(village);
            payload[BotOptionPayloadKeys.TownHallCelebrationMode] = mode;
            _botService.EnqueueRuntime("run_town_hall_celebration", "Town Hall celebration", payload, priority: -50, maxRetries: 0);
        }

        if (consideredGroups.Contains(QueueGroup.Farming) && !IsFarmingGroupBlocked())
        {
            var goldClubEnabled = await _botService.ReadAndPersistGoldClubStatusAsync(options, AppendLog, cancellationToken);
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
                    var farmingPayload = new FarmingPayload(selectedFarmLists, selectedSnapshot.Ids).ToDictionary();
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
                        _botService.EnqueueRuntime("send_farmlists", displayName, payload, priority: -50, maxRetries: 0);
                        AppendLog($"Continuous farming queued for village '{farmingVillage.Name}'.");
                    }
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
    // analysis so automation knows their layout. Dispatcher-owned (same threading as _villageStatusCacheByName):
    // filled on the UI thread from village reads, drained one-per-loop-pass via Dispatcher round-trips.
    private sealed record PendingVillageAnalysis(string Name, string? Url, int Attempts);
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

        var missing = NewVillageStartupAnalyzer.FindVillagesWithoutKnownStatus(villages, _villageStatusCacheByName);
        foreach (var village in missing)
        {
            var name = NormalizeVillageName(village.Name);
            if (name is null || _villagesPendingFirstAnalysis.ContainsKey(name))
            {
                continue;
            }

            _villagesPendingFirstAnalysis[name] = new PendingVillageAnalysis(name, village.Url, 0);
            AppendLog($"[new-village-runtime] Discovered '{name}' without cached dorf1/dorf2 status. Queued for one-time analysis.");
        }
    }

    private bool HasCachedDorf1Dorf2Status(string villageName)
        => _villageStatusCacheByName.TryGetValue(villageName, out var status)
           && status.ResourceFields is { Count: > 0 }
           && status.Buildings is { Count: > 0 };

    // Picks the next village still needing analysis (Dispatcher thread). Drops entries that became cached
    // meanwhile, or that exhausted their attempts, and counts the attempt for the returned one.
    private (string Name, string? Url)? TakeNextVillagePendingFirstAnalysis()
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
            return (entry.Name, entry.Url);
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

        var (name, url) = pick.Value;
        try
        {
            AppendLog($"[new-village-runtime] Analyzing '{name}' (dorf1/dorf2) so automation knows its layout.");
            var status = await _botService.ReadVillageStatusAsync(options, AppendLog, name, url, token);
            await Dispatcher.InvokeAsync(() =>
            {
                CacheVillageStatus(status, name);
                _villagesPendingFirstAnalysis.Remove(name);
            });
            AppendLog($"[new-village-runtime] Cached '{name}': fields={status.ResourceFields.Count}, buildings={status.Buildings.Count}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep it queued (attempt already counted) for a later pass; a transient nav/read error must not
            // silently drop the analysis. TakeNext drops it once attempts are exhausted.
            AppendLog($"[new-village-runtime] Could not analyze '{name}': {ex.Message}");
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
                    ContinuousLoopSelector.IsUtilityTask(item.TaskName) &&
                    IsAutoCollectUtilityTaskEnabledNow(item.TaskName, options) &&
                    IsQueueItemAllowedByAutomationSettings(item) &&
                    item.Status == QueueStatus.Pending &&
                    item.NextAttemptAt <= now));
        var activeVillageKey = _activeWorkingVillageKey;
        var readyUtilityItem = ContinuousLoopSelector.SelectReadyUtilityItem(
            readyUtilityItems,
            activeVillageKey,
            GetQueueItemVillageKey);
        if (readyUtilityItem is not null)
        {
            return readyUtilityItem;
        }

        // Consider the union of enabled runtime groups across all active villages. Persistent Queue-page
        // work is appended below only to preserve group ordering; every item is still gated by its own
        // village Auto toggle and per-village group toggle before it can run.
        var orderedGroups = ContinuousLoopSelector.BuildConsideredGroups(
            GetContinuousLoopConsideredGroupsInOrder(),
            queueItems);

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
                    !ContinuousLoopSelector.IsUtilityTask(item.TaskName) &&
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

            // Rotate non-construction groups across villages too: troop-training/smithy/farming items are
            // tagged per village, so a village whose head item is waiting must not block another village's
            // ready item. For truly global/village-less groups (hero, …) all items share one village key,
            // so this collapses to the original strict in-order head selection.
            var groupRotationKey = GetContinuousGroupRotationVillageKey(group);
            var candidate = QueueVillageRotation.SelectByVillageRotation(
                orderedGroupItems,
                GetQueueItemVillageKey,
                villageItems => group == QueueGroup.Hero
                    ? ContinuousLoopSelector.SelectReadyHeroGroupItem(villageItems, now)
                    : ContinuousLoopSelector.SelectReadyGroupHead(villageItems, now),
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
        if (string.Equals(item.TaskName, "send_farmlists", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(GetQueueItemVillageKey(item))
            && string.IsNullOrWhiteSpace(GetQueueItemVillageName(item)))
        {
            return false;
        }

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

        var villageName = NormalizeVillageName(village.Name);
        VillageStatus? status = null;
        if (villageName is not null)
        {
            _villageStatusCacheByName.TryGetValue(villageName, out status);
        }

        if (status is null
            && _lastBuildingStatus is not null
            && string.Equals(NormalizeVillageName(_lastBuildingStatus.ActiveVillage), villageName, StringComparison.OrdinalIgnoreCase))
        {
            status = _lastBuildingStatus;
        }

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
            villageItems => SelectNextConstructionQueueItem(villageItems, now, out _),
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

        if (TryDeferConstructUntilActivePrerequisiteFinishes(selection.Item, now, preview, out var dependencySkipReason))
        {
            skipReason = dependencySkipReason;
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

        var item = villageItems.FirstOrDefault();
        return item is null
            ? ConstructionQueueState.ResolveAvailability(status, _travianPlusActive, now)
            : ConstructionQueueState.ResolveAvailabilityForItem(status, _travianPlusActive, item, now);
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

    private async Task MaybeCheckInboxDuringContinuousLoopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        await RefreshInboxIndicatorsAsync(logErrors: false, force: true, cancellationToken);
    }

    private void MarkContinuousBrowserActivity()
    {
        var now = DateTimeOffset.UtcNow;
        _lastContinuousBrowserActivityUtc = now;
        _nextContinuousKeepAliveAtUtc = now.Add(ResolveContinuousKeepAliveDelay());
    }

    private static TimeSpan ResolveContinuousKeepAliveDelay()
    {
        return TimeSpan.FromSeconds(Random.Shared.Next(
            ContinuousKeepAliveMinIntervalSeconds,
            ContinuousKeepAliveMaxIntervalSeconds + 1));
    }

    // Keep the Travian page from going stale while the loop is idle-waiting, but only when queued work is
    // due soon. Long idle periods should stay idle instead of refreshing on a fixed robotic cadence.
    private async Task MaybeKeepBrowserFreshDuringContinuousLoopAsync(BotOptions options, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (_nextContinuousKeepAliveAtUtc == DateTimeOffset.MinValue)
        {
            var anchor = _lastContinuousBrowserActivityUtc == DateTimeOffset.MinValue
                ? now
                : _lastContinuousBrowserActivityUtc;
            _nextContinuousKeepAliveAtUtc = anchor.Add(ResolveContinuousKeepAliveDelay());
        }

        if (now < _nextContinuousKeepAliveAtUtc)
        {
            return;
        }

        if (now - _lastContinuousKeepAliveFailureUtc < TimeSpan.FromMinutes(2))
        {
            return;
        }

        if (IsSessionSleeping)
        {
            // A sleeping session must stay idle: refreshing here would log in and reload the page,
            // waking it and breaking the sleep throttle. Mark activity so this only logs once per
            // keep-alive interval instead of every wait tick.
            MarkContinuousBrowserActivity();
            AppendLog("[keep-alive:verbose] skipped because the session is sleeping.");
            return;
        }

        if (_resourceSnapshotRefreshRunning)
        {
            MarkContinuousBrowserActivity();
            AppendLog("[keep-alive:verbose] skipped because resource refresh is already reading the browser.");
            return;
        }

        if (!HasContinuousLoopWorkDueSoon(now))
        {
            _nextContinuousKeepAliveAtUtc = now.Add(ResolveContinuousKeepAliveDelay());
            AppendLog("[keep-alive:verbose] skipped because no continuous-loop work is due soon.");
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

    private bool HasContinuousLoopWorkDueSoon(DateTimeOffset now)
    {
        try
        {
            var dueBefore = now.AddSeconds(ContinuousKeepAliveDueSoonSeconds);
            return GetContinuousLoopRelevantQueueItems()
                .Any(item => item.Status == QueueStatus.Pending && item.NextAttemptAt <= dueBefore);
        }
        catch
        {
            return true;
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
        // Reschedule the first idle "step away" break and idle browse relative to this run's start.
        _nextIdleBreakDueUtc = DateTimeOffset.MinValue;
        _nextIdleBrowseDueUtc = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            if (_loopController.LoopStopRequested)
            {
                AppendLog("Loop stop requested. Exiting after current action.");
                break;
            }

            var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
            var tickId = Interlocked.Increment(ref _loopTickCounter);
            var tickSw = Stopwatch.StartNew();
            try
            {
                // Occasional human-like "stepped away from the computer" pause. Between tasks only, so it
                // never interrupts a build/click.
                await MaybeTakeIdleBreakAsync(options, token);
                // Occasional human-like "look around" — open a non-functional page (map/reports/etc.)
                // and read nothing. Between tasks only, same as the idle break.
                await MaybeDoIdleBrowseAsync(options, token);
                await EnsureChromiumInstalledAsync();
                await HonorPendingVillageSwitchAsync(options, token);
                await EnsureContinuousLoopConstructionStatusAsync(options, token);
                await MaybeAnalyzeNewVillageDuringContinuousLoopAsync(options, token);
                await EnsureContinuousLoopRuntimeItemsAsync(options, token);
                await MaybeCheckInboxDuringContinuousLoopAsync(token);

                var next = SelectNextQueueItemForContinuousLoop();
                if (next is not null)
                {
                    AppendLog($"[LOOP {tickId}] PICK group={next.Group}, task={next.TaskName}, retries={next.Retries}/{next.MaxRetries}");
                    // Real activity is its own liveness signal — reset the idle heartbeat so it only fires
                    // after the loop has genuinely gone quiet.
                    _lastLoopIdleHeartbeatUtc = DateTimeOffset.UtcNow;
                    // Pause pressed while picking: exit before sitting out the pre-task pacing delay. The
                    // item stays pending and runs on resume — nothing is in flight yet, so this is safe and
                    // makes Pause react immediately instead of waiting out a few seconds of pacing.
                    if (_loopController.LoopStopRequested)
                    {
                        break;
                    }

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

                    // Pause pressed during the task: skip the post-task cooldown (pure idle pacing, nothing
                    // in flight) so the loop exits right after the action instead of waiting it out.
                    if (_loopController.LoopStopRequested)
                    {
                        break;
                    }

                    await ApplyPostTaskCooldownAsync(next, options, token);
                }
                else
                {
                    var waitDelay = ResolveContinuousLoopWaitDelay();
                    await WaitForNextContinuousLoopPassAsync(tickId, waitDelay, options, token, routineIdleWait: true);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppendLog($"[LOOP {tickId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s | {FormatExceptionForLog(ex)}");
                await WaitForNextContinuousLoopPassAsync(tickId, null, options, token, routineIdleWait: false);
            }
        }
    }

    private TimeSpan? ResolveContinuousLoopWaitDelay()
    {
        try
        {
            if (GetContinuousLoopConsideredGroupsInOrder().Count <= 0)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var nextDeferred = GetContinuousLoopRelevantQueueItems()
                .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                .OrderBy(item => item.NextAttemptAt)
                .FirstOrDefault();
            if (nextDeferred is null)
            {
                return null;
            }

            var delay = nextDeferred.NextAttemptAt - now;
            if (delay < TimeSpan.FromSeconds(1))
            {
                return TimeSpan.FromSeconds(1);
            }

            return delay;
        }
        catch
        {
            return null;
        }
    }

    private async Task WaitForNextContinuousLoopPassAsync(long tickId, TimeSpan? waitDelay, BotOptions options, CancellationToken token, bool routineIdleWait = false)
    {
        var totalSeconds = waitDelay is null
            ? Math.Max(1, options.LoopIntervalSeconds)
            : Math.Max(1, (int)Math.Ceiling(waitDelay.Value.TotalSeconds));
        if (options.ActionPacingEnabled)
        {
            var minMs = (int)Math.Round(Math.Max(0, options.ActionPacingLoopMinSeconds) * 1000);
            var maxMs = (int)Math.Round(Math.Max(options.ActionPacingLoopMinSeconds, options.ActionPacingLoopMaxSeconds) * 1000);
            var pacingSeconds = Random.Shared.Next(minMs, maxMs + 1) / 1000.0;
            var pacingTotalSeconds = Math.Max(1, (int)Math.Ceiling(pacingSeconds));
            totalSeconds = waitDelay is null
                ? pacingTotalSeconds
                : Math.Min(totalSeconds, pacingTotalSeconds);
        }

        // Routine idle waits are throttled to a single "[LOOP n] idle" heartbeat every couple of minutes
        // so the loop spine no longer fills the log; the FAIL path (and any non-idle caller) still logs
        // its wait every time. Logging-only — the wait below is unchanged.
        if (!routineIdleWait)
        {
            AppendLog($"[LOOP {tickId}] WAIT {totalSeconds}s");
        }
        else if (DateTimeOffset.UtcNow - _lastLoopIdleHeartbeatUtc >= LoopIdleHeartbeatInterval)
        {
            _lastLoopIdleHeartbeatUtc = DateTimeOffset.UtcNow;
            AppendLog($"[LOOP {tickId}] idle — nothing ready, waiting {totalSeconds}s");
        }

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

    // Occasional human-like "stepped away from the computer" pause. Called at the top of a loop
    // iteration (between tasks only, never mid-action). Somewhere within the interval range a random
    // pause of the duration range fires; when it ends the interval reschedules. Logged under Pacing.
    private async Task MaybeTakeIdleBreakAsync(BotOptions options, CancellationToken token)
    {
        if (!options.ActionPacingIdleBreakEnabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_nextIdleBreakDueUtc == DateTimeOffset.MinValue)
        {
            // First pass of this run: schedule the first break, don't fire immediately.
            _nextIdleBreakDueUtc = now.Add(RandomIdleBreakInterval(options));
            return;
        }

        if (now < _nextIdleBreakDueUtc)
        {
            return;
        }

        // Only "step away" during normal, logged-in operation. Never while the session is sleeping
        // (the loop is stopped then anyway) or mid login/recovery — and if a break came due during such
        // a window, reschedule instead of firing the instant work resumes.
        if (IsSessionSleeping || !_isLoggedIn || !_browserSessionLikelyOpen)
        {
            _nextIdleBreakDueUtc = now.Add(RandomIdleBreakInterval(options));
            return;
        }

        var durationMinutes = RandomInRangeMinutes(
            options.ActionPacingIdleBreakDurationMinMinutes,
            options.ActionPacingIdleBreakDurationMaxMinutes);
        var totalSeconds = Math.Max(1, (int)Math.Round(durationMinutes * 60));
        AppendLog($"[pacing] idle break: stepping away for {totalSeconds}s.");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(totalSeconds);
        var stopped = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (_loopController.LoopStopRequested)
            {
                stopped = true;
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

        AppendLog("[pacing] idle break over; resuming.");
        _nextIdleBreakDueUtc = DateTimeOffset.UtcNow.Add(RandomIdleBreakInterval(options));
    }

    private static TimeSpan RandomIdleBreakInterval(BotOptions options)
    {
        var minutes = RandomInRangeMinutes(
            options.ActionPacingIdleBreakIntervalMinMinutes,
            options.ActionPacingIdleBreakIntervalMaxMinutes);
        // Floor so a mis-set 0/tiny interval can't turn the loop into a constant-break busy loop.
        return TimeSpan.FromSeconds(Math.Max(5.0, minutes * 60.0));
    }

    // Occasional "idle browse": open a random enabled non-functional page (map/statistics/reports/
    // messages) and read nothing, so the server-visible page mix looks like a real player browsing
    // instead of only build pages. Between loop passes only — mirrors MaybeTakeIdleBreakAsync.
    private async Task MaybeDoIdleBrowseAsync(BotOptions options, CancellationToken token)
    {
        if (!options.ActionPacingIdleBrowseEnabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_nextIdleBrowseDueUtc == DateTimeOffset.MinValue)
        {
            // First pass of this run: schedule the first browse, don't fire immediately.
            _nextIdleBrowseDueUtc = now.Add(RandomIdleBrowseInterval(options));
            return;
        }

        if (now < _nextIdleBrowseDueUtc)
        {
            return;
        }

        // Only browse during normal, logged-in operation. Never while the session is sleeping or mid
        // login/recovery — and if a browse came due during such a window, reschedule instead of firing
        // the instant work resumes (same rule as the idle break).
        if (IsSessionSleeping || !_isLoggedIn || !_browserSessionLikelyOpen)
        {
            _nextIdleBrowseDueUtc = now.Add(RandomIdleBrowseInterval(options));
            return;
        }

        var pages = GetEnabledIdleBrowsePages(options);
        if (pages.Count == 0)
        {
            // No page selected: treat as off, but keep rescheduling cheaply so re-enabling one works.
            AppendLog("[pacing:verbose] idle browse skipped: no pages selected.");
            _nextIdleBrowseDueUtc = now.Add(RandomIdleBrowseInterval(options));
            return;
        }

        var page = pages[Random.Shared.Next(pages.Count)];
        AppendLog($"[pacing] idle browse: viewing {page}.");
        try
        {
            if (RequiresStatisticsLandingPage(page))
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

        _nextIdleBrowseDueUtc = DateTimeOffset.UtcNow.Add(RandomIdleBrowseInterval(options));
    }

    // Official page paths for the idle-browse whitelist, filtered to the ones toggled on in settings.
    internal static List<string> GetEnabledIdleBrowsePages(BotOptions options)
    {
        var pages = new List<string>(8);
        if (options.ActionPacingIdleBrowsePageMap) pages.Add("karte.php");
        if (options.ActionPacingIdleBrowsePageStatistics) pages.Add("/statistics/general");
        if (options.ActionPacingIdleBrowsePageStatisticsHero) pages.Add("/statistics/hero");
        if (options.ActionPacingIdleBrowsePageStatisticsTop10) pages.Add("/statistics/player/top10");
        if (options.ActionPacingIdleBrowsePageStatisticsDefenders) pages.Add("/statistics/player/defenders");
        if (options.ActionPacingIdleBrowsePageStatisticsAttackers) pages.Add("/statistics/player/attackers");
        if (options.ActionPacingIdleBrowsePageReports) pages.Add("berichte.php");
        if (options.ActionPacingIdleBrowsePageMessages) pages.Add("nachrichten.php");
        return pages;
    }

    internal static bool RequiresStatisticsLandingPage(string page)
    {
        return page.StartsWith("/statistics/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(page, "/statistics", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan RandomIdleBrowseInterval(BotOptions options)
    {
        var minutes = RandomInRangeMinutes(
            options.ActionPacingIdleBrowseIntervalMinMinutes,
            options.ActionPacingIdleBrowseIntervalMaxMinutes);
        // Floor so a mis-set 0/tiny interval can't turn the loop into a constant-browse busy loop.
        return TimeSpan.FromSeconds(Math.Max(5.0, minutes * 60.0));
    }

    private static double RandomInRangeMinutes(double min, double max)
    {
        var lo = Math.Max(0, min);
        var hi = Math.Max(lo, max);
        return lo + (Random.Shared.NextDouble() * (hi - lo));
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

    private async Task ExecuteQueuedItemsNowAsync(CancellationToken cancellationToken)
    {
        var runId = System.Threading.Interlocked.Increment(ref _operationCounter);
        // Each run starts a fresh village rotation so it does not resume mid-drain on a stale village.
        _autoQueueRotationVillageKey = null;
        LogConservativeAutomationWarnings(AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions()));
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
                var continueAutoQueue = await WaitForNextAutoQueuePassAsync(runId, waitDelay, cancellationToken);
                if (!continueAutoQueue)
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

            // Pause pressed during the task: skip the post-task cooldown (pure idle pacing, nothing in
            // flight) so the queue run stops promptly instead of waiting it out.
            if (_loopController.QueueStopRequested)
            {
                return;
            }

            await ApplyPostTaskCooldownAsync(next, options, cancellationToken);
        }
    }

    private async Task<bool> WaitForNextAutoQueuePassAsync(
        long runId,
        TimeSpan waitDelay,
        CancellationToken cancellationToken)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow.Add(waitDelay);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_loopController.QueueStopRequested)
                {
                    AppendLog($"[AUTOQ {runId}] WAIT canceled by stop.");
                    return false;
                }

                if (Interlocked.Exchange(ref _continuousLoopWakeRequested, 0) == 1)
                {
                    AppendLog($"[AUTOQ {runId}] WAIT ended early: queue state or settings changed.");
                    return true;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return true;
                }

                var slice = remaining < TimeSpan.FromSeconds(ContinuousLoopMaxSleepSliceSeconds)
                    ? remaining
                    : TimeSpan.FromSeconds(ContinuousLoopMaxSleepSliceSeconds);
                await Task.Delay(slice, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return true;
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
        if (!options.ActionPacingEnabled)
        {
            warnings.Add("[conservative] action pacing is disabled; automation can run faster than the conservative defaults.");
        }

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
                warnings.Add("[conservative] session daily max is disabled; default is 16h.");
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
        if (string.Equals(signature, _lastConservativeAutomationWarningSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastConservativeAutomationWarningSignature = signature;
        foreach (var warning in warnings)
        {
            AppendLog(warning);
        }
    }
}
