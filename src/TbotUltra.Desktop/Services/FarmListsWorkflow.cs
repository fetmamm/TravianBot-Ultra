using TbotUltra.Core.Configuration;
using TbotUltra.Core.Farming;
using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;
using System.Text.Json;
using System.IO;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Owns the Farm Lists desktop workflow and its account-scoped settings.
/// Browser work crosses the single <see cref="IFarmingPanelClient"/> seam.
/// </summary>
public sealed class FarmListsWorkflow(
    IFarmingPanelClient client,
    BotConfigStore configStore,
    string projectRoot,
    Func<string> activeAccountName,
    Action<string> log)
{
    public const int MaximumVisibleLists = 120;
    private static readonly TimeSpan RecentAnalysisWindow = TimeSpan.FromMinutes(5);
    private readonly object _stateLock = new();
    private FarmListsAutomationSnapshot _automationSnapshot = FarmListsAutomationSnapshot.Empty;

    public FarmListsAutomationSnapshot AutomationSnapshot
    {
        get
        {
            lock (_stateLock)
            {
                return _automationSnapshot;
            }
        }
    }

    public void CaptureAutomationState(IEnumerable<FarmListStatusRow> rows, DateTimeOffset? analyzedAt = null)
    {
        var realRows = rows.Where(FarmListsViewModel.IsRealRow).ToList();
        lock (_stateLock)
        {
            _automationSnapshot = new FarmListsAutomationSnapshot(
                realRows.Count,
                realRows
                    .Where(row => row.IsEnabled && !string.IsNullOrWhiteSpace(row.Name))
                    .Select(row => row.Name)
                    .ToList(),
                realRows
                    .Where(row => !string.IsNullOrWhiteSpace(row.Name))
                    .Select(row => row.Name)
                    .ToList(),
                analyzedAt ?? _automationSnapshot.LastAnalysisAt);
        }
    }

    public bool CanReuseRecentAnalysis(DateTimeOffset now)
    {
        var lastAnalysisAt = AutomationSnapshot.LastAnalysisAt;
        return lastAnalysisAt != DateTimeOffset.MinValue
            && lastAnalysisAt >= now - RecentAnalysisWindow;
    }

    public void InvalidateAnalysis()
    {
        lock (_stateLock)
        {
            _automationSnapshot = _automationSnapshot with { LastAnalysisAt = DateTimeOffset.MinValue };
        }
    }

    public void ResetProjection()
    {
        lock (_stateLock)
        {
            _automationSnapshot = FarmListsAutomationSnapshot.Empty;
        }
    }

    public FarmListsProjection ProjectOverview(
        IReadOnlyList<FarmListOverview> lists,
        BotOptions options,
        IReadOnlyDictionary<string, string> villageCoordinates,
        FarmListsPresentationOptions presentation)
    {
        var defaultMin = FarmingDefaults.NormalizeDispatchDelayMinMinutes(
            options.ContinuousFarmDispatchDelayMinMinutes);
        var defaultMax = Math.Max(
            defaultMin,
            FarmingDefaults.NormalizeDispatchDelayMaxMinutes(options.ContinuousFarmDispatchDelayMaxMinutes));
        var selectedNames = options.ContinuousFarmListNames
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIds = options.ContinuousFarmListIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dispatchStates = LoadDispatchStates();
        var mergedByKey = new Dictionary<string, MergedFarmList>(StringComparer.OrdinalIgnoreCase);
        var orderedKeys = new List<string>();
        var analyzedCoordinates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incompleteReads = new List<string>();

        foreach (var list in lists)
        {
            if (list is null)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(list.Name) ? "Farm list" : list.Name.Trim();
            var listId = string.IsNullOrWhiteSpace(list.ListId) ? null : list.ListId.Trim();
            var villageName = string.IsNullOrWhiteSpace(list.VillageName) ? null : list.VillageName.Trim();
            var mergeKey = listId ?? name;
            var coordinates = (list.FarmCoordinates ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            analyzedCoordinates.UnionWith(coordinates);

            var total = Math.Max(0, list.TotalFarmCount);
            if (total > 0 && coordinates.Count < total)
            {
                incompleteReads.Add($"'{name}' {coordinates.Count}/{total}");
            }

            if (!mergedByKey.TryGetValue(mergeKey, out var existing))
            {
                orderedKeys.Add(mergeKey);
                mergedByKey[mergeKey] = new MergedFarmList(
                    name,
                    villageName,
                    list.VillageIndex is >= 0 ? list.VillageIndex : null,
                    Math.Max(0, list.ActiveFarmCount),
                    total,
                    list.RemainingSeconds is > 0 ? list.RemainingSeconds : null,
                    listId,
                    list.Capacity,
                    coordinates);
                continue;
            }

            mergedByKey[mergeKey] = existing with
            {
                VillageName = existing.VillageName ?? villageName,
                VillageIndex = existing.VillageIndex ?? (list.VillageIndex is >= 0 ? list.VillageIndex : null),
                Active = Math.Max(existing.Active, Math.Max(0, list.ActiveFarmCount)),
                Total = Math.Max(existing.Total, total),
                RemainingSeconds = existing.RemainingSeconds is > 0
                    ? existing.RemainingSeconds
                    : list.RemainingSeconds is > 0 ? list.RemainingSeconds : null,
                ListId = string.IsNullOrWhiteSpace(existing.ListId) ? listId : existing.ListId,
                Capacity = existing.Capacity ?? list.Capacity,
                Coordinates = existing.Coordinates.Concat(coordinates).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }

        var initializedIntervals = InitializeDispatchIntervals(
            orderedKeys.Select(key => mergedByKey[key]),
            dispatchStates,
            defaultMin,
            defaultMax);
        if (initializedIntervals > 0)
        {
            SaveDispatchStates(dispatchStates);
            log($"[farm-list] initialized {initializedIntervals} list interval(s) from the shared default {defaultMin}-{defaultMax} minutes.");
        }

        var hasSelection = selectedNames.Count > 0 || selectedIds.Count > 0;
        var rows = orderedKeys
            .Take(MaximumVisibleLists)
            .Select(key =>
            {
                var value = mergedByKey[key];
                dispatchStates.TryGetValue(FarmListDispatchStateStore.CreateKey(value.ListId, value.Name), out var state);
                var villageName = value.VillageName ?? string.Empty;
                return new FarmListStatusRow
                {
                    Name = value.Name,
                    VillageName = villageName,
                    VillageOrdinal = value.VillageIndex ?? -1,
                    VillageHeaderText = BuildVillageHeader(villageName, villageCoordinates),
                    ListId = value.ListId,
                    ActiveFarmCount = value.Active,
                    TotalFarmCount = value.Total,
                    Capacity = value.Capacity,
                    IsEnabled = !hasSelection
                        || (value.ListId is not null && selectedIds.Contains(value.ListId))
                        || selectedNames.Contains(value.Name),
                    RemainingSeconds = value.RemainingSeconds,
                    LastSentAtUtc = state?.LastSentAtUtc,
                    NextSendAtUtc = state?.NextSendAtUtc,
                    IntervalMinMinutesText = state?.IntervalMinMinutes?.ToString() ?? defaultMin.ToString(),
                    IntervalMaxMinutesText = state?.IntervalMaxMinutes?.ToString() ?? defaultMax.ToString(),
                    LastSendFailed = state?.Failed == true,
                    ShowLastSentTimer = presentation.ShowLastSentTimer,
                    LastSentLimitEnabled = presentation.LastSentLimitEnabled,
                    LastSentLimitHours = presentation.LastSentLimitHours,
                };
            })
            .ToList();

        if (mergedByKey.Count > MaximumVisibleLists)
        {
            log($"Farm list UI limited to {MaximumVisibleLists} rows (detected {mergedByKey.Count}).");
        }

        if (incompleteReads.Count > 0)
        {
            log($"[farm-list] WARNING: {incompleteReads.Count} farm list(s) not fully read "
                + $"({string.Join(", ", incompleteReads)}). Duplicate protection may miss those farms — re-run Analyze.");
        }

        var capacitiesByName = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            capacitiesByName[row.Name] = row.Capacity;
        }

        return new FarmListsProjection(
            rows,
            analyzedCoordinates,
            incompleteReads,
            capacitiesByName,
            mergedByKey.Count);
    }

    public async Task SaveSnapshotAsync(
        IReadOnlyList<FarmListOverview> lists,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = AccountStoragePaths.FarmListsSnapshotPath(projectRoot, activeAccountName());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = new FarmListsSnapshotDto
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Lists = lists.Select(FarmListSnapshotEntryDto.FromOverview).ToList(),
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload), cancellationToken);
            var coordinateCount = lists
                .SelectMany(item => item.FarmCoordinates ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            log($"[farm-list] saved analysis snapshot with {coordinateCount} unique coordinate(s).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"Could not save farm list analysis snapshot: {ex.Message}");
        }
    }

    public Task<IReadOnlyList<FarmListOverview>?> LoadFreshSnapshotAsync(CancellationToken cancellationToken)
        => LoadSnapshotAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), rebaseTimers: false, cancellationToken);

    public Task<IReadOnlyList<FarmListOverview>?> LoadRestoredSnapshotAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => LoadSnapshotAsync(now, maximumAge: null, rebaseTimers: true, cancellationToken);

    private async Task<IReadOnlyList<FarmListOverview>?> LoadSnapshotAsync(
        DateTimeOffset now,
        TimeSpan? maximumAge,
        bool rebaseTimers,
        CancellationToken cancellationToken)
    {
        var path = AccountStoragePaths.FarmListsSnapshotPath(projectRoot, activeAccountName());
        if (!File.Exists(path))
        {
            return null;
        }

        FarmListsSnapshotDto? snapshot;
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            snapshot = JsonSerializer.Deserialize<FarmListsSnapshotDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"Farm list snapshot could not be parsed: {ex.Message}");
            return null;
        }

        if (snapshot?.Lists is null
            || snapshot.Lists.Count == 0
            || (maximumAge is not null
                && (snapshot.CapturedAtUtc is null || now - snapshot.CapturedAtUtc.Value > maximumAge.Value)))
        {
            return null;
        }

        var elapsedSeconds = rebaseTimers && snapshot.CapturedAtUtc is { } capturedAt
            ? Math.Max(0, (int)(now - capturedAt).TotalSeconds)
            : 0;
        return snapshot.Lists
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry!.ToOverview(elapsedSeconds))
            .ToList();
    }

    internal static string BuildVillageHeader(
        string villageName,
        IReadOnlyDictionary<string, string> villageCoordinates)
        => !string.IsNullOrWhiteSpace(villageName)
            && villageCoordinates.TryGetValue(villageName, out var coordinates)
                ? $"{villageName} {coordinates}"
                : villageName;

    private Dictionary<string, FarmListDispatchState> LoadDispatchStates()
    {
        try
        {
            return FarmListDispatchStateStore.Load(projectRoot, activeAccountName())
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            log($"Could not load farm list dispatch status: {ex.Message}");
            return new Dictionary<string, FarmListDispatchState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private int InitializeDispatchIntervals(
        IEnumerable<MergedFarmList> lists,
        IDictionary<string, FarmListDispatchState> states,
        int defaultMin,
        int defaultMax)
    {
        var initializedCount = 0;
        foreach (var list in lists)
        {
            var key = FarmListDispatchStateStore.CreateKey(list.ListId, list.Name);
            states.TryGetValue(key, out var previous);
            var initialized = FarmListDispatchStateStore.WithDefaultInterval(
                previous ?? new FarmListDispatchState(null, Failed: false),
                defaultMin,
                defaultMax);
            states[key] = initialized;
            if (previous is null || initialized != previous)
            {
                initializedCount++;
            }
        }

        return initializedCount;
    }

    private void SaveDispatchStates(IReadOnlyDictionary<string, FarmListDispatchState> states)
    {
        try
        {
            FarmListDispatchStateStore.Save(projectRoot, activeAccountName(), states);
        }
        catch (Exception ex)
        {
            log($"Could not save default farm list intervals: {ex.Message}");
        }
    }

    private sealed record MergedFarmList(
        string Name,
        string? VillageName,
        int? VillageIndex,
        int Active,
        int Total,
        int? RemainingSeconds,
        string? ListId,
        int? Capacity,
        IReadOnlyList<string> Coordinates);

    private sealed class FarmListsSnapshotDto
    {
        public DateTimeOffset? CapturedAtUtc { get; init; }
        public List<FarmListSnapshotEntryDto>? Lists { get; init; }
    }

    private sealed class FarmListSnapshotEntryDto
    {
        public string? Name { get; init; }
        public string? VillageName { get; init; }
        public int? VillageIndex { get; init; }
        public int ActiveFarmCount { get; init; }
        public int TotalFarmCount { get; init; }
        public int? RemainingSeconds { get; init; }
        public string? ListId { get; init; }
        public int? Capacity { get; init; }
        public IReadOnlyList<string>? FarmCoordinates { get; init; }

        public static FarmListSnapshotEntryDto FromOverview(FarmListOverview overview) => new()
        {
            Name = overview.Name,
            VillageName = overview.VillageName,
            VillageIndex = overview.VillageIndex,
            ActiveFarmCount = overview.ActiveFarmCount,
            TotalFarmCount = overview.TotalFarmCount,
            RemainingSeconds = overview.RemainingSeconds,
            ListId = overview.ListId,
            Capacity = overview.Capacity,
            FarmCoordinates = overview.FarmCoordinates,
        };

        public FarmListOverview ToOverview(int elapsedSeconds)
        {
            var remaining = RemainingSeconds is > 0
                ? Math.Max(0, RemainingSeconds.Value - elapsedSeconds)
                : RemainingSeconds;
            return new FarmListOverview(
                Name: Name!,
                ActiveFarmCount: ActiveFarmCount,
                TotalFarmCount: TotalFarmCount,
                RemainingSeconds: remaining is > 0 ? remaining : null,
                ListId: string.IsNullOrWhiteSpace(ListId) ? null : ListId,
                Capacity: Capacity,
                FarmCoordinates: FarmCoordinates ?? [],
                VillageName: string.IsNullOrWhiteSpace(VillageName) ? null : VillageName,
                VillageIndex: VillageIndex);
        }
    }

    public Task<bool> ReadAndPersistGoldClubStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => client.ReadAndPersistGoldClubStatusAsync(options, log, cancellationToken);

    public Task<IReadOnlyList<FarmListOverview>> ReadOverviewAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => client.ReadOverviewAsync(options, log, cancellationToken);

    public Task<FarmAddBatchResult> AddFarmsAsync(
        BotOptions options, string farmListName, string troopType, int troopCount, int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates, bool useDefaultTroops, FarmTargetProtectionContext? protection, Action<string> log,
        IProgress<FarmAddProgress>? progress, CancellationToken cancellationToken)
        => client.AddFarmsAsync(options, farmListName, troopType, troopCount, requestedCount,
            coordinates, useDefaultTroops, protection, log, progress, cancellationToken);

    public Task<FarmTargetIdentity> ReadTargetProtectionIdentityAsync(
        BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => client.ReadTargetProtectionIdentityAsync(options, log, cancellationToken);

    public Task<FarmListCreateBatchResult> CreateListsAsync(
        BotOptions options, FarmListCreateRequest request, Action<string> log,
        IProgress<FarmListCreateProgress>? progress, CancellationToken cancellationToken)
        => client.CreateListsAsync(options, request, log, progress, cancellationToken);

    public Task<int?> SendOneAsync(BotOptions options, string farmListName, Action<string> log, CancellationToken cancellationToken)
        => client.SendOneAsync(options, farmListName, log, cancellationToken);

    public Task<int> SendSelectedAsync(
        BotOptions options, IReadOnlyCollection<string> names, IReadOnlyCollection<string> ids,
        Action<string> log, CancellationToken cancellationToken)
        => client.SendSelectedAsync(options, names, ids, log, cancellationToken);

    public Task<int> SendAllAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => client.SendAllAsync(options, log, cancellationToken);

    public FarmingSettingsSaveResult SaveSettings(FarmingPanelSettings settings)
    {
        var config = configStore.Load();
        var redDestination = settings.SelectedRedDestination;
        var yellowDestination = settings.SelectedYellowDestination;
        var redMoveEnabled = settings.DeactivateRedLosses && settings.MoveRedLosses && redDestination is not null;
        var yellowMoveEnabled = settings.DeactivateYellowLosses && settings.MoveYellowLosses && yellowDestination is not null;

        var sendMode = FarmingDefaults.NormalizeSendMode(settings.SendMode);
        config[BotOptionPayloadKeys.ContinuousFarmSendMode] = sendMode;
        config[BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinMinutes] = settings.DispatchDelayMinMinutes;
        config[BotOptionPayloadKeys.ContinuousFarmDispatchDelayMaxMinutes] = settings.DispatchDelayMaxMinutes;
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateRedLosses] = settings.DeactivateRedLosses;
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateYellowLosses] = settings.DeactivateYellowLosses;
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateRedOasisLosses] = settings.DeactivateRedOasisLosses;
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateYellowOasisLosses] = settings.DeactivateYellowOasisLosses;
        config[BotOptionPayloadKeys.ContinuousFarmMoveRedLosses] = redMoveEnabled;
        config[BotOptionPayloadKeys.ContinuousFarmMoveYellowLosses] = yellowMoveEnabled;
        SaveDestination(config, true, redDestination);
        SaveDestination(config, false, yellowDestination);

        // Keep legacy aggregate keys coherent for older queued payloads while all new behavior reads the split keys.
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateLosses] = settings.DeactivateRedLosses || settings.DeactivateYellowLosses;
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses] = settings.DeactivateRedOasisLosses || settings.DeactivateYellowOasisLosses;
        config[BotOptionPayloadKeys.ContinuousFarmMoveLosses] = redMoveEnabled || yellowMoveEnabled;
        configStore.Save(config);

        return new FarmingSettingsSaveResult(
            sendMode,
            settings.DispatchDelayMinMinutes,
            settings.DispatchDelayMaxMinutes,
            redMoveEnabled,
            yellowMoveEnabled,
            redDestination?.Name,
            yellowDestination?.Name);
    }

    public void SaveDestinationBaseName(bool isRed, string name)
    {
        var config = configStore.Load();
        config[isRed
            ? BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName
            : BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationBaseName] = name;
        configStore.Save(config);
    }

    private static void SaveDestination(System.Text.Json.Nodes.JsonObject config, bool isRed, FarmLossDestinationOption? destination)
    {
        var idKey = isRed ? BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListId : BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListId;
        var nameKey = isRed ? BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListName : BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListName;
        var baseNameKey = isRed ? BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName : BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationBaseName;
        var existingId = config[idKey]?.GetValue<string>()
            ?? config[BotOptionPayloadKeys.ContinuousFarmLossDestinationListId]?.GetValue<string>()
            ?? string.Empty;
        var priorBaseName = config[baseNameKey]?.GetValue<string>()
            ?? config[BotOptionPayloadKeys.ContinuousFarmLossDestinationBaseName]?.GetValue<string>();
        var changedByUser = destination is not null
            && !string.Equals(existingId, destination.ListId, StringComparison.OrdinalIgnoreCase);

        config[idKey] = destination?.ListId ?? string.Empty;
        config[nameKey] = destination?.Name ?? string.Empty;
        config[baseNameKey] = changedByUser || string.IsNullOrWhiteSpace(priorBaseName)
            ? destination?.Name ?? string.Empty
            : priorBaseName;
    }
}

