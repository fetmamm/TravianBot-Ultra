namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static async Task ExecuteCollectTasksAsync(TaskExecutionContext context)
    {
        var result = await context.Client.CollectTaskRewardsAsync(context.CancellationToken);
        context.Log(result);
    }

    private static async Task ExecuteCollectDailyQuestsAsync(TaskExecutionContext context)
    {
        var result = await context.Client.CollectDailyQuestRewardsAsync(context.CancellationToken);
        context.Log(result);
        context.RecordTaskResult("collect_daily_quests", result);
    }

    private static async Task ExecuteReadDailyResetAsync(TaskExecutionContext context)
    {
        var result = await context.Client.ReadDailyResetHourAsync(context.CancellationToken);
        context.Log(result);
        context.RecordTaskResult("read_daily_reset", result);
    }

    private static async Task ExecuteActivateProductionBonusAsync(TaskExecutionContext context)
    {
        var result = await context.Client.ActivateProductionBonusVideosAsync(context.CancellationToken);
        context.Log(result);
        context.RecordTaskResult("activate_production_bonus", result);
        ThrowIfTaskBlocked("activate_production_bonus", result);
    }
}
