using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void ReleaseDisabledCropShortageDefers()
    {
        var released = 0;
        foreach (var item in _botService.GetQueueItemsForDisplay().Where(item =>
                     item.Status == QueueStatus.Pending
                     && item.NextAttemptAt > DateTimeOffset.UtcNow
                     && ConstructionQueueState.ResolveDeferReason(item) == ConstructionDeferReason.CropShortage))
        {
            if (_botService.UpdateDeferredQueueItem(item.Id, item.Payload, TimeSpan.Zero))
            {
                released++;
            }
        }

        if (released <= 0)
        {
            return;
        }

        RequestContinuousAutomationWake();
        AppendLog($"[crop-shortage] recovery enabled; released {released} deferred Construction item(s).");
        RequestQueueUiRefresh();
    }

    private async Task HandleCropShortageDeferAsync(QueueItem parent)
    {
        var options = LoadBotOptions();
        if (!options.ConstructionCropShortageRecoveryEnabled)
        {
            _botService.UpdateDeferredQueueItem(parent.Id, parent.Payload, TimeSpan.FromMinutes(30));
            AppendLog(
                $"ALARM: Construction in village '{GetQueueItemVillageName(parent) ?? "-"}' is blocked by lack of food. " +
                "Automatic cropland recovery is disabled in Settings > Construction; retrying in 30 minutes.");
            return;
        }

        var status = ResolveBuildingStatusForQueueItem(parent);
        if (status is null || status.ResourceFields.Count == 0)
        {
            _botService.UpdateDeferredQueueItem(parent.Id, parent.Payload, TimeSpan.FromMinutes(30));
            AppendLog(
                $"ALARM: Cropland recovery could not read a fresh dorf1 snapshot for village " +
                $"'{GetQueueItemVillageName(parent) ?? "-"}'. Construction retries in 30 minutes.");
            return;
        }

        await EnsureCropShortageRecoveryAsync(parent, status);
    }

    private Task EnsureCropShortageRecoveryAsync(QueueItem parent, VillageStatus status)
    {
        var allItems = _botService.GetQueueItemsForDisplay();
        var sameVillage = BuildSameVillageQueueFilter(parent);
        var villageItems = allItems.Where(sameVillage).ToList();
        var plan = CropShortageRecoveryPlanner.Plan(
            status,
            villageItems,
            ResolveResourceMaxLevelFromStatus(status));

        if (plan.CropProductionPerHour is null)
        {
            _botService.UpdateDeferredQueueItem(parent.Id, parent.Payload, TimeSpan.FromMinutes(30));
            AppendLog(
                $"ALARM: Cropland recovery could not read crop production for village " +
                $"'{GetQueueItemVillageName(parent) ?? "-"}'. No recovery tasks were added; Construction retries in 30 minutes.");
            return Task.CompletedTask;
        }

        if (plan.AllCroplandsAtMax)
        {
            _botService.PauseQueueItem(parent.Id);
            AppendLog(
                $"ALARM: Construction in village '{GetQueueItemVillageName(parent) ?? "-"}' is blocked by lack of food, " +
                "but every cropland is already at the server-supported maximum. Construction for this village was paused.");
            RequestQueueUiRefresh(parent.Id);
            return Task.CompletedTask;
        }

        if (plan.Steps.Count == 0 && plan.ActiveCroplandCount == 0)
        {
            _botService.UpdateDeferredQueueItem(parent.Id, parent.Payload, TimeSpan.FromMinutes(30));
            AppendLog(
                $"ALARM: Cropland recovery found no eligible cropland step in village " +
                $"'{GetQueueItemVillageName(parent) ?? "-"}'. Construction retries in 30 minutes.");
            return Task.CompletedTask;
        }

        var maxPriority = allItems.Select(item => item.Priority).DefaultIfEmpty(parent.Priority).Max();
        var recoveryPriority = maxPriority == int.MaxValue ? int.MaxValue : maxPriority + 1;
        Guid? firstRecoveryId = null;
        foreach (var step in plan.Steps)
        {
            var existing = step.ExistingQueueItemId is Guid existingId
                ? allItems.First(item => item.Id == existingId)
                : null;
            var payload = existing is null
                ? new ResourceUpgradePayload(step.SlotId, step.TargetLevel, step.Name).ToDictionary()
                : new Dictionary<string, string>(existing.Payload, StringComparer.OrdinalIgnoreCase);
            payload[BotOptionPayloadKeys.CropShortageRecoveryParentId] = parent.Id.ToString();
            if (existing is null)
            {
                payload[BotOptionPayloadKeys.AutoAddedBy] = BotOptionPayloadKeys.AutoAddedByCropShortageRecovery;
                payload[BotOptionPayloadKeys.AutoAddedReason] = "Lack of food: extend cropland first!";
            }
            CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.TargetVillageName);
            CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.TargetVillageUrl);
            CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.TargetVillageKey);
            CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.NpcTradeEnabled);
            CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.ConstructFasterEnabled);

            if (existing is not null)
            {
                if (!payload.ContainsKey(BotOptionPayloadKeys.CropShortageOriginalPriority))
                {
                    payload[BotOptionPayloadKeys.CropShortageOriginalPriority] = existing.Priority.ToString();
                }
                if (_botService.UpdatePendingQueueItem(existing.Id, payload, recoveryPriority, TimeSpan.Zero))
                {
                    firstRecoveryId ??= existing.Id;
                    AppendLog($"[crop-shortage] promoted cropland slot={step.SlotId} target={step.TargetLevel} id={existing.Id}.");
                }
                continue;
            }

            var created = _botService.Enqueue("upgrade_resource_to_level", payload, recoveryPriority, maxRetries: 3);
            firstRecoveryId ??= created.Id;
            AppendLog($"[crop-shortage] queued cropland slot={step.SlotId} target={step.TargetLevel} id={created.Id}.");
        }

        var parentPayload = new Dictionary<string, string>(parent.Payload, StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonCropShortage,
            [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] = ConstructionQueueState.CurrentDeferClassificationVersion,
        };
        _botService.UpdateDeferredQueueItem(parent.Id, parentPayload, TimeSpan.FromMinutes(30));
        parent.Payload = parentPayload;
        RequestQueueUiRefresh(firstRecoveryId ?? parent.Id);
        return Task.CompletedTask;
    }

    private async Task HandleCropShortageRecoveryStepSucceededAsync(QueueItem recoveryItem)
    {
        if (!recoveryItem.Payload.TryGetValue(BotOptionPayloadKeys.CropShortageRecoveryParentId, out var parentIdRaw)
            || !Guid.TryParse(parentIdRaw, out var parentId))
        {
            return;
        }

        var allItems = _botService.GetQueueItemsForDisplay();
        var parent = allItems.FirstOrDefault(item => item.Id == parentId);
        if (parent is null || parent.Status is not (QueueStatus.Pending or QueueStatus.Paused))
        {
            return;
        }

        var status = ResolveBuildingStatusForQueueItem(recoveryItem);
        var cropProduction = status?.ResourceStorageForecasts?
            .FirstOrDefault(forecast => string.Equals(forecast.ResourceKey, "crop", StringComparison.OrdinalIgnoreCase))
            ?.ProductionPerHour;
        if (cropProduction > 0)
        {
            foreach (var candidate in allItems.Where(item =>
                         item.Status == QueueStatus.Pending
                         && item.Payload.TryGetValue(BotOptionPayloadKeys.CropShortageRecoveryParentId, out var raw)
                         && string.Equals(raw, parentIdRaw, StringComparison.OrdinalIgnoreCase)))
            {
                var activeSlots = ConstructionQueueState.ResolveCurrentActiveConstructions(status)
                    .Select(active => active.SlotId)
                    .ToHashSet();
                var slotId = ResourceUpgradePayload.TryFromDictionary(candidate.Payload, out var payload, ResourceFieldMaxLevel)
                    ? payload!.SlotId
                    : 0;
                if (activeSlots.Contains(slotId) || ConstructionQueueState.IsConstructionInProgressDeferred(candidate))
                {
                    continue;
                }

                if (candidate.Payload.TryGetValue(BotOptionPayloadKeys.CropShortageOriginalPriority, out var originalRaw)
                    && int.TryParse(originalRaw, out var originalPriority))
                {
                    var restored = new Dictionary<string, string>(candidate.Payload, StringComparer.OrdinalIgnoreCase);
                    restored.Remove(BotOptionPayloadKeys.CropShortageRecoveryParentId);
                    restored.Remove(BotOptionPayloadKeys.CropShortageOriginalPriority);
                    _botService.UpdatePendingQueueItem(candidate.Id, restored, originalPriority, TimeSpan.Zero);
                }
                else
                {
                    _botService.RemoveQueueItem(candidate.Id);
                }
            }

            var releasedPayload = new Dictionary<string, string>(parent.Payload, StringComparer.OrdinalIgnoreCase);
            releasedPayload.Remove(BotOptionPayloadKeys.UpgradeDeferReason);
            releasedPayload.Remove(BotOptionPayloadKeys.UpgradeDeferClassificationVersion);
            _botService.UpdateDeferredQueueItem(parent.Id, releasedPayload, TimeSpan.Zero);
            AppendLog($"[crop-shortage] crop production is positive again ({cropProduction:0.##}/h); original Construction queue resumed.");
            RequestQueueUiRefresh(parent.Id);
            return;
        }

        if (status is null)
        {
            AppendLog("ALARM: Cropland recovery completed a step, but crop production could not be read; the parent remains deferred.");
            return;
        }

        await EnsureCropShortageRecoveryAsync(parent, status);
    }

    private static void CopyIfPresent(
        IReadOnlyDictionary<string, string> source,
        IDictionary<string, string> target,
        string key)
    {
        if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }
}
