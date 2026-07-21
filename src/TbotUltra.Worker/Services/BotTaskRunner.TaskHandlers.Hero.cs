namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static async Task ExecuteHeroManageAsync(TaskExecutionContext context)
    {
        var result = await context.Client.ManageHeroAsync(
            context.Options.HeroMinHpForAdventure, context.Options.HeroAutoRevive,
            context.Options.HeroAutoAssignPoints, context.Options.HeroAutoUseOintments,
            context.Options.HeroStatPriority, context.Options.HeroAdventurePickOrder,
            context.Options.HeroHpRegenPerDayPercent, context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("hero_manage", result);
    }

    private static async Task ExecuteSpendHeroAttributePointsAsync(TaskExecutionContext context)
    {
        var result = await context.Client.SpendHeroAttributePointsAsync(context.Options.HeroStatPriority, context.CancellationToken);
        context.Log(result);
    }
}
