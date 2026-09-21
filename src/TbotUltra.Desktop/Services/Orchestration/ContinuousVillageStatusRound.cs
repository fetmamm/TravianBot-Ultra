using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IContinuousVillageStatusRoundPort : IVillageStatusRoundPort
{
    DateTimeOffset GetNextRoundUtc();
    string? ActiveVillageKey { get; }
    string? ActiveAccountName { get; }
    ValueTask<bool> EnsureVillageMembershipVerifiedAsync(
        BotOptions options,
        CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<VillageStatusRoundVillage>> LoadVillagesAsync(BotOptions options);
    IDisposable BeginRoundActivity(int villageCount);
    VillageStatusRoundScheduleResult ScheduleNext(
        string? expectedAccountName,
        int minMinutes,
        int maxMinutes);
    void Log(string message);
}

internal sealed class ContinuousVillageStatusRound(
    VillageStatusRoundCoordinator coordinator,
    IContinuousVillageStatusRoundPort port,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _loginRoundSync = new();
    private bool _loginRoundPending;
    private List<string>? _loginRoundRemainingKeys;

    internal bool LoginRoundPending
    {
        get { lock (_loginRoundSync) return _loginRoundPending; }
    }

    internal void RequestLoginRound(bool preserveIncomplete = false)
    {
        lock (_loginRoundSync)
        {
            if (preserveIncomplete && _loginRoundPending)
                return;
            _loginRoundPending = true;
            _loginRoundRemainingKeys = null;
        }
        port.Log("[village-round] post-login village round queued.");
    }

    internal void ResetLoginRound()
    {
        lock (_loginRoundSync) { _loginRoundPending = false; _loginRoundRemainingKeys = null; }
    }

    internal async ValueTask RunIfDueAsync(
        BotOptions options,
        CancellationToken cancellationToken,
        bool force = false)
    {
        var loginRound = LoginRoundPending;
        if ((!options.VillageStatusSweepEnabled && !force && !loginRound)
            || (!force && !loginRound && _timeProvider.GetUtcNow() < port.GetNextRoundUtc()))
        {
            return;
        }

        if (!await port.EnsureVillageMembershipVerifiedAsync(options, cancellationToken))
        {
            port.Log("[village-scan] round deferred until village ownership can be verified.");
            return;
        }

        var accountName = port.ActiveAccountName;
        var villages = await port.LoadVillagesAsync(options);
        if (villages.Count == 0)
        {
            return;
        }

        using var activity = port.BeginRoundActivity(villages.Count);
        if (loginRound)
        {
            lock (_loginRoundSync)
            {
                _loginRoundRemainingKeys ??= coordinator.OrderStartingAt(villages, port.ActiveVillageKey)
                    .Select(village => village.Key).ToList();
            }
            var remaining = _loginRoundRemainingKeys!;
            var ordered = remaining.Select(key => villages.FirstOrDefault(village =>
                    string.Equals(village.Key, key, StringComparison.OrdinalIgnoreCase)))
                .Where(village => village is not null).Select(village => village!).ToList();
            if (ordered.Count == 0)
            {
                lock (_loginRoundSync) { _loginRoundPending = false; _loginRoundRemainingKeys = null; }
                return;
            }
            port.Log($"[village-round] starting/resuming post-login round; {ordered.Count} village(s) remaining.");
            var loginResult = await coordinator.RunAsync(ordered, port, cancellationToken,
                preserveOrder: true,
                onVisited: village => { lock (_loginRoundSync) remaining.RemoveAll(key =>
                    string.Equals(key, village.Key, StringComparison.OrdinalIgnoreCase)); },
                startIndex: villages.Count - ordered.Count,
                totalCount: villages.Count);
            if (!loginResult.Completed)
                return;
            lock (_loginRoundSync) { _loginRoundPending = false; _loginRoundRemainingKeys = null; }
            port.Log("[village-round] post-login round complete.");
            if (!options.VillageStatusSweepEnabled)
                return;
        }
        else
        {
            var result = await coordinator.RunAsync(villages, port, cancellationToken);
            if (!result.Completed)
                return;
        }

        var min = Math.Min(
            options.VillageStatusSweepRoundMinMinutes,
            options.VillageStatusSweepRoundMaxMinutes);
        var max = Math.Max(
            options.VillageStatusSweepRoundMinMinutes,
            options.VillageStatusSweepRoundMaxMinutes);
        var schedule = port.ScheduleNext(accountName, min, max);
        if (schedule.AccountChanged)
        {
            return;
        }
        if (!schedule.WasPersisted)
        {
            port.Log(
                "[village-scan] could not persist the next-scan deadline; "
                + "it may run again after restart.");
        }
        if (schedule.NextRoundUtc is { } nextRoundUtc)
        {
            port.Log($"[village-scan] round complete; next round after {nextRoundUtc:HH:mm}.");
        }
    }
}
