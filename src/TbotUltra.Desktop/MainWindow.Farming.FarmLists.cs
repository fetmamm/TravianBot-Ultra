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

    private void UpdateFarmingUiState()
    {
        if (!_farmingFeaturesAvailable || FarmingStatusTextBlock is null)
        {
            return;
        }

        if (_farmLists.Count <= 0)
        {
            FarmingStatusTextBlock.Text = "No farm lists loaded. Click Analyze Farmlists.";
            return;
        }

        var readyCount = _farmLists.Count(item => item.IsReady);
        FarmingStatusTextBlock.Text = $"Loaded {_farmLists.Count} farm list(s). Ready: {readyCount}.";
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
                SetFarmingFeatureAvailability(false, "Farming unavailable: Gold Club is not active on this account.");
            });
            return false;
        }

        var lists = await _botService.ReadFarmListsOverviewAsync(options, AppendLog, cancellationToken) ?? [];
        await ApplyFarmListOverviewToUiAsync(lists);
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
        var analyzedCoordinates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            if (!mergedByName.TryGetValue(normalizedName, out var existing))
            {
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
                _farmListCapacitiesByName.Clear();
                var displayedRows = 0;
                foreach (var pair in mergedByName.OrderBy(pair => pair.Key))
                {
                    if (displayedRows >= MaxFarmListsShown)
                    {
                        break;
                    }

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
            }
            finally
            {
                _suppressFarmListUiRefresh = false;
            }

            SetFarmingFeatureAvailability(true);
            _lastFarmListsAnalysisAt = DateTimeOffset.UtcNow;
            if (_farmLists.Count > 0)
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
            CompleteOperation(operationId, operationSw, available
                ? $"Loaded {_farmLists.Count} farm list(s)."
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
            SetFarmingFunctionRunning(false);
            DisposeOperationCts();
        }
    }

    private async void AddFarmsToListButton_Click(object sender, RoutedEventArgs e)
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
            if (!options.IsPrivateServer)
            {
                await EnsureChromiumInstalledAsync();
                var available = await RefreshFarmListsFromServerAsync(options, operationToken);
                if (!available)
                {
                    CompleteOperation(operationId, operationSw, "Gold Club is not active.");
                    return;
                }

                var sourceLists = _travcoListStore.LoadAll()
                    .Where(list => list.Rows.Any(row => row.Selected))
                    .ToList();
                if (sourceLists.Count == 0)
                {
                    AppDialog.Show(this, "No saved Travco lists with selected farms were found.", "Add farms", MessageBoxButton.OK, MessageBoxImage.Information);
                    CompleteOperation(operationId, operationSw, "No saved Travco lists found.");
                    return;
                }

                var targetLists = _farmLists
                    .Select(item => new FarmListSelectionOption
                    {
                        Name = item.Name,
                        ActiveFarmCount = item.ActiveFarmCount,
                        TotalFarmCount = item.TotalFarmCount,
                        Capacity = _farmListCapacitiesByName.GetValueOrDefault(item.Name),
                    })
                    .ToList();

                async Task<OfficialFarmAddRunResult> RunOfficialPlansAsync(
                    IReadOnlyList<OfficialFarmAddPlan> plans,
                    bool useDefaultTroops,
                    string troopType,
                    int troopCount,
                    IProgress<FarmAddProgress> progress,
                    CancellationToken cancellationToken)
                {
                    var requested = plans.Sum(plan => plan.Coordinates.Count);
                    var processed = 0;
                    var added = 0;
                    var duplicates = 0;
                    var failed = 0;

                    foreach (var plan in plans)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var processedBeforeList = processed;
                        var addedBeforeList = added;
                        var aggregateProgress = new Progress<FarmAddProgress>(value =>
                        {
                            progress.Report(new FarmAddProgress(
                                value.FarmListName,
                                processedBeforeList + value.ProcessedCount,
                                requested,
                                addedBeforeList + value.AddedCount));
                        });

                        AppendLog(
                            $"Add farms from Travco: target='{plan.TargetName}', requested={plan.Coordinates.Count}, " +
                            $"troops={(useDefaultTroops ? "default" : $"{troopCount} {troopType}")}.");
                        var result = await _botService.AddFarmsFromCoordinatesAsync(
                            options,
                            plan.TargetName,
                            troopType,
                            troopCount,
                            plan.Coordinates,
                            useDefaultTroops,
                            AppendLog,
                            aggregateProgress,
                            cancellationToken);
                        processed += result.AttemptedCount;
                        added += result.AddedCount;
                        duplicates += result.AlreadyInListCount;
                        failed += result.FailedCount;
                        AppendLog(
                            $"Finished '{plan.TargetName}': added={result.AddedCount}, " +
                            $"duplicates={result.AlreadyInListCount}, failed={result.FailedCount}.");
                    }

                    return new OfficialFarmAddRunResult(requested, added, duplicates, failed);
                }

                var officialDialog = new OfficialAddFarmsWindow(
                    ResolveCurrentTribeForFarming(),
                    LoadAddFarmsTroopCount(),
                    sourceLists,
                    targetLists,
                    _analyzedFarmCoordinates,
                    RunOfficialPlansAsync,
                    operationToken)
                {
                    Owner = this,
                };
                if (officialDialog.ShowDialog() != true || officialDialog.RunResult is null)
                {
                    CompleteOperation(operationId, operationSw, "Add farms canceled.");
                    return;
                }

                await RefreshFarmListsFromServerAsync(options, operationToken);
                var runResult = officialDialog.RunResult;
                CompleteOperation(
                    operationId,
                    operationSw,
                    $"Added {runResult.Added}; duplicates {runResult.Duplicates}; failed {runResult.Failed}.");

                return;
            }

            async Task<AddFarmsLoadResult> LoadAsync(CancellationToken token)
            {
                await EnsureChromiumInstalledAsync();
                var available = await RefreshFarmListsFromServerAsync(options, token);
                if (!available)
                {
                    return new AddFarmsLoadResult(false, null, [], 0);
                }

                if (_farmLists.Count <= 0)
                {
                    return new AddFarmsLoadResult(false, "No farm lists found on farmpage.", [], 0);
                }

                var optionsForDialog = _farmLists
                    .Select(item => new FarmListSelectionOption
                    {
                        Name = item.Name,
                        ActiveFarmCount = item.ActiveFarmCount,
                        TotalFarmCount = item.TotalFarmCount,
                    })
                    .ToList();

                var natarCount = await _botService.EnsureNatarFarmCacheAndReturnToFarmListAsync(options, AppendLog, false, token);
                if (natarCount <= 0)
                {
                    SetNatarsProfileAnalyzed(false);
                    return new AddFarmsLoadResult(false, "No villages named 'Natar farm village' were found.", [], 0);
                }

                SetNatarsProfileAnalyzed(true);
                return new AddFarmsLoadResult(true, null, optionsForDialog, natarCount);
            }

            var dialog = new AddFarmsToListWindow(
                ResolveCurrentTribeForFarming(),
                LoadAddFarmsTroopCount(),
                LoadAsync,
                operationToken)
            {
                Owner = this,
            };

            var addRequested = dialog.ShowDialog() == true && dialog.Targets.Count > 0;
            if (!addRequested)
            {
                if (!string.IsNullOrWhiteSpace(dialog.LoadFailureMessage))
                {
                    AppDialog.Show(this, dialog.LoadFailureMessage, "Add farms", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                CompleteOperation(operationId, operationSw, string.IsNullOrWhiteSpace(dialog.LoadFailureMessage)
                    ? "Add farms canceled."
                    : dialog.LoadFailureMessage!);
                return;
            }

            SaveAddFarmsTroopCount(dialog.TroopCount);
            var troopType = dialog.SelectedTroopType;
            var troopCount = dialog.TroopCount;
            var targets = dialog.Targets;

            var totalAdded = 0;
            var totalExisting = 0;
            var totalFailed = 0;
            for (var i = 0; i < targets.Count; i++)
            {
                operationToken.ThrowIfCancellationRequested();
                var target = targets[i];
                var row = _farmLists.FirstOrDefault(item => string.Equals(item.Name, target.Name, StringComparison.OrdinalIgnoreCase));
                var baseActive = row?.ActiveFarmCount ?? 0;
                if (row is not null)
                {
                    row.IsProcessing = true;
                }

                try
                {
                    AppendLog($"Add farms [{i + 1}/{targets.Count}] list '{target.Name}' (requested {target.RequestedFarmCount}).");
                    var progress = new Progress<int>(added =>
                    {
                        if (row is null)
                        {
                            return;
                        }

                        row.ActiveFarmCount = Math.Min(row.TotalFarmCount, baseActive + added);
                        UpdateFarmingUiState();
                    });

                    var result = await _botService.AddFarmsFromNatarsAsync(
                        options,
                        target.Name,
                        troopType,
                        troopCount,
                        target.RequestedFarmCount,
                        AppendLog,
                        progress,
                        operationToken);

                    totalAdded += result.AddedCount;
                    totalExisting += result.AlreadyInListCount;
                    totalFailed += result.FailedCount;

                    if (row is not null)
                    {
                        row.ActiveFarmCount = Math.Min(row.TotalFarmCount, baseActive + result.AddedCount);
                        UpdateFarmingUiState();
                    }

                    if (result.AlreadyInListCount > 0)
                    {
                        AppendLog($"Duplicate farms in '{target.Name}': {result.AlreadyInListCount} ('This village is already in the selected farm list.').");
                    }
                }
                finally
                {
                    if (row is not null)
                    {
                        row.IsProcessing = false;
                    }
                }
            }

            await RefreshFarmListsFromServerAsync(options, operationToken);
            AppDialog.Show(
                this,
                $"Lists processed: {targets.Count}.\nAdded: {totalAdded}, Already in list: {totalExisting}, Failed: {totalFailed}.",
                "Add farms",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            CompleteOperation(operationId, operationSw, $"Add farms done. Lists={targets.Count}, Added={totalAdded}, Existing={totalExisting}, Failed={totalFailed}.");
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
            SetFarmingFunctionRunning(false);
            DisposeOperationCts();
        }
    }

    private async void CreateFarmListButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockIfSessionSleeping("Create farmlists"))
        {
            return;
        }

        if (!_farmingFeaturesAvailable)
        {
            AppendLog("Create Farmlist is unavailable while Gold Club farming is disabled.");
            return;
        }

        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        if (options.IsPrivateServer)
        {
            AppendLog("Create Farmlists is currently available on official servers only.");
            return;
        }

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
        SetFarmingFunctionRunning(true);
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
            SetFarmingFunctionRunning(false);
            DisposeOperationCts();
        }
    }

    private void SyncFarmingControlsEnabledState()
    {
        var sleepAllowsActions = !IsSessionSleeping;
        var farmControlsEnabled = sleepAllowsActions && !_farmingOperationBusy && _farmingFeaturesAvailable;
        SetEnabled(AddFarmsToListButton, farmControlsEnabled);
        SetEnabled(CreateFarmListButton, farmControlsEnabled);
        SetEnabled(FarmListsItemsControl, farmControlsEnabled);
        SetEnabled(AnalyzeFarmListsButton, sleepAllowsActions && !_farmingOperationBusy);
        SetEnabled(AnalyzeNatarsProfileButton, farmControlsEnabled);
        SetEnabled(ShowNatarsListButton, farmControlsEnabled && _natarsProfileAnalyzed);
        SetEnabled(StartManualFarmingButton, sleepAllowsActions && _farmingFeaturesAvailable);
        SetEnabled(StartCatapultWavesButton, farmControlsEnabled);
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

            var view = CollectionViewSource.GetDefaultView(FarmListsItemsControl.ItemsSource);
            view?.Refresh();
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
            var enabledRows = _farmLists.Where(item => item.IsEnabled).ToList();
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
