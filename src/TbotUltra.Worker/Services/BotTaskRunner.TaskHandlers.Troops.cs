using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;

namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static async Task ExecuteUpgradeTroopsAtSmithyAsync(TaskExecutionContext context)
    {
        var targets = SmithyUpgradePayload.Parse(context.Options.SmithyUpgradeTargets);
        if (targets.Count == 0)
        {
            context.Log("Smithy: no troops selected for upgrade — configure them via 'Upgrade options'. Nothing to do.");
            return;
        }

        var result = await context.Client.UpgradeSelectedTroopsAtSmithyAsync(targets, context.CancellationToken);
        context.Log(result);
        await RefreshBuildingsSnapshotAfterTaskAsync(context);
        ThrowIfTroopsGroupBlocked(result);
        ThrowIfTaskBlocked("upgrade_troops_at_smithy", result);
    }

    private static async Task ExecuteBuildTroopsAsync(TaskExecutionContext context)
    {
        context.Log("[troops] build_troops starting");
        var result = await context.Client.BuildTroopsAsync(context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("build_troops", result);
    }
}
