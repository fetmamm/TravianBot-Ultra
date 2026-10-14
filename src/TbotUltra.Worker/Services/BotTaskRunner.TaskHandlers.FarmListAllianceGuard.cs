namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static async Task ExecuteCheckFarmlistsAllianceAsync(TaskExecutionContext context)
    {
        var identity = await context.Client.ReadCurrentFarmTargetIdentityAsync(context.CancellationToken);
        if (!identity.IsResolved)
        {
            throw new InvalidOperationException("Farm-list alliance check: own player identity could not be read.");
        }

        if (string.IsNullOrWhiteSpace(identity.Alliance))
        {
            context.Log("[farm-guard] own player has no alliance; nothing to protect.");
            context.RecordTaskResult("check_farmlists_alliance", "no alliance");
            return;
        }

        var members = await context.Client.ReadOwnAllianceMembersAsync(context.CancellationToken);
        var ownNameKey = MapSqlPlayerParser.NormalizeNameKey(identity.PlayerName);
        // An empty or foreign list must never be read as "no members": that would leave alliance targets active.
        if (members.Count == 0
            || !members.Any(member => MapSqlPlayerParser.NormalizeNameKey(member.PlayerName) == ownNameKey))
        {
            throw new InvalidOperationException(
                $"Farm-list alliance check: member list of '{identity.Alliance}' is unreadable (members={members.Count}, own player listed=false).");
        }

        var mapSql = await DownloadMapSqlAsync(context.Options, context.Log, context.CancellationToken);
        var villages = MapSqlPlayerParser.ParseVillages(mapSql);
        context.Log($"[farm-guard] checking farm lists against alliance '{identity.Alliance}': members={members.Count}, mapVillages={villages.Count}.");

        var result = await context.Client.DeactivateAllianceMemberFarmTargetsAsync(villages, members, context.CancellationToken);
        var summary = $"alliance={identity.Alliance} members={members.Count} activeRows={result.ActiveRows} "
            + $"allianceTargets={result.AllianceTargets} deactivated={result.Deactivated} failed={result.Failed} unknownOwner={result.UnknownOwner}";
        context.Log($"[farm-guard] {summary}");
        context.RecordTaskResult("check_farmlists_alliance", summary);
        if (result.Failed > 0)
        {
            throw new InvalidOperationException($"Farm-list alliance check could not deactivate {result.Failed} alliance target(s).");
        }
    }
}
