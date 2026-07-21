namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
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
}
