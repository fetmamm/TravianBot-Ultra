using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Infrastructure;

namespace TbotUltra.Core.Farming;

public sealed record FarmListDispatchState(
    DateTimeOffset? LastSentAtUtc,
    bool Failed,
    int? IntervalMinMinutes = null,
    int? IntervalMaxMinutes = null,
    DateTimeOffset? NextSendAtUtc = null);

public static class FarmListDispatchStateStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static bool IsSuccessfulDispatch(bool sendActionCompleted, int? remainingSeconds)
    {
        // A list can dispatch successfully without exposing a positive cooldown (for example when
        // Travian immediately reports it ready again). Cooldown describes list availability, not whether
        // its send action succeeded.
        return sendActionCompleted;
    }

    public static bool ShouldTrackDispatch(bool sendAllLists, bool isEnabled, bool isReady, bool isEmpty)
        => !isEmpty && isReady && (sendAllLists || isEnabled);

    public static string CreateKey(string? listId, string? listName)
    {
        if (!string.IsNullOrWhiteSpace(listId))
        {
            return $"lid:{listId.Trim()}";
        }

        return $"name:{(listName ?? string.Empty).Trim()}";
    }

    public static FarmListDispatchState WithDefaultInterval(
        FarmListDispatchState state,
        int defaultMinMinutes,
        int defaultMaxMinutes)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.IntervalMinMinutes is > 0 &&
            state.IntervalMaxMinutes is not null &&
            state.IntervalMaxMinutes >= state.IntervalMinMinutes)
        {
            return state;
        }

        var minMinutes = Math.Max(1, defaultMinMinutes);
        var maxMinutes = Math.Max(minMinutes, defaultMaxMinutes);
        return state with
        {
            IntervalMinMinutes = minMinutes,
            IntervalMaxMinutes = maxMinutes,
        };
    }

    public static IReadOnlyDictionary<string, FarmListDispatchState> Load(string projectRoot, string accountName)
    {
        lock (Gate)
        {
            return LoadCore(projectRoot, accountName);
        }
    }

    public static void Save(string projectRoot, string accountName, IReadOnlyDictionary<string, FarmListDispatchState> states)
    {
        lock (Gate)
        {
            SaveCore(projectRoot, accountName, states);
        }
    }

    public static FarmListDispatchState Update(
        string projectRoot,
        string accountName,
        string key,
        Func<FarmListDispatchState?, FarmListDispatchState> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(update);

        lock (Gate)
        {
            var states = LoadCore(projectRoot, accountName)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var state = update(states.GetValueOrDefault(key));
            states[key] = state;
            SaveCore(projectRoot, accountName, states);
            return state;
        }
    }

    private static IReadOnlyDictionary<string, FarmListDispatchState> LoadCore(string projectRoot, string accountName)
    {
        var path = AccountStoragePaths.FarmListDispatchStatePath(projectRoot, accountName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, FarmListDispatchState>(StringComparer.OrdinalIgnoreCase);
        }

        var file = JsonSerializer.Deserialize<DispatchStateFile>(File.ReadAllText(path));
        return (file?.Lists ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var item = group.Last();
                    return new FarmListDispatchState(
                        item.LastSentAtUtc,
                        item.Failed,
                        NormalizeInterval(item.IntervalMinMinutes),
                        NormalizeInterval(item.IntervalMaxMinutes),
                        item.NextSendAtUtc);
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveCore(
        string projectRoot,
        string accountName,
        IReadOnlyDictionary<string, FarmListDispatchState> states)
    {
        var path = AccountStoragePaths.FarmListDispatchStatePath(projectRoot, accountName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new DispatchStateFile(
            states
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new DispatchStateEntry(
                    pair.Key,
                    pair.Value.LastSentAtUtc,
                    pair.Value.Failed,
                    NormalizeInterval(pair.Value.IntervalMinMinutes),
                    NormalizeInterval(pair.Value.IntervalMaxMinutes),
                    pair.Value.NextSendAtUtc))
                .ToList());
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
    }

    private static int? NormalizeInterval(int? value) => value is > 0 ? value : null;

    private sealed record DispatchStateFile(List<DispatchStateEntry> Lists);

    private sealed record DispatchStateEntry(
        string Key,
        DateTimeOffset? LastSentAtUtc,
        bool Failed,
        int? IntervalMinMinutes = null,
        int? IntervalMaxMinutes = null,
        DateTimeOffset? NextSendAtUtc = null);
}
