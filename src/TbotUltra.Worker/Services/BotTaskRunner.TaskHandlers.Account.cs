namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static async Task ExecuteStatusAsync(TaskExecutionContext context)
    {
        var status = await context.Client.ReadVillageStatusAsync(context.CancellationToken);
        context.Log($"Village status read. ActiveVillage={status.ActiveVillage}, Villages={status.Villages.Count}, Resources={status.Resources.Count}, ResourceFields={status.ResourceFields.Count}, Buildings={status.Buildings.Count}, Queue={status.BuildQueue.Count}");
    }

    private static async Task ExecuteScanAllVillagesAsync(TaskExecutionContext context)
    {
        var statuses = await context.Client.ReadAllVillageStatusesAsync(context.CancellationToken);
        context.Log($"[scan] all villages scanned — {statuses.Count} status(es)");
    }

    private static async Task ExecuteAccountSnapshotAsync(TaskExecutionContext context)
    {
        var snapshot = await context.Client.ReadAccountSnapshotAsync(cancellationToken: context.CancellationToken);
        context.Log($"Account snapshot read. Tribe={snapshot.Tribe}, ActiveVillage={snapshot.ActiveVillage}, VillageCount={snapshot.VillageCount}, ServerTimeUtc={snapshot.ServerTimeUtc}");
    }
}
