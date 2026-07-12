using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private readonly HashSet<string> _analyzedFarmCoordinates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?> _farmListCapacitiesByName = new(StringComparer.OrdinalIgnoreCase);

    // Farm lists whose last analysis read fewer target coordinates than the list claims to hold (e.g. an
    // expansion that did not finish). Their farms can be missed by the "don't add duplicates" check, so the
    // Add-farms dialog warns when this is non-empty. Format: "'Name' read/total".
    private IReadOnlyList<string> _farmListIncompleteReads = [];

    private static bool IsRealFarmListRow(FarmListStatusRow row)
    {
        return !row.IsPlaceholder;
    }

    private void EnsureFarmListPlaceholderRow()
    {
        if (_farmLists.Any(IsRealFarmListRow))
        {
            foreach (var row in _farmLists.Where(row => row.IsPlaceholder).ToList())
            {
                _farmLists.Remove(row);
            }

            return;
        }

        if (!_farmLists.Any(row => row.IsPlaceholder))
        {
            _farmLists.Add(new FarmListStatusRow
            {
                IsPlaceholder = true,
                IsEnabled = false,
            });
        }
    }

    private void UpdateFarmingUiState()
    {
        if (!_farmingFeaturesAvailable || FarmingStatusTextBlock is null)
        {
            return;
        }

        var realFarmLists = _farmLists.Where(IsRealFarmListRow).ToList();
        if (realFarmLists.Count <= 0)
        {
            FarmingStatusTextBlock.Text = "No farm lists loaded. Click Analyze Farmlists.";
            return;
        }

        var readyCount = realFarmLists.Count(item => item.IsReady);
        FarmingStatusTextBlock.Text = $"Loaded {realFarmLists.Count} farm list(s). Ready: {readyCount}.";
    }

    private void SetFarmingFeatureAvailability(bool enabled, string? reason = null)
    {
        _farmingFeaturesAvailable = enabled;
        SyncFarmingControlsEnabledState();

        if (!enabled)
        {
            if (FarmingStatusTextBlock is not null)
            {
                FarmingStatusTextBlock.Text = string.IsNullOrWhiteSpace(reason)
                    ? "Farming is unavailable for this account."
                    : reason;
            }
        }
        else
        {
            UpdateFarmingUiState();
        }
    }

    private void TickFarmListCountdowns()
    {
        if (_farmLists.Count <= 0)
        {
            return;
        }

        var changed = false;
        var snapshot = _farmLists.ToList();
        foreach (var list in snapshot)
        {
            changed |= list.TickOneSecond();
        }

        if (changed)
        {
            UpdateFarmingUiState();
        }
    }

    private async Task<bool> RefreshFarmListsFromServerAsync(BotOptions options, CancellationToken cancellationToken)
    {
        var goldClubEnabled = await _botService.ReadAndPersistGoldClubStatusAsync(options, AppendLog, cancellationToken);
        UpdateGoldClubInfo(goldClubEnabled);
        if (!goldClubEnabled)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _farmLists.Clear();
                EnsureFarmListPlaceholderRow();
                SetFarmingFeatureAvailability(false, "Farming unavailable: Gold Club is not active on this account.");
            });
            return false;
        }

        var lists = await _botService.ReadFarmListsOverviewAsync(options, AppendLog, cancellationToken) ?? [];
        await ApplyFarmListOverviewToUiAsync(lists);
        await Dispatcher.InvokeAsync(() =>
            UpdateCachedTimerStatus(GetSelectedVillageName(), status => status with { FarmLists = lists }));
        await SaveFarmListsSnapshotAsync(lists, cancellationToken);
        return true;
    }

    private async Task SaveFarmListsSnapshotAsync(IReadOnlyList<FarmListOverview> lists, CancellationToken cancellationToken)
    {
        try
        {
            var path = AccountStoragePaths.FarmListsSnapshotPath(_projectRoot, _accountStore.ActiveAccountName());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = new
            {
                capturedAtUtc = DateTimeOffset.UtcNow,
                lists = lists.Select(item => new
                {
                    name = item.Name,
                    activeFarmCount = item.ActiveFarmCount,
                    totalFarmCount = item.TotalFarmCount,
                    remainingSeconds = item.RemainingSeconds,
                    listId = item.ListId,
                    capacity = item.Capacity,
                    farmCoordinates = item.FarmCoordinates,
                }),
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload), cancellationToken);
            AppendLog($"[farm-list] saved analysis snapshot with {_analyzedFarmCoordinates.Count} unique coordinate(s).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save farm list analysis snapshot: {ex.Message}");
        }
    }

    // Merges a freshly read farm-list overview into the UI rows: dedupes by name, keeps timers/lids,
    // and re-applies the persisted selection (by lid, falling back to name). Shared by the full
    // server re-analyze and the instant post-send snapshot load so both produce identical rows.
    private async Task ApplyFarmListOverviewToUiAsync(IReadOnlyList<FarmListOverview> lists)
    {
        var selectedFarmLists = LoadConfiguredContinuousFarmListNames();
        var selectedFarmListIds = LoadConfiguredContinuousFarmListIds();
        var mergedByName = new Dictionary<string, (int Active, int Total, int? RemainingSeconds, string? ListId, int? Capacity, IReadOnlyList<string> Coordinates)>(StringComparer.OrdinalIgnoreCase);
        var orderedNames = new List<string>();
        var analyzedCoordinates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incompleteReads = new List<string>();
        foreach (var list in lists)
        {
            if (list is null)
            {
                continue;
            }

            var normalizedName = string.IsNullOrWhiteSpace(list.Name) ? "Farm list" : list.Name.Trim();
            var incomingListId = string.IsNullOrWhiteSpace(list.ListId) ? null : list.ListId.Trim();
            var incomingCoordinates = (list.FarmCoordinates ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            analyzedCoordinates.UnionWith(incomingCoordinates);

            // Tier 1 detection: a fully-read list yields one coordinate per farm. Fewer means the read
            // missed targets (incomplete expansion or an unexpected DOM), so the dedup check can miss them.
            var listTotal = Math.Max(0, list.TotalFarmCount);
            if (listTotal > 0 && incomingCoordinates.Count < listTotal)
            {
                incompleteReads.Add($"'{normalizedName}' {incomingCoordinates.Count}/{listTotal}");
            }
            if (!mergedByName.TryGetValue(normalizedName, out var existing))
            {
                orderedNames.Add(normalizedName);
                mergedByName[normalizedName] = (
                    Active: Math.Max(0, list.ActiveFarmCount),
                    Total: Math.Max(0, list.TotalFarmCount),
                    RemainingSeconds: list.RemainingSeconds is > 0 ? list.RemainingSeconds : null,
                    ListId: incomingListId,
                    Capacity: list.Capacity,
                    Coordinates: incomingCoordinates);
                continue;
            }

            var incomingRemaining = list.RemainingSeconds is > 0 ? list.RemainingSeconds : null;
            mergedByName[normalizedName] = (
                Active: Math.Max(existing.Active, Math.Max(0, list.ActiveFarmCount)),
                Total: Math.Max(existing.Total, Math.Max(0, list.TotalFarmCount)),
                RemainingSeconds: existing.RemainingSeconds is > 0
                    ? existing.RemainingSeconds
                    : incomingRemaining,
                ListId: string.IsNullOrWhiteSpace(existing.ListId) ? incomingListId : existing.ListId,
                Capacity: existing.Capacity ?? list.Capacity,
                Coordinates: existing.Coordinates.Concat(incomingCoordinates).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        await Dispatcher.InvokeAsync(() =>
        {
            _suppressFarmListUiRefresh = true;
            try
            {
                _farmLists.Clear();
                _analyzedFarmCoordinates.Clear();
                _analyzedFarmCoordinates.UnionWith(analyzedCoordinates);
                _farmListIncompleteReads = incompleteReads;
                _farmListCapacitiesByName.Clear();
                var displayedRows = 0;
                foreach (var name in orderedNames)
                {
                    if (displayedRows >= MaxFarmListsShown)
                    {
                        break;
                    }

                    var pair = new KeyValuePair<string, (int Active, int Total, int? RemainingSeconds, string? ListId, int? Capacity, IReadOnlyList<string> Coordinates)>(
                        name,
                        mergedByName[name]);
                    var hasSelection = selectedFarmLists.Count > 0 || selectedFarmListIds.Count > 0;
                    var isSelected = !hasSelection
                        || (pair.Value.ListId is not null && selectedFarmListIds.Contains(pair.Value.ListId))
                        || selectedFarmLists.Contains(pair.Key);
                    _farmLists.Add(new FarmListStatusRow
                    {
                        Name = pair.Key,
                        ListId = pair.Value.ListId,
                        ActiveFarmCount = pair.Value.Active,
                        TotalFarmCount = pair.Value.Total,
                        Capacity = pair.Value.Capacity,
                        IsEnabled = isSelected,
                        RemainingSeconds = pair.Value.RemainingSeconds,
                    });
                    _farmListCapacitiesByName[pair.Key] = pair.Value.Capacity;
                    displayedRows++;
                }

                EnsureFarmListPlaceholderRow();
            }
            finally
            {
                _suppressFarmListUiRefresh = false;
            }

            SetFarmingFeatureAvailability(true);
            _lastFarmListsAnalysisAt = DateTimeOffset.UtcNow;
            if (_farmLists.Any(IsRealFarmListRow))
            {
                if (string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoFarmLists, StringComparison.OrdinalIgnoreCase))
                {
                    ClearFarmingBlockedState();
                }
            }
            else
            {
                SetFarmingBlockedState(FarmingBlockedReasonNoFarmLists, "No farmlists available");
            }

            UpdateFarmingUiState();
            SyncFarmListSelectionHandlers();
            RefreshFarmListsItemsControl();
        });

        if (mergedByName.Count > MaxFarmListsShown)
        {
            AppendLog($"Farm list UI limited to {MaxFarmListsShown} rows (detected {mergedByName.Count}).");
        }

        if (incompleteReads.Count > 0)
        {
            AppendLog($"[farm-list] WARNING: {incompleteReads.Count} farm list(s) not fully read "
                + $"({string.Join(", ", incompleteReads)}). Duplicate protection may miss those farms — re-run Analyze.");
        }
    }

    // After the auto-loop send_farmlists task actually dispatches a list it defers with a
    // "cooldown active" message. The worker reads the new timer on its side but the desktop
    // rows are never updated, so they keep showing "Ready" and Send Now stays clickable until
    // the user manually clicks Analyze. Re-analyze here so timers, names and buttons stay in
    // sync — the same effect as the Analyze Farmlists button. We also re-analyze on the
    // "not found" defer (a likely rename) so the current list names surface for re-selection.
    // The frequent "no list ready" defer is skipped — names are unchanged and a re-read there
    // would navigate the browser on every loop iteration.
    private async Task RefreshFarmListsUiAfterAutoSendIfNeededAsync(QueueItem item, string message)
    {
        if (!string.Equals(item.TaskName, "send_farmlists", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sendHappened = message.IndexOf("cooldown active", StringComparison.OrdinalIgnoreCase) >= 0;
        var possibleRename = message.IndexOf("were not found on the farm page", StringComparison.OrdinalIgnoreCase) >= 0;
        if (string.IsNullOrEmpty(message) || (!sendHappened && !possibleRename))
        {
            return;
        }

        if (_farmingOperationBusy)
        {
            return;
        }

        try
        {
            // On a real send the worker just read the farm page and wrote a fresh snapshot — apply
            // it directly so the UI updates instantly without navigating the browser again. On a
            // rename ("not found") there is no fresh snapshot, so fall back to a full re-analyze.
            if (sendHappened && await TryApplyFarmListsSnapshotAsync())
            {
                return;
            }

            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await RefreshFarmListsFromServerAsync(options, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendLog($"Farm list UI refresh after send failed: {ex.Message}");
        }
    }

    // Loads the snapshot the worker writes immediately after a send and applies it to the UI rows.
    // Returns false (so the caller can fall back to a server re-analyze) when the snapshot is
    // missing, unparseable, or too old to trust.
    private async Task<bool> TryApplyFarmListsSnapshotAsync()
    {
        var snapshotPath = AccountStoragePaths.FarmListsSnapshotPath(_projectRoot, _accountStore.ActiveAccountName());
        if (!File.Exists(snapshotPath))
        {
            return false;
        }

        FarmListsSnapshotDto? snapshot;
        try
        {
            var json = await File.ReadAllTextAsync(snapshotPath);
            snapshot = JsonSerializer.Deserialize<FarmListsSnapshotDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Farm list snapshot could not be parsed: {ex.Message}");
            return false;
        }

        if (snapshot?.Lists is null
            || snapshot.CapturedAtUtc is null
            || DateTimeOffset.UtcNow - snapshot.CapturedAtUtc.Value > TimeSpan.FromMinutes(2))
        {
            return false;
        }

        var lists = snapshot.Lists
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new FarmListOverview(
                Name: entry!.Name!,
                ActiveFarmCount: entry.ActiveFarmCount,
                TotalFarmCount: entry.TotalFarmCount,
                RemainingSeconds: entry.RemainingSeconds,
                ListId: string.IsNullOrWhiteSpace(entry.ListId) ? null : entry.ListId,
                Capacity: entry.Capacity,
                FarmCoordinates: entry.FarmCoordinates ?? []))
            .ToList();

        await ApplyFarmListOverviewToUiAsync(lists);
        return true;
    }

    private sealed class FarmListsSnapshotDto
    {
        public DateTimeOffset? CapturedAtUtc { get; init; }
        public List<FarmListSnapshotEntryDto>? Lists { get; init; }
    }

    private sealed class FarmListSnapshotEntryDto
    {
        public string? Name { get; init; }
        public int ActiveFarmCount { get; init; }
        public int TotalFarmCount { get; init; }
        public int? RemainingSeconds { get; init; }
        public string? ListId { get; init; }
        public int? Capacity { get; init; }
        public List<string>? FarmCoordinates { get; init; }
    }

    private async void AnalyzeFarmListsButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(AnalyzeFarmListsButtonClickAsync);

    private async Task AnalyzeFarmListsButtonClickAsync()
    {
        if (BlockIfSessionSleeping("Analyze farmlists"))
        {
            return;
        }

        var operationId = BeginOperation("Analyze Farmlists");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        SetFarmingFunctionRunning(true, showCancelButton: false);
        BusyOverlay.ShowCancel = true;
        ShowBusyOverlay("Analyze farmlists", "Reading current farmlists...");
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            var available = await RefreshFarmListsFromServerAsync(options, operationToken);
            var loadedCount = _farmLists.Count(IsRealFarmListRow);
            CompleteOperation(operationId, operationSw, available
                ? $"Loaded {loadedCount} farm list(s)."
                : "Gold Club is not active.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Analyze farmlists paused.");
        }
        catch (Exception ex)
        {
            if (FarmingStatusTextBlock is not null)
            {
                FarmingStatusTextBlock.Text = "Analyze failed. Previous farm list state kept.";
            }
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            HideBusyOverlay();
            SetFarmingFunctionRunning(false);
            DisposeOperationCts();
        }
    }

    private async void AddFarmsToListButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(AddFarmsToListButtonClickAsync);

    private async Task AddFarmsToListButtonClickAsync()
    {
        if (BlockIfSessionSleeping("Add farms to list"))
        {
            return;
        }

        if (!_farmingFeaturesAvailable)
        {
            AppendLog("Add Farms to List is unavailable while Gold Club farming is disabled.");
            return;
        }

        var operationId = BeginOperation("Add Farms To List");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        SetFarmingFunctionRunning(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            async Task<OfficialAddFarmsLoadResult> LoadOfficialAsync(CancellationToken cancellationToken)
            {
                await EnsureChromiumInstalledAsync();
                var available = await RefreshFarmListsFromServerAsync(options, cancellationToken);
                if (!available)
                {
                    return new OfficialAddFarmsLoadResult(
                        false,
                        "Gold Club is not active.",
                        [],
                        [],
                        new HashSet<string>());
                }

                var sourceLists = _travcoListStore.LoadAll()
                    .Where(list => list.Rows.Any(row => row.Selected))
                    .ToList();
                if (sourceLists.Count == 0)
                {
                    return new OfficialAddFarmsLoadResult(
                        false,
                        "No saved Travco lists with selected farms were found.",
                        [],
                        [],
                        new HashSet<string>());
                }

                var targetLists = _farmLists
                    .Where(IsRealFarmListRow)
                    .Select(item => new FarmListSelectionOption
                    {
                        Name = item.Name,
                        ActiveFarmCount = item.ActiveFarmCount,
                        TotalFarmCount = item.TotalFarmCount,
                        Capacity = _farmListCapacitiesByName.GetValueOrDefault(item.Name),
                    })
                    .ToList();
                return new OfficialAddFarmsLoadResult(
                    true,
                    null,
                    sourceLists,
                    targetLists,
                    new HashSet<string>(_analyzedFarmCoordinates, StringComparer.OrdinalIgnoreCase),
                    _farmListIncompleteReads);
            }

            async Task<OfficialFarmAddRunResult> RunOfficialPlansAsync(
                IReadOnlyList<OfficialFarmAddPlan> plans,
                bool useDefaultTroops,
                string troopType,
                int troopCount,
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
                var invalidCoordinates = new List<FarmCoordinate>();

                foreach (var plan in plans)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var processedBeforeList = processed;
                    var addedBeforeList = added;
                    var notFoundBeforeList = notFound;
                    var occupiedBeforeList = occupiedSkipped;
                    var aggregateProgress = new Progress<FarmAddProgress>(value =>
                    {
                        progress.Report(new FarmAddProgress(
                            value.FarmListName,
                            processedBeforeList + value.ProcessedCount,
                            requested,
                            addedBeforeList + value.AddedCount,
                            notFoundBeforeList + value.NotFoundCount,
                            value.InvalidCoordinate,
                            occupiedBeforeList + value.OccupiedOasisSkippedCount));
                    });

                    AppendLog(
                        $"Add farms from Travco: target='{plan.TargetName}', requested={plan.DesiredCount}, " +
                        $"candidates={plan.Coordinates.Count}, " +
                        $"troops={(useDefaultTroops ? "default" : $"{troopCount} {troopType}")}.");
                    var result = await _botService.AddFarmsFromCoordinatesAsync(
                        options,
                        plan.TargetName,
                        troopType,
                        troopCount,
                        plan.DesiredCount,
                        plan.Coordinates,
                        useDefaultTroops,
                        AppendLog,
                        aggregateProgress,
                        cancellationToken);
                    processed += result.AttemptedCount;
                    added += result.AddedCount;
                    duplicates += result.AlreadyInListCount;
                    failed += result.FailedCount;
                    notFound += result.NotFoundCount;
                    occupiedSkipped += result.OccupiedOasisSkippedCount;
                    invalidCoordinates.AddRange(result.InvalidCoordinates ?? []);
                    AppendLog(
                        $"Finished '{plan.TargetName}': added={result.AddedCount}, " +
                        $"duplicates={result.AlreadyInListCount}, invalid={result.NotFoundCount}, " +
                        $"occupiedSkipped={result.OccupiedOasisSkippedCount}, failed={result.FailedCount}.");
                }

                return new OfficialFarmAddRunResult(
                    requested,
                    added,
                    duplicates,
                    failed,
                    invalidCoordinates
                        .Distinct()
                        .ToList(),
                    OccupiedSkipped: occupiedSkipped);
            }

            var villageOptions = GetFarmListCreationVillages()
                .Select(village => new OfficialAddFarmsWindow.AddFarmsVillageOption(
                    village.Name,
                    village.CoordX,
                    village.CoordY))
                .ToList();
            var officialDialog = new OfficialAddFarmsWindow(
                ResolveCurrentTribeForFarming(),
                LoadAddFarmsTroopCount(),
                LoadOfficialAsync,
                RunOfficialPlansAsync,
                operationToken,
                villageOptions,
                GetSelectedVillageName())
            {
                Owner = this,
            };
            if (officialDialog.ShowDialog() != true || officialDialog.RunResult is null)
            {
                if (!string.IsNullOrWhiteSpace(officialDialog.LoadFailureMessage))
                {
                    AppDialog.Show(
                        this,
                        officialDialog.LoadFailureMessage,
                        "Add farms",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                CompleteOperation(
                    operationId,
                    operationSw,
                    string.IsNullOrWhiteSpace(officialDialog.LoadFailureMessage)
                        ? "Add farms canceled."
                        : officialDialog.LoadFailureMessage);
                return;
            }

            BusyOverlay.ShowCancel = false;
            ShowBusyOverlay("Adding farms", "Finalizing farm list updates...");
            await RefreshFarmListsFromServerAsync(options, operationToken);
            var runResult = officialDialog.RunResult;
            if (runResult.InvalidCoordinates.Count > 0)
            {
                HideBusyOverlay();
                var removeInvalid = AppDialog.Show(
                    this,
                    $"{runResult.InvalidCoordinates.Count} invalid village coordinate(s) were found.\n\n" +
                    $"Remove them from Travco list '{runResult.SourceListName}'?",
                    "Remove invalid villages",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (removeInvalid == MessageBoxResult.Yes)
                {
                    var removed = _travcoListStore.RemoveRowsByCoordinates(
                        runResult.SourceListId,
                        runResult.InvalidCoordinates);
                    AppendLog(
                        $"Removed {removed}/{runResult.InvalidCoordinates.Count} invalid coordinate(s) " +
                        $"from Travco list '{runResult.SourceListName}'.");
                }
            }

            HideBusyOverlay();
            CompleteOperation(
                operationId,
                operationSw,
                $"Added {runResult.Added}; duplicates {runResult.Duplicates}; occupied skipped {runResult.OccupiedSkipped}; failed {runResult.Failed}.");

            return;
        }
        catch (OperationCanceledException)
        {
            AppendLog("Add farms paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            HideBusyOverlay();
            SetFarmingFunctionRunning(false);
            DisposeOperationCts();
        }
    }

    private string ResolveCurrentTribeForFarming()
    {
        var tribeFromUi = TribeInfoTextBlock.Text?.Replace("Tribe:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (!string.IsNullOrWhiteSpace(tribeFromUi) && !string.Equals(tribeFromUi, "-", StringComparison.OrdinalIgnoreCase))
        {
            return tribeFromUi;
        }

        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (!string.IsNullOrWhiteSpace(accountName)
                && _accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl())
                && analysis is not null
                && !string.IsNullOrWhiteSpace(analysis.Tribe))
            {
                return analysis.Tribe;
            }
        }
        catch
        {
            // Ignore lookup errors and use fallback tribe.
        }

        return "Unknown";
    }

    private async void CreateFarmListButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(CreateFarmListButtonClickAsync);

    private async Task CreateFarmListButtonClickAsync()
    {
        if (BlockIfSessionSleeping("Create farmlists"))
        {
            return;
        }

        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var villages = GetFarmListCreationVillages();
        if (villages.Count == 0)
        {
            AppendLog("Create Farmlists requires at least one loaded village.");
            return;
        }

        var operationId = BeginOperation("Create Farmlists");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        SetFarmingFunctionRunning(true);
        try
        {
            BusyOverlay.ShowCancel = true;
            ShowBusyOverlay("Analyze farmlists", "Reading current farmlists...");
            await EnsureChromiumInstalledAsync();
            AppendLog("[farm-list-create] analyzing current farmlists before opening create dialog.");
            var available = await RefreshFarmListsFromServerAsync(options, operationToken);
            HideBusyOverlay();
            if (!available)
            {
                CompleteOperation(operationId, operationSw, "Gold Club is not active.");
                return;
            }

            async Task<FarmListCreateBatchResult> RunAsync(
                FarmListCreateRequest request,
                IProgress<FarmListCreateProgress> progress,
                CancellationToken cancellationToken)
            {
                await EnsureChromiumInstalledAsync();
                progress.Report(new FarmListCreateProgress(
                    "Analyzing farmlists",
                    0,
                    request.Names.Count));
                AppendLog("[farm-list-create] analyzing current farmlist page before creation.");
                var available = await RefreshFarmListsFromServerAsync(options, cancellationToken);
                if (!available)
                {
                    throw new InvalidOperationException("Gold Club is not active.");
                }

                AppendLog(
                    $"[farm-list-create] requested={request.Names.Count}, village='{request.VillageName}', " +
                    $"default={request.TroopCount} {request.TroopType}.");
                return await _botService.CreateFarmListsAsync(
                    options,
                    request,
                    AppendLog,
                    progress,
                    cancellationToken);
            }

            var dialog = new CreateFarmListsWindow(
                ResolveCurrentTribeForFarming(),
                villages,
                RunAsync,
                operationToken)
            {
                Owner = this,
            };
            if (dialog.ShowDialog() != true || dialog.RunResult is null)
            {
                CompleteOperation(operationId, operationSw, "Create farmlists canceled.");
                return;
            }

            await RefreshFarmListsFromServerAsync(options, operationToken);
            CompleteOperation(
                operationId,
                operationSw,
                $"Created {dialog.RunResult.CreatedCount}/{dialog.RunResult.RequestedCount} farmlists.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Create farmlists canceled.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            HideBusyOverlay();
            SetFarmingFunctionRunning(false);
            DisposeOperationCts();
        }
    }

    private IReadOnlyList<VillageSelectionItem> GetFarmListCreationVillages()
    {
        var source = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? [];
        return source
            .Where(village => !string.IsNullOrWhiteSpace(village.Name)
                              && !string.Equals(village.Name, "-", StringComparison.Ordinal))
            .GroupBy(
                village => string.IsNullOrWhiteSpace(village.Url)
                    ? $"name:{village.Name.Trim()}"
                    : village.Url,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private async void FarmListSendNowButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(() => FarmListSendNowButtonClickAsync(sender, e));

    private async Task FarmListSendNowButtonClickAsync(object sender, RoutedEventArgs e)
    {
        if (BlockIfSessionSleeping("Farm send now"))
        {
            return;
        }

        if (sender is not Button { Tag: FarmListStatusRow list })
        {
            return;
        }

        if (!list.CanSendNow)
        {
            return;
        }

        var operationId = BeginOperation("Farm Send Now");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        // Use the shared busy overlay with its built-in cancel instead of the separate cancel button.
        SetFarmingFunctionRunning(true, showCancelButton: false);
        BusyOverlay.ShowCancel = true;
        ShowBusyOverlay("Send now", $"Sending '{list.Name}'...");
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            var timerSeconds = await _botService.SendFarmListNowAsync(options, list.Name, AppendLog, operationToken);
            list.RemainingSeconds = timerSeconds is > 0 ? timerSeconds : null;
            UpdateFarmingUiState();
            CompleteOperation(operationId, operationSw, $"Sent '{list.Name}'.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Farm list send paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            HideBusyOverlay();
            SetFarmingFunctionRunning(false);
            DisposeOperationCts();
        }
    }

    private async void FarmListSendAllNowButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(FarmListSendAllNowButtonClickAsync);

    private async Task FarmListSendAllNowButtonClickAsync()
    {
        if (BlockIfSessionSleeping("Farm send all now"))
        {
            return;
        }

        if (!_farmLists.Any(IsRealFarmListRow))
        {
            AppendLog("[farm-list] Send all now ignored: no farm lists loaded.");
            return;
        }

        var operationId = BeginOperation("Farm Send All Now");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        SetFarmingFunctionRunning(true, showCancelButton: false);
        BusyOverlay.ShowCancel = true;
        ShowBusyOverlay("Send all now", "Sending all farmlists...");
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            var sentCount = await _botService.SendAllFarmListsNowAsync(options, AppendLog, operationToken);
            await RefreshFarmListsFromServerAsync(options, operationToken);
            CompleteOperation(operationId, operationSw, $"Sent all farmlists ({sentCount} list(s)).");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Farm list send-all paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            HideBusyOverlay();
            SetFarmingFunctionRunning(false);
            DisposeOperationCts();
        }
    }

    private void SyncFarmingControlsEnabledState()
    {
        var sleepAllowsActions = !IsSessionSleeping;
        var farmControlsEnabled = sleepAllowsActions && !_farmingOperationBusy && _farmingFeaturesAvailable;
        SetEnabled(AddFarmsToListButton, farmControlsEnabled);
        SetEnabled(CreateFarmListButton, sleepAllowsActions && !_farmingOperationBusy);
        SetEnabled(FarmListsItemsControl, farmControlsEnabled);
        SetEnabled(FarmListSendAllNowButton, farmControlsEnabled && _farmLists.Any(IsRealFarmListRow));
        SetEnabled(AnalyzeFarmListsButton, sleepAllowsActions && !_farmingOperationBusy);
        SetEnabled(StartManualFarmingButton, false);
        // Catapult waves temporarily disabled — feature under review.
        SetEnabled(StartCatapultWavesButton, false);
        UpdateManualFarmingRunningState();
    }

    private void RefreshFarmListsItemsControl()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke((Action)RefreshFarmListsItemsControl, DispatcherPriority.Render);
            return;
        }

        if (FarmListsItemsControl is null)
        {
            return;
        }

        try
        {
            if (!ReferenceEquals(FarmListsItemsControl.ItemsSource, _farmLists))
            {
                FarmListsItemsControl.ItemsSource = _farmLists;
            }

            EnsureFarmListPlaceholderRow();
            var view = CollectionViewSource.GetDefaultView(FarmListsItemsControl.ItemsSource);
            view?.Refresh();
            SyncFarmingControlsEnabledState();
        }
        catch (Exception ex)
        {
            AppendLog($"Farm list UI refresh warning: {ex.Message}");
        }
    }

    private void SyncFarmListSelectionHandlers()
    {
        foreach (var row in _farmLists)
        {
            row.PropertyChanged -= FarmListStatusRow_PropertyChanged;
            row.PropertyChanged += FarmListStatusRow_PropertyChanged;
        }
    }

    private void FarmListStatusRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressFarmListUiRefresh)
        {
            return;
        }

        if (!string.Equals(e.PropertyName, nameof(FarmListStatusRow.IsEnabled), StringComparison.Ordinal))
        {
            return;
        }

        PersistContinuousFarmListSelectionToConfig();
        UpdateAutomationLoopRunningIndicators();
        UpdateFarmingUiState();
    }

    private IReadOnlySet<string> LoadConfiguredContinuousFarmListNames()
    {
        try
        {
            var options = LoadBotOptions();
            return options.ContinuousFarmListNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private IReadOnlySet<string> LoadConfiguredContinuousFarmListIds()
    {
        try
        {
            var options = LoadBotOptions();
            return options.ContinuousFarmListIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void ApplyFarmingSettingsToUi(BotOptions options)
    {
        _suppressFarmingSettingsConfigWrite = true;
        try
        {
            var mode = FarmingDefaults.NormalizeSendMode(options.ContinuousFarmSendMode);
            if (FarmSendListPerListRadioButton is not null)
            {
                FarmSendListPerListRadioButton.IsChecked = string.Equals(mode, FarmingDefaults.SendModeListPerList, StringComparison.Ordinal);
            }

            if (FarmSendAllAtOnceRadioButton is not null)
            {
                FarmSendAllAtOnceRadioButton.IsChecked = string.Equals(mode, FarmingDefaults.SendModeAllAtOnce, StringComparison.Ordinal);
            }

            SelectFarmDispatchDelayMinMinutes(options.ContinuousFarmDispatchDelayMinMinutes);
            SelectFarmDispatchDelayMaxMinutes(options.ContinuousFarmDispatchDelayMaxMinutes);

            if (DeactivateFarmLossesCheckBox is not null)
            {
                DeactivateFarmLossesCheckBox.IsChecked = options.ContinuousFarmDeactivateLosses;
            }

            if (DeactivateFarmOasisLossesCheckBox is not null)
            {
                DeactivateFarmOasisLossesCheckBox.IsChecked = options.ContinuousFarmDeactivateOasisLosses;
            }
        }
        finally
        {
            _suppressFarmingSettingsConfigWrite = false;
        }
    }

    private void FarmingSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFarmingSettingsConfigWrite)
        {
            return;
        }

        try
        {
            var config = _botConfigStore.Load();
            var mode = FarmSendAllAtOnceRadioButton?.IsChecked == true
                ? FarmingDefaults.SendModeAllAtOnce
                : FarmingDefaults.SendModeListPerList;
            var delayMinMinutes = GetSelectedFarmDispatchDelayMinMinutes();
            var delayMaxMinutes = GetSelectedFarmDispatchDelayMaxMinutes();
            config[BotOptionPayloadKeys.ContinuousFarmSendMode] = mode;
            config[BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinMinutes] = delayMinMinutes;
            config[BotOptionPayloadKeys.ContinuousFarmDispatchDelayMaxMinutes] = delayMaxMinutes;
            config[BotOptionPayloadKeys.ContinuousFarmDeactivateLosses] = DeactivateFarmLossesCheckBox?.IsChecked == true;
            config[BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses] = DeactivateFarmOasisLossesCheckBox?.IsChecked == true;
            _botConfigStore.Save(config);
            AppendLog($"[farm-settings] mode={mode}; delay={delayMinMinutes}-{delayMaxMinutes}m; deactivateLosses={DeactivateFarmLossesCheckBox?.IsChecked == true}; deactivateOasis={DeactivateFarmOasisLossesCheckBox?.IsChecked == true}");
            UpdateAutomationLoopRunningIndicators();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save farm settings: {ex.Message}");
        }
    }

    private void SelectFarmDispatchDelayMinMinutes(int minutes)
    {
        if (FarmDispatchDelayMinTextBox is not null)
        {
            FarmDispatchDelayMinTextBox.Text = FarmingDefaults.NormalizeDispatchDelayMinMinutes(minutes).ToString();
        }
    }

    private int GetSelectedFarmDispatchDelayMinMinutes()
    {
        return FarmingDefaults.NormalizeDispatchDelayMinMinutes(
            int.TryParse(FarmDispatchDelayMinTextBox?.Text?.Trim(), out var minutes) ? minutes : 0);
    }

    private void SelectFarmDispatchDelayMaxMinutes(int minutes)
    {
        if (FarmDispatchDelayMaxTextBox is not null)
        {
            FarmDispatchDelayMaxTextBox.Text = FarmingDefaults.NormalizeDispatchDelayMaxMinutes(minutes).ToString();
        }
    }

    private int GetSelectedFarmDispatchDelayMaxMinutes()
    {
        var max = FarmingDefaults.NormalizeDispatchDelayMaxMinutes(
            int.TryParse(FarmDispatchDelayMaxTextBox?.Text?.Trim(), out var minutes) ? minutes : 0);
        return Math.Max(GetSelectedFarmDispatchDelayMinMinutes(), max);
    }

    private const string AddFarmsTroopCountConfigKey = "addFarmsTroopCount";
    private const int AddFarmsDefaultTroopCount = 100;

    private int LoadAddFarmsTroopCount()
    {
        try
        {
            var config = _botConfigStore.Load();
            if (config.TryGetPropertyValue(AddFarmsTroopCountConfigKey, out var node) && node is not null)
            {
                var value = node.GetValue<int>();
                if (value > 0)
                {
                    return value;
                }
            }
        }
        catch
        {
            // fall through to default
        }

        return AddFarmsDefaultTroopCount;
    }

    private void SaveAddFarmsTroopCount(int troopCount)
    {
        if (troopCount <= 0)
        {
            return;
        }

        try
        {
            var config = _botConfigStore.Load();
            config[AddFarmsTroopCountConfigKey] = JsonValue.Create(troopCount);
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save add-farms troop count: {ex.Message}");
        }
    }

    private void PersistContinuousFarmListSelectionToConfig()
    {
        try
        {
            var enabledRows = _farmLists.Where(item => IsRealFarmListRow(item) && item.IsEnabled).ToList();
            var selectedNames = enabledRows
                .Select(item => item.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Persist the stable lids too so the selection survives a village/list rename.
            var selectedIds = enabledRows
                .Select(item => item.ListId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.ContinuousFarmListNames] = new JsonArray(selectedNames.Select(name => JsonValue.Create(name)!).ToArray());
            config[BotOptionPayloadKeys.ContinuousFarmListIds] = new JsonArray(selectedIds.Select(id => JsonValue.Create(id)!).ToArray());
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save selected farmlists: {ex.Message}");
        }
    }
}
