using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal sealed record AutomationRuntimeVillage(
    string Key,
    string Name,
    string? Url,
    bool IsCapital,
    int? CoordX,
    int? CoordY);

internal sealed record AutomationFarmListSelection(
    IReadOnlyList<string> Names,
    IReadOnlyList<string> Ids,
    int AvailableCount);

internal interface IContinuousRuntimeItemPreparationPort
{
    IReadOnlyList<QueueGroup> GetEnabledGroups();
    IReadOnlyList<QueueGroup> GetConsideredGroups();
    IReadOnlySet<QueueGroup> GetEnabledGroupsForVillage(string villageKey);
    bool ShouldKeepHeroAdventurePolling();
    IReadOnlyList<QueueItem> GetQueueItems();
    string? CanonicalizeVillageKey(string? key);
    IReadOnlyList<AutomationRuntimeVillage> GetAutomationVillages(AutomationRuntimeVillage? onlyVillage);
    IAutomationRuntimeQueuePort RuntimeQueue { get; }
    void PrepareHeroCropAntiStarve(BotOptions options);
    ValueTask<int?> RefreshAdventureCountAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask ApplyHeroAdventureAvailabilityAsync(int? adventureCount);
    Dictionary<string, string> BuildHeroPayload();
    bool IsTroopsGroupBlocked();
    IReadOnlyDictionary<string, string> LoadSmithyPayload(string villageKey);
    Dictionary<string, string> BuildVillagePayload(AutomationRuntimeVillage village);
    Dictionary<string, string>? LoadTroopTrainingPayload(string villageKey);
    bool HasEnabledTroopTrainingBuilding(BotOptions options);
    bool ShouldGateTroopTrainingOnActiveQueue(BotOptions options);
    int? ResolveActiveTroopTrainingQueueWaitSeconds(AutomationRuntimeVillage village, BotOptions options);
    bool IsBreweryCelebrationAvailable { get; }
    string? LoadTownHallMode(string villageKey);
    TownHallCelebrationState? LoadActiveTownHallState(string villageKey, DateTimeOffset now);
    bool DeferPendingItem(Guid itemId, Dictionary<string, string>? payload, TimeSpan delay);
    bool FarmingBlockedForOtherReason { get; }
    ValueTask<bool> ResolveGoldClubStatusAsync(BotOptions options, CancellationToken cancellationToken);
    void UpdateGoldClubInfo(bool enabled);
    ValueTask EnsureFarmListsReadyAsync(BotOptions options, CancellationToken cancellationToken);
    AutomationFarmListSelection GetFarmListSelection();
    void SetFarmingBlockedForMissingLists();
    void ClearFarmingMissingListsBlock();
    void SetFarmingBlockedForMissingGoldClub();
    bool CanRunResourceTransfer(BotOptions options);
    bool CanRunReinforcements(BotOptions options);
    Dictionary<string, string> BuildReinforcementPayload(
        BotOptions options,
        IReadOnlyList<string> selectedSources);
    bool ScheduleReinforcementSend(
        Dictionary<string, string> payload,
        TimeSpan delay,
        BotOptions options);
    string FormatServerTime(DateTimeOffset value);
    string FormatDuration(int seconds);
    void Log(string message);
    void LogVerbose(string message, string key);
}

