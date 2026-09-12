using System;
using TbotUltra.Desktop.Services.Orchestration;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Farming;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static readonly TimeSpan RecentFarmListAnalysisWindow = TimeSpan.FromMinutes(5);
    private readonly HashSet<string> _analyzedFarmCoordinates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?> _farmListCapacitiesByName = new(StringComparer.OrdinalIgnoreCase);
    private bool _showFarmListLastSentTimer = FarmingDefaults.ShowLastSentTimer;
    private bool _farmListLastSentLimitEnabled = FarmingDefaults.LastSentLimitEnabled;
    private int _farmListLastSentLimitHours = FarmingDefaults.DefaultLastSentLimitHours;
    private bool _farmLossDestinationSelectionInProgress;

    // Farm lists whose last analysis read fewer target coordinates than the list claims to hold (e.g. an
    // expansion that did not finish). Their farms can be missed by the "don't add duplicates" check, so the
    // Add-farms dialog warns when this is non-empty. Format: "'Name' read/total".
    private IReadOnlyList<string> _farmListIncompleteReads = [];

    private static bool IsRealFarmListRow(FarmListStatusRow row)
        => FarmListsViewModel.IsRealRow(row);

    private bool HasFarmListWithFarms()
        => _farmLists.Any(row => IsRealFarmListRow(row) && !row.IsEmpty);

    internal static bool CanReuseRecentFarmListAnalysis(DateTimeOffset lastAnalysisAt, DateTimeOffset now)
        => lastAnalysisAt != DateTimeOffset.MinValue
            && lastAnalysisAt >= now - RecentFarmListAnalysisWindow;

    internal static string BuildFarmListVillageHeader(
        string villageName,
        IReadOnlyDictionary<string, string> villageCoordsByName)
    {
        return !string.IsNullOrWhiteSpace(villageName)
            && villageCoordsByName.TryGetValue(villageName, out var coords)
            ? $"{villageName} {coords}"
            : villageName;
    }

    private void EnsureFarmListPlaceholderRow()
        => _farmListsViewModel.EnsurePlaceholderRow();

    private void UpdateFarmingUiState()
    {
        if (!_farmingFeaturesAvailable || FarmingStatusTextBlock is null)
        {
            return;
        }

        // Farming available: the per-list rows already show every list's state, so no status line is
        // shown (the old "Loaded N farm list(s)" text is intentionally hidden here).
        FarmingStatusTextBlock.Text = string.Empty;
        FarmingStatusTextBlock.Visibility = Visibility.Collapsed;
    }

    private void SetFarmingFeatureAvailability(bool enabled, string? reason = null)
    {
        _farmingFeaturesAvailable = enabled;
        SyncFarmingControlsEnabledState();

        if (!enabled)
        {
            if (FarmingStatusTextBlock is not null)
            {
                // The status line is reserved for problems only — surface why farming is unavailable.
                FarmingStatusTextBlock.Text = string.IsNullOrWhiteSpace(reason)
                    ? "Farming is unavailable for this account."
                    : reason;
                FarmingStatusTextBlock.Visibility = Visibility.Visible;
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
        var goldClubEnabled = await _farmingPanelService.ReadAndPersistGoldClubStatusAsync(options, AppendLog, cancellationToken);
        UpdateGoldClubInfo(goldClubEnabled);
        if (!goldClubEnabled)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _farmLists.Clear();
                EnsureFarmListPlaceholderRow();
                RefreshFarmLossDestinationOptions();
                SetFarmingFeatureAvailability(false, "Farming unavailable: Gold Club is not active on this account.");
            });
            return false;
        }

        var lists = await _farmingPanelService.ReadOverviewAsync(options, AppendLog, cancellationToken) ?? [];
        await ApplyFarmListOverviewToUiAsync(lists);
        await Dispatcher.InvokeAsync(() =>
            UpdateSelectedCachedTimerStatus(status => status with { FarmLists = lists }));
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
                    villageName = item.VillageName,
                    villageIndex = item.VillageIndex,
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
        var options = LoadBotOptions();
        var defaultIntervalMinMinutes = FarmingDefaults.NormalizeDispatchDelayMinMinutes(
            options.ContinuousFarmDispatchDelayMinMinutes);
        var defaultIntervalMaxMinutes = Math.Max(
            defaultIntervalMinMinutes,
            FarmingDefaults.NormalizeDispatchDelayMaxMinutes(options.ContinuousFarmDispatchDelayMaxMinutes));
        var selectedFarmLists = LoadConfiguredContinuousFarmListNames();
        var selectedFarmListIds = LoadConfiguredContinuousFarmListIds();
        Dictionary<string, FarmListDispatchState> dispatchStates;
        try
        {
            dispatchStates = FarmListDispatchStateStore.Load(_projectRoot, _accountStore.ActiveAccountName())
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not load farm list dispatch status: {ex.Message}");
            dispatchStates = new Dictionary<string, FarmListDispatchState>(StringComparer.OrdinalIgnoreCase);
        }
        // Keyed by the stable lid (falling back to name for layouts without one) so two same-named lists
        // in different villages stay separate — a name-only key would merge them into one row/group.
        var mergedByKey = new Dictionary<string, (string Name, string? VillageName, int? VillageIndex, int Active, int Total, int? RemainingSeconds, string? ListId, int? Capacity, IReadOnlyList<string> Coordinates)>(StringComparer.OrdinalIgnoreCase);
        var orderedKeys = new List<string>();
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
            var incomingVillageName = string.IsNullOrWhiteSpace(list.VillageName) ? null : list.VillageName.Trim();
            var mergeKey = incomingListId ?? normalizedName;
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
            if (!mergedByKey.TryGetValue(mergeKey, out var existing))
            {
                orderedKeys.Add(mergeKey);
                mergedByKey[mergeKey] = (
                    Name: normalizedName,
                    VillageName: incomingVillageName,
                    VillageIndex: list.VillageIndex is >= 0 ? list.VillageIndex : null,
                    Active: Math.Max(0, list.ActiveFarmCount),
                    Total: Math.Max(0, list.TotalFarmCount),
                    RemainingSeconds: list.RemainingSeconds is > 0 ? list.RemainingSeconds : null,
                    ListId: incomingListId,
                    Capacity: list.Capacity,
                    Coordinates: incomingCoordinates);
                continue;
            }

            var incomingRemaining = list.RemainingSeconds is > 0 ? list.RemainingSeconds : null;
            mergedByKey[mergeKey] = (
                Name: existing.Name,
                VillageName: existing.VillageName ?? incomingVillageName,
                VillageIndex: existing.VillageIndex ?? (list.VillageIndex is >= 0 ? list.VillageIndex : null),
                Active: Math.Max(existing.Active, Math.Max(0, list.ActiveFarmCount)),
                Total: Math.Max(existing.Total, Math.Max(0, list.TotalFarmCount)),
                RemainingSeconds: existing.RemainingSeconds is > 0
                    ? existing.RemainingSeconds
                    : incomingRemaining,
                ListId: string.IsNullOrWhiteSpace(existing.ListId) ? incomingListId : existing.ListId,
                Capacity: existing.Capacity ?? list.Capacity,
                Coordinates: existing.Coordinates.Concat(incomingCoordinates).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        var initializedIntervals = 0;
        foreach (var key in orderedKeys)
        {
            var value = mergedByKey[key];
            var stateKey = FarmListDispatchStateStore.CreateKey(value.ListId, value.Name);
            var previous = dispatchStates.GetValueOrDefault(stateKey);
            var initialized = FarmListDispatchStateStore.WithDefaultInterval(
                previous ?? new FarmListDispatchState(null, Failed: false),
                defaultIntervalMinMinutes,
                defaultIntervalMaxMinutes);
            dispatchStates[stateKey] = initialized;
            if (previous is null || initialized != previous)
            {
                initializedIntervals++;
            }
        }

        if (initializedIntervals > 0)
        {
            try
            {
                FarmListDispatchStateStore.Save(
                    _projectRoot,
                    _accountStore.ActiveAccountName(),
                    dispatchStates);
                AppendLog($"[farm-list] initialized {initializedIntervals} list interval(s) from the shared default {defaultIntervalMinMinutes}-{defaultIntervalMaxMinutes} minutes.");
            }
            catch (Exception ex)
            {
                AppendLog($"Could not save default farm list intervals: {ex.Message}");
            }
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
                // Coordinates are not on the farm page per village, so resolve them from the known village
                // list by name — only when the name is unique (a duplicated name is ambiguous, so no coords).
                var villageCoordsByName = BuildUniqueVillageCoordsByName();
                var displayedRows = 0;
                foreach (var key in orderedKeys)
                {
                    if (displayedRows >= MaxFarmListsShown)
                    {
                        break;
                    }

                    var value = mergedByKey[key];
                    dispatchStates.TryGetValue(FarmListDispatchStateStore.CreateKey(value.ListId, value.Name), out var dispatchState);
                    var hasSelection = selectedFarmLists.Count > 0 || selectedFarmListIds.Count > 0;
                    var isSelected = !hasSelection
                        || (value.ListId is not null && selectedFarmListIds.Contains(value.ListId))
                        || selectedFarmLists.Contains(value.Name);
                    var villageName = value.VillageName ?? string.Empty;
                    var headerText = BuildFarmListVillageHeader(villageName, villageCoordsByName);

                    _farmLists.Add(new FarmListStatusRow
                    {
                        Name = value.Name,
                        VillageName = villageName,
                        VillageOrdinal = value.VillageIndex ?? -1,
                        VillageHeaderText = headerText,
                        ListId = value.ListId,
                        ActiveFarmCount = value.Active,
                        TotalFarmCount = value.Total,
                        Capacity = value.Capacity,
                        IsEnabled = isSelected,
                        RemainingSeconds = value.RemainingSeconds,
                        LastSentAtUtc = dispatchState?.LastSentAtUtc,
                        NextSendAtUtc = dispatchState?.NextSendAtUtc,
                        IntervalMinMinutesText = dispatchState?.IntervalMinMinutes?.ToString() ?? defaultIntervalMinMinutes.ToString(),
                        IntervalMaxMinutesText = dispatchState?.IntervalMaxMinutes?.ToString() ?? defaultIntervalMaxMinutes.ToString(),
                        LastSendFailed = dispatchState?.Failed == true,
                        ShowLastSentTimer = _showFarmListLastSentTimer,
                        LastSentLimitEnabled = _farmListLastSentLimitEnabled,
                        LastSentLimitHours = _farmListLastSentLimitHours,
                    });
                    _farmListCapacitiesByName[value.Name] = value.Capacity;
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

            _suppressFarmingSettingsConfigWrite = true;
            try
            {
                RefreshFarmLossDestinationOptions();
            }
            finally
            {
                _suppressFarmingSettingsConfigWrite = false;
            }

            UpdateFarmingUiState();
            SyncFarmListSelectionHandlers();
            RefreshFarmListsItemsControl();
        });

        if (mergedByKey.Count > MaxFarmListsShown)
        {
            AppendLog($"Farm list UI limited to {MaxFarmListsShown} rows (detected {mergedByKey.Count}).");
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

        var sendAllLists = string.Equals(
            FarmingDefaults.NormalizeSendMode(LoadBotOptions().ContinuousFarmSendMode),
            FarmingDefaults.SendModeAllAtOnce,
            StringComparison.Ordinal);
        var attemptedKeys = sendAllLists
            ? _farmLists
                .Where(row => IsRealFarmListRow(row) &&
                    FarmListDispatchStateStore.ShouldTrackDispatch(
                        true,
                        row.IsEnabled,
                        row.IsReady,
                        row.IsEmpty))
                .Select(FarmListDispatchKey)
                .ToList()
            : [];

        try
        {
            // On a real send the worker just read the farm page and wrote a fresh snapshot — apply
            // it directly so the UI updates instantly without navigating the browser again. On a
            // rename ("not found") there is no fresh snapshot, so fall back to a full re-analyze.
            if (sendHappened && await TryApplyFarmListsSnapshotAsync())
            {
                if (sendAllLists)
                {
                    ReconcileFarmListDispatches(attemptedKeys);
                }
                return;
            }

            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await RefreshFarmListsFromServerAsync(options, _loopController.AcquireSessionScopeToken());
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
                FarmCoordinates: entry.FarmCoordinates ?? [],
                VillageName: string.IsNullOrWhiteSpace(entry.VillageName) ? null : entry.VillageName,
                VillageIndex: entry.VillageIndex))
            .ToList();

        await ApplyFarmListOverviewToUiAsync(lists);
        return true;
    }

    // Restores the last analyzed farm lists from the persisted snapshot at startup / after an account
    // switch, so the farming panel is never blank when lists were already analyzed in a prior session.
    // Unlike TryApplyFarmListsSnapshotAsync (post-send, freshness-gated) this accepts a snapshot of any
    // age: timers are re-based on the capture time so a stale countdown never keeps ticking from an old
    // value, and _lastFarmListsAnalysisAt stays MinValue so a real re-analyze is still triggered when due.
    private async Task RestoreFarmListsFromSnapshotForActiveAccount()
    {
        var snapshotPath = AccountStoragePaths.FarmListsSnapshotPath(_projectRoot, _accountStore.ActiveAccountName());
        if (!File.Exists(snapshotPath))
        {
            return;
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
            AppendLog($"Could not restore saved farm lists: {ex.Message}");
            return;
        }

        if (snapshot?.Lists is null || snapshot.Lists.Count == 0)
        {
            return;
        }

        // Re-base each timer against the capture time so restored countdowns reflect elapsed time.
        var elapsedSeconds = snapshot.CapturedAtUtc is { } capturedAt
            ? Math.Max(0, (int)(DateTimeOffset.UtcNow - capturedAt).TotalSeconds)
            : 0;
        var lists = snapshot.Lists
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry =>
            {
                var remaining = entry!.RemainingSeconds is > 0
                    ? Math.Max(0, entry.RemainingSeconds.Value - elapsedSeconds)
                    : entry.RemainingSeconds;
                return new FarmListOverview(
                    Name: entry.Name!,
                    ActiveFarmCount: entry.ActiveFarmCount,
                    TotalFarmCount: entry.TotalFarmCount,
                    RemainingSeconds: remaining is > 0 ? remaining : null,
                    ListId: string.IsNullOrWhiteSpace(entry.ListId) ? null : entry.ListId,
                    Capacity: entry.Capacity,
                    FarmCoordinates: entry.FarmCoordinates ?? [],
                    VillageName: string.IsNullOrWhiteSpace(entry.VillageName) ? null : entry.VillageName,
                    VillageIndex: entry.VillageIndex);
            })
            .ToList();
        if (lists.Count == 0)
        {
            return;
        }

        await ApplyFarmListOverviewToUiAsync(lists);
        // A restore is not a fresh analyze: keep the marker unset so the continuous loop still runs one.
        await Dispatcher.InvokeAsync(() => _lastFarmListsAnalysisAt = DateTimeOffset.MinValue);
        AppendLog($"[farm-list] restored {lists.Count} saved farm list(s) from the last analysis.");
    }

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
        SetFarmingFunctionRunning(true);
        BusyOverlay.ShowCancel = true;
        ShowBusyOverlay("Analyze farmlists", "Reading current farmlists...");
        BeginManualFunctionPacingPause();
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
                FarmingStatusTextBlock.Visibility = Visibility.Visible;
            }
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            EndManualFunctionPacingPause();
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
        BeginManualFunctionPacingPause();
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
                var ownIdentity = await _farmingPanelService.ReadTargetProtectionIdentityAsync(
                    options,
                    AppendLog,
                    cancellationToken);
                return new OfficialAddFarmsLoadResult(
                    true,
                    null,
                    sourceLists,
                    targetLists,
                    new HashSet<string>(_analyzedFarmCoordinates, StringComparer.OrdinalIgnoreCase),
                    _farmListIncompleteReads,
                    ownIdentity);
            }

            async Task<OfficialFarmAddRunResult> RunOfficialPlansAsync(
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

                    AppendLog(
                        $"Add farms from Travco: target='{plan.TargetName}', requested={plan.DesiredCount}, " +
                        $"candidates={plan.Coordinates.Count}, " +
                        $"troops={(useDefaultTroops ? "default" : $"{troopCount} {troopType}")}.");
                    var result = await _farmingPanelService.AddFarmsAsync(
                        options,
                        plan.TargetName,
                        troopType,
                        troopCount,
                        plan.DesiredCount,
                        plan.Coordinates,
                        useDefaultTroops,
                        protection,
                        AppendLog,
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
                    AppendLog(
                        $"Finished '{plan.TargetName}': added={result.AddedCount}, " +
                        $"duplicates={result.AlreadyInListCount}, invalid={result.NotFoundCount}, " +
                        $"occupiedSkipped={result.OccupiedOasisSkippedCount}, " +
                        $"excludedPlayers={result.ExcludedPlayerCount}, excludedAlliances={result.ExcludedAllianceCount}, " +
                        $"identityUnavailable={result.IdentityUnavailableCount}, failed={result.FailedCount}.");
                }

                return new OfficialFarmAddRunResult(
                    requested,
                    added,
                    duplicates,
                    failed,
                    invalidCoordinates
                        .Distinct()
                        .ToList(),
                    OccupiedSkipped: occupiedSkipped,
                    ExcludedPlayers: excludedPlayers,
                    ExcludedAlliances: excludedAlliances,
                    IdentityUnavailable: identityUnavailable);
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
                LoadAddFarmsProtectionPreferences(),
                SaveAddFarmsProtectionPreferences,
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
            HideBusyOverlay();

            // Single modern completion popup: the run summary as stat tiles, and — only when dead villages
            // were found — the "remove them from the Travco list?" question baked into the same dialog
            // (Keep / Remove them) so the user never sees two separate popups.
            var elapsed = officialDialog.RunDuration;
            var completeWindow = new AddFarmsCompleteWindow(
                this,
                runResult.Added,
                runResult.Duplicates,
                runResult.OccupiedSkipped,
                runResult.ExcludedPlayers,
                runResult.ExcludedAlliances,
                runResult.IdentityUnavailable,
                runResult.Failed,
                elapsed,
                runResult.InvalidCoordinates.Count,
                runResult.SourceListName);
            completeWindow.ShowDialog();
            if (runResult.InvalidCoordinates.Count > 0 && completeWindow.RemoveInvalidCoordinates)
            {
                var removed = _travcoListStore.RemoveRowsByCoordinates(
                    runResult.SourceListId,
                    runResult.InvalidCoordinates);
                AppendLog(
                    $"Removed {removed}/{runResult.InvalidCoordinates.Count} invalid coordinate(s) " +
                    $"from Travco list '{runResult.SourceListName}'.");
            }

            CompleteOperation(
                operationId,
                operationSw,
                $"Added {runResult.Added}; duplicates {runResult.Duplicates}; occupied skipped {runResult.OccupiedSkipped}; " +
                $"players skipped {runResult.ExcludedPlayers}; alliances skipped {runResult.ExcludedAlliances}; " +
                $"identity unavailable {runResult.IdentityUnavailable}; failed {runResult.Failed}.");

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
            EndManualFunctionPacingPause();
            HideBusyOverlay();
            SetFarmingFunctionRunning(false);
            DisposeOperationCts();
        }
    }

    private string ResolveCurrentTribeForFarming()
    {
        return ResolveStoredTroopTrainingTribe();
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
        BeginManualFunctionPacingPause();
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
                return await _farmingPanelService.CreateListsAsync(
                    options,
                    request,
                    AppendLog,
                    progress,
                    cancellationToken);
            }

            var dialog = new CreateFarmListsWindow(
                ResolveCurrentTribeForFarming(),
                villages,
                options.FarmListOnlyCreateReportsWithLosses,
                SaveFarmListOnlyCreateReportsWithLosses,
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

            var createdCount = dialog.RunResult.CreatedCount;
            AppDialog.ShowCustom(
                this,
                $"{createdCount} farmlist{(createdCount == 1 ? " was" : "s were")} created.",
                "Create farmlists complete",
                [("OK", MessageBoxResult.OK)],
                MessageBoxImage.Information,
                defaultResult: MessageBoxResult.OK,
                cancelResult: MessageBoxResult.OK,
                successResult: MessageBoxResult.OK);

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
            EndManualFunctionPacingPause();
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

    // Maps village name -> "(x | y)" for names that identify exactly one village. Duplicate names are left
    // out (ambiguous — the farm page carries no coordinates to disambiguate them), so the group heading for
    // a same-named village falls back to the bare name and the two villages still stay in separate groups.
    private Dictionary<string, string> BuildUniqueVillageCoordsByName()
    {
        var coordsByName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var village in GetFarmListCreationVillages())
        {
            if (string.IsNullOrWhiteSpace(village.Name) || village.CoordX is null || village.CoordY is null)
            {
                continue;
            }

            var name = village.Name.Trim();
            var coords = $"({village.CoordX} | {village.CoordY})";
            if (coordsByName.TryGetValue(name, out var existing))
            {
                if (!string.Equals(existing, coords, StringComparison.Ordinal))
                {
                    coordsByName[name] = null; // Same name, different coordinates -> ambiguous.
                }
            }
            else
            {
                coordsByName[name] = coords;
            }
        }

        return coordsByName
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshFarmListVillageHeaders()
    {
        if (!_farmLists.Any(IsRealFarmListRow))
        {
            return;
        }

        var villageCoordsByName = BuildUniqueVillageCoordsByName();
        foreach (var row in _farmLists.Where(IsRealFarmListRow))
        {
            row.VillageHeaderText = BuildFarmListVillageHeader(row.VillageName, villageCoordsByName);
        }

        CollectionViewSource.GetDefaultView(_farmLists).Refresh();
    }

    private async void FarmListSendNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FarmListStatusRow list })
        {
            await GuardUiAsync(() => FarmListSendNowButtonClickAsync(list));
        }
    }

    private async Task FarmListSendNowButtonClickAsync(FarmListStatusRow list)
    {
        if (BlockIfSessionSleeping("Farm send now"))
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
        SetFarmingFunctionRunning(true);
        BusyOverlay.ShowCancel = true;
        ShowBusyOverlay("Send now", $"Sending '{list.Name}'...");
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            var timerSeconds = await _farmingPanelService.SendOneAsync(options, list.Name, AppendLog, operationToken);
            list.RemainingSeconds = timerSeconds is > 0 ? timerSeconds : null;
            RecordFarmListDispatch(list, succeeded: true);
            UpdateFarmingUiState();
            CompleteOperation(operationId, operationSw, $"Sent '{list.Name}'.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Farm list send paused.");
        }
        catch (Exception ex)
        {
            RecordFarmListDispatch(list, succeeded: false);
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

        if (!HasFarmListWithFarms())
        {
            AppendLog("[farm-list] Send all now ignored: no farms are available in the loaded lists.");
            return;
        }

        // Let the user pick how to send: only the toggled lists (paced, like continuous farming) or every
        // list at once via Travian's "Start all farm lists" button.
        var chooser = new SendAllFarmListsWindow(this);
        if (chooser.ShowDialog() != true || chooser.Choice == SendAllFarmListsWindow.SendAllChoice.Cancel)
        {
            return;
        }

        var sendToggled = chooser.Choice == SendAllFarmListsWindow.SendAllChoice.Toggled;
        List<string> toggledNames = [];
        List<string> toggledIds = [];
        if (sendToggled)
        {
            var enabledRows = _farmLists.Where(row => IsRealFarmListRow(row) && row.IsEnabled).ToList();
            toggledNames = enabledRows
                .Select(row => row.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            toggledIds = enabledRows
                .Select(row => row.ListId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (toggledNames.Count == 0 && toggledIds.Count == 0)
            {
                AppendLog("[farm-list] Send all toggled: no farm lists are toggled on.");
                AppDialog.Show(this, "No farm lists are toggled on.", "Send farmlists", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var attemptedKeys = _farmLists
            .Where(row => IsRealFarmListRow(row) && !row.IsEmpty && row.IsReady && (!sendToggled || row.IsEnabled))
            .Select(FarmListDispatchKey)
            .ToList();

        var operationId = BeginOperation("Farm Send All Now");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        SetFarmingFunctionRunning(true);
        BusyOverlay.ShowCancel = true;
        ShowBusyOverlay("Send all now", sendToggled ? "Sending toggled farmlists..." : "Sending all farmlists...");
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            var sentCount = sendToggled
                ? await _farmingPanelService.SendSelectedAsync(options, toggledNames, toggledIds, AppendLog, operationToken)
                : await _farmingPanelService.SendAllAsync(options, AppendLog, operationToken);
            await RefreshFarmListsFromServerAsync(options, operationToken);
            ReconcileFarmListDispatches(attemptedKeys);
            CompleteOperation(operationId, operationSw, $"Sent {(sendToggled ? "toggled" : "all")} farmlists ({sentCount} list(s)).");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Farm list send-all paused.");
        }
        catch (Exception ex)
        {
            foreach (var row in _farmLists.Where(row => attemptedKeys.Contains(FarmListDispatchKey(row))))
            {
                RecordFarmListDispatch(row, succeeded: false);
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

    private void SyncFarmingControlsEnabledState()
    {
        var sleepAllowsActions = !IsSessionSleeping;
        var farmControlsEnabled = sleepAllowsActions && !_farmingOperationBusy && _farmingFeaturesAvailable;
        SetEnabled(AddFarmsToListButton, farmControlsEnabled);
        SetEnabled(CreateFarmListButton, sleepAllowsActions && !_farmingOperationBusy);
        SetEnabled(FarmListsItemsControl, farmControlsEnabled);
        SetEnabled(FarmListSendAllNowButton, farmControlsEnabled && HasFarmListWithFarms());
        SetEnabled(AnalyzeFarmListsButton, sleepAllowsActions && !_farmingOperationBusy);
        SetEnabled(StartCatapultWavesButton, sleepAllowsActions && !_farmingOperationBusy);
        _farmListsViewModel.UpdateCommandAvailability(
            sleepAllowsActions && !_farmingOperationBusy,
            farmControlsEnabled,
            sleepAllowsActions && !_farmingOperationBusy,
            farmControlsEnabled && HasFarmListWithFarms());
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
            EnsureFarmListPlaceholderRow();
            var view = CollectionViewSource.GetDefaultView(_farmLists);
            // Group rows under their owning village so each village gets its own heading. Grouped by the
            // village ordinal (not name) so two villages sharing a display name stay in separate groups; the
            // placeholder row (ordinal -1, empty header) forms one group whose header is hidden.
            if (view is not null && view.GroupDescriptions.Count == 0)
            {
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FarmListStatusRow.VillageOrdinal)));
            }

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

        if (sender is not FarmListStatusRow row)
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(FarmListStatusRow.IsEnabled), StringComparison.Ordinal))
        {
            PersistContinuousFarmListSelectionToConfig();
            RefreshQueuedContinuousFarmListSelections();
        }
        else if (string.Equals(e.PropertyName, nameof(FarmListStatusRow.IntervalMinMinutesText), StringComparison.Ordinal) ||
                 string.Equals(e.PropertyName, nameof(FarmListStatusRow.IntervalMaxMinutesText), StringComparison.Ordinal))
        {
            if (!row.TryGetDispatchInterval(out var minMinutes, out var maxMinutes))
            {
                UpdateFarmingUiState();
                return;
            }

            PersistFarmListDispatchInterval(row, minMinutes!.Value, maxMinutes!.Value);
        }
        else
        {
            return;
        }

        UpdateAutomationLoopRunningIndicators();
        UpdateFarmingUiState();
    }

    private void RefreshQueuedContinuousFarmListSelections()
    {
        var enabledRows = _farmLists.Where(item => IsRealFarmListRow(item) && item.IsEnabled).ToList();
        var selection = new FarmingPayload(
            enabledRows.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
            enabledRows.Select(item => item.ListId).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!.Trim()).ToList());
        var updatedCount = 0;

        foreach (var item in _botService.GetQueueItemsForDisplay())
        {
            if (!string.Equals(item.TaskName, "send_farmlists", StringComparison.OrdinalIgnoreCase)
                || item.Status != QueueStatus.Pending
                || string.IsNullOrWhiteSpace(GetQueueItemVillageKey(item)))
            {
                continue;
            }

            var updatedPayload = selection.ApplySelectionTo(item.Payload);
            if (ContinuousLoopSelector.PayloadEquals(item.Payload, updatedPayload))
            {
                continue;
            }

            if (_botService.UpdateDeferredQueueItem(item.Id, updatedPayload))
            {
                updatedCount++;
            }
        }

        if (updatedCount > 0)
        {
            AppendLog($"[farm-list] applied the updated toggle selection to {updatedCount} queued automatic farm-list send(s).");
        }
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
        _showFarmListLastSentTimer = options.ShowFarmListLastSentTimer;
        _farmListLastSentLimitEnabled = options.FarmListLastSentLimitEnabled;
        _farmListLastSentLimitHours = FarmingDefaults.NormalizeLastSentLimitHours(options.FarmListLastSentLimitHours);
        foreach (var row in _farmLists.Where(IsRealFarmListRow))
        {
            row.ShowLastSentTimer = _showFarmListLastSentTimer;
            row.LastSentLimitEnabled = _farmListLastSentLimitEnabled;
            row.LastSentLimitHours = _farmListLastSentLimitHours;
        }

        _suppressFarmingSettingsConfigWrite = true;
        try
        {
            var mode = FarmingDefaults.NormalizeSendMode(options.ContinuousFarmSendMode);
            _farmListsViewModel.LoadSettings(
                mode,
                options.ContinuousFarmDispatchDelayMinMinutes,
                options.ContinuousFarmDispatchDelayMaxMinutes,
                options.ContinuousFarmDeactivateRedLosses,
                options.ContinuousFarmDeactivateYellowLosses,
                options.ContinuousFarmDeactivateRedOasisLosses,
                options.ContinuousFarmDeactivateYellowOasisLosses,
                options.ContinuousFarmMoveRedLosses,
                options.ContinuousFarmMoveYellowLosses);
            RefreshFarmLossDestinationOptions(options);
        }
        finally
        {
            _suppressFarmingSettingsConfigWrite = false;
        }
    }

    private void PersistFarmingSettings()
    {
        if (_suppressFarmingSettingsConfigWrite)
        {
            return;
        }

        try
        {
            var delayMinMinutes = FarmingDefaults.NormalizeDispatchDelayMinMinutes(
                int.TryParse(_farmListsViewModel.DispatchDelayMinMinutes, out var parsedMin) ? parsedMin : 0);
            var delayMaxMinutes = Math.Max(
                delayMinMinutes,
                FarmingDefaults.NormalizeDispatchDelayMaxMinutes(
                    int.TryParse(_farmListsViewModel.DispatchDelayMaxMinutes, out var parsedMax) ? parsedMax : 0));
            var saved = _farmingPanelService.SaveSettings(new FarmingPanelSettings(
                _farmListsViewModel.SendMode,
                delayMinMinutes,
                delayMaxMinutes,
                _farmListsViewModel.DeactivateRedLosses,
                _farmListsViewModel.DeactivateYellowLosses,
                _farmListsViewModel.DeactivateRedOasisLosses,
                _farmListsViewModel.DeactivateYellowOasisLosses,
                _farmListsViewModel.MoveRedLosses,
                _farmListsViewModel.MoveYellowLosses,
                _farmListsViewModel.SelectedRedLossDestination,
                _farmListsViewModel.SelectedYellowLossDestination));
            AppendLog($"[farm-settings] mode={saved.SendMode}; delay={saved.DelayMinMinutes}-{saved.DelayMaxMinutes}m; deactivateRed={_farmListsViewModel.DeactivateRedLosses}; deactivateYellow={_farmListsViewModel.DeactivateYellowLosses}; oasisRed={_farmListsViewModel.DeactivateRedOasisLosses}; oasisYellow={_farmListsViewModel.DeactivateYellowOasisLosses}; moveRed={saved.MoveRedLossesEnabled}; redDestination='{saved.RedDestinationName ?? "-"}'; moveYellow={saved.MoveYellowLossesEnabled}; yellowDestination='{saved.YellowDestinationName ?? "-"}'");
            UpdateAutomationLoopRunningIndicators();
            RefreshQueuedFarmLossDestinationSettings();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save farm settings: {ex.Message}");
        }
    }

    private void FarmingSettings_Changed(object sender, RoutedEventArgs e)
        => PersistFarmingSettings();

    private void UpdateNextFarmListSendDisplay()
    {
        var farmingCard = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Farming), StringComparison.OrdinalIgnoreCase));
        var hasScheduledSend = farmingCard?.HasTimer == true;
        FarmingPanelControl.SetNextSendDisplay(hasScheduledSend
            ? $"Next send: {farmingCard!.TimerText}"
            : "Next send: --");
    }

    private static string FarmListDispatchKey(FarmListStatusRow row)
        => FarmListDispatchStateStore.CreateKey(row.ListId, row.Name);

    private void RecordFarmListDispatch(FarmListStatusRow row, bool succeeded)
    {
        try
        {
            var key = FarmListDispatchKey(row);
            var options = LoadBotOptions();
            var state = FarmListDispatchStateStore.Update(
                _projectRoot,
                _accountStore.ActiveAccountName(),
                key,
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
                        NextSendAtUtc = sentAtUtc.AddSeconds(CalculateFarmListDispatchDelaySeconds(previous, options)),
                    };
                });
            row.LastSentAtUtc = state.LastSentAtUtc;
            row.NextSendAtUtc = state.NextSendAtUtc;
            row.LastSendFailed = state.Failed;
            if (succeeded)
            {
                WakeContinuousFarmScheduling();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save farm list dispatch status: {ex.Message}");
        }
    }

    private void PersistFarmListDispatchInterval(FarmListStatusRow row, int minMinutes, int maxMinutes)
    {
        try
        {
            var options = LoadBotOptions();
            var state = FarmListDispatchStateStore.Update(
                _projectRoot,
                _accountStore.ActiveAccountName(),
                FarmListDispatchKey(row),
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
                            CalculateFarmListDispatchDelaySeconds(updated, options)),
                    };
                });
            row.NextSendAtUtc = state.NextSendAtUtc;
            AppendLog($"[farm-list] '{row.Name}' dispatch interval set to {minMinutes}-{maxMinutes} minutes.");
            WakeContinuousFarmScheduling();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save farm list dispatch interval: {ex.Message}");
        }
    }

    private static int CalculateFarmListDispatchDelaySeconds(FarmListDispatchState state, BotOptions options)
    {
        var initialized = FarmListDispatchStateStore.WithDefaultInterval(
            state,
            FarmingDefaults.NormalizeDispatchDelayMinMinutes(options.ContinuousFarmDispatchDelayMinMinutes),
            FarmingDefaults.NormalizeDispatchDelayMaxMinutes(options.ContinuousFarmDispatchDelayMaxMinutes));
        var minMinutes = initialized.IntervalMinMinutes!.Value;
        var maxMinutes = initialized.IntervalMaxMinutes!.Value;
        return FarmingDefaults.CalculateDispatchDelaySeconds(minMinutes, Math.Max(minMinutes, maxMinutes));
    }

    private void WakeContinuousFarmScheduling()
    {
        var updated = false;
        foreach (var item in _botService.GetQueueItemsForDisplay())
        {
            if (string.Equals(item.TaskName, "send_farmlists", StringComparison.OrdinalIgnoreCase) &&
                item.Status == QueueStatus.Pending &&
                _botService.UpdateDeferredQueueItem(item.Id, item.Payload, TimeSpan.Zero))
            {
                updated = true;
            }
        }

        if (updated)
        {
            _automationDesk.Wake(AutomationWakeReason.QueueChanged);
        }
    }

    private void ReconcileFarmListDispatches(IReadOnlyCollection<string> attemptedKeys)
    {
        foreach (var row in _farmLists.Where(IsRealFarmListRow))
        {
            if (attemptedKeys.Contains(FarmListDispatchKey(row)))
            {
                RecordFarmListDispatch(row, FarmListDispatchStateStore.IsSuccessfulDispatch(
                    sendActionCompleted: true,
                    row.RemainingSeconds));
            }
        }
    }

    private void OnFarmLossDestinationChanged(FarmLossDestinationChange change)
    {
        if (!string.Equals(change.AccountName, _accountStore.ActiveAccountName(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var config = _botConfigStore.Load();
                var isRed = change.LossColors == FarmListLossColors.Red;
                config[isRed ? BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListId : BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListId] = change.ListId;
                config[isRed ? BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListName : BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListName] = change.ListName;
                config[isRed ? BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName : BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationBaseName] = change.BaseName;
                config[isRed ? BotOptionPayloadKeys.ContinuousFarmMoveRedLosses : BotOptionPayloadKeys.ContinuousFarmMoveYellowLosses] = true;
                _botConfigStore.Save(config);
                SelectChangedFarmLossDestination(change);
                RefreshQueuedFarmLossDestinationSettings();
                AppendLog($"[farm-list] {change.LossColors.ToString().ToLowerInvariant()} loss destination changed to '{change.ListName}' ({change.ListId}).");
            }
            catch (Exception ex)
            {
                AppendLog($"ALARM: Could not persist loss destination change: {ex.Message}");
            }
        });
    }

    private void SelectChangedFarmLossDestination(FarmLossDestinationChange change)
    {
        var options = _farmListsViewModel.LossDestinations
            .Where(option => !string.Equals(option.ListId, change.ListId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var selected = new FarmLossDestinationOption(change.ListId, change.ListName, change.VillageName, 0, 100);
        options.Add(selected);

        _suppressFarmingSettingsConfigWrite = true;
        try
        {
            _farmListsViewModel.ReplaceLossDestinations(
                options,
                change.LossColors == FarmListLossColors.Red ? selected : _farmListsViewModel.SelectedRedLossDestination,
                change.LossColors == FarmListLossColors.Yellow ? selected : _farmListsViewModel.SelectedYellowLossDestination);
            if (change.LossColors == FarmListLossColors.Red)
                _farmListsViewModel.MoveRedLosses = true;
            else
                _farmListsViewModel.MoveYellowLosses = true;
        }
        finally
        {
            _suppressFarmingSettingsConfigWrite = false;
        }
    }

    private void RefreshQueuedFarmLossDestinationSettings()
    {
        var options = LoadBotOptions();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ContinuousFarmDeactivateRedLosses] = options.ContinuousFarmDeactivateRedLosses.ToString(),
            [BotOptionPayloadKeys.ContinuousFarmDeactivateYellowLosses] = options.ContinuousFarmDeactivateYellowLosses.ToString(),
            [BotOptionPayloadKeys.ContinuousFarmDeactivateRedOasisLosses] = options.ContinuousFarmDeactivateRedOasisLosses.ToString(),
            [BotOptionPayloadKeys.ContinuousFarmDeactivateYellowOasisLosses] = options.ContinuousFarmDeactivateYellowOasisLosses.ToString(),
            [BotOptionPayloadKeys.ContinuousFarmMoveRedLosses] = options.ContinuousFarmMoveRedLosses.ToString(),
            [BotOptionPayloadKeys.ContinuousFarmMoveYellowLosses] = options.ContinuousFarmMoveYellowLosses.ToString(),
            [BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListId] = options.ContinuousFarmRedLossDestinationListId,
            [BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListName] = options.ContinuousFarmRedLossDestinationListName,
            [BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName] = options.ContinuousFarmRedLossDestinationBaseName,
            [BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListId] = options.ContinuousFarmYellowLossDestinationListId,
            [BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListName] = options.ContinuousFarmYellowLossDestinationListName,
            [BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationBaseName] = options.ContinuousFarmYellowLossDestinationBaseName,
        };

        foreach (var item in _botService.GetQueueItemsForDisplay())
        {
            if (!string.Equals(item.TaskName, "send_farmlists", StringComparison.OrdinalIgnoreCase)
                || item.Status != QueueStatus.Pending)
            {
                continue;
            }

            var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                payload[pair.Key] = pair.Value;
            }
            _botService.UpdateDeferredQueueItem(item.Id, payload);
        }
    }

    private void RefreshFarmLossDestinationOptions(BotOptions? loadedOptions = null)
    {
        loadedOptions ??= LoadBotOptions();
        var options = _farmLists
            .Where(IsRealFarmListRow)
            .Where(row => !string.IsNullOrWhiteSpace(row.ListId))
            .Select(row => new FarmLossDestinationOption(
                row.ListId!.Trim(),
                row.Name.Trim(),
                row.VillageName?.Trim() ?? string.Empty,
                Math.Max(0, row.TotalFarmCount),
                row.Capacity is > 0 ? row.Capacity.Value : 100))
            .ToList();

        FarmLossDestinationOption? ResolveSelected(string listId, string listName)
        {
            var selected = options.FirstOrDefault(option =>
                    !string.IsNullOrWhiteSpace(listId)
                    && string.Equals(option.ListId, listId, StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault(option =>
                    !string.IsNullOrWhiteSpace(listName)
                    && string.Equals(option.Name, listName, StringComparison.OrdinalIgnoreCase));
            if (selected is not null || string.IsNullOrWhiteSpace(listName))
                return selected;

            selected = new FarmLossDestinationOption(listId, listName, "Missing", 0, 100);
            options.Add(selected);
            return selected;
        }

        var selectedRed = ResolveSelected(loadedOptions.ContinuousFarmRedLossDestinationListId, loadedOptions.ContinuousFarmRedLossDestinationListName);
        var selectedYellow = ResolveSelected(loadedOptions.ContinuousFarmYellowLossDestinationListId, loadedOptions.ContinuousFarmYellowLossDestinationListName);
        _farmListsViewModel.ReplaceLossDestinations(options, selectedRed, selectedYellow);
    }

    private async Task EnsureFarmLossDestinationSelectedAsync(FarmListLossColors lossColor)
    {
        var isRed = lossColor == FarmListLossColors.Red;
        if (_suppressFarmingSettingsConfigWrite
            || !(isRed ? _farmListsViewModel.MoveRedLosses : _farmListsViewModel.MoveYellowLosses)
            || _farmLossDestinationSelectionInProgress)
        {
            return;
        }

        if (!(isRed ? _farmListsViewModel.DeactivateRedLosses : _farmListsViewModel.DeactivateYellowLosses))
        {
            SetMoveLosses(isRed, false);
            return;
        }

        if (!_isLoggedIn)
        {
            SetMoveLosses(isRed, false);
            AppendLog("[farm-list] loss destination setup blocked because the user is logged out.");
            AppDialog.Show(
                this,
                "You must log in first.",
                "Login required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // A configured destination remains usable even when it is represented by the "Missing" placeholder.
        // The Worker owns the overnight self-heal and recreates that list by its persisted base name.
        if ((isRed ? _farmListsViewModel.SelectedRedLossDestination : _farmListsViewModel.SelectedYellowLossDestination) is not null)
        {
            return;
        }

        if (BlockIfSessionSleeping("Choose loss farmlist"))
        {
            SetMoveLosses(isRed, false);
            return;
        }

        var resumeContinuous = IsContinuousLoopRunning() || _startContinuousLoopAfterQueueStop;
        var resumeQueue = !resumeContinuous && _autoQueueRunning;
        var operationToken = _loopController.StartOperation("loss-farmlist-destination");
        _farmLossDestinationSelectionInProgress = true;
        BeginManualFunctionPacingPause();
        BusyOverlay.ShowCancel = true;
        ShowBusyOverlay("Choose loss farmlist", "Pausing automation after the current action...");
        try
        {
            await PauseAutomationForFarmLossDestinationAsync(
                resumeContinuous || resumeQueue,
                operationToken);

            BusyOverlay.Text = "Reading all existing farmlists...";
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            if (!await RefreshFarmListsFromServerAsync(options, operationToken))
            {
                throw new InvalidOperationException("Gold Club is not active, so existing farmlists could not be loaded.");
            }

            AppendLog("[farm-list] read all existing farmlists before choosing a loss destination.");
            HideBusyOverlay();

            var existingNames = _farmLists.Where(IsRealFarmListRow).Select(row => row.Name).ToList();
            var suggestedName = FarmLossListNaming.NextAvailable(isRed ? "Red farms" : "Yellow farms", existingNames);
            var existingDestinations = _farmListsViewModel.LossDestinations.ToList();
            var dialog = new CreateLossFarmListWindow(suggestedName, existingDestinations) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                SetMoveLosses(isRed, false);
                AppendLog("[farm-list] loss destination selection canceled.");
                return;
            }

            if (dialog.SelectedExistingDestination is { } selectedDestination)
            {
                var selected = existingDestinations.FirstOrDefault(option =>
                    string.Equals(option.ListId, selectedDestination.ListId, StringComparison.OrdinalIgnoreCase))
                    ?? selectedDestination;
                SetSelectedLossDestination(isRed, selected);
                AppendLog($"[farm-list] selected '{selectedDestination.Name}' as the {lossColor.ToString().ToLowerInvariant()} loss destination.");
                return;
            }

            await CreateAndSelectFarmLossDestinationAsync(options, lossColor, dialog.ListName, operationToken);
        }
        catch (OperationCanceledException)
        {
            SetMoveLosses(isRed, false);
            AppendLog("[farm-list] loss destination setup canceled.");
        }
        catch (Exception ex)
        {
            SetMoveLosses(isRed, false);
            HideBusyOverlay();
            AppendLog($"ALARM: Could not configure the loss farmlist: {ex.Message}");
            AppDialog.Show(this, ex.Message, "Choose loss farmlist", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            HideBusyOverlay();
            EndManualFunctionPacingPause();
            DisposeOperationCts();
            _farmLossDestinationSelectionInProgress = false;
            await ResumeAutomationAfterFarmLossDestinationAsync(resumeContinuous, resumeQueue);
        }
    }

    private async Task PauseAutomationForFarmLossDestinationAsync(
        bool automationWasRunning,
        CancellationToken cancellationToken)
    {
        if (!automationWasRunning)
        {
            AppendLog("[farm-list] bot already paused; starting loss destination setup.");
            return;
        }

        _startContinuousLoopAfterQueueStop = false;
        _restartContinuousLoopAfterStop = false;
        RequestAutomationStop(AutomationStopMode.AfterCurrentAction);
        UpdateExecutionStateIndicator();
        AppendLog("[farm-list] pause requested; waiting for the current bot action to finish.");

        while (_autoQueueRunning || IsContinuousLoopRunning() || _uiBusy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken);
        }

        // Auto-queue clears its running flag immediately before releasing its execution gate.
        // Give that finally block one UI-sized beat so a resume cannot race the old gate lease.
        await Task.Delay(100, cancellationToken);
        AppendLog("[farm-list] automation paused; loss destination setup has priority.");
    }

    private async Task ResumeAutomationAfterFarmLossDestinationAsync(
        bool resumeContinuous,
        bool resumeQueue)
    {
        if (!resumeContinuous && !resumeQueue)
        {
            return;
        }

        if (_loopController.IsClosing || !_isLoggedIn || IsSessionSleeping || IsFreezeActive)
        {
            AppendLog("[farm-list] automation was not resumed because the session is unavailable.");
            return;
        }

        if (resumeContinuous && !IsContinuousLoopRunning())
        {
            AppendLog("[farm-list] resuming continuous loop after loss destination setup.");
            StartContinuousLoopRunner();
            return;
        }

        if (resumeQueue && !_autoQueueRunning)
        {
            AppendLog("[farm-list] resuming queue auto-run after loss destination setup.");
            await TriggerQueueAutoRunAsync();
        }
    }

    private async Task CreateAndSelectFarmLossDestinationAsync(
        BotOptions options,
        FarmListLossColors lossColor,
        string listName,
        CancellationToken cancellationToken)
    {
        var villages = GetFarmListCreationVillages();
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

        var tribe = TroopCatalog.IsKnownTribe(village.Tribe) ? village.Tribe : ResolveCurrentTribeForFarming();
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

        BusyOverlay.ShowCancel = true;
        ShowBusyOverlay("Creating loss farmlist", $"Creating '{listName}'...");
        await EnsureChromiumInstalledAsync();
        var createResult = await _farmingPanelService.CreateListsAsync(
            options,
            request,
            AppendLog,
            null,
            cancellationToken);
        if (createResult.CreatedCount != 1
            || !createResult.CreatedNames.Contains(listName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Travian did not confirm creation of farmlist '{listName}'.");
        }

        await RefreshFarmListsFromServerAsync(options, cancellationToken);
        var created = _farmListsViewModel.LossDestinations
            .FirstOrDefault(item => string.Equals(item.Name, listName, StringComparison.OrdinalIgnoreCase));
        if (created is null)
        {
            // The panel limits displayed rows, so verify the complete overview before reporting failure.
            var verifiedLists = await _farmingPanelService.ReadOverviewAsync(options, AppendLog, cancellationToken);
            var verified = verifiedLists.FirstOrDefault(item =>
                string.Equals(item.Name, listName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.VillageName, village.Name, StringComparison.OrdinalIgnoreCase));
            if (verified is null || string.IsNullOrWhiteSpace(verified.ListId))
            {
                throw new InvalidOperationException($"Created farmlist '{listName}' could not be verified after refresh.");
            }

            created = new FarmLossDestinationOption(
                verified.ListId.Trim(),
                verified.Name.Trim(),
                verified.VillageName?.Trim() ?? village.Name,
                Math.Max(0, verified.TotalFarmCount),
                verified.Capacity is > 0 ? verified.Capacity.Value : 100);
            _farmListsViewModel.LossDestinations.Add(created);
        }

        var isRed = lossColor == FarmListLossColors.Red;
        _farmingPanelService.SaveDestinationBaseName(isRed, listName);
        SetSelectedLossDestination(isRed, created);
        AppendLog($"[farm-list] created and selected '{created.Name}' as the {lossColor.ToString().ToLowerInvariant()} loss destination.");
    }

    private void SetMoveLosses(bool isRed, bool value)
    {
        if (isRed)
            _farmListsViewModel.MoveRedLosses = value;
        else
            _farmListsViewModel.MoveYellowLosses = value;
    }

    private void SetSelectedLossDestination(bool isRed, FarmLossDestinationOption destination)
    {
        if (isRed)
            _farmListsViewModel.SelectedRedLossDestination = destination;
        else
            _farmListsViewModel.SelectedYellowLossDestination = destination;
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
    private const int AddFarmsDefaultTroopCount = 2;

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

    private AddFarmsProtectionPreferences LoadAddFarmsProtectionPreferences()
    {
        try
        {
            var config = _botConfigStore.Load();
            var excludeOwnAlliance = !config.TryGetPropertyValue(BotOptionPayloadKeys.AddFarmsExcludeOwnAlliance, out var ownNode)
                || ownNode is null
                || ownNode.GetValue<bool>();
            var excludedPlayers = config[BotOptionPayloadKeys.AddFarmsExcludedPlayers]?.GetValue<string>() ?? string.Empty;
            var excludedAlliances = config[BotOptionPayloadKeys.AddFarmsExcludedAlliances]?.GetValue<string>() ?? string.Empty;
            return new AddFarmsProtectionPreferences(excludeOwnAlliance, excludedPlayers, excludedAlliances);
        }
        catch (Exception ex)
        {
            AppendLog($"[farm-list] Could not load Add farms protection settings: {ex.Message}");
            return new AddFarmsProtectionPreferences(true, string.Empty, string.Empty);
        }
    }

    private void SaveAddFarmsProtectionPreferences(AddFarmsProtectionPreferences preferences)
    {
        try
        {
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.AddFarmsExcludeOwnAlliance] = JsonValue.Create(preferences.ExcludeOwnAlliance);
            config[BotOptionPayloadKeys.AddFarmsExcludedPlayers] = JsonValue.Create(preferences.ExcludedPlayers);
            config[BotOptionPayloadKeys.AddFarmsExcludedAlliances] = JsonValue.Create(preferences.ExcludedAlliances);
            _botConfigStore.Save(config);
            AppendLog("[farm-list] Saved Add farms target-protection settings.");
        }
        catch (Exception ex)
        {
            AppendLog($"[farm-list] Could not save Add farms protection settings: {ex.Message}");
        }
    }

    private void SaveFarmListOnlyCreateReportsWithLosses(bool enabled)
    {
        try
        {
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.FarmListOnlyCreateReportsWithLosses] = JsonValue.Create(enabled);
            _botConfigStore.Save(config);
            AppendLog($"[farm-list-create] Only create reports with losses set to {enabled}.");
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save farm-list report preference: {ex.Message}");
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