public sealed record FarmListsPresentationOptions(
    bool ShowLastSentTimer,
    bool LastSentLimitEnabled,
    int LastSentLimitHours);

public sealed record FarmListsProjection(
    IReadOnlyList<FarmListStatusRow> Rows,
    IReadOnlySet<string> AnalyzedCoordinates,
    IReadOnlyList<string> IncompleteReads,
    IReadOnlyDictionary<string, int?> CapacitiesByName,
    int DetectedCount);

public sealed record FarmListsAutomationSnapshot(
    int TotalCount,
    IReadOnlyList<string> SelectedNames,
    IReadOnlyList<string> AvailableNames,
    DateTimeOffset LastAnalysisAt)
{
    public static FarmListsAutomationSnapshot Empty { get; } = new(
        0,
        Array.Empty<string>(),
        Array.Empty<string>(),
        DateTimeOffset.MinValue);

    public bool NeedsAnalysis => TotalCount <= 0
        || SelectedNames.Count <= 0
        || SelectedNames.Any(name => !AvailableNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        || LastAnalysisAt == DateTimeOffset.MinValue;
}

public sealed record FarmingPanelSettings(
    string SendMode,
    int DispatchDelayMinMinutes,
    int DispatchDelayMaxMinutes,
    bool DeactivateRedLosses,
    bool DeactivateYellowLosses,
    bool DeactivateRedOasisLosses,
    bool DeactivateYellowOasisLosses,
    bool MoveRedLosses,
    bool MoveYellowLosses,
    FarmLossDestinationOption? SelectedRedDestination,
    FarmLossDestinationOption? SelectedYellowDestination);

public sealed record FarmingSettingsSaveResult(
    string SendMode,
    int DelayMinMinutes,
    int DelayMaxMinutes,
    bool MoveRedLossesEnabled,
    bool MoveYellowLossesEnabled,
    string? RedDestinationName,
    string? YellowDestinationName);
