using TbotUltra.Core.Configuration;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Farming;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services.Automation;
using System.Text.Json;

namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static async Task WriteFarmListsSnapshotAsync(TaskExecutionContext context, IReadOnlyList<FarmListOverview> overview)
    {
        try
        {
            var activeAccount = context.Runner._accountProvider.LoadAccount().Name;
            var outputPath = AccountStoragePaths.FarmListsSnapshotPath(context.Runner._projectContext.RootPath, activeAccount);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var payload = new
            {
                account = activeAccount,
                capturedAtUtc = DateTimeOffset.UtcNow,
                lists = overview.Where(item => item is not null).Select(item => new
                {
                    item.Name, item.VillageName, item.VillageIndex, item.ActiveFarmCount, item.TotalFarmCount, item.RemainingSeconds, item.ListId, item.Capacity, item.FarmCoordinates,
                }).ToList(),
            };
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(payload), context.CancellationToken);
        }
        catch (Exception ex)
        {
            context.Log($"Could not write farm list snapshot: {ex.Message}");
        }
    }

    private static async Task ExecuteSendFarmlistsAsync(TaskExecutionContext context)
    {
        var mode = FarmingDefaults.NormalizeSendMode(context.Options.ContinuousFarmSendMode);
        var minDelaySeconds = FarmingDefaults.NormalizeDispatchDelayMinMinutes(context.Options.ContinuousFarmDispatchDelayMinMinutes) * 60;
        var maxDelaySeconds = Math.Max(minDelaySeconds, FarmingDefaults.NormalizeDispatchDelayMaxMinutes(context.Options.ContinuousFarmDispatchDelayMaxMinutes) * 60);
        var dispatchDelaySeconds = FarmingDefaults.CalculateDispatchDelaySeconds(context.Options.ContinuousFarmDispatchDelayMinMinutes, context.Options.ContinuousFarmDispatchDelayMaxMinutes);
        var lossRequests = CreateFarmListLossHandlingRequests(context.Options);
        var activeAccount = context.Runner._accountProvider.LoadAccount().Name;
        var dispatchStates = LoadAndInitializeFarmListDispatchStates(context, activeAccount);
        context.Log($"Continuous farming mode={mode}; delayRange={minDelaySeconds}-{maxDelaySeconds}s; selectedDelay={dispatchDelaySeconds}s; targetVillage='{(string.IsNullOrWhiteSpace(context.Options.TargetVillageName) ? "(default)" : context.Options.TargetVillageName)}'; redLosses={context.Options.ContinuousFarmDeactivateRedLosses}; yellowLosses={context.Options.ContinuousFarmDeactivateYellowLosses}; redOasis={context.Options.ContinuousFarmDeactivateRedOasisLosses}; yellowOasis={context.Options.ContinuousFarmDeactivateYellowOasisLosses}.");

        var operation = new ContinuousFarmingOperation(context.Client);
        var result = await operation.ExecuteAsync(
            new ContinuousFarmingDispatchRequest(
                mode,
                context.Options.ContinuousFarmListNames,
                context.Options.ContinuousFarmListIds,
                dispatchDelaySeconds,
                lossRequests.Count > 0,
                null,
                lossRequests,
                dispatchStates.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.NextSendAtUtc,
                    StringComparer.OrdinalIgnoreCase)),
            context.Log,
            context.CancellationToken);

        if (string.Equals(mode, FarmingDefaults.SendModeListPerList, StringComparison.Ordinal) &&
            result.AttemptedLists is { Count: > 0 })
        {
            dispatchStates = UpdateFarmListDispatchStates(
                context,
                activeAccount,
                dispatchStates,
                result.AttemptedLists,
                result.SentLists ?? []);
            result = result with
            {
                WaitSeconds = CalculateNextFarmListWaitSeconds(
                    context.Options,
                    result.Snapshot ?? [],
                    dispatchStates,
                    DateTimeOffset.UtcNow,
                    dispatchDelaySeconds),
            };
        }

        foreach (var lossResult in result.LossHandlingResults ?? [])
        {
            context.Runner.PublishFarmLossDestinationChange(context.Options, lossResult);
        }

        if (result.Snapshot is not null)
        {
            await WriteFarmListsSnapshotAsync(context, result.Snapshot);
        }

        if (result.ScheduleNextRound)
        {
            LogContinuousFarmNextSchedule(context, result.WaitSeconds, 0);
        }

        throw BuildContinuousFarmDefer(result.WaitMessage, result.WaitSeconds, 0, result.WaitReasonCode);
    }

    private static Dictionary<string, FarmListDispatchState> LoadAndInitializeFarmListDispatchStates(
        TaskExecutionContext context,
        string activeAccount)
    {
        var states = FarmListDispatchStateStore.Load(context.Runner._projectContext.RootPath, activeAccount)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var defaultMinMinutes = FarmingDefaults.NormalizeDispatchDelayMinMinutes(
            context.Options.ContinuousFarmDispatchDelayMinMinutes);
        var defaultMaxMinutes = FarmingDefaults.NormalizeDispatchDelayMaxMinutes(
            context.Options.ContinuousFarmDispatchDelayMaxMinutes);
        var changed = false;
        foreach (var pair in states.ToList())
        {
            var initialized = FarmListDispatchStateStore.WithDefaultInterval(
                pair.Value,
                defaultMinMinutes,
                defaultMaxMinutes);
            if (initialized != pair.Value)
            {
                states[pair.Key] = initialized;
                changed = true;
            }

            if (initialized.LastSentAtUtc is null || initialized.NextSendAtUtc is not null)
            {
                continue;
            }

            var delaySeconds = CalculateFarmListDelaySeconds(initialized);
            states[pair.Key] = initialized with
            {
                NextSendAtUtc = initialized.LastSentAtUtc.Value.AddSeconds(delaySeconds),
            };
            changed = true;
        }

        if (changed)
        {
            FarmListDispatchStateStore.Save(context.Runner._projectContext.RootPath, activeAccount, states);
            context.Log("[farm-list] initialized missing per-list intervals and deadlines from the shared default.");
        }

        return states;
    }

    private static Dictionary<string, FarmListDispatchState> UpdateFarmListDispatchStates(
        TaskExecutionContext context,
        string activeAccount,
        Dictionary<string, FarmListDispatchState> states,
        IReadOnlyList<FarmListSendEntry> attemptedLists,
        IReadOnlyList<FarmListSendEntry> sentLists)
    {
        var sentKeys = sentLists
            .Select(item => FarmListDispatchStateStore.CreateKey(item.ListId, item.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sentAtUtc = DateTimeOffset.UtcNow;

        foreach (var attempted in attemptedLists)
        {
            var key = FarmListDispatchStateStore.CreateKey(attempted.ListId, attempted.Name);
            states[key] = FarmListDispatchStateStore.Update(
                context.Runner._projectContext.RootPath,
                activeAccount,
                key,
                previous =>
                {
                    previous = FarmListDispatchStateStore.WithDefaultInterval(
                        previous ?? new FarmListDispatchState(null, Failed: false),
                        FarmingDefaults.NormalizeDispatchDelayMinMinutes(context.Options.ContinuousFarmDispatchDelayMinMinutes),
                        FarmingDefaults.NormalizeDispatchDelayMaxMinutes(context.Options.ContinuousFarmDispatchDelayMaxMinutes));
                    return sentKeys.Contains(key)
                        ? previous with
                        {
                            LastSentAtUtc = sentAtUtc,
                            Failed = false,
                            NextSendAtUtc = sentAtUtc.AddSeconds(CalculateFarmListDelaySeconds(previous)),
                        }
                        : previous with { Failed = true };
                });
        }

        context.Log($"[farm-list] saved independent dispatch schedules for {attemptedLists.Count} attempted list(s); confirmed={sentKeys.Count}.");
        return FarmListDispatchStateStore.Load(context.Runner._projectContext.RootPath, activeAccount)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static int CalculateNextFarmListWaitSeconds(
        BotOptions options,
        IReadOnlyList<FarmListOverview> overview,
        IReadOnlyDictionary<string, FarmListDispatchState> states,
        DateTimeOffset nowUtc,
        int fallbackSeconds)
    {
        var selectedNames = options.ContinuousFarmListNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIds = options.ContinuousFarmListIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var waits = new List<int>();
        foreach (var list in overview)
        {
            var selected = selectedIds.Count > 0
                ? !string.IsNullOrWhiteSpace(list.ListId) && selectedIds.Contains(list.ListId)
                : selectedNames.Contains(list.Name);
            if (!selected)
            {
                continue;
            }

            var key = FarmListDispatchStateStore.CreateKey(list.ListId, list.Name);
            if (states.TryGetValue(key, out var state) && state.NextSendAtUtc > nowUtc)
            {
                waits.Add(Math.Max(1, (int)Math.Ceiling((state.NextSendAtUtc.Value - nowUtc).TotalSeconds)));
            }
            else if (list.RemainingSeconds is > 0)
            {
                waits.Add(list.RemainingSeconds.Value + Random.Shared.Next(5, 16));
            }
            else
            {
                waits.Add(60);
            }
        }

        return waits.Count > 0 ? waits.Min() : Math.Max(1, fallbackSeconds);
    }

    private static int CalculateFarmListDelaySeconds(FarmListDispatchState state)
    {
        if (state.IntervalMinMinutes is not > 0 ||
            state.IntervalMaxMinutes is null ||
            state.IntervalMaxMinutes < state.IntervalMinMinutes)
        {
            throw new InvalidOperationException("Farm-list dispatch interval must contain valid Min and Max values.");
        }

        var minMinutes = state.IntervalMinMinutes.Value;
        var maxMinutes = state.IntervalMaxMinutes.Value;
        return FarmingDefaults.CalculateDispatchDelaySeconds(minMinutes, Math.Max(minMinutes, maxMinutes));
    }

    private static void LogContinuousFarmNextSchedule(TaskExecutionContext context, int waitSeconds, int nextIndex)
    {
        var nextTime = DateTimeOffset.Now.AddSeconds(Math.Max(1, waitSeconds));
        context.Log($"Continuous farming next scheduled send time={nextTime:yyyy-MM-dd HH:mm:ss zzz}; nextListIndex={nextIndex}; wait={waitSeconds}s.");
    }

    private static TaskWaitException BuildContinuousFarmDefer(string message, int waitSeconds, int nextIndex, string? reasonCode = null) =>
        new(Math.Max(1, waitSeconds), $"{message} queue_wait_seconds={Math.Max(1, waitSeconds)} {BotOptionPayloadKeys.ContinuousFarmNextListIndex}={Math.Max(0, nextIndex)}", reasonCode);
}
