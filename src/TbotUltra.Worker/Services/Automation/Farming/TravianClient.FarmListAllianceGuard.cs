namespace TbotUltra.Worker.Services;

using Microsoft.Playwright;

public sealed partial class TravianClient
{
    internal async Task<IReadOnlyList<AllianceMember>> ReadOwnAllianceMembersAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await GotoAsync(Paths.AllianceMembers, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        try
        {
            await _page.Locator("table.allianceMembers td.player a[href*='/profile/']").First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = _config.TimeoutMs,
            }).WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            Notify("[farm-guard] alliance member table did not render.");
            return [];
        }

        var members = await _page.EvaluateAsync<AllianceMemberJs[]>(
            """
            () => Array.from(document.querySelectorAll("table.allianceMembers td.player a[href*='/profile/']"))
              .map(link => {
                const match = (link.getAttribute('href') || '').match(/\/profile\/(\d+)/);
                return {
                  playerId: match ? parseInt(match[1], 10) : 0,
                  playerName: (link.textContent || '').replace(/\s+/g, ' ').trim()
                };
              })
              .filter(member => member.playerId > 0)
            """).WaitAsync(cancellationToken);
        var result = (members ?? [])
            .Select(member => new AllianceMember(member.PlayerId, member.PlayerName ?? string.Empty))
            .DistinctBy(member => member.PlayerId)
            .ToList();
        Notify($"[farm-guard] read {result.Count} alliance member(s) from {Paths.AllianceMembers}.");
        return result;
    }

    internal async Task<FarmListAllianceGuardResult> DeactivateAllianceMemberFarmTargetsAsync(
        IReadOnlyList<MapSqlVillage> villages,
        IReadOnlyList<AllianceMember> members,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        await DismissDeactivatedTargetsNoticeAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await WaitForFarmListsRenderedAsync(cancellationToken);
        await EnsureOfficialFarmListsExpandedAsync(cancellationToken);

        var rows = await ReadFarmListLossRowsFromCurrentPageAsync(cancellationToken);
        var plan = FarmListAllianceGuard.Plan(rows.Select(ToGuardRow).ToList(), villages, members);
        foreach (var unknown in plan.UnknownOwnerRows)
        {
            Notify($"[farm-guard] owner unknown in map.sql target='{unknown.TargetName}' ({unknown.X}|{unknown.Y}) list='{unknown.ListName}'; not checked.");
        }

        var deactivated = 0;
        var failed = 0;
        foreach (var hit in plan.Hits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(hit.Row.SlotId))
            {
                failed++;
                Notify($"ALARM: [farm-guard] alliance member target='{hit.Row.TargetName}' player='{hit.PlayerName}' has no slot id; deactivate it manually.");
                continue;
            }

            var current = await ReadFarmListLossRowsFromCurrentPageAsync(cancellationToken);
            var row = current.FirstOrDefault(candidate =>
                string.Equals(candidate.SlotId, hit.Row.SlotId, StringComparison.Ordinal) && !candidate.Disabled);
            if (row is null)
            {
                continue;
            }

            if (await TryDeactivateFarmListLossRowAsync(row, cancellationToken))
            {
                deactivated++;
                Notify($"[farm-guard] deactivated alliance member target='{hit.Row.TargetName}' player='{hit.PlayerName}' ({hit.Row.X}|{hit.Row.Y}) slot='{hit.Row.SlotId}' list='{hit.Row.ListName}'.");
            }
            else
            {
                failed++;
                Notify($"ALARM: [farm-guard] could not deactivate alliance member target='{hit.Row.TargetName}' player='{hit.PlayerName}' slot='{hit.Row.SlotId}'.");
            }

            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
        }

        var result = new FarmListAllianceGuardResult(plan.ActiveRows, plan.Hits.Count, deactivated, failed, plan.UnknownOwnerRows.Count);
        Notify($"[farm-guard] done: activeRows={result.ActiveRows}, allianceTargets={result.AllianceTargets}, deactivated={result.Deactivated}, failed={result.Failed}, unknownOwner={result.UnknownOwner}.");
        return result;
    }

    private static FarmListGuardRow ToGuardRow(FarmListLossRowJs row) => new(
        row.SlotId ?? string.Empty,
        row.ListName ?? string.Empty,
        row.TargetName ?? string.Empty,
        row.TargetX,
        row.TargetY,
        row.Disabled);

    private sealed class AllianceMemberJs
    {
        [System.Text.Json.Serialization.JsonPropertyName("playerId")]
        public long PlayerId { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("playerName")]
        public string? PlayerName { get; init; }
    }
}

internal sealed record FarmListAllianceGuardResult(
    int ActiveRows,
    int AllianceTargets,
    int Deactivated,
    int Failed,
    int UnknownOwner);
