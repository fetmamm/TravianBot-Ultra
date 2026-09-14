using System.Diagnostics;
using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IAutomationQueueItemFailurePort
{
    ValueTask<bool> TryHandleTroopsBlockedExecutionAsync(
        QueueItem item,
        Exception exception,
        string logPrefix);
    bool TryHandleTownHallUnavailableExecution(
        QueueItem item,
        Exception exception,
        string logPrefix);
    ValueTask ApplyConstructionInlineWaitAsync(
        TimeSpan delay,
        string? humanizeVillageKey,
        TimeSpan? humanizeWait);
    ValueTask ApplyHeroLowHpCooldownAsync(TimeSpan delay);
    void ApplyBreweryCelebrationDeferSignal(string? message, TimeSpan delay);
    void ApplyTownHallCelebrationDeferSignal(QueueItem item, string? message, TimeSpan delay);
    bool MarkDeferred(Guid itemId, TimeSpan delay);
    string? GetVillageKey(QueueItem item);
    string? GetVillageName(QueueItem item);
    void ClearConstructionLoginFillForBlockedHead(QueueItem item, string source);
    AutomationConstructionRequirementContext GetConstructionRequirementContext(QueueItem item);
    bool PatchDeferredPayload(QueueItem item, Dictionary<string, string> payload);
    bool MarkPermanentlyFailed(Guid itemId);
    void RaisePermanentFailureAlarm(QueueItem item, string message);
    ValueTask RefreshVillageActivityIndicatorsAsync();
    string FormatServerTime(DateTimeOffset value);
    void RebindPendingTemplateStep(QueueItem item, int effectiveSlotId);
    ValueTask HandleStorageCapacityDependencyAsync(
        QueueItem item,
        Dictionary<string, string> payload);
    ValueTask RefreshFarmListsAfterAutoSendAsync(QueueItem item, string message);
    ValueTask RefreshConstructionStatusAfterDeferAsync();
    ValueTask HandleCropShortageDeferAsync(QueueItem item);
    ValueTask RefreshTroopTrainingAfterBuildAsync(QueueItem item);
    bool UpdateDeferredPayload(Guid itemId, Dictionary<string, string> payload);
    bool MarkExecutionFailed(Guid itemId);
    void HandleStorageDependencyFailed(QueueItem item, string message);
    string FormatException(Exception exception);
    void Log(string message);
}

