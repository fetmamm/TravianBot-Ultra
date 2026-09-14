using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;
using System.Text.RegularExpressions;

namespace TbotUltra.Desktop.Services.Orchestration;

internal readonly record struct QueueItemSuccessRefreshResult(
    bool BuildingsStatusRead,
    bool StorageStatusRead);

internal interface IAutomationQueueItemSuccessPort
{
    bool MarkSucceeded(Guid itemId);
    bool RemoveQueueItem(Guid itemId);
    void RebindPendingBuildingUpgrades(QueueItem item, int liveSlotId);
    void RebindPendingBuildingTemplateStep(QueueItem item, int liveSlotId);
    void RequestQueueUiRefresh();
    ValueTask<bool> RefreshResourceStatusAsync(CancellationToken cancellationToken);
    ValueTask<QueueItemSuccessRefreshResult> RefreshConstructionStatusAsync(
        QueueItem item,
        CancellationToken cancellationToken);
    ValueTask RefreshCurrentPageStorageStatusAsync(
        BotOptions options,
        string reason,
        CancellationToken cancellationToken);
    ValueTask HandleStorageDependencySucceededAsync(QueueItem item);
    ValueTask HandleCropShortageRecoveryStepSucceededAsync(QueueItem item);
    ValueTask RefreshHeroAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask RefreshTroopTrainingAsync(
        QueueItem item,
        BotOptions options,
        CancellationToken cancellationToken);
    ValueTask RefreshBreweryAsync(
        QueueItem item,
        BotOptions options,
        CancellationToken cancellationToken);
    void ScheduleNextReinforcementSend(BotOptions options);
    void ApplyProductionBonusResult(string? message);
    void ApplyDailyResetResult(string? message);
    void Log(string message);
}

internal sealed class AutomationQueueItemSuccess(IAutomationQueueItemSuccessPort port)
{
    internal async ValueTask<bool> HandleAsync(
        QueueItem item,
        BotOptions options,
        BotTaskExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        port.MarkSucceeded(item.Id);

        if (IsTask(item, "construct_building")
            && TryExtractPayloadInt(
                executionResult.LastTask?.Message,
                BotOptionPayloadKeys.BuildingConstructSlotId,
                out var effectiveConstructSlot))
        {
            port.RebindPendingBuildingUpgrades(item, effectiveConstructSlot);
            port.RebindPendingBuildingTemplateStep(item, effectiveConstructSlot);
        }

        if (IsTask(item, "construct_building")
            && executionResult.LastTask?.ConstructionOutcome == ConstructionTaskOutcome.AlreadyExists)
        {
            if (port.RemoveQueueItem(item.Id))
            {
                port.Log(
                    "[queue] removed construct task — building already exists (confirmed). "
                    + executionResult.LastTask?.Message);
            }

            port.RequestQueueUiRefresh();
            return false;
        }

        var fullConstructionRefreshDone = false;
        var resourceStatusRead = false;
        if (IsResourceUpgradeTask(item.TaskName))
        {
            resourceStatusRead = await port.RefreshResourceStatusAsync(cancellationToken);
        }

        if (IsBuildingMutationTask(item.TaskName))
        {
            var refresh = await port.RefreshConstructionStatusAsync(item, cancellationToken);
            fullConstructionRefreshDone = refresh.BuildingsStatusRead;
            if (!refresh.StorageStatusRead)
            {
                await port.RefreshCurrentPageStorageStatusAsync(
                    options,
                    "construction_success",
                    cancellationToken);
            }
            await port.HandleStorageDependencySucceededAsync(item);
        }
        else if (IsResourceUpgradeTask(item.TaskName))
        {
            if (!resourceStatusRead)
            {
                await port.RefreshCurrentPageStorageStatusAsync(
                    options,
                    "construction_success",
                    cancellationToken);
            }
            if (item.Payload.ContainsKey(BotOptionPayloadKeys.CropShortageRecoveryParentId))
            {
                await port.HandleCropShortageRecoveryStepSucceededAsync(item);
            }
        }
        else if (IsTask(item, "hero_manage") || IsTask(item, "spend_hero_attribute_points"))
        {
            await TryRefreshAsync(
                () => port.RefreshHeroAsync(options, cancellationToken),
                "Hero stats refresh after run failed");
        }
        else if (IsTask(item, "build_troops"))
        {
            await TryRefreshAsync(
                () => port.RefreshTroopTrainingAsync(item, options, cancellationToken),
                "Troop/resource refresh after run failed");
        }
        else if (IsTask(item, "run_brewery_celebration"))
        {
            await TryRefreshAsync(
                () => port.RefreshBreweryAsync(item, options, cancellationToken),
                "Brewery celebration refresh after run failed");
        }
        else if (IsTask(item, "send_reinforcements_between_villages"))
        {
            port.ScheduleNextReinforcementSend(options);
        }
        else if (IsTask(item, "activate_production_bonus"))
        {
            port.ApplyProductionBonusResult(executionResult.LastTask?.Message);
        }
        else if (IsTask(item, "read_daily_reset") || IsTask(item, "collect_daily_quests"))
        {
            port.ApplyDailyResetResult(executionResult.LastTask?.Message);
        }

        return fullConstructionRefreshDone;
    }

    private async ValueTask TryRefreshAsync(Func<ValueTask> refresh, string failurePrefix)
    {
        try
        {
            await refresh();
        }
        catch (Exception exception)
        {
            port.Log($"{failurePrefix}: {exception.Message}");
        }
    }

    private static bool IsTask(QueueItem item, string taskName) =>
        string.Equals(item.TaskName, taskName, StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildingMutationTask(string? taskName) =>
        string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "demolish_building_to_level", StringComparison.OrdinalIgnoreCase);

    private static bool IsResourceUpgradeTask(string? taskName) =>
        string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase);

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
}
