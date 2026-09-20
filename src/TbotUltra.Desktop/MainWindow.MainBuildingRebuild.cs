using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void TryQueueMainBuildingRebuild(
        VillageStatus liveStatus,
        VillageStatus? previousStatus,
        string villageName,
        string? villageKey,
        bool readOnlyObservation)
    {
        var options = LoadBotOptions();
        if (readOnlyObservation || !options.ConstructionMainBuildingRebuildEnabled)
        {
            return;
        }

        var queueItems = _botService.GetQueueItemsForDisplay();
        var sameVillageItems = queueItems
            .Where(item => IsQueueItemForVillage(item, villageName, villageKey))
            .ToList();
        var plan = MainBuildingRebuildPlanner.Plan(
            liveStatus,
            previousStatus,
            sameVillageItems,
            options.ConstructionMainBuildingRebuildTargetLevel);
        if (plan is null)
        {
            return;
        }

        var payload = new BuildingConstructPayload(
            plan.SlotId,
            15,
            "Main Building",
            plan.TargetLevel).ToDictionary();
        payload[BotOptionPayloadKeys.TargetVillageName] = villageName;
        if (!string.IsNullOrWhiteSpace(villageKey))
        {
            payload[BotOptionPayloadKeys.TargetVillageKey] = villageKey;
        }

        var activeVillage = liveStatus.Villages.FirstOrDefault(village =>
            liveStatus.ActiveVillageCoordX.HasValue
            && liveStatus.ActiveVillageCoordY.HasValue
            && village.CoordX == liveStatus.ActiveVillageCoordX
            && village.CoordY == liveStatus.ActiveVillageCoordY)
            ?? liveStatus.Villages.FirstOrDefault(village => string.Equals(
                NormalizeVillageName(village.Name),
                villageName,
                StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(activeVillage?.Url))
        {
            payload[BotOptionPayloadKeys.TargetVillageUrl] = activeVillage.Url;
        }

        ApplyConstructFasterSettingsToPayload(payload, options, villageKey, villageName);
        payload[BotOptionPayloadKeys.NpcTradeEnabled] = IsNpcTradeEnabledForVillageKey(villageKey) ? "true" : "false";
        payload[BotOptionPayloadKeys.BuildingConstructAllowSlotFallback] = bool.TrueString;
        payload[BotOptionPayloadKeys.AutoAddedBy] = BotOptionPayloadKeys.AutoAddedByMainBuildingRebuild;
        payload[BotOptionPayloadKeys.AutoAddedReason] = "Complete Dorf2 scan confirmed that the Main Building is missing.";

        var maxPriority = queueItems.Select(item => item.Priority).DefaultIfEmpty(0).Max();
        var priority = maxPriority == int.MaxValue ? int.MaxValue : maxPriority + 1;
        var created = _botService.Enqueue("construct_building", payload, priority, maxRetries: 3);
        AppendLog(
            $"[main-building] complete Dorf2 scan confirmed missing Main Building in '{villageName}'; "
            + $"queued slot={plan.SlotId} target={plan.TargetLevel} id={created.Id}. No dedicated pre-build scan was used.");
        RequestContinuousAutomationWake();
        RequestQueueUiRefresh(created.Id);
    }

    private async Task VerifyMainBuildingAfterDurationAnomalyAsync(QueueItem item)
    {
        var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
        AppendLog(
            $"[main-building] high construction duration detected for village "
            + $"'{GetQueueItemVillageName(item) ?? "-"}'; verifying with one Dorf2 scan.");
        var status = await _botService.ReadBuildingsStatusAsync(
            options,
            AppendLog,
            _loopController.AcquireSessionScopeToken());
        await Dispatcher.InvokeAsync(() =>
        {
            SetActiveWorkingVillageFromStatus(status);
            CacheVillageStatus(status);
            ReconcilePendingBuildingQueueWithLiveStatus(status);
        });
    }
}