internal sealed class AutomationQueueItemFailure(
    IAutomationQueueItemFailurePort port,
    TimeProvider? timeProvider = null)
{
    private const int MaxConsecutiveRequirementDefers = 12;
    private const string HeroDeferReasonKey = "hero_defer_reason";
    private const string HeroDeferReasonReviving = "reviving";
    private const string HeroDeferReasonAway = "away";
    private const string HeroDeferReasonLowHp = "low_hp";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal async ValueTask<bool> HandleAsync(
        QueueItem item,
        Exception ex,
        string logPrefix,
        Stopwatch timer,
        AutomationRunMode mode)
    {
        if (await port.TryHandleTroopsBlockedExecutionAsync(item, ex, logPrefix))
        {
            return true;
        }

        if (port.TryHandleTownHallUnavailableExecution(item, ex, logPrefix))
        {
            return true;
        }

        // Prefer the typed defer signal (TaskWaitException.DelaySeconds) over parsing the message;
        // message parsing remains as a fallback for exceptions that carry the wait hint only as text.
        TimeSpan queueWaitDelay;
        bool hasQueueWait;
        if (ex is TaskWaitException typedWait)
        {
            queueWaitDelay = TimeSpan.FromSeconds(typedWait.DelaySeconds);
            hasQueueWait = true;
        }
        else
        {
            hasQueueWait = TryExtractQueueWaitDelay(ex.Message, out queueWaitDelay);
        }

        if (hasQueueWait)
        {
            if (IsConstructionQueueTask(item.TaskName)
                && ConstructionQueueState.IsConstructionRequirementDeferMessage(ex.Message)
                && TryResolveConstructActivePrerequisiteDelay(
                    item,
                    _timeProvider.GetUtcNow(),
                    out var dependencyDelay))
            {
                queueWaitDelay = dependencyDelay.Delay;
                port.Log(
                    $"[construction-dependency:verbose] worker requirement wait aligned to active prerequisite " +
                    $"id={item.Id} task='{item.TaskName}' waitSeconds={queueWaitDelay.TotalSeconds:F0} " +
                    $"requirements='{dependencyDelay.Detail}'");
            }

            var isHumanizeDefer = IsConstructionQueueTask(item.TaskName)
                && ex.Message.Contains("humanized construction start delay", StringComparison.OrdinalIgnoreCase);
            if (IsConstructionQueueTask(item.TaskName))
            {
                var humanizeVillage = isHumanizeDefer ? port.GetVillageKey(item) : null;
                TimeSpan? humanizeWait = isHumanizeDefer ? queueWaitDelay : null;
                await port.ApplyConstructionInlineWaitAsync(queueWaitDelay, humanizeVillage, humanizeWait);
            }

            if (IsHeroLowHpCooldown(item, ex))
            {
                await port.ApplyHeroLowHpCooldownAsync(queueWaitDelay);
            }

            // Mirror the brewery defer signal onto the Troops-tab celebration card so
            // its badge tracks the dashboard countdown. The continuous-loop brewery
            // task always defers (queue_wait_seconds is its happy-path return), so the
            // success-side RefreshBreweryCelebrationStatusAsync never fires; without
            // this push the troops badge stayed N/A while the dashboard timer ticked.
            if (string.Equals(item.TaskName, "run_brewery_celebration", StringComparison.OrdinalIgnoreCase))
            {
                port.ApplyBreweryCelebrationDeferSignal(ex.Message, queueWaitDelay);
            }

            if (string.Equals(item.TaskName, "run_town_hall_celebration", StringComparison.OrdinalIgnoreCase))
            {
                port.ApplyTownHallCelebrationDeferSignal(item, ex.Message, queueWaitDelay);
            }

            if (IsConstructionQueueTask(item.TaskName)
                && ConstructionQueueState.IsQueueOccupancyDeferMessage(ex.Message)
                && TryExtractPayloadInt(
                    ex.Message,
                    BotOptionPayloadKeys.QueueHumanizeExtraSeconds,
                    out var queueHumanizeExtraSeconds))
            {
                var observedAt = _timeProvider.GetUtcNow();
                var effectiveReadyAt = observedAt + queueWaitDelay;
                var rawSlotFinishAt = effectiveReadyAt.AddSeconds(-queueHumanizeExtraSeconds);
                var trigger = item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionPreSleepFill)
                    ? "pre-sleep"
                    : item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionLoginFill)
                        ? "login"
                        : "normal";
                port.Log(
                    $"[construction-timing] village='{port.GetVillageName(item) ?? "-"}' " +
                    $"task='{item.TaskName}' trigger={trigger} observedAt='{observedAt:O}' " +
                    $"rawSlotFinishAt='{rawSlotFinishAt:O}' humanDelaySeconds={queueHumanizeExtraSeconds} " +
                    $"effectiveReadyAt='{effectiveReadyAt:O}' navigation=completed.");
            }

            var deferred = port.MarkDeferred(item.Id, queueWaitDelay);
            if (deferred)
            {
                var constructionSuffix = IsConstructionQueueTask(item.TaskName)
                    ? FormatQueueDeferredConstructionSuffix(mode)
                    : string.Empty;
                var payloadChanged = DeferredWaitCalculator.TryMergeDeferredUpgradePayload(ex.Message, item.Payload, out var updatedPayload);
                if (IsDemolition(item)
                    && TryExtractPayloadInt(ex.Message, "demolish_server_wait_seconds", out var serverWaitSeconds)
                    && TryExtractPayloadInt(ex.Message, BotOptionPayloadKeys.DemolishDelaySeconds, out var demolishDelaySeconds))
                {
                    updatedPayload[BotOptionPayloadKeys.DemolishServerFinishAtUnixSeconds] =
                        _timeProvider.GetUtcNow().AddSeconds(serverWaitSeconds).ToUnixTimeSeconds().ToString();
                    updatedPayload[BotOptionPayloadKeys.DemolishDelaySeconds] = demolishDelaySeconds.ToString();
                    payloadChanged = true;
                }
                if (IsConstructionQueueTask(item.TaskName))
                {
                    // Record WHY this construction item deferred so the resource-driven refresh
                    // (RefreshDeferredConstructionWaitsAsync) doesn't resume a queue-full deferral
                    // the moment resources look sufficient, which caused a brief "Ready" flash
                    // before the worker re-deferred on the still-full build queue.
                    updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason] =
                        ConstructionQueueState.IsQueueOccupancyDeferMessage(ex.Message)
                            ? BotOptionPayloadKeys.UpgradeDeferReasonQueueFull
                            : ConstructionQueueState.IsConstructionInProgressDeferMessage(ex.Message)
                                ? BotOptionPayloadKeys.UpgradeDeferReasonInProgress
                            : ConstructionQueueState.IsConstructionStorageCapacityDeferMessage(ex.Message)
                                    ? BotOptionPayloadKeys.UpgradeDeferReasonStorageCapacity
                                : ConstructionQueueState.IsCropShortageDeferMessage(ex.Message)
                                    ? BotOptionPayloadKeys.UpgradeDeferReasonCropShortage
                                : ConstructionQueueState.IsConstructionRequirementDeferMessage(ex.Message)
                                        ? BotOptionPayloadKeys.UpgradeDeferReasonRequirements
                                        : ConstructionQueueState.IsConstructionResourceDeferMessage(ex.Message)
                                            ? BotOptionPayloadKeys.UpgradeDeferReasonResources
                                            : ConstructionQueueState.IsConstructionHumanizeDeferMessage(ex.Message)
                                                ? BotOptionPayloadKeys.UpgradeDeferReasonHumanize
                                                : BotOptionPayloadKeys.UpgradeDeferReasonRetry;
                    updatedPayload[BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                        ConstructionQueueState.CurrentDeferClassificationVersion;
                    payloadChanged = true;

                    // The pre-sleep fill flag is valid for exactly one execution attempt — this attempt
                    // just ran, so drop it. The sweep re-flags the item if it defers into the window again.
                    updatedPayload.Remove(BotOptionPayloadKeys.ConstructionPreSleepFill);
                    updatedPayload.Remove(BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied);
                    if (!TryExtractPayloadInt(
                            ex.Message,
                            BotOptionPayloadKeys.QueueHumanizeExtraSeconds,
                            out _))
                    {
                        updatedPayload.Remove(BotOptionPayloadKeys.QueueHumanizeExtraSeconds);
                    }
                    // The immediate-fill override stays meaningful only while a construction was started
                    // or its own Travian category is full. Resource/requirement/storage/retry waits are
                    // a real unstarted head and end the burst for later rows too.
                    var fillCanContinue = string.Equals(
                            updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason],
                            BotOptionPayloadKeys.UpgradeDeferReasonInProgress,
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason],
                            BotOptionPayloadKeys.UpgradeDeferReasonQueueFull,
                            StringComparison.OrdinalIgnoreCase);
                    updatedPayload.Remove(BotOptionPayloadKeys.ConstructionLoginFill);
                    updatedPayload.Remove(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds);
                    if (!fillCanContinue)
                    {
                        port.ClearConstructionLoginFillForBlockedHead(item, "empty-queue");
                    }

                    // Safety net for an unsatisfiable requirement. Requirement defers don't consume Retries
                    // (the prerequisite could still arrive), so without a bound a construct whose prerequisite
                    // never comes — e.g. the desktop cascade missed a cross-village/not-yet-loaded dependent —
                    // would defer forever. Count consecutive requirement defers and abandon (mark Failed +
                    // alarm) once the prerequisite has clearly not been built after many retries. Any other
                    // defer reason resets the counter below.
                    if (string.Equals(
                            updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason],
                            BotOptionPayloadKeys.UpgradeDeferReasonRequirements,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var requirementDeferCount =
                            (GetIntPayload(item.Payload, BotOptionPayloadKeys.RequirementDeferCount) ?? 0) + 1;
                        updatedPayload[BotOptionPayloadKeys.RequirementDeferCount] = requirementDeferCount.ToString();

                        // Never abandon while the village is actively building something — the prerequisite
                        // may be that in-progress construction (e.g. a user-started Academy 15 that Hospital
                        // waits on). Only give up once the village build queue is idle and the requirement is
                        // still unmet, which means the prerequisite is genuinely not coming.
                        if (requirementDeferCount >= MaxConsecutiveRequirementDefers
                            && !HasQueuedOrActivePrerequisite(item, _timeProvider.GetUtcNow()))
                        {
                            var payloadPersisted = port.PatchDeferredPayload(item, updatedPayload);
                            item.Payload = updatedPayload;
                            if (!payloadPersisted)
                            {
                                port.Log(
                                    $"[construction-queue] requirement-abandon payload persistence failed " +
                                    $"id={item.Id} task='{item.TaskName}'");
                            }

                            if (port.MarkPermanentlyFailed(item.Id))
                            {
                                port.Log(
                                    $"{logPrefix} ABANDONED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                                    $"requirement still unmet after {requirementDeferCount} retries — the prerequisite " +
                                    $"building is not built, queued or in progress. Removed from the active queue. " +
                                    $"Source='{ex.Message.Replace(Environment.NewLine, " ")}'");
                                port.RaisePermanentFailureAlarm(item, ex.Message);
                                await port.RefreshVillageActivityIndicatorsAsync();
                                return true;
                            }

                            port.Log(
                                $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                                $"requirement abandon threshold reached but terminal failure could not be persisted; " +
                                $"next try in {queueWaitDelay.TotalSeconds:F0}s");
                        }
                    }
                    else
                    {
                        // Progress is possible again — start a fresh count next time requirements stall.
                        updatedPayload.Remove(BotOptionPayloadKeys.RequirementDeferCount);
                    }

                    if (string.Equals(
                            updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason],
                            BotOptionPayloadKeys.UpgradeDeferReasonQueueFull,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var villageName = NormalizeVillageName(port.GetVillageName(item)) ?? "-";
                        var retryAt = _timeProvider.GetUtcNow() + queueWaitDelay;
                        port.Log(
                            $"[construction-queue:verbose] queue-full defer classified " +
                            $"id={item.Id} task='{item.TaskName}' village='{villageName}' mode={mode} " +
                            $"waitSeconds={queueWaitDelay.TotalSeconds:F0} retryAt='{port.FormatServerTime(retryAt)}' " +
                            $"source='{ex.Message.Replace(Environment.NewLine, " ")}'");
                        port.Log(
                            $"[construction] BUILD QUEUE FULL village='{villageName}'. " +
                            $"No more Construction will run in this village until the first active construction finishes. " +
                            $"Next retry: {port.FormatServerTime(retryAt)} (in {queueWaitDelay.TotalSeconds:F0}s).");
                    }
                    else if (string.Equals(
                        updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason],
                        BotOptionPayloadKeys.UpgradeDeferReasonInProgress,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        var villageName = NormalizeVillageName(port.GetVillageName(item)) ?? "-";
                        var retryAt = _timeProvider.GetUtcNow() + queueWaitDelay;
                        port.Log(
                            $"[construction-queue:verbose] in-progress defer classified " +
                            $"id={item.Id} task='{item.TaskName}' village='{villageName}' mode={mode} " +
                            $"retryAt='{port.FormatServerTime(retryAt)}'; later construction is held in queue order.");
                    }
                }

                if (payloadChanged)
                {
                    var payloadPersisted = port.PatchDeferredPayload(item, updatedPayload);
                    item.Payload = updatedPayload;
                    if (IsConstructionQueueTask(item.TaskName) && !payloadPersisted)
                    {
                        port.Log(
                            $"[construction-queue] construction payload persistence failed " +
                            $"id={item.Id} task='{item.TaskName}' " +
                            $"reason='{updatedPayload.GetValueOrDefault(BotOptionPayloadKeys.UpgradeDeferReason, "-")}'");
                    }
                }

                if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                    && TryExtractPayloadInt(ex.Message, BotOptionPayloadKeys.BuildingConstructSlotId, out var effectiveConstructSlot))
                {
                    if (BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
                        && construct is not null
                        && construct.SlotId != effectiveConstructSlot)
                    {
                        var reboundPayload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
                        {
                            [BotOptionPayloadKeys.BuildingConstructSlotId] = effectiveConstructSlot.ToString(),
                        };
                        if (port.PatchDeferredPayload(item, reboundPayload))
                        {
                            item.Payload = reboundPayload;
                            port.Log(
                                $"[construct-chain] persisted effective slot {effectiveConstructSlot} " +
                                $"for {construct.Name ?? $"gid {construct.Gid}"} target level {construct.TargetLevel}.");
                        }
                    }
                    port.RebindPendingTemplateStep(item, effectiveConstructSlot);
                }

                if (IsConstructionQueueTask(item.TaskName))
                {
                    await port.HandleStorageCapacityDependencyAsync(item, updatedPayload);
                }

                await port.RefreshFarmListsAfterAutoSendAsync(item, ex.Message);
                port.Log($"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | next try in {queueWaitDelay.TotalSeconds:F0}s{constructionSuffix}");
                if (string.Equals(item.TaskName, "anti_starve_hero_crop", StringComparison.OrdinalIgnoreCase)
                    && ex.Message.Contains("anti_starve_alarm=true", StringComparison.OrdinalIgnoreCase))
                {
                    port.Log(
                        $"ALARM: Hero crop anti-starve needs attention in village "
                        + $"'{port.GetVillageName(item) ?? "-"}'. {ex.Message.Replace(Environment.NewLine, " ")}");
                }
                // A building or resource mutation can start one build and then defer because the NEXT level
                // is blocked. That deferral skips the success-path construction refresh, so the cached live
                // Travian queue can stay empty even though the worker just observed a full queue. Re-read the
                // current village's construction status (the browser is already on it) before repainting.
                if ((IsBuildingMutationTask(item.TaskName) || IsResourceUpgradeTask(item.TaskName))
                    && !isHumanizeDefer)
                {
                    try
                    {
                        await port.RefreshConstructionStatusAfterDeferAsync();
                    }
                    catch (Exception refreshEx)
                    {
                        port.Log($"Construction status refresh after defer skipped: {refreshEx.Message}");
                    }
                }

                if (IsConstructionQueueTask(item.TaskName)
                    && ConstructionQueueState.IsCropShortageDeferMessage(ex.Message))
                {
                    await port.HandleCropShortageDeferAsync(item);
                }

                // build_troops always DEFERS on its happy path: it queues troops, then returns
                // queue_wait_seconds for the cooldown. That skips the success-path troop refresh, so the
                // per-village troop-training queue cache (and the Troops B/S/W icon) stayed grey even though
                // a training queue is now active. Re-read the village's queues when troops were actually
                // queued, so the icon turns green and the state is cached (and thus persisted across restart).
                if (string.Equals(item.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase)
                    && ex is TaskWaitException { ReasonCode: TaskWaitReasons.WorkQueued })
                {
                    try
                    {
                        await port.RefreshTroopTrainingAfterBuildAsync(item);
                    }
                    catch (Exception refreshEx)
                    {
                        port.Log($"Troop training refresh after deferred build skipped: {refreshEx.Message}");
                    }
                }

                // Tag deferred Hero state so the jitter refresh can release the task early when a live
                // signal supersedes its estimate (bucket revive, early return, sufficient HP/level-up).
                if (string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase)
                    && ex is TaskWaitException heroWait
                    && heroWait.ReasonCode is TaskWaitReasons.HeroReviving
                        or TaskWaitReasons.HeroAway
                        or TaskWaitReasons.HeroHpTooLow)
                {
                    var heroPayload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
                    {
                        [HeroDeferReasonKey] = heroWait.ReasonCode switch
                        {
                            TaskWaitReasons.HeroReviving => HeroDeferReasonReviving,
                            TaskWaitReasons.HeroAway => HeroDeferReasonAway,
                            _ => HeroDeferReasonLowHp,
                        },
                    };
                    if (port.UpdateDeferredPayload(item.Id, heroPayload))
                    {
                        item.Payload = heroPayload;
                    }
                }

                // Repaint the per-village overview icons so the deferred task shows its amber "waiting" state.
                await port.RefreshVillageActivityIndicatorsAsync();
                return true;
            }
        }

        port.MarkExecutionFailed(item.Id);
        port.HandleStorageDependencyFailed(item, ex.Message);
        port.Log(FormatQueueFailureLog(logPrefix, timer, item, ex, mode));
        port.RaisePermanentFailureAlarm(item, ex.Message);
        return true;
    }

    private bool TryResolveConstructActivePrerequisiteDelay(
        QueueItem item,
        DateTimeOffset now,
        out ConstructionDependencyDelay dependencyDelay)
    {
        dependencyDelay = null!;
        var status = port.GetConstructionRequirementContext(item).Status;
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

    private bool HasQueuedOrActivePrerequisite(QueueItem item, DateTimeOffset now)
    {
        var context = port.GetConstructionRequirementContext(item);
        if (context.Status is null)
        {
            return false;
        }

        var result = ConstructionDependencyGate.ResolveConstructRequirementGuard(
            item,
            context.Status,
            context.SameVillageItems,
            now);
        if (result.Action is ConstructionRequirementGuardAction.DeferForActivePrerequisite
            or ConstructionRequirementGuardAction.DeferForQueuedPrerequisite)
        {
            return true;
        }

        return result.Action == ConstructionRequirementGuardAction.None
            && ConstructionQueueState.ResolveCurrentActiveConstructions(context.Status).Count > 0;
    }

    private static bool TryExtractQueueWaitDelay(string message, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var match = Regex.Match(message, @"queue_wait_seconds=(?<seconds>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["seconds"].Value, out var seconds))
        {
            return false;
        }

        delay = TimeSpan.FromSeconds(Math.Max(1, seconds));
        return true;
    }

    private static bool TryExtractPayloadInt(string? message, string key, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var match = Regex.Match(
            message,
            $@"(?<!\S){Regex.Escape(key)}=(?<value>\d+)",
            RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["value"].Value, out value);
    }

    private static int? GetIntPayload(IReadOnlyDictionary<string, string> payload, string key) =>
        payload.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : null;

    private static bool IsConstructionQueueTask(string? taskName) =>
        string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "demolish_building_to_level", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildingMutationTask(string? taskName) => IsConstructionQueueTask(taskName);

    private static bool IsResourceUpgradeTask(string? taskName) =>
        string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase);

    private static bool IsDemolition(QueueItem item) =>
        string.Equals(item.TaskName, "demolish_building_to_level", StringComparison.OrdinalIgnoreCase);

    private static bool IsHeroLowHpCooldown(QueueItem item, Exception exception) =>
        string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase)
        && exception is TaskWaitException { ReasonCode: TaskWaitReasons.HeroHpTooLow };

    private string FormatQueueFailureLog(
        string logPrefix,
        Stopwatch timer,
        QueueItem item,
        Exception exception,
        AutomationRunMode mode) =>
        mode == AutomationRunMode.ContinuousLoop
            ? $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s | {port.FormatException(exception)}"
            : $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | {port.FormatException(exception)}";

    private static string FormatQueueDeferredConstructionSuffix(AutomationRunMode mode) =>
        mode == AutomationRunMode.ContinuousLoop
            ? " | construction wait timer updated; continuing with next enabled group; no Hero refresh was triggered by this defer"
            : " | construction wait timer updated; continuing with other ready tasks; no Hero refresh was triggered by this defer";

    private static string? NormalizeVillageName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

