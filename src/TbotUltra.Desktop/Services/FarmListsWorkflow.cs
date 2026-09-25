using TbotUltra.Core.Configuration;
using TbotUltra.Core.Farming;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using System.Text.RegularExpressions;

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
    Action<string> log,
    TimeSpan? automationStopCleanupTimeout = null)
{
    public const int MaximumVisibleLists = 120;
    private static readonly TimeSpan RecentAnalysisWindow = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _automationStopCleanupTimeout = automationStopCleanupTimeout
        is { } configuredTimeout && configuredTimeout > TimeSpan.Zero
            ? configuredTimeout
            : TimeSpan.FromSeconds(5);
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

    public async Task<FarmListsViewResult> AnalyzeAsync(
        FarmListsViewRequest request,
        CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeOverviewAsync(request.Options, cancellationToken);
        return CreateViewResult(request, analysis, analyzedAt: DateTimeOffset.UtcNow);
    }

    public async Task<FarmListsViewResult?> RestoreAsync(
        FarmListsViewRequest request,
        DateTimeOffset now,
        bool requireFreshSnapshot,
        CancellationToken cancellationToken = default)
    {
        var lists = await LoadSnapshotAsync(
            now,
            requireFreshSnapshot ? TimeSpan.FromMinutes(2) : null,
            rebaseTimers: !requireFreshSnapshot,
            cancellationToken);
        if (lists is null || lists.Count == 0)
        {
            return null;
        }

        var result = CreateViewResult(
            request,
            new FarmListsAnalysisResult(true, lists),
            analyzedAt: requireFreshSnapshot ? now : null);
        if (!requireFreshSnapshot)
        {
            // A restored process snapshot is presentation state, not a live analysis suitable for automation.
            InvalidateAnalysis();
        }

        return result;
    }

    public FarmListsViewResult ProjectCached(
        FarmListsViewRequest request,
        IReadOnlyList<FarmListOverview> lists)
    {
        var result = CreateViewResult(
            request,
            new FarmListsAnalysisResult(true, lists),
            analyzedAt: null);
        InvalidateAnalysis();
        return result;
    }

    private async Task<FarmListsAnalysisResult> AnalyzeOverviewAsync(
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

    internal async Task<FarmListsCreateResult> CreateAsync(
        FarmListsViewRequest viewRequest,
        FarmListCreateRequest request,
        IProgress<FarmListCreateProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new FarmListCreateProgress("Analyzing farmlists", 0, request.Names.Count));
        log("[farm-list-create] analyzing current farmlist page before creation.");
        var analysis = await AnalyzeOverviewAsync(viewRequest.Options, cancellationToken);
        if (!analysis.IsAvailable)
        {
            throw new InvalidOperationException("Gold Club is not active.");
        }

        log($"[farm-list-create] requested={request.Names.Count}, village='{request.VillageName}', "
            + $"default={request.TroopCount} {request.TroopType}.");
        var creation = await client.CreateListsAsync(
            viewRequest.Options,
            request,
            log,
            progress,
            cancellationToken);
        var refreshed = await AnalyzeAsync(viewRequest, cancellationToken);
        return new FarmListsCreateResult(creation, refreshed);
    }

    internal FarmListsCreateSession CreateCreateSession(FarmListsViewRequest viewRequest)
        => new(this, viewRequest);

    internal FarmListsAddSession CreateAddSession(
        FarmListsViewRequest viewRequest,
        Func<IReadOnlyList<TravcoListStore.TravcoSavedList>> loadSourceLists)
        => new(this, viewRequest, loadSourceLists, LoadTargetProtectionPreferences());

    public async Task<FarmLossDestinationCreationResult> CreateLossDestinationAsync(
        FarmListsViewRequest viewRequest,
        IReadOnlyList<VillageSelectionItem> villages,
        string fallbackTribe,
        FarmListLossColors lossColor,
        string listName,
        CancellationToken cancellationToken)
    {
        var options = viewRequest.Options;
        var village = villages.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(options.TargetVillageUrl)
                && string.Equals(item.Url, options.TargetVillageUrl, StringComparison.OrdinalIgnoreCase))
            ?? villages.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(options.TargetVillageName)
                && string.Equals(item.Name, options.TargetVillageName, StringComparison.OrdinalIgnoreCase))
            ?? villages.FirstOrDefault(item => item.IsCapital)
            ?? villages.FirstOrDefault();
        if (village is null)
        {
            throw new InvalidOperationException("Load at least one village before creating a loss farmlist.");
        }

        var tribe = TroopCatalog.IsKnownTribe(village.Tribe) ? village.Tribe : fallbackTribe;
        var troopType = TroopCatalog.ResolveTroopTypesForTribe(tribe).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(troopType))
        {
            throw new InvalidOperationException("Could not resolve a default troop type for the selected village.");
        }

        var villageIdMatch = Regex.Match(village.Url ?? string.Empty, @"[?&]newdid=(\d+)", RegexOptions.IgnoreCase);
        var request = new FarmListCreateRequest(
            [listName],
            village.Name,
            villageIdMatch.Success ? villageIdMatch.Groups[1].Value : null,
            troopType,
            1,
            OnlyCreateReportsWithLosses: options.FarmListOnlyCreateReportsWithLosses);
        var createResult = await client.CreateListsAsync(options, request, log, progress: null, cancellationToken);
        if (createResult.CreatedCount != 1
            || !createResult.CreatedNames.Contains(listName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Travian did not confirm creation of farmlist '{listName}'.");
        }

        var view = await AnalyzeAsync(viewRequest, cancellationToken);
        var analysis = new FarmListsAnalysisResult(view.IsAvailable, view.Lists);
        var verified = analysis.Lists.FirstOrDefault(item =>
            string.Equals(item.Name, listName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.VillageName, village.Name, StringComparison.OrdinalIgnoreCase));
        if (verified is null || string.IsNullOrWhiteSpace(verified.ListId))
        {
            throw new InvalidOperationException($"Created farmlist '{listName}' could not be verified after refresh.");
        }

        var destination = new FarmLossDestinationOption(
            verified.ListId.Trim(),
            verified.Name.Trim(),
            verified.VillageName?.Trim() ?? village.Name,
            Math.Max(0, verified.TotalFarmCount),
            verified.Capacity is > 0 ? verified.Capacity.Value : 100);
        SaveDestinationBaseName(lossColor, listName);
        return new FarmLossDestinationCreationResult(destination, view);
    }

    internal async Task<FarmListsAddRunResult> RunAddPlansAsync(
        FarmListsViewRequest viewRequest,
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
                viewRequest.Options,
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

        var run = new OfficialFarmAddRunResult(
            requested,
            added,
            duplicates,
            failed,
            invalidCoordinates.Distinct().ToList(),
            OccupiedSkipped: occupiedSkipped,
            ExcludedPlayers: excludedPlayers,
            ExcludedAlliances: excludedAlliances,
            IdentityUnavailable: identityUnavailable);
        var refreshed = await AnalyzeAsync(viewRequest, cancellationToken);
        return new FarmListsAddRunResult(run, refreshed);
    }

    public async Task<bool> DispatchOneAsync(
        BotOptions options,
        FarmListStatusRow row,
        CancellationToken cancellationToken)
    {
        try
        {
            var timerSeconds = await client.SendOneAsync(options, row.Name, log, cancellationToken);
            row.RemainingSeconds = timerSeconds is > 0 ? timerSeconds : null;
            return RecordDispatch(row, succeeded: true, options);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            RecordDispatch(row, succeeded: false, options);
            throw;
        }
    }

    private async Task<FarmListBatchDispatchResult> DispatchManyAsync(
        BotOptions options,
        IReadOnlyList<FarmListStatusRow> rows,
        bool enabledOnly,
        CancellationToken cancellationToken)
    {
        var attemptedKeys = GetReadyDispatchKeys(rows, enabledOnly);
        try
        {
            int sentCount;
            if (enabledOnly)
            {
                var enabledRows = rows.Where(row => FarmListsViewModel.IsRealRow(row) && row.IsEnabled).ToList();
                var names = enabledRows
                    .Select(row => row.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var ids = enabledRows
                    .Select(row => row.ListId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                sentCount = await client.SendSelectedAsync(options, names, ids, log, cancellationToken);
            }
            else
            {
                sentCount = await client.SendAllAsync(options, log, cancellationToken);
            }

            return new FarmListBatchDispatchResult(sentCount, attemptedKeys);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            foreach (var row in rows.Where(row => attemptedKeys.Contains(DispatchKey(row))))
            {
                RecordDispatch(row, succeeded: false, options);
            }

            throw;
        }
    }

    public async Task<FarmListsBatchDispatchOutcome> DispatchManyAsync(
        FarmListsViewRequest viewRequest,
        IReadOnlyList<FarmListStatusRow> rows,
        bool enabledOnly,
        CancellationToken cancellationToken)
    {
        var dispatch = await DispatchManyAsync(
            viewRequest.Options,
            rows,
            enabledOnly,
            cancellationToken);
        var view = await AnalyzeAsync(viewRequest, cancellationToken);
        var successfulDispatch = view.IsAvailable
            && ReconcileDispatches(view.Projection.Rows, dispatch.AttemptedKeys, viewRequest.Options);
        return new FarmListsBatchDispatchOutcome(dispatch.SentCount, successfulDispatch, view);
    }

    public FarmListsAutomaticDispatch ReconcileAutomaticDispatch(
        FarmListsViewResult view,
        IReadOnlyCollection<string> attemptedKeys,
        BotOptions options)
    {
        var successfulDispatch = view.IsAvailable
            && ReconcileDispatches(view.Projection.Rows, attemptedKeys, options);
        return new FarmListsAutomaticDispatch(view, successfulDispatch);
    }

    private FarmListsAutomationResume CaptureAutomationResume()
    {
        var resumeContinuous = automation.ContinuousLoopRunning || automation.StartContinuousAfterQueueStop;
        var resumeQueue = !resumeContinuous && automation.AutoQueueRunning;
        return new FarmListsAutomationResume(resumeContinuous, resumeQueue);
    }

    private async Task PauseAutomationAsync(
        FarmListsAutomationResume resume,
        CancellationToken cancellationToken)
    {
        var resumeContinuous = resume.ContinuousLoop;
        var resumeQueue = resume.AutoQueue;
        if (!resumeContinuous && !resumeQueue)
        {
            log("[farm-list] bot already paused; starting loss destination setup.");
            return;
        }

        automation.ClearPendingRestarts();
        automation.RequestStopAfterCurrentAction();
        automation.UpdateExecutionIndicator();
        log("[farm-list] pause requested; waiting for the current bot action to finish.");

        await WaitForAutomationStopAsync(cancellationToken);
        log("[farm-list] automation paused; loss destination setup has priority.");
    }

    private async Task WaitForAutomationStopAsync(CancellationToken cancellationToken)
    {
        while (!AutomationStopped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken);
        }

        await Task.Delay(100, cancellationToken);
    }

    private bool AutomationStopped =>
        !automation.AutoQueueRunning && !automation.ContinuousLoopRunning && !automation.UiBusy;

    private async Task<bool> WaitForAutomationStopDuringCancellationAsync()
    {
        using var timeout = new CancellationTokenSource(_automationStopCleanupTimeout);
        try
        {
            await WaitForAutomationStopAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return AutomationStopped;
        }
    }

    private async Task<IAsyncDisposable> AcquireAutomationPauseAsync(CancellationToken cancellationToken)
    {
        var pause = new AutomationPauseLease(this, CaptureAutomationResume());
        try
        {
            await PauseAutomationAsync(pause.Resume, cancellationToken);
            return pause;
        }
        catch
        {
            if (await WaitForAutomationStopDuringCancellationAsync())
            {
                await pause.DisposeAsync();
            }
            else
            {
                pause.ResumeAfterStop();
                log(
                    "[farm-list] cancellation cleanup timed out before automation stopped; "
                    + "the original automation mode will resume after the stop completes.");
            }
            throw;
        }
    }

    public async Task<FarmLossDestinationSetupResult> ConfigureLossDestinationAsync(
        FarmListsViewRequest viewRequest,
        IReadOnlyList<VillageSelectionItem> villages,
        string fallbackTribe,
        FarmListLossColors lossColor,
        Func<FarmListsViewResult, CancellationToken, ValueTask<FarmLossDestinationChoice>> chooseDestinationAsync,
        CancellationToken cancellationToken)
    {
        await using var pause = await AcquireAutomationPauseAsync(cancellationToken);
        var view = await AnalyzeAsync(viewRequest, cancellationToken);
        if (!view.IsAvailable)
        {
            throw new InvalidOperationException(
                "Gold Club is not active, so existing farmlists could not be loaded.");
        }

        var choice = await chooseDestinationAsync(view, cancellationToken);
        switch (choice)
        {
            case FarmLossDestinationChoice.Cancel:
                return new FarmLossDestinationSetupResult(null, view, Cancelled: true, Created: false);
            case FarmLossDestinationChoice.UseExisting existing:
                return new FarmLossDestinationSetupResult(
                    existing.Destination,
                    view,
                    Cancelled: false,
                    Created: false);
            case FarmLossDestinationChoice.Create create:
                var created = await CreateLossDestinationAsync(
                    viewRequest,
                    villages,
                    fallbackTribe,
                    lossColor,
                    create.ListName,
                    cancellationToken);
                return new FarmLossDestinationSetupResult(
                    created.Destination,
                    created.View,
                    Cancelled: false,
                    Created: true);
            default:
                throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown loss destination choice.");
        }
    }

    private async Task ResumeAutomationAsync(FarmListsAutomationResume resume)
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

    private sealed class AutomationPauseLease(
        FarmListsWorkflow workflow,
        FarmListsAutomationResume resume) : IAsyncDisposable
    {
        private bool _disposed;

        internal FarmListsAutomationResume Resume => resume;

        internal void ResumeAfterStop()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            workflow.BeginResumeAfterStop(resume);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await workflow.ResumeAutomationAsync(resume);
        }
    }

    private void BeginResumeAfterStop(FarmListsAutomationResume resume) =>
        _ = ResumeAutomationWhenStoppedAsync(resume);

    private async Task ResumeAutomationWhenStoppedAsync(FarmListsAutomationResume resume)
    {
        try
        {
            while (!AutomationStopped)
            {
                await Task.Delay(250);
            }

            await ResumeAutomationAsync(resume);
        }
        catch (Exception ex)
        {
            log($"[farm-list] delayed automation resume failed: {ex.Message}");
        }
    }

    private FarmListsViewResult CreateViewResult(
        FarmListsViewRequest request,
        FarmListsAnalysisResult analysis,
        DateTimeOffset? analyzedAt)
    {
        if (!analysis.IsAvailable)
        {
            ResetProjection();
            return FarmListsViewResult.Unavailable;
        }

        var projection = ProjectOverview(
            analysis.Lists,
            request.Options,
            request.VillageCoordinates,
            request.Presentation);
        CaptureAutomationState(projection.Rows, analyzedAt);
        return new FarmListsViewResult(true, analysis.Lists, projection);
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

    private FarmListsProjection ProjectOverview(
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

    internal async Task<FarmListsAddPreparation> PrepareAddFarmsAsync(
        FarmListsViewRequest viewRequest,
        IReadOnlyList<TravcoListStore.TravcoSavedList> availableSourceLists,
        CancellationToken cancellationToken)
    {
        var view = await AnalyzeAsync(viewRequest, cancellationToken);
        if (!view.IsAvailable)
        {
            return new FarmListsAddPreparation(
                view,
                new OfficialAddFarmsLoadResult(
                    false,
                    "Gold Club is not active.",
                    [],
                    [],
                    new HashSet<string>()));
        }

        var sourceLists = availableSourceLists
            .Where(list => list.Rows.Any(row => row.Selected))
            .ToList();
        if (sourceLists.Count == 0)
        {
            return new FarmListsAddPreparation(
                view,
                new OfficialAddFarmsLoadResult(
                    false,
                    "No saved Travco lists with selected farms were found.",
                    [],
                    [],
                    new HashSet<string>()));
        }

        var ownIdentity = await client.ReadTargetProtectionIdentityAsync(
            viewRequest.Options,
            log,
            cancellationToken);
        var projection = view.Projection;

        var targets = projection.Rows
            .Select(row => new FarmListSelectionOption
            {
                Name = row.Name,
                ActiveFarmCount = row.ActiveFarmCount,
                TotalFarmCount = row.TotalFarmCount,
                Capacity = row.Capacity,
            })
            .ToList();
        return new FarmListsAddPreparation(
            view,
            new OfficialAddFarmsLoadResult(
                true,
                null,
                sourceLists,
                targets,
                new HashSet<string>(projection.AnalyzedCoordinates, StringComparer.OrdinalIgnoreCase),
                projection.IncompleteReads,
                ownIdentity));
    }

    internal IReadOnlyList<OfficialFarmAddPlan> BuildAddPlans(OfficialFarmAddPlanRequest request)
    {
        var workingExisting = new HashSet<string>(request.ExistingCoordinates, StringComparer.OrdinalIgnoreCase);
        var plans = new List<OfficialFarmAddPlan>();
        foreach (var target in request.Targets.Where(target => target.Selected))
        {
            const int officialFarmListCapacity = 100;
            var availableSlots = Math.Max(0, officialFarmListCapacity - target.FarmCount);
            var amount = request.FillAvailable
                ? availableSlots
                : Math.Min(availableSlots, request.RequestedAmount);
            if (amount <= 0)
            {
                continue;
            }

            var coordinates = OfficialFarmSelection.Filter(
                request.SourceRows,
                workingExisting,
                request.SourceRows.Count,
                request.Order,
                request.PopulationMode,
                request.PopulationLimit,
                request.MaximumDistance,
                request.SkipDuplicates,
                request.ReferenceVillage,
                request.OasisTypes,
                request.IncludeOccupied,
                request.SkipLowPopulationVillages,
                requireUnoccupiedOasis: request.IsOasisList && !request.IncludeOccupied,
                minimumDistance: request.MinimumDistance,
                excludeNatars: request.ExcludeNatars);
            if (coordinates.Count == 0)
            {
                continue;
            }

            plans.Add(new OfficialFarmAddPlan(
                request.SourceListId,
                request.SourceListName,
                target.Name,
                amount,
                coordinates));
            if (!request.SkipDuplicates)
            {
                continue;
            }

            foreach (var coordinate in coordinates.Take(amount))
            {
                workingExisting.Add($"{coordinate.X}|{coordinate.Y}");
            }
        }

        return plans;
    }

    public Task SaveSnapshotAsync(
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
            cancellationToken.ThrowIfCancellationRequested();
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(payload));
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

        return Task.CompletedTask;
    }

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

    public IReadOnlyList<string> GetAutoDispatchKeys(
        IEnumerable<FarmListStatusRow> rows,
        bool sendAllLists)
        => sendAllLists
            ? rows
                .Where(row => FarmListsViewModel.IsRealRow(row)
                    && FarmListDispatchStateStore.ShouldTrackDispatch(
                        true,
                        row.IsEnabled,
                        row.IsReady,
                        row.IsEmpty))
                .Select(DispatchKey)
                .ToList()
            : [];

    public IReadOnlyList<string> GetReadyDispatchKeys(
        IEnumerable<FarmListStatusRow> rows,
        bool enabledOnly)
        => rows
            .Where(row => FarmListsViewModel.IsRealRow(row)
                && !row.IsEmpty
                && row.IsReady
                && (!enabledOnly || row.IsEnabled))
            .Select(DispatchKey)
            .ToList();

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

    public FarmLossDestinationSetupValidation ValidateLossDestinationSetup(bool isLoggedIn)
        => isLoggedIn
            ? new FarmLossDestinationSetupValidation(true, null)
            : new FarmLossDestinationSetupValidation(false, "You must log in first.");

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
            QuarantineCorruptSnapshot(path, ex);
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

    private void QuarantineCorruptSnapshot(string path, Exception parseException)
    {
        try
        {
            var quarantinePath = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(path, quarantinePath, overwrite: false);
            log($"Farm list snapshot was corrupt and moved to '{Path.GetFileName(quarantinePath)}': {parseException.Message}");
        }
        catch (Exception quarantineException)
        {
            log($"Farm list snapshot could not be parsed or quarantined: {parseException.Message} "
                + $"Quarantine failed: {quarantineException.Message}");
        }
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

    private void SaveDestinationBaseName(FarmListLossColors lossColor, string name)
    {
        var config = configStore.Load();
        config[lossColor == FarmListLossColors.Red
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

public sealed record FarmListsViewRequest(
    BotOptions Options,
    IReadOnlyDictionary<string, string> VillageCoordinates,
    FarmListsPresentationOptions Presentation);

public sealed record FarmListsViewResult(
    bool IsAvailable,
    IReadOnlyList<FarmListOverview> Lists,
    FarmListsProjection Projection)
{
    public static FarmListsViewResult Unavailable { get; } = new(false, [], FarmListsProjection.Empty);
}

internal sealed record FarmListsAnalysisResult(
    bool IsAvailable,
    IReadOnlyList<FarmListOverview> Lists);

public sealed record OfficialFarmAddPlan(
    Guid SourceListId,
    string SourceListName,
    string TargetName,
    int DesiredCount,
    IReadOnlyList<FarmCoordinate> Coordinates);

public sealed record OfficialFarmAddRunResult(
    int Requested,
    int Added,
    int Duplicates,
    int Failed,
    IReadOnlyList<FarmCoordinate> InvalidCoordinates,
    Guid SourceListId = default,
    string SourceListName = "",
    int OccupiedSkipped = 0,
    int ExcludedPlayers = 0,
    int ExcludedAlliances = 0,
    int IdentityUnavailable = 0);

internal sealed record FarmListsAddPreparation(
    FarmListsViewResult View,
    OfficialAddFarmsLoadResult LoadResult);

internal sealed record FarmListsAddRunResult(
    OfficialFarmAddRunResult RunResult,
    FarmListsViewResult View);

internal sealed record FarmListsCreateResult(
    FarmListCreateBatchResult Creation,
    FarmListsViewResult View);

public sealed record OfficialAddFarmsLoadResult(
    bool Ok,
    string? Message,
    IReadOnlyList<TravcoListStore.TravcoSavedList> SourceLists,
    IReadOnlyList<FarmListSelectionOption> TargetLists,
    IReadOnlySet<string> ExistingCoordinates,
    IReadOnlyList<string>? IncompleteFarmLists = null,
    FarmTargetIdentity? OwnIdentity = null);

public sealed record OfficialFarmAddTarget(string Name, int FarmCount, bool Selected);

public sealed record OfficialFarmAddPlanRequest(
    Guid SourceListId,
    string SourceListName,
    IReadOnlyList<TravcoListStore.TravcoSavedRow> SourceRows,
    IReadOnlyList<OfficialFarmAddTarget> Targets,
    IReadOnlySet<string> ExistingCoordinates,
    string Order,
    string PopulationMode,
    long PopulationLimit,
    double? MinimumDistance,
    double? MaximumDistance,
    (int X, int Y)? ReferenceVillage,
    IReadOnlySet<string>? OasisTypes,
    bool IncludeOccupied,
    bool SkipLowPopulationVillages,
    bool IsOasisList,
    bool FillAvailable,
    int RequestedAmount,
    bool SkipDuplicates,
    bool ExcludeNatars = false);

internal sealed class FarmListsCreateSession(
    FarmListsWorkflow workflow,
    FarmListsViewRequest viewRequest)
{
    public FarmListsViewResult? LatestView { get; private set; }

    public async Task<FarmListCreateBatchResult> RunAsync(
        FarmListCreateRequest request,
        IProgress<FarmListCreateProgress> progress,
        CancellationToken cancellationToken)
    {
        var result = await workflow.CreateAsync(viewRequest, request, progress, cancellationToken);
        LatestView = result.View;
        return result.Creation;
    }
}

internal sealed class FarmListsAddSession(
    FarmListsWorkflow workflow,
    FarmListsViewRequest viewRequest,
    Func<IReadOnlyList<TravcoListStore.TravcoSavedList>> loadSourceLists,
    AddFarmsProtectionPreferences protectionPreferences)
{
    public FarmListsViewResult? LatestView { get; private set; }
    public AddFarmsProtectionPreferences ProtectionPreferences { get; } = protectionPreferences;

    public async Task<OfficialAddFarmsLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var preparation = await workflow.PrepareAddFarmsAsync(
            viewRequest,
            loadSourceLists(),
            cancellationToken);
        LatestView = preparation.View;
        return preparation.LoadResult;
    }

    public async Task<OfficialFarmAddRunResult> RunAsync(
        IReadOnlyList<OfficialFarmAddPlan> plans,
        bool useDefaultTroops,
        string troopType,
        int troopCount,
        FarmTargetProtectionContext protection,
        IProgress<FarmAddProgress> progress,
        CancellationToken cancellationToken)
    {
        var result = await workflow.RunAddPlansAsync(
            viewRequest,
            plans,
            useDefaultTroops,
            troopType,
            troopCount,
            protection,
            progress,
            cancellationToken);
        LatestView = result.View;
        return result.RunResult;
    }

    public IReadOnlyList<OfficialFarmAddPlan> BuildPlans(OfficialFarmAddPlanRequest request)
        => workflow.BuildAddPlans(request);

    public FarmTargetProtectionPreparation PrepareTargetProtection(
        FarmTargetIdentity identity,
        AddFarmsProtectionPreferences requested)
        => workflow.PrepareTargetProtection(identity, requested);
}

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

internal sealed record FarmListsAutomationResume(bool ContinuousLoop, bool AutoQueue)
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

public sealed record FarmLossDestinationSetupValidation(bool CanStart, string? FailureMessage);

public sealed record FarmLossDestinationCreationResult(
    FarmLossDestinationOption Destination,
    FarmListsViewResult View);

public abstract record FarmLossDestinationChoice
{
    private FarmLossDestinationChoice()
    {
    }

    public sealed record Cancel : FarmLossDestinationChoice;

    public sealed record UseExisting(FarmLossDestinationOption Destination) : FarmLossDestinationChoice;

    public sealed record Create(string ListName) : FarmLossDestinationChoice;
}

public sealed record FarmLossDestinationSetupResult(
    FarmLossDestinationOption? Destination,
    FarmListsViewResult View,
    bool Cancelled,
    bool Created);

public sealed record FarmListBatchDispatchResult(
    int SentCount,
    IReadOnlyList<string> AttemptedKeys);

public sealed record FarmListsBatchDispatchOutcome(
    int SentCount,
    bool SuccessfulDispatch,
    FarmListsViewResult View);

public sealed record FarmListsAutomaticDispatch(
    FarmListsViewResult View,
    bool SuccessfulDispatch);

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
