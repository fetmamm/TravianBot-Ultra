using TbotUltra.Core.Configuration;
using TbotUltra.Core.Farming;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Owns the Farm Lists desktop workflow and its account-scoped settings.
/// Browser work crosses the single <see cref="IFarmListsBrowserAdapter"/> seam.
/// </summary>
public sealed class FarmListsWorkflow(
    IFarmListsBrowserAdapter client,
    IFarmListsAutomationAdapter automation,
    BotConfigStore configStore,
    string projectRoot,
    Func<string> activeAccountName,
    Action<string> log)
{
    public const int MaximumVisibleLists = 120;
    private static readonly TimeSpan RecentAnalysisWindow = TimeSpan.FromMinutes(5);
    private readonly object _stateLock = new();
    private FarmListsAutomationSnapshot _automationSnapshot = FarmListsAutomationSnapshot.Empty;
    private FarmListsProjection _currentProjection = FarmListsProjection.Empty;

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

    public async Task<FarmListsAnalysisResult> AnalyzeAsync(
        BotOptions options,
        CancellationToken cancellationToken)
    {
        var goldClubEnabled = await client.ReadAndPersistGoldClubStatusAsync(options, log, cancellationToken);
        if (!goldClubEnabled)
        {
            return new FarmListsAnalysisResult(false, []);
        }

        var lists = await client.ReadOverviewAsync(options, log, cancellationToken) ?? [];
        await SaveSnapshotAsync(lists, cancellationToken);
        return new FarmListsAnalysisResult(true, lists);
    }

    public async Task<FarmListCreateBatchResult> CreateAfterAnalysisAsync(
        BotOptions options,
        FarmListCreateRequest request,
        IProgress<FarmListCreateProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new FarmListCreateProgress("Analyzing farmlists", 0, request.Names.Count));
        log("[farm-list-create] analyzing current farmlist page before creation.");
        var analysis = await AnalyzeAsync(options, cancellationToken);
        if (!analysis.IsAvailable)
        {
            throw new InvalidOperationException("Gold Club is not active.");
        }

        log($"[farm-list-create] requested={request.Names.Count}, village='{request.VillageName}', "
            + $"default={request.TroopCount} {request.TroopType}.");
        return await client.CreateListsAsync(options, request, log, progress, cancellationToken);
    }

    public async Task<OfficialFarmAddRunResult> RunAddPlansAsync(
        BotOptions options,
        IReadOnlyList<OfficialFarmAddPlan> plans,
        bool useDefaultTroops,
        string troopType,
        int troopCount,
        FarmTargetProtectionContext protection,
        IProgress<FarmAddProgress> progress,
        CancellationToken cancellationToken)
    {
        var requested = plans.Sum(plan => plan.DesiredCount);
        var processed = 0;
        var added = 0;
        var duplicates = 0;
        var failed = 0;
        var notFound = 0;
        var occupiedSkipped = 0;
        var excludedPlayers = 0;
        var excludedAlliances = 0;
        var identityUnavailable = 0;
        var invalidCoordinates = new List<FarmCoordinate>();

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processedBeforeList = processed;
            var addedBeforeList = added;
            var notFoundBeforeList = notFound;
            var occupiedBeforeList = occupiedSkipped;
            var excludedPlayersBeforeList = excludedPlayers;
            var excludedAlliancesBeforeList = excludedAlliances;
            var identityUnavailableBeforeList = identityUnavailable;
            var aggregateProgress = new Progress<FarmAddProgress>(value =>
            {
                progress.Report(new FarmAddProgress(
                    value.FarmListName,
                    processedBeforeList + value.ProcessedCount,
                    requested,
                    addedBeforeList + value.AddedCount,
                    notFoundBeforeList + value.NotFoundCount,
                    value.InvalidCoordinate,
                    occupiedBeforeList + value.OccupiedOasisSkippedCount,
                    excludedPlayersBeforeList + value.ExcludedPlayerCount,
                    excludedAlliancesBeforeList + value.ExcludedAllianceCount,
                    identityUnavailableBeforeList + value.IdentityUnavailableCount));
            });

            log($"Add farms from Travco: target='{plan.TargetName}', requested={plan.DesiredCount}, "
                + $"candidates={plan.Coordinates.Count}, "
                + $"troops={(useDefaultTroops ? "default" : $"{troopCount} {troopType}")}.");
            var result = await client.AddFarmsAsync(
                options,
                plan.TargetName,
                troopType,
                troopCount,
                plan.DesiredCount,
                plan.Coordinates,
                useDefaultTroops,
                protection,
                log,
                aggregateProgress,
                cancellationToken);
            processed += result.AttemptedCount;
            added += result.AddedCount;
            duplicates += result.AlreadyInListCount;
            failed += result.FailedCount;
            notFound += result.NotFoundCount;
            occupiedSkipped += result.OccupiedOasisSkippedCount;
            excludedPlayers += result.ExcludedPlayerCount;
            excludedAlliances += result.ExcludedAllianceCount;
            identityUnavailable += result.IdentityUnavailableCount;
            invalidCoordinates.AddRange(result.InvalidCoordinates ?? []);
            log($"Finished '{plan.TargetName}': added={result.AddedCount}, "
                + $"duplicates={result.AlreadyInListCount}, invalid={result.NotFoundCount}, "
                + $"occupiedSkipped={result.OccupiedOasisSkippedCount}, "
                + $"excludedPlayers={result.ExcludedPlayerCount}, excludedAlliances={result.ExcludedAllianceCount}, "
                + $"identityUnavailable={result.IdentityUnavailableCount}, failed={result.FailedCount}.");
        }

        return new OfficialFarmAddRunResult(
            requested,
            added,
            duplicates,
            failed,
            invalidCoordinates.Distinct().ToList(),
            OccupiedSkipped: occupiedSkipped,
            ExcludedPlayers: excludedPlayers,
            ExcludedAlliances: excludedAlliances,
            IdentityUnavailable: identityUnavailable);
    }

    public async Task<FarmListsAutomationResume> PauseAutomationAsync(CancellationToken cancellationToken)
    {
        var resumeContinuous = automation.ContinuousLoopRunning || automation.StartContinuousAfterQueueStop;
        var resumeQueue = !resumeContinuous && automation.AutoQueueRunning;
        if (!resumeContinuous && !resumeQueue)
        {
            log("[farm-list] bot already paused; starting loss destination setup.");
            return FarmListsAutomationResume.None;
        }

        automation.ClearPendingRestarts();
        automation.RequestStopAfterCurrentAction();
        automation.UpdateExecutionIndicator();
        log("[farm-list] pause requested; waiting for the current bot action to finish.");

        while (automation.AutoQueueRunning || automation.ContinuousLoopRunning || automation.UiBusy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken);
        }

        await Task.Delay(100, cancellationToken);
        log("[farm-list] automation paused; loss destination setup has priority.");
        return new FarmListsAutomationResume(resumeContinuous, resumeQueue);
    }

    public async Task ResumeAutomationAsync(FarmListsAutomationResume resume)
    {
        if (resume == FarmListsAutomationResume.None)
        {
            return;
        }

        if (!automation.SessionAvailable)
        {
            log("[farm-list] automation was not resumed because the session is unavailable.");
            return;
        }

        if (resume.ContinuousLoop && !automation.ContinuousLoopRunning)
        {
            log("[farm-list] resuming continuous loop after loss destination setup.");
            automation.StartContinuousLoop();
            return;
        }

        if (resume.AutoQueue && !automation.AutoQueueRunning)
        {
            log("[farm-list] resuming queue auto-run after loss destination setup.");
            await automation.StartAutoQueueAsync();
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

    public void SaveSelection(IEnumerable<FarmListStatusRow> rows)
    {
        var enabledRows = rows
            .Where(FarmListsViewModel.IsRealRow)
            .Where(row => row.IsEnabled)
            .ToList();
        var selectedNames = enabledRows
            .Select(row => row.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedIds = enabledRows
            .Select(row => row.ListId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        CaptureAutomationState(rows);
        try
        {
            var config = configStore.Load();
            config[BotOptionPayloadKeys.ContinuousFarmListNames] = new JsonArray(
                selectedNames.Select(name => JsonValue.Create(name)!).ToArray());
            config[BotOptionPayloadKeys.ContinuousFarmListIds] = new JsonArray(
                selectedIds.Select(id => JsonValue.Create(id)!).ToArray());
            configStore.Save(config);
        }
        catch (Exception ex)
        {
            log($"Could not save selected farmlists: {ex.Message}");
        }

        var selection = new FarmingPayload(selectedNames, selectedIds);
        var updatedCount = 0;
        foreach (var item in automation.QueueItems)
        {
            if (!string.Equals(item.TaskName, "send_farmlists", StringComparison.OrdinalIgnoreCase)
                || item.Status != QueueStatus.Pending
                || !item.Payload.TryGetValue(BotOptionPayloadKeys.TargetVillageKey, out var villageKey)
                || string.IsNullOrWhiteSpace(villageKey))
            {
                continue;
            }

            var updatedPayload = selection.ApplySelectionTo(item.Payload);
            if (!ContinuousLoopSelector.PayloadEquals(item.Payload, updatedPayload)
                && automation.UpdateDeferredQueueItem(item.Id, updatedPayload))
            {
                updatedCount++;
            }
        }

        if (updatedCount > 0)
        {
            log($"[farm-list] applied the updated toggle selection to {updatedCount} queued automatic farm-list send(s).");
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
            _currentProjection = FarmListsProjection.Empty;
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

        var projection = new FarmListsProjection(
            rows,
            analyzedCoordinates,
            incompleteReads,
            capacitiesByName,
            mergedByKey.Count);
        lock (_stateLock)
        {
            _currentProjection = projection;
        }

        return projection;
    }

    public OfficialAddFarmsLoadResult BuildAddFarmsLoadResult(
        IReadOnlyList<TravcoListStore.TravcoSavedList> sourceLists,
        FarmTargetIdentity ownIdentity)
    {
        if (sourceLists.Count == 0)
        {
            return new OfficialAddFarmsLoadResult(
                false,
                "No saved Travco lists with selected farms were found.",
                [],
                [],
                new HashSet<string>());
        }

        FarmListsProjection projection;
        lock (_stateLock)
        {
            projection = _currentProjection;
        }

        var targets = projection.Rows
            .Select(row => new FarmListSelectionOption
            {
                Name = row.Name,
                ActiveFarmCount = row.ActiveFarmCount,
                TotalFarmCount = row.TotalFarmCount,
                Capacity = row.Capacity,
            })
            .ToList();
        return new OfficialAddFarmsLoadResult(
            true,
            null,
            sourceLists,
            targets,
            new HashSet<string>(projection.AnalyzedCoordinates, StringComparer.OrdinalIgnoreCase),
            projection.IncompleteReads,
            ownIdentity);
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

    public bool RecordDispatch(FarmListStatusRow row, bool succeeded, BotOptions options)
    {
        try
        {
            var state = FarmListDispatchStateStore.Update(
                projectRoot,
                activeAccountName(),
                DispatchKey(row),
                previous =>
                {
                    previous = FarmListDispatchStateStore.WithDefaultInterval(
                        previous ?? new FarmListDispatchState(null, Failed: false),
                        FarmingDefaults.NormalizeDispatchDelayMinMinutes(options.ContinuousFarmDispatchDelayMinMinutes),
                        FarmingDefaults.NormalizeDispatchDelayMaxMinutes(options.ContinuousFarmDispatchDelayMaxMinutes));
                    if (!succeeded)
                    {
                        return previous with { Failed = true };
                    }

                    var sentAtUtc = DateTimeOffset.UtcNow;
                    return previous with
                    {
                        LastSentAtUtc = sentAtUtc,
                        Failed = false,
                        NextSendAtUtc = sentAtUtc.AddSeconds(CalculateDispatchDelaySeconds(previous, options)),
                    };
                });
            row.LastSentAtUtc = state.LastSentAtUtc;
            row.NextSendAtUtc = state.NextSendAtUtc;
            row.LastSendFailed = state.Failed;
            return succeeded;
        }
        catch (Exception ex)
        {
            log($"Could not save farm list dispatch status: {ex.Message}");
            return false;
        }
    }

    public bool PersistDispatchInterval(
        FarmListStatusRow row,
        int minMinutes,
        int maxMinutes,
        BotOptions options)
    {
        try
        {
            var state = FarmListDispatchStateStore.Update(
                projectRoot,
                activeAccountName(),
                DispatchKey(row),
                previous =>
                {
                    previous ??= new FarmListDispatchState(null, Failed: false);
                    var updated = previous with
                    {
                        IntervalMinMinutes = minMinutes,
                        IntervalMaxMinutes = maxMinutes,
                    };
                    return updated with
                    {
                        NextSendAtUtc = updated.LastSentAtUtc?.AddSeconds(
                            CalculateDispatchDelaySeconds(updated, options)),
                    };
                });
            row.NextSendAtUtc = state.NextSendAtUtc;
            log($"[farm-list] '{row.Name}' dispatch interval set to {minMinutes}-{maxMinutes} minutes.");
            return true;
        }
        catch (Exception ex)
        {
            log($"Could not save farm list dispatch interval: {ex.Message}");
            return false;
        }
    }

    public bool ReconcileDispatches(
        IEnumerable<FarmListStatusRow> rows,
        IReadOnlyCollection<string> attemptedKeys,
        BotOptions options)
    {
        var successfulDispatch = false;
        foreach (var row in rows.Where(FarmListsViewModel.IsRealRow))
        {
            if (!attemptedKeys.Contains(DispatchKey(row)))
            {
                continue;
            }

            var succeeded = FarmListDispatchStateStore.IsSuccessfulDispatch(
                sendActionCompleted: true,
                row.RemainingSeconds);
            successfulDispatch |= RecordDispatch(row, succeeded, options);
        }

        return successfulDispatch;
    }

    public static string DispatchKey(FarmListStatusRow row)
        => FarmListDispatchStateStore.CreateKey(row.ListId, row.Name);

    public AddFarmsProtectionPreferences LoadTargetProtectionPreferences()
    {
        try
        {
            var config = configStore.Load();
            var excludeOwnAlliance = !config.TryGetPropertyValue(BotOptionPayloadKeys.AddFarmsExcludeOwnAlliance, out var ownNode)
                || ownNode is null
                || ownNode.GetValue<bool>();
            return new AddFarmsProtectionPreferences(
                excludeOwnAlliance,
                config[BotOptionPayloadKeys.AddFarmsExcludedPlayers]?.GetValue<string>() ?? string.Empty,
                config[BotOptionPayloadKeys.AddFarmsExcludedAlliances]?.GetValue<string>() ?? string.Empty);
        }
        catch (Exception ex)
        {
            log($"[farm-list] Could not load Add farms protection settings: {ex.Message}");
            return new AddFarmsProtectionPreferences(true, string.Empty, string.Empty);
        }
    }

    public FarmTargetProtectionPreparation PrepareTargetProtection(
        FarmTargetIdentity identity,
        AddFarmsProtectionPreferences requested)
    {
        if (!identity.IsResolved || string.IsNullOrWhiteSpace(identity.PlayerName))
        {
            return FarmTargetProtectionPreparation.Unavailable(
                "Your player identity could not be read from Travian. Close this dialog and try again.");
        }

        var preferences = requested with
        {
            ExcludeOwnAlliance = requested.ExcludeOwnAlliance && !string.IsNullOrWhiteSpace(identity.Alliance),
        };
        try
        {
            var config = configStore.Load();
            config[BotOptionPayloadKeys.AddFarmsExcludeOwnAlliance] = preferences.ExcludeOwnAlliance;
            config[BotOptionPayloadKeys.AddFarmsExcludedPlayers] = preferences.ExcludedPlayers;
            config[BotOptionPayloadKeys.AddFarmsExcludedAlliances] = preferences.ExcludedAlliances;
            configStore.Save(config);
            log("[farm-list] Saved Add farms target-protection settings.");
        }
        catch (Exception ex)
        {
            log($"[farm-list] Could not save Add farms protection settings: {ex.Message}");
        }

        return new FarmTargetProtectionPreparation(
            new FarmTargetProtectionContext(
                identity.PlayerName,
                identity.Alliance,
                preferences.ExcludeOwnAlliance,
                ParseProtectionList(preferences.ExcludedPlayers),
                ParseProtectionList(preferences.ExcludedAlliances)),
            null);
    }

    internal static IReadOnlyList<string> ParseProtectionList(string? value)
        => (value ?? string.Empty)
            .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    private static int CalculateDispatchDelaySeconds(FarmListDispatchState state, BotOptions options)
    {
        var initialized = FarmListDispatchStateStore.WithDefaultInterval(
            state,
            FarmingDefaults.NormalizeDispatchDelayMinMinutes(options.ContinuousFarmDispatchDelayMinMinutes),
            FarmingDefaults.NormalizeDispatchDelayMaxMinutes(options.ContinuousFarmDispatchDelayMaxMinutes));
        var minMinutes = initialized.IntervalMinMinutes!.Value;
        var maxMinutes = initialized.IntervalMaxMinutes!.Value;
        return FarmingDefaults.CalculateDispatchDelaySeconds(minMinutes, Math.Max(minMinutes, maxMinutes));
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

    public Task<bool> IsGoldClubActiveAsync(BotOptions options, CancellationToken cancellationToken)
        => client.ReadAndPersistGoldClubStatusAsync(options, log, cancellationToken);

    public Task<IReadOnlyList<FarmListOverview>> ReadOverviewAsync(BotOptions options, CancellationToken cancellationToken)
        => client.ReadOverviewAsync(options, log, cancellationToken);

    public Task<FarmTargetIdentity> ReadTargetProtectionIdentityAsync(
        BotOptions options, CancellationToken cancellationToken)
        => client.ReadTargetProtectionIdentityAsync(options, log, cancellationToken);

    public Task<FarmListCreateBatchResult> CreateListsAsync(
        BotOptions options, FarmListCreateRequest request,
        IProgress<FarmListCreateProgress>? progress, CancellationToken cancellationToken)
        => client.CreateListsAsync(options, request, log, progress, cancellationToken);

    public Task<int?> SendOneAsync(BotOptions options, string farmListName, CancellationToken cancellationToken)
        => client.SendOneAsync(options, farmListName, log, cancellationToken);

    public Task<int> SendSelectedAsync(
        BotOptions options, IReadOnlyCollection<string> names, IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken)
        => client.SendSelectedAsync(options, names, ids, log, cancellationToken);

    public Task<int> SendAllAsync(BotOptions options, CancellationToken cancellationToken)
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

public sealed record FarmListsAnalysisResult(
    bool IsAvailable,
    IReadOnlyList<FarmListOverview> Lists);

public interface IFarmListsAutomationAdapter
{
    bool ContinuousLoopRunning { get; }
    bool StartContinuousAfterQueueStop { get; }
    bool AutoQueueRunning { get; }
    bool UiBusy { get; }
    bool SessionAvailable { get; }
    IReadOnlyList<QueueItem> QueueItems { get; }
    void ClearPendingRestarts();
    void RequestStopAfterCurrentAction();
    void UpdateExecutionIndicator();
    void StartContinuousLoop();
    Task StartAutoQueueAsync();
    bool UpdateDeferredQueueItem(Guid id, Dictionary<string, string> payload);
}

public sealed record FarmListsAutomationResume(bool ContinuousLoop, bool AutoQueue)
{
    public static FarmListsAutomationResume None { get; } = new(false, false);
}

public sealed record AddFarmsProtectionPreferences(
    bool ExcludeOwnAlliance,
    string ExcludedPlayers,
    string ExcludedAlliances);

public sealed record FarmTargetProtectionPreparation(
    FarmTargetProtectionContext? Context,
    string? FailureMessage)
{
    public bool IsAvailable => Context is not null;

    public static FarmTargetProtectionPreparation Unavailable(string message) => new(null, message);
}

public sealed record FarmListsProjection(
    IReadOnlyList<FarmListStatusRow> Rows,
    IReadOnlySet<string> AnalyzedCoordinates,
    IReadOnlyList<string> IncompleteReads,
    IReadOnlyDictionary<string, int?> CapacitiesByName,
    int DetectedCount)
{
    public static FarmListsProjection Empty { get; } = new(
        [],
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        [],
        new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase),
        0);
}

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
