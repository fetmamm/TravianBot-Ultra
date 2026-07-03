using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;

namespace TbotUltra.Desktop.Services;

public static class TroopTrainingQuickSettings
{
    public static TroopTrainingPayload FromOptions(BotOptions options)
    {
        return new TroopTrainingPayload(
            new TroopTrainingBuildingPayload(
                options.TroopTrainingBarracksEnabled,
                options.TroopTrainingBarracksTroopType,
                options.TroopTrainingBarracksMaxQueueHours,
                options.TroopTrainingBarracksAmountMode,
                options.TroopTrainingBarracksKeepResourcesPercent,
                options.TroopTrainingBarracksRunMode,
                options.TroopTrainingBarracksMinimumTroops,
                options.TroopTrainingBarracksMinimumResourcesPercent,
                options.TroopTrainingBarracksTimedMinMinutes,
                options.TroopTrainingBarracksTimedMaxMinutes,
                options.TroopTrainingBarracksCheckWood,
                options.TroopTrainingBarracksCheckClay,
                options.TroopTrainingBarracksCheckIron,
                options.TroopTrainingBarracksCheckCrop),
            new TroopTrainingBuildingPayload(
                options.TroopTrainingStableEnabled,
                options.TroopTrainingStableTroopType,
                options.TroopTrainingStableMaxQueueHours,
                options.TroopTrainingStableAmountMode,
                options.TroopTrainingStableKeepResourcesPercent,
                options.TroopTrainingStableRunMode,
                options.TroopTrainingStableMinimumTroops,
                options.TroopTrainingStableMinimumResourcesPercent,
                options.TroopTrainingStableTimedMinMinutes,
                options.TroopTrainingStableTimedMaxMinutes,
                options.TroopTrainingStableCheckWood,
                options.TroopTrainingStableCheckClay,
                options.TroopTrainingStableCheckIron,
                options.TroopTrainingStableCheckCrop),
            new TroopTrainingBuildingPayload(
                options.TroopTrainingWorkshopEnabled,
                options.TroopTrainingWorkshopTroopType,
                options.TroopTrainingWorkshopMaxQueueHours,
                options.TroopTrainingWorkshopAmountMode,
                options.TroopTrainingWorkshopKeepResourcesPercent,
                options.TroopTrainingWorkshopRunMode,
                options.TroopTrainingWorkshopMinimumTroops,
                options.TroopTrainingWorkshopMinimumResourcesPercent,
                options.TroopTrainingWorkshopTimedMinMinutes,
                options.TroopTrainingWorkshopTimedMaxMinutes,
                options.TroopTrainingWorkshopCheckWood,
                options.TroopTrainingWorkshopCheckClay,
                options.TroopTrainingWorkshopCheckIron,
                options.TroopTrainingWorkshopCheckCrop),
            options.TroopTrainingFallbackCooldownSeconds);
    }

    public static TroopTrainingBuildingPayload BuildingPayloadFor(
        TroopTrainingPayload payload,
        TroopTrainingBuildingType buildingType)
    {
        return buildingType switch
        {
            TroopTrainingBuildingType.Barracks => payload.Barracks,
            TroopTrainingBuildingType.Stable => payload.Stable,
            _ => payload.Workshop,
        };
    }
}
