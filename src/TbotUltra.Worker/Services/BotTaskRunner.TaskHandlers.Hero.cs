namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static async Task ExecuteHeroManageAsync(TaskExecutionContext context)
    {
        var result = await new Automation.HeroAutomationOperation(context.Client).ManageAsync(
            context.Options.HeroMinHpForAdventure, context.Options.HeroAutoRevive,
            context.Options.HeroAutoAssignPoints, context.Options.HeroAutoUseOintments,
            context.Options.HeroOintmentTargetHpPercent,
            context.Options.HeroStatPriority, context.Options.HeroAdventurePickOrder,
            context.Options.HeroHpRegenPerDayPercent, context.CancellationToken);
        context.Log(result);
        if (IsHeroAdventureDispatched(result))
        {
            context.RecordTaskResult("hero_adventure", result);
        }
        ThrowIfTaskBlocked("hero_manage", result);
    }

    private static async Task ExecuteSpendHeroAttributePointsAsync(TaskExecutionContext context)
    {
        var result = await new Automation.HeroAutomationOperation(context.Client)
            .SpendAttributePointsAsync(context.Options.HeroStatPriority, context.CancellationToken);
        context.Log(result);
    }

    private static async Task ExecuteAntiStarveHeroCropAsync(TaskExecutionContext context)
    {
        var result = await context.Client.RunHeroCropAntiStarveAsync(
            context.Options.HeroCropAntiStarveTriggerMinutes,
            context.Options.HeroCropAntiStarveTargetMinutes,
            context.Options.HeroCropAntiStarveMaxCropPerTransfer,
            context.Options.HeroCropAntiStarveMinHeroCropRemaining,
            context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("anti_starve_hero_crop", result);
    }
}
