using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;

namespace TbotUltra.Desktop.Services;

public sealed record TroopTrainingQuickBuildingSelection(
    TroopTrainingBuildingType BuildingType,
    bool Enabled,
    string TroopType);

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
                options.TroopTrainingWorkshopCheckWood,
                options.TroopTrainingWorkshopCheckClay,
                options.TroopTrainingWorkshopCheckIron,
                options.TroopTrainingWorkshopCheckCrop),
            options.TroopTrainingFallbackCooldownSeconds);
    }

    public static TroopTrainingPayload ApplySelections(
        TroopTrainingPayload source,
        IEnumerable<TroopTrainingQuickBuildingSelection> selections)
    {
        var barracks = source.Barracks;
        var stable = source.Stable;
        var workshop = source.Workshop;

        foreach (var selection in selections)
        {
            var troopType = selection.TroopType?.Trim() ?? string.Empty;
            switch (selection.BuildingType)
            {
                case TroopTrainingBuildingType.Barracks:
                    barracks = barracks with { Enabled = selection.Enabled, TroopType = troopType };
                    break;
                case TroopTrainingBuildingType.Stable:
                    stable = stable with { Enabled = selection.Enabled, TroopType = troopType };
                    break;
                case TroopTrainingBuildingType.Workshop:
                    workshop = workshop with { Enabled = selection.Enabled, TroopType = troopType };
                    break;
            }
        }

        return source with
        {
            Barracks = barracks,
            Stable = stable,
            Workshop = workshop,
        };
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