internal sealed class ContinuousRuntimeItemPreparation(IContinuousRuntimeItemPreparationPort port)
{
    internal async ValueTask PrepareAsync(
        BotOptions options,
        CancellationToken cancellationToken,
        AutomationRuntimeVillage? onlyVillage = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enabledGroups = port.GetEnabledGroups();
        var consideredGroups = port.GetConsideredGroups();
        var heroPollingEnabled = consideredGroups.Contains(QueueGroup.Hero)
            || port.ShouldKeepHeroAdventurePolling();
        if (consideredGroups.Count <= 0
            && !heroPollingEnabled
            && !options.HeroCropAntiStarveEnabled
            && onlyVillage is null)
        {
            return;
        }

        var queueItems = port.GetQueueItems();
        var runtimeItems = new AutomationRuntimeItemReconciler(
            queueItems,
            port.CanonicalizeVillageKey,
            port.RuntimeQueue);
        var villages = port.GetAutomationVillages(onlyVillage);

        port.PrepareHeroCropAntiStarve(options);
        if (onlyVillage is null && heroPollingEnabled && !runtimeItems.HasActive("hero_manage"))
        {
            var adventureCount = await port.RefreshAdventureCountAsync(options, cancellationToken);
            await port.ApplyHeroAdventureAvailabilityAsync(adventureCount);
            if (adventureCount is > 0)
            {
                var payload = port.BuildHeroPayload();
                runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                    "hero_manage", "Hero adventure", payload, -50, 0));
                port.Log(
                    $"Hero group: queued hero_manage because adventures available={adventureCount.Value}. "
                    + $"priority={payload[BotOptionPayloadKeys.HeroStatPriority]}");
            }
        }

        PrepareSmithyItems(runtimeItems, villages);
        PrepareTroopTrainingItems(runtimeItems, villages, options);
        PrepareBreweryItem(runtimeItems, villages);
        PrepareTownHallItems(runtimeItems, villages, options);
        await PrepareFarmingItemsAsync(
            runtimeItems,
            villages,
            consideredGroups,
            options,
            onlyVillage?.Key,
            cancellationToken);
        PrepareResourceTransferItem(runtimeItems, enabledGroups, options, onlyVillage?.Key);
        PrepareReinforcementItem(runtimeItems, enabledGroups, queueItems, options, onlyVillage?.Key);
    }

    private void PrepareSmithyItems(
        AutomationRuntimeItemReconciler runtimeItems,
        IReadOnlyList<AutomationRuntimeVillage> villages)
    {
        if (port.IsTroopsGroupBlocked())
        {
            return;
        }

        foreach (var village in villages)
        {
            if (!port.GetEnabledGroupsForVillage(village.Key).Contains(QueueGroup.Troops)
                || runtimeItems.HasActiveForVillage("upgrade_troops_at_smithy", village.Key))
            {
                continue;
            }

            var smithyPayload = port.LoadSmithyPayload(village.Key);
            if (smithyPayload.Count == 0)
            {
                continue;
            }

            var payload = port.BuildVillagePayload(village);
            foreach (var pair in smithyPayload)
            {
                payload[pair.Key] = pair.Value;
            }
            runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                "upgrade_troops_at_smithy", "Troop upgrades", payload, -50, 0, village.Key));
        }
    }

    private void PrepareTroopTrainingItems(
        AutomationRuntimeItemReconciler runtimeItems,
        IReadOnlyList<AutomationRuntimeVillage> villages,
        BotOptions options)
    {
        foreach (var village in villages)
        {
            if (!port.GetEnabledGroupsForVillage(village.Key).Contains(QueueGroup.TroopTraining))
            {
                continue;
            }

            var trainingPayload = port.BuildVillagePayload(village);
            var villageTraining = port.LoadTroopTrainingPayload(village.Key);
            if (villageTraining is not null)
            {
                foreach (var pair in villageTraining)
                {
                    trainingPayload[pair.Key] = pair.Value;
                }
            }

            if (runtimeItems.HasActiveForVillage("build_troops", village.Key))
            {
                var refresh = runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                    "build_troops", "Build troops", trainingPayload, -50, 0, village.Key,
                    RefreshPendingPayload: true));
                if (refresh.Change is AutomationRuntimeItemChange.PayloadUpdated
                    or AutomationRuntimeItemChange.PayloadAndPriorityUpdated)
                {
                    port.Log(
                        $"[troops] refreshed deferred build_troops payload for '{village.Name}' "
                        + "with updated troop settings.");
                }
                continue;
            }

            var trainingOptions = villageTraining is null
                ? options
                : BotOptionsPayloadApplier.Apply(options, villageTraining);
            if (!port.HasEnabledTroopTrainingBuilding(trainingOptions))
            {
                port.LogVerbose(
                    $"[troops:verbose] skipped build_troops enqueue for '{village.Name}' — "
                    + "no troop-training building is enabled.",
                    $"troops:no-enabled:{village.Key}");
                continue;
            }

            var activeQueueWaitSeconds = port.ShouldGateTroopTrainingOnActiveQueue(trainingOptions)
                ? port.ResolveActiveTroopTrainingQueueWaitSeconds(village, trainingOptions)
                : null;
            if (activeQueueWaitSeconds is > 0)
            {
                port.LogVerbose(
                    $"[troops:verbose] skipped build_troops enqueue for '{village.Name}' — "
                    + $"enabled training queue still active for {port.FormatDuration(activeQueueWaitSeconds.Value)}.",
                    $"troops:active-queue:{village.Key}");
                continue;
            }

            runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                "build_troops", "Build troops", trainingPayload, -50, 0, village.Key));
        }
    }

    private void PrepareBreweryItem(
        AutomationRuntimeItemReconciler runtimeItems,
        IReadOnlyList<AutomationRuntimeVillage> villages)
    {
        if (!port.IsBreweryCelebrationAvailable)
        {
            return;
        }

        var capital = villages.FirstOrDefault(village => village.IsCapital);
        if (capital is not null
            && port.GetEnabledGroupsForVillage(capital.Key).Contains(QueueGroup.BreweryCelebration)
            && !runtimeItems.HasActiveForVillage("run_brewery_celebration", capital.Key))
        {
            runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                "run_brewery_celebration",
                "Auto celebration",
                port.BuildVillagePayload(capital),
                -50,
                0,
                capital.Key));
        }
    }

    private void PrepareTownHallItems(
        AutomationRuntimeItemReconciler runtimeItems,
        IReadOnlyList<AutomationRuntimeVillage> villages,
        BotOptions options)
    {
        foreach (var village in villages)
        {
            if (!port.GetEnabledGroupsForVillage(village.Key).Contains(QueueGroup.TownHallCelebration)
                || runtimeItems.HasActiveForVillage("run_town_hall_celebration", village.Key))
            {
                continue;
            }

            var mode = TownHallCelebrationDefaults.NormalizeMode(
                port.LoadTownHallMode(village.Key) ?? options.TownHallCelebrationMode);
            var now = DateTimeOffset.UtcNow;
            var remembered = port.LoadActiveTownHallState(village.Key, now);
            if (remembered is not null)
            {
                var restoredPayload = port.BuildVillagePayload(village);
                restoredPayload[BotOptionPayloadKeys.TownHallCelebrationMode] = remembered.Mode;
                var restoredItem = runtimeItems.EnqueueNew(new AutomationRuntimeItemSpec(
                    "run_town_hall_celebration",
                    "Town Hall celebration",
                    restoredPayload,
                    -50,
                    0,
                    village.Key));
                var restoreDelay = remembered.EndsAtUtc > now
                    ? remembered.EndsAtUtc - now
                    : TimeSpan.Zero;
                if (port.DeferPendingItem(restoredItem.Id, null, restoreDelay))
                {
                    port.Log(
                        $"[town-hall] restored running celebration for '{village.Name}' "
                        + $"until {port.FormatServerTime(remembered.EndsAtUtc)}.");
                }
                else
                {
                    port.Log(
                        $"[town-hall] restored celebration for '{village.Name}' but could not defer it "
                        + $"to {port.FormatServerTime(remembered.EndsAtUtc)}; it will re-check the Town Hall "
                        + "on the next pass.");
                }
                continue;
            }

            var payload = port.BuildVillagePayload(village);
            payload[BotOptionPayloadKeys.TownHallCelebrationMode] = mode;
            runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                "run_town_hall_celebration", "Town Hall celebration", payload, -50, 0, village.Key));
        }
    }

    private async ValueTask PrepareFarmingItemsAsync(
        AutomationRuntimeItemReconciler runtimeItems,
        IReadOnlyList<AutomationRuntimeVillage> villages,
        IReadOnlyList<QueueGroup> consideredGroups,
        BotOptions options,
        string? onlyVillageKey,
        CancellationToken cancellationToken)
    {
        if (onlyVillageKey is not null
            || !consideredGroups.Contains(QueueGroup.Farming)
            || port.FarmingBlockedForOtherReason)
        {
            return;
        }

        var goldClubEnabled = await port.ResolveGoldClubStatusAsync(options, cancellationToken);
        port.UpdateGoldClubInfo(goldClubEnabled);
        if (!goldClubEnabled)
        {
            port.SetFarmingBlockedForMissingGoldClub();
            return;
        }

        var sendMode = FarmingDefaults.NormalizeSendMode(options.ContinuousFarmSendMode);
        var sendsAllListsAtOnce = string.Equals(
            sendMode,
            FarmingDefaults.SendModeAllAtOnce,
            StringComparison.Ordinal);
        var enabledFarmingVillages = villages
            .Where(village => port.GetEnabledGroupsForVillage(village.Key).Contains(QueueGroup.Farming))
            .ToList();
        var dispatchMissing = sendsAllListsAtOnce
            ? enabledFarmingVillages.Count > 0 && !runtimeItems.HasActive("send_farmlists")
            : enabledFarmingVillages.Any(village =>
                !runtimeItems.HasActiveForVillage("send_farmlists", village.Key));
        if (!dispatchMissing)
        {
            port.LogVerbose(
                "[farm-list] skipping runtime analysis because every enabled farming scope already has an active dispatch.",
                "farm-list-runtime-analysis-active-dispatch");
            return;
        }

        var selection = port.GetFarmListSelection();
        var selectionKnown = selection.AvailableCount > 0
            || selection.Names.Count > 0
            || selection.Ids.Count > 0;
        if (!selectionKnown)
        {
            await port.EnsureFarmListsReadyAsync(options, cancellationToken);
            selection = port.GetFarmListSelection();
        }
        else
        {
            port.LogVerbose(
                "[farm-list] reusing the known selection while creating a missing runtime dispatch; the send task will refresh live state.",
                "farm-list-runtime-known-selection");
        }

        if (selection.AvailableCount <= 0)
        {
            port.SetFarmingBlockedForMissingLists();
        }
        else
        {
            port.ClearFarmingMissingListsBlock();
        }

        if (selection.Names.Count <= 0 && selection.Ids.Count <= 0)
        {
            port.LogVerbose("[farm-list] no lists are enabled; no automatic send was queued.", "farm-list-runtime-no-enabled-lists");
            return;
        }

        var farmingPayload = new FarmingPayload(
            selection.Names,
            selection.Ids,
            MoveRedLosses: options.ContinuousFarmMoveRedLosses,
            RedLossDestinationListId: options.ContinuousFarmRedLossDestinationListId,
            RedLossDestinationListName: options.ContinuousFarmRedLossDestinationListName,
            RedLossDestinationBaseName: options.ContinuousFarmRedLossDestinationBaseName,
            MoveYellowLosses: options.ContinuousFarmMoveYellowLosses,
            YellowLossDestinationListId: options.ContinuousFarmYellowLossDestinationListId,
            YellowLossDestinationListName: options.ContinuousFarmYellowLossDestinationListName,
            YellowLossDestinationBaseName: options.ContinuousFarmYellowLossDestinationBaseName).ToDictionary();
        foreach (var village in villages)
        {
            var hasActiveDispatch = sendsAllListsAtOnce
                ? runtimeItems.HasActive("send_farmlists")
                : runtimeItems.HasActiveForVillage("send_farmlists", village.Key);
            if (!port.GetEnabledGroupsForVillage(village.Key).Contains(QueueGroup.Farming)
                || hasActiveDispatch)
            {
                continue;
            }

            var payload = new Dictionary<string, string>(farmingPayload, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in port.BuildVillagePayload(village))
            {
                payload[pair.Key] = pair.Value;
            }
            runtimeItems.Ensure(new AutomationRuntimeItemSpec(
                "send_farmlists",
                sendsAllListsAtOnce ? "Send all enabled farmlists" : "Send selected farmlists",
                payload,
                -50,
                0,
                village.Key));
            port.Log(sendsAllListsAtOnce
                ? $"Continuous farming Send all enabled queued once for the account via village '{village.Name}'."
                : $"Continuous farming queued for village '{village.Name}'.");
        }
    }

    private void PrepareResourceTransferItem(
        AutomationRuntimeItemReconciler runtimeItems,
        IReadOnlyList<QueueGroup> enabledGroups,
        BotOptions options,
        string? onlyVillageKey)
    {
        if (onlyVillageKey is not null
            || !enabledGroups.Contains(QueueGroup.ResourceTransfer)
            || runtimeItems.HasActive("send_resources_between_villages")
            || !port.CanRunResourceTransfer(options))
        {
            return;
        }

        var selectedSources = options.ResourceTransferSourceVillageNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            "send_resources_between_villages", "Resource transfer", payload, -50, 0));
    }

    private void PrepareReinforcementItem(
        AutomationRuntimeItemReconciler runtimeItems,
        IReadOnlyList<QueueGroup> enabledGroups,
        IReadOnlyList<QueueItem> queueItems,
        BotOptions options,
        string? onlyVillageKey)
    {
        if (onlyVillageKey is not null
            || !enabledGroups.Contains(QueueGroup.Reinforcements)
            || runtimeItems.HasActive("send_reinforcements_between_villages")
            || !port.CanRunReinforcements(options))
        {
            return;
        }

        var selectedSources = options.ReinforcementsSourceVillageNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !string.Equals(
                name,
                options.ReinforcementsTargetVillageName,
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var payload = port.BuildReinforcementPayload(options, selectedSources);
        var delay = ContinuousLoopSelector.ResolveReinforcementSendDelay(
            options,
            queueItems,
            DateTimeOffset.UtcNow);
        port.ScheduleReinforcementSend(payload, delay, options);
    }
}
