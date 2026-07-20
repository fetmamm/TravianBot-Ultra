using TbotUltra.Core.Configuration;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services.Automation;
using Microsoft.Playwright;
using System.Text.Json;

namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static async Task ExecuteStatusAsync(TaskExecutionContext context)
    {
        var status = await context.Client.ReadVillageStatusAsync(context.CancellationToken);
        context.Log($"Village status read. ActiveVillage={status.ActiveVillage}, Villages={status.Villages.Count}, Resources={status.Resources.Count}, ResourceFields={status.ResourceFields.Count}, Buildings={status.Buildings.Count}, Queue={status.BuildQueue.Count}");
    }

    private static async Task ExecuteScanAllVillagesAsync(TaskExecutionContext context)
    {
        var statuses = await context.Client.ReadAllVillageStatusesAsync(context.CancellationToken);
        context.Log($"[scan] all villages scanned — {statuses.Count} status(es)");
    }

    private static async Task ExecuteAccountSnapshotAsync(TaskExecutionContext context)
    {
        var snapshot = await context.Client.ReadAccountSnapshotAsync(cancellationToken: context.CancellationToken);
        context.Log($"Account snapshot read. Tribe={snapshot.Tribe}, ActiveVillage={snapshot.ActiveVillage}, VillageCount={snapshot.VillageCount}, ServerTimeUtc={snapshot.ServerTimeUtc}");
    }

    private static async Task ExecuteUpgradeResourceToLevelAsync(TaskExecutionContext context)
    {
        if (context.Options.ResourceUpgradeSlotId is null || context.Options.ResourceUpgradeTargetLevel is null)
        {
            context.Log("Task 'upgrade_resource_to_level' requires config values resource_upgrade_slot_id and resource_upgrade_target_level.");
            return;
        }

        var result = await context.Client.UpgradeResourceToLevelAsync(
            context.Options.ResourceUpgradeSlotId.Value,
            context.Options.ResourceUpgradeTargetLevel.Value,
            context.CancellationToken);
        context.Log(result);
        context.RecordTaskResult("upgrade_resource_to_level", result);
        ThrowIfTaskBlocked("upgrade_resource_to_level", result);
    }

    private static async Task ExecuteUpgradeAllResourcesToLevelAsync(TaskExecutionContext context)
    {
        if (context.Options.ResourceUpgradeTargetLevel is null)
        {
            context.Log("Task 'upgrade_all_resources_to_level' requires config value resource_upgrade_target_level.");
            return;
        }

        var result = await context.Client.UpgradeAllResourcesToLevelAsync(
            context.Options.ResourceUpgradeTargetLevel.Value,
            context.Options.ResourceBuildStrategy,
            context.CancellationToken);
        context.Log(result);
        context.RecordTaskResult("upgrade_all_resources_to_level", result);
        ThrowIfTaskBlocked("upgrade_all_resources_to_level", result);
    }

    private static async Task ExecuteUpgradeBuildingToLevelAsync(TaskExecutionContext context)
    {
        if (context.Options.BuildingUpgradeSlotId is null || context.Options.BuildingUpgradeTargetLevel is null)
        {
            context.Log("Task 'upgrade_building_to_level' requires config values building_upgrade_slot_id and building_upgrade_target_level.");
            return;
        }

        var result = await context.Client.UpgradeBuildingToLevelAsync(
            context.Options.BuildingUpgradeSlotId.Value,
            context.Options.BuildingUpgradeTargetLevel.Value,
            context.CancellationToken);
        context.Log(result);
        context.RecordTaskResult("upgrade_building_to_level", result);
        // Desktop's HandleQueueItemSucceededAsync triggers RefreshConstructionStatusAsync
        // (fresh dorf1+dorf2 read) immediately after this task returns. A worker-side snapshot
        // read here would be discarded by that fresh read — skip it.
        ThrowIfTaskBlocked("upgrade_building_to_level", result);
    }

    private static async Task ExecuteUpgradeBuildingToMaxAsync(TaskExecutionContext context)
    {
        if (context.Options.BuildingUpgradeSlotId is null)
        {
            context.Log("Task 'upgrade_building_to_max' requires config value building_upgrade_slot_id.");
            return;
        }

        var result = await context.Client.UpgradeBuildingToMaxAsync(
            context.Options.BuildingUpgradeSlotId.Value,
            context.Options.BuildingUpgradeMaxAttempts,
            context.CancellationToken);
        context.Log(result);
        context.RecordTaskResult("upgrade_building_to_max", result);
        ThrowIfTaskBlocked("upgrade_building_to_max", result);
    }

    private static async Task ExecuteConstructBuildingAsync(TaskExecutionContext context)
    {
        if (context.Options.BuildingConstructSlotId is null || context.Options.BuildingConstructGid is null)
        {
            context.Log("Task 'construct_building' requires config values building_construct_slot_id and building_construct_gid.");
            return;
        }

        var buildingName = string.IsNullOrWhiteSpace(context.Options.BuildingConstructName)
            ? $"gid {context.Options.BuildingConstructGid.Value}"
            : context.Options.BuildingConstructName;

        var result = await context.Client.ConstructBuildingAsync(
            context.Options.BuildingConstructSlotId.Value,
            context.Options.BuildingConstructGid.Value,
            buildingName,
            context.CancellationToken,
            context.Options.BuildingConstructAllowSlotFallback,
            context.Options.BuildingConstructFallbackExcludedSlots);
        context.Log(result);
        context.RecordTaskResult("construct_building", result);
        ThrowIfTaskBlocked("construct_building", result);
    }

    private static async Task ExecuteUpgradeTroopsAtSmithyAsync(TaskExecutionContext context)
    {
        // No selection => no-op (the user hasn't picked any troops in 'Upgrade options'). Old queued tasks
        // carry no payload and therefore safely do nothing instead of blindly upgrading every troop.
        var targets = SmithyUpgradePayload.Parse(context.Options.SmithyUpgradeTargets);
        if (targets.Count == 0)
        {
            context.Log("Smithy: no troops selected for upgrade — configure them via 'Upgrade options'. Nothing to do.");
            return;
        }

        var result = await context.Client.UpgradeSelectedTroopsAtSmithyAsync(targets, context.CancellationToken);
        context.Log(result);
        await RefreshBuildingsSnapshotAfterTaskAsync(context);
        ThrowIfTroopsGroupBlocked(result);
        ThrowIfTaskBlocked("upgrade_troops_at_smithy", result);
    }

    private static async Task ExecuteBuildTroopsAsync(TaskExecutionContext context)
    {
        context.Log("[troops] build_troops starting");
        var result = await context.Client.BuildTroopsAsync(context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("build_troops", result);
    }

    private static async Task ExecuteRunBreweryCelebrationAsync(TaskExecutionContext context)
    {
        context.Log("[brewery] run_brewery_celebration starting");
        var result = await context.Client.RunBreweryCelebrationAsync(
            context.Options.BreweryCelebrationRestartDelayEnabled,
            context.Options.BreweryCelebrationRestartDelayMinMinutes,
            context.Options.BreweryCelebrationRestartDelayMaxMinutes,
            context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("run_brewery_celebration", result);
    }

    private static async Task ExecuteRunTownHallCelebrationAsync(TaskExecutionContext context)
    {
        var mode = TownHallCelebrationDefaults.NormalizeMode(context.Options.TownHallCelebrationMode);
        var count = TownHallCelebrationDefaults.NormalizeCount(context.Options.TownHallCelebrationCount);
        context.Log($"[town-hall] run_town_hall_celebration starting mode={mode} count={count}");
        var result = await context.Client.RunTownHallCelebrationAsync(
            mode,
            count,
            context.Options.TownHallCelebrationRestartDelayEnabled,
            context.Options.TownHallCelebrationRestartDelayMinMinutes,
            context.Options.TownHallCelebrationRestartDelayMaxMinutes,
            context.CancellationToken);
        context.Log(result);
        if (result.Contains("town_hall_unavailable=missing", StringComparison.OrdinalIgnoreCase))
        {
            throw new TaskBlockedPermanentlyException(
                $"Task 'run_town_hall_celebration' blocked permanently: town_hall_unavailable=missing | {result}");
        }
        ThrowIfTaskBlocked("run_town_hall_celebration", result);
    }

    private static async Task ExecuteLoadBuildingsSnapshotAsync(TaskExecutionContext context)
    {
        // The Desktop storage preflight consumes warehouse/granary capacity together with the building
        // list. Read the full village status here so queued Load buildings behaves like the direct button
        // path instead of persisting a dorf2-only snapshot with null capacities.
        var status = await context.Client.ReadVillageStatusAsync(context.CancellationToken);
        await WriteBuildingsSnapshotAsync(context, status);
        context.Log($"Loaded {status.Buildings.Count} building slots.");
    }

    private static async Task WriteBuildingsSnapshotAsync(TaskExecutionContext context, TbotUltra.Worker.Domain.VillageStatus status)
    {
        var activeAccount = context.Runner._accountProvider.LoadAccount().Name;
        var safeAccount = string.IsNullOrWhiteSpace(activeAccount) ? "main" : activeAccount.Trim().ToLowerInvariant();
        var outputDir = Path.Combine(context.Runner._projectContext.RootPath, "temp_build_out", "buildings-snapshots");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"{safeAccount}.json");

        var payload = new
        {
            account = activeAccount,
            activeVillage = status.ActiveVillage,
            tribe = status.Tribe,
            isCapital = status.IsCapital,
            warehouseCapacity = status.WarehouseCapacity,
            granaryCapacity = status.GranaryCapacity,
            buildings = status.Buildings.Select(building => new
            {
                slotId = building.SlotId,
                name = building.Name,
                level = building.Level,
                url = building.Url,
                gid = building.Gid,
            }).ToList(),
            resourceFields = status.ResourceFields.Select(field => new
            {
                slotId = field.SlotId,
                fieldType = field.FieldType,
                name = field.Name,
                level = field.Level,
                url = field.Url,
            }).ToList(),
        };

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(payload), context.CancellationToken);
    }

    private static async Task WriteFarmListsSnapshotAsync(TaskExecutionContext context, IReadOnlyList<FarmListOverview> overview)
    {
        try
        {
            var activeAccount = context.Runner._accountProvider.LoadAccount().Name;
            var outputPath = AccountStoragePaths.FarmListsSnapshotPath(context.Runner._projectContext.RootPath, activeAccount);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            var payload = new
            {
                account = activeAccount,
                capturedAtUtc = DateTimeOffset.UtcNow,
                lists = overview
                    .Where(item => item is not null)
                    .Select(item => new
                    {
                        name = item.Name,
                        activeFarmCount = item.ActiveFarmCount,
                        totalFarmCount = item.TotalFarmCount,
                        remainingSeconds = item.RemainingSeconds,
                        listId = item.ListId,
                        capacity = item.Capacity,
                        farmCoordinates = item.FarmCoordinates,
                    })
                    .ToList(),
            };

            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(payload), context.CancellationToken);
        }
        catch (Exception ex)
        {
            context.Log($"Could not write farm list snapshot: {ex.Message}");
        }
    }

    private static async Task RefreshBuildingsSnapshotAfterTaskAsync(TaskExecutionContext context)
    {
        try
        {
            var status = await context.Client.ReadBuildingsStatusAsync(context.CancellationToken);
            await WriteBuildingsSnapshotAsync(context, status);
            context.Log($"Buildings snapshot refreshed ({status.Buildings.Count} slots).");
        }
        catch (Exception ex)
        {
            context.Log($"Could not refresh buildings snapshot: {ex.Message}");
        }
    }

    private static async Task ExecuteDemolishBuildingToLevelAsync(TaskExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Options.TargetBuildingSlotOrName) || context.Options.TargetLevel is null)
        {
            context.Log("Task 'demolish_building_to_level' requires config values target_building_slot_or_name and target_level.");
            return;
        }

        var result = await context.Client.DemolishBuildingToLevelAsync(
            context.Options.TargetBuildingSlotOrName,
            context.Options.TargetLevel.Value,
            context.CancellationToken);
        context.Log(result);
    }

    private static async Task ExecuteSendFarmlistsAsync(TaskExecutionContext context)
    {
        var mode = FarmingDefaults.NormalizeSendMode(context.Options.ContinuousFarmSendMode);
        var minDelaySeconds = FarmingDefaults.NormalizeDispatchDelayMinMinutes(context.Options.ContinuousFarmDispatchDelayMinMinutes) * 60;
        var maxDelaySeconds = Math.Max(
            minDelaySeconds,
            FarmingDefaults.NormalizeDispatchDelayMaxMinutes(context.Options.ContinuousFarmDispatchDelayMaxMinutes) * 60);
        var dispatchDelaySeconds = FarmingDefaults.CalculateDispatchDelaySeconds(
            context.Options.ContinuousFarmDispatchDelayMinMinutes,
            context.Options.ContinuousFarmDispatchDelayMaxMinutes);
        context.Log(
            $"Continuous farming mode={mode}; delayRange={minDelaySeconds}-{maxDelaySeconds}s; " +
            $"selectedDelay={dispatchDelaySeconds}s; " +
            $"targetVillage='{(string.IsNullOrWhiteSpace(context.Options.TargetVillageName) ? "(default)" : context.Options.TargetVillageName)}'; " +
            $"deactivateLosses={context.Options.ContinuousFarmDeactivateLosses}; " +
            $"deactivateOasis={context.Options.ContinuousFarmDeactivateOasisLosses}.");

        if (string.Equals(mode, FarmingDefaults.SendModeAllAtOnce, StringComparison.Ordinal))
        {
            await ExecuteSendAllFarmlistsAsync(context, dispatchDelaySeconds);
            return;
        }

        await ExecuteSendFarmlistsListPerListAsync(context, dispatchDelaySeconds);
    }

    private static async Task ExecuteSendFarmlistsListPerListAsync(TaskExecutionContext context, int dispatchDelaySeconds)
    {
        var selectedNames = (context.Options.ContinuousFarmListNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedIds = (context.Options.ContinuousFarmListIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedNames.Count <= 0 && selectedIds.Count <= 0)
        {
            throw new InvalidOperationException("No farm lists selected for continuous farming.");
        }

        // Match by the stable list id (lid) first so a renamed village/list still resolves; fall
        // back to name for selections saved before lids existed or lists without a resolvable lid.
        bool IsSelected(FarmListOverview item) =>
            (item.ListId is not null && selectedIds.Contains(item.ListId))
            || selectedNames.Contains(item.Name, StringComparer.OrdinalIgnoreCase);

        var overview = await context.Client.ReadFarmListsOverviewAsync(context.CancellationToken);
        var matchingLists = overview
            .Where(item => item is not null && IsSelected(item))
            .ToList();
        if (matchingLists.Count <= 0)
        {
            // The selection is stored by farm-list name. If the user renamed a village/list on
            // Travian, the saved names no longer match the freshly read page. Don't raise a hard
            // alarm — defer quietly so the desktop UI can re-analyze and surface the current names
            // for re-selection. Embedding queue_wait_seconds routes this through the defer path.
            var retryWaitSeconds = dispatchDelaySeconds;
            context.Log(overview.Count > 0
                ? $"Continuous farming: none of the selected farm lists ({string.Join(", ", selectedNames)}) were found on the farm page. They may have been renamed — re-analyze and re-select. Retrying in {retryWaitSeconds}s."
                : $"Continuous farming: no farm lists were found on the farm page. Retrying in {retryWaitSeconds}s.");
            throw BuildContinuousFarmDefer("Selected farm lists were not found on the farm page.", retryWaitSeconds, 0);
        }

        var currentIndex = Math.Clamp(context.Options.ContinuousFarmNextListIndex, 0, matchingLists.Count - 1);
        var current = matchingLists[currentIndex];
        context.Log($"Continuous farming selected farmlist index={currentIndex + 1}/{matchingLists.Count} name='{current.Name}'.");
        if (current.RemainingSeconds is > 0)
        {
            // Exact page timers get a small render margin so the next read does not land on the
            // disabled-button boundary. An estimated timer already includes a conservative minute.
            var renderMarginSeconds = current.TimerIsEstimated ? 0 : Random.Shared.Next(5, 16);
            var waitSeconds = Math.Max(1, current.RemainingSeconds.Value + renderMarginSeconds);
            var estimateSuffix = current.TimerIsEstimated ? " (estimated)" : $" + {renderMarginSeconds}s render margin";
            context.Log($"Continuous farming: selected list '{current.Name}' is not ready. Remaining time={waitSeconds}s{estimateSuffix}.");
            throw BuildContinuousFarmDefer($"Selected farm list '{current.Name}' is not ready.", waitSeconds, currentIndex);
        }

        await RunFarmListLossDeactivationIfEnabledAsync(context);
        await context.Client.SendFarmListNowAsync(current.Name, context.CancellationToken);
        context.Log($"Continuous farming sent list '{current.Name}'. Delay between sends={dispatchDelaySeconds}s.");

        var refreshedOverview = await context.Client.ReadFarmListsOverviewAsync(context.CancellationToken);
        // Persist the freshly read page so the desktop can update its farm-list UI instantly after
        // the send, without paying for the extra navigations a full re-analyze would cost.
        await WriteFarmListsSnapshotAsync(context, refreshedOverview);

        var nextIndex = (currentIndex + 1) % matchingLists.Count;
        LogContinuousFarmNextSchedule(context, dispatchDelaySeconds, nextIndex);
        throw BuildContinuousFarmDefer("Continuous farming cooldown active.", dispatchDelaySeconds, nextIndex, TaskWaitReasons.WorkQueued);
    }

    private static async Task ExecuteSendAllFarmlistsAsync(TaskExecutionContext context, int dispatchDelaySeconds)
    {
        await RunFarmListLossDeactivationIfEnabledAsync(context);
        context.Log("Continuous farming send-all started.");
        var listCount = await context.Client.SendAllFarmListsNowAsync(context.CancellationToken);
        context.Log($"Continuous farming send-all completed. Lists considered={listCount}.");

        var refreshedOverview = await context.Client.ReadFarmListsOverviewAsync(context.CancellationToken);
        await WriteFarmListsSnapshotAsync(context, refreshedOverview);
        LogContinuousFarmNextSchedule(context, dispatchDelaySeconds, 0);
        throw BuildContinuousFarmDefer("Continuous farming cooldown active.", dispatchDelaySeconds, 0, TaskWaitReasons.WorkQueued);
    }

    private static async Task RunFarmListLossDeactivationIfEnabledAsync(TaskExecutionContext context)
    {
        if (!context.Options.ContinuousFarmDeactivateLosses)
        {
            context.Log("Continuous farming loss deactivation disabled.");
            return;
        }

        var result = await context.Client.DeactivateFarmListLossTargetsAsync(
            context.Options.ContinuousFarmDeactivateOasisLosses,
            context.CancellationToken);
        context.Log(
            "Continuous farming loss deactivation result: " +
            $"found={result.RowsFound}, deactivated={result.RowsDeactivated}, skippedOasis={result.SkippedOasisRows}.");
    }

    private static void LogContinuousFarmNextSchedule(TaskExecutionContext context, int waitSeconds, int nextIndex)
    {
        var nextTime = DateTimeOffset.Now.AddSeconds(Math.Max(1, waitSeconds));
        context.Log($"Continuous farming next scheduled send time={nextTime:yyyy-MM-dd HH:mm:ss zzz}; nextListIndex={nextIndex}; wait={waitSeconds}s.");
    }

    // Farm-send deferrals are normal control flow (cooldown after a send, list not ready, renamed
    // lists), so they throw TaskWaitException and log as DEFERRED — not FAILED — and never consume
    // retries. The message keeps the queue_wait_seconds / next-list-index tokens the desktop's
    // payload extractor reads.
    private static TaskWaitException BuildContinuousFarmDefer(string message, int waitSeconds, int nextIndex, string? reasonCode = null)
    {
        return new TaskWaitException(
            Math.Max(1, waitSeconds),
            $"{message} queue_wait_seconds={Math.Max(1, waitSeconds)} {BotOptionPayloadKeys.ContinuousFarmNextListIndex}={Math.Max(0, nextIndex)}",
            reasonCode);
    }

    private static async Task ExecuteSendResourcesBetweenVillagesAsync(TaskExecutionContext context)
    {
        context.Log("send_resources_between_villages: starting.");
        var result = await context.Client.SendResourcesBetweenOwnVillagesAsync(context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("send_resources_between_villages", result);
    }

    private static async Task ExecuteSendReinforcementsBetweenVillagesAsync(TaskExecutionContext context)
    {
        context.Log("send_reinforcements_between_villages: starting.");
        var result = await context.Client.SendReinforcementsBetweenOwnVillagesAsync(context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("send_reinforcements_between_villages", result);
    }

    private static async Task ExecuteHeroManageAsync(TaskExecutionContext context)
    {
        var result = await context.Client.ManageHeroAsync(
            context.Options.HeroMinHpForAdventure,
            context.Options.HeroAutoRevive,
            context.Options.HeroAutoAssignPoints,
            context.Options.HeroAutoUseOintments,
            context.Options.HeroStatPriority,
            context.Options.HeroAdventurePickOrder,
            context.Options.HeroHpRegenPerDayPercent,
            context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("hero_manage", result);
    }

    private static async Task ExecuteSpendHeroAttributePointsAsync(TaskExecutionContext context)
    {
        var result = await context.Client.SpendHeroAttributePointsAsync(
            context.Options.HeroStatPriority,
            context.CancellationToken);
        context.Log(result);
    }

    private static async Task ExecuteCollectTasksAsync(TaskExecutionContext context)
    {
        var result = await context.Client.CollectTaskRewardsAsync(context.CancellationToken);
        context.Log(result);
    }

    private static async Task ExecuteCollectDailyQuestsAsync(TaskExecutionContext context)
    {
        var result = await context.Client.CollectDailyQuestRewardsAsync(context.CancellationToken);
        context.Log(result);
        // Record so the desktop can read the piggybacked daily_reset_hour=... token off LastTask.Message.
        context.RecordTaskResult("collect_daily_quests", result);
    }

    private static async Task ExecuteReadDailyResetAsync(TaskExecutionContext context)
    {
        var result = await context.Client.ReadDailyResetHourAsync(context.CancellationToken);
        context.Log(result);
        // Record so the desktop can read the daily_reset_hour=... token off LastTask.Message.
        context.RecordTaskResult("read_daily_reset", result);
    }

    private static async Task ExecuteActivateProductionBonusAsync(TaskExecutionContext context)
    {
        var result = await context.Client.ActivateProductionBonusVideosAsync(context.CancellationToken);
        context.Log(result);
        // Record so the desktop can read the production_bonus=... state token off LastTask.Message.
        context.RecordTaskResult("activate_production_bonus", result);
        ThrowIfTaskBlocked("activate_production_bonus", result);
    }

    private static void ThrowIfTaskBlocked(string taskName, string result)
    {
        if (!IsBlockedTaskResult(result))
        {
            return;
        }

        // Permanent blocks: the request can never succeed in its current form. Mark Failed
        // immediately rather than letting the worker burn the retry budget on it.
        if (IsPermanentlyBlockedTaskResult(result))
        {
            throw new TaskBlockedPermanentlyException($"Task '{taskName}' blocked permanently: {result}");
        }

        // Transient blocks with an explicit wait hint → defer without consuming retries.
        if (TryExtractQueueWaitSeconds(result, out var waitSeconds))
        {
            throw new TaskWaitException(waitSeconds, $"Task '{taskName}' waiting: {result}", DeriveTaskWaitReason(result));
        }

        // Blocked but no wait hint — fall back to old behavior (counts toward MaxRetries).
        throw new InvalidOperationException($"Task '{taskName}' could not execute successfully: {result}");
    }

    // Single place that maps the clients' free-text result messages onto typed wait reasons
    // (TaskWaitReasons). Downstream consumers (Desktop queue handling) read ReasonCode instead of
    // sniffing message text, so a reworded message only needs updating here.
    internal static string? DeriveTaskWaitReason(string result)
    {
        if (result.Contains("hero_reviving", StringComparison.OrdinalIgnoreCase))
        {
            return TaskWaitReasons.HeroReviving;
        }

        if (result.Contains("Hero is away", StringComparison.OrdinalIgnoreCase))
        {
            return TaskWaitReasons.HeroAway;
        }

        // Both forms: the action token (in "Actions: ..." summaries) and the dedicated hp-too-low
        // defer message ("Hero HP too low to send."), which does not carry the action token.
        if (result.Contains("adventure_skipped_hp_too_low", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Hero HP too low", StringComparison.OrdinalIgnoreCase))
        {
            return TaskWaitReasons.HeroHpTooLow;
        }

        if (result.Contains("queued", StringComparison.OrdinalIgnoreCase))
        {
            return TaskWaitReasons.WorkQueued;
        }

        return null;
    }

    internal static ConstructionTaskOutcome ClassifyConstructionTaskResult(string taskName, string? result)
    {
        if (!IsConstructionTaskResult(taskName) || string.IsNullOrWhiteSpace(result))
        {
            return ConstructionTaskOutcome.None;
        }

        if (IsBlockedTaskResult(result))
        {
            return ConstructionTaskOutcome.WaitingOrBlocked;
        }

        var value = result.ToLowerInvariant();
        // Construct hit a slot that already holds the building (confirmed live) — the task can never run, so
        // the desktop removes it from the queue (see HandleQueueItemSucceededAsync).
        if (value.Contains("already exists at slot", StringComparison.Ordinal))
        {
            return ConstructionTaskOutcome.AlreadyExists;
        }

        if ((string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
            && value.Contains("is empty", StringComparison.Ordinal)
            && value.Contains("construct the building before upgrading", StringComparison.Ordinal))
        {
            return ConstructionTaskOutcome.MissingBuilding;
        }

        if (value.Contains("queued", StringComparison.Ordinal)
            || value.Contains("still in progress", StringComparison.Ordinal)
            || value.Contains("active construction detected", StringComparison.Ordinal)
            || value.Contains("build queue contains", StringComparison.Ordinal))
        {
            return ConstructionTaskOutcome.QueuedOrInProgress;
        }

        if (value.Contains("reached level", StringComparison.Ordinal)
            || value.Contains("reached max level", StringComparison.Ordinal)
            || value.Contains("constructed ", StringComparison.Ordinal)
            || value.Contains("confirmed level", StringComparison.Ordinal))
        {
            return ConstructionTaskOutcome.ConfirmedComplete;
        }

        if (value.Contains("already at level", StringComparison.Ordinal)
            || value.Contains("already at max", StringComparison.Ordinal)
            || (value.Contains("target ", StringComparison.Ordinal)
                && value.Contains(" reached", StringComparison.Ordinal)))
        {
            return ConstructionTaskOutcome.AlreadySatisfied;
        }

        return ConstructionTaskOutcome.UnknownSuccess;
    }

    private static bool IsConstructionTaskResult(string taskName) =>
        string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase);

    private static void ThrowIfTroopsGroupBlocked(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        if (result.Contains("Smithy not found in this village", StringComparison.OrdinalIgnoreCase))
        {
            throw new TaskBlockedPermanentlyException($"Task 'upgrade_troops_at_smithy' blocked permanently: troops_blocked=smithy_missing | {result}");
        }

        if (result.Contains("Smithy:", StringComparison.OrdinalIgnoreCase)
            && result.Contains("All done", StringComparison.OrdinalIgnoreCase))
        {
            throw new TaskBlockedPermanentlyException($"Task 'upgrade_troops_at_smithy' blocked permanently: troops_blocked=all_done | {result}");
        }
    }

    internal static bool IsBlockedTaskResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return false;
        }

        var value = result.ToLowerInvariant();
        return
            value.Contains(" blocked ")
            || value.Contains("blocked (")
            || value.Contains("queue_wait_seconds=")
            || value.Contains("cannot be built yet")
            || value.Contains("cannot be upgraded yet")
            || value.Contains("is not listed by the server")
            || value.Contains("cannot be built in slot")
            || value.Contains("reports max level reached");
    }

    private static bool IsPermanentlyBlockedTaskResult(string result)
    {
        var value = result.ToLowerInvariant();
        return
            value.Contains("reports max level reached")
            || value.Contains("is not listed by the server")
            || value.Contains("cannot be built in slot");
    }

    private static bool TryExtractQueueWaitSeconds(string result, out int seconds)
    {
        seconds = 0;
        const string token = "queue_wait_seconds=";
        var index = result.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var start = index + token.Length;
        var end = start;
        while (end < result.Length && (char.IsDigit(result[end]) || result[end] == '-'))
        {
            end++;
        }

        if (end == start)
        {
            return false;
        }

        if (!int.TryParse(result.AsSpan(start, end - start), out var parsed))
        {
            return false;
        }

        seconds = parsed;
        return true;
    }

}
