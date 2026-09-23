using System;
using TbotUltra.Desktop.Services.Orchestration;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
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
    private bool _showFarmListLastSentTimer = FarmingDefaults.ShowLastSentTimer;
    private bool _farmListLastSentLimitEnabled = FarmingDefaults.LastSentLimitEnabled;
    private int _farmListLastSentLimitHours = FarmingDefaults.DefaultLastSentLimitHours;
    private bool _farmLossDestinationSelectionInProgress;

    private static bool IsRealFarmListRow(FarmListStatusRow row)
        => FarmListsViewModel.IsRealRow(row);

    private bool HasFarmListWithFarms()
        => _farmLists.Any(row => IsRealFarmListRow(row) && !row.IsEmpty);

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
        var request = await CreateFarmListsViewRequestAsync(options);
        var result = await _farmListsWorkflow.AnalyzeAsync(request, cancellationToken);
        UpdateGoldClubInfo(result.IsAvailable);
        if (!result.IsAvailable)
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

        await ApplyFarmListsViewToUiAsync(result);
        await Dispatcher.InvokeAsync(() =>
            UpdateSelectedCachedTimerStatus(status => status with { FarmLists = result.Lists }));
        return true;
    }

    private async Task<FarmListsViewRequest> CreateFarmListsViewRequestAsync(BotOptions options)
    {
        var villageCoordinates = Dispatcher.CheckAccess()
            ? BuildUniqueVillageCoordsByName()
            : await Dispatcher.InvokeAsync(BuildUniqueVillageCoordsByName);
        return new FarmListsViewRequest(
            options,
            villageCoordinates,
            new FarmListsPresentationOptions(
                _showFarmListLastSentTimer,
                _farmListLastSentLimitEnabled,
                _farmListLastSentLimitHours));
    }

    // The workflow owns projection and automation state; WPF only renders the returned view.
    private async Task ApplyFarmListsViewToUiAsync(FarmListsViewResult result)
    {
        if (!result.IsAvailable)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _farmLists.Clear();
                EnsureFarmListPlaceholderRow();
                RefreshFarmLossDestinationOptions();
                SetFarmingFeatureAvailability(false, "Farming unavailable: Gold Club is not active on this account.");
            });
            return;
        }

        var projection = result.Projection;
        await Dispatcher.InvokeAsync(() =>
        {
            _suppressFarmListUiRefresh = true;
            try
            {
                _farmLists.Clear();
                foreach (var row in projection.Rows)
                {
                    _farmLists.Add(row);
                }

                EnsureFarmListPlaceholderRow();
            }
            finally
            {
                _suppressFarmListUiRefresh = false;
            }

            SetFarmingFeatureAvailability(true);
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
    }

    private async Task ApplyCachedFarmListsToUiAsync(IReadOnlyList<FarmListOverview> lists)
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var request = await CreateFarmListsViewRequestAsync(options);
        await ApplyFarmListsViewToUiAsync(_farmListsWorkflow.ProjectCached(request, lists));
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
        var attemptedKeys = _farmListsWorkflow.GetAutoDispatchKeys(_farmLists, sendAllLists);

        try
        {
            // On a real send the worker just read the farm page and wrote a fresh snapshot — apply
            // it directly so the UI updates instantly without navigating the browser again. On a
            // rename ("not found") there is no fresh snapshot, so fall back to a full re-analyze.
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var snapshot = sendHappened
                ? await TryApplyFarmListsSnapshotAsync(options)
                : null;
            if (snapshot is not null)
            {
                if (sendAllLists)
                {
                    var dispatch = _farmListsWorkflow.ReconcileAutomaticDispatch(
                        snapshot,
                        attemptedKeys,
                        options);
                    await ApplyFarmListsViewToUiAsync(dispatch.View);
                    if (dispatch.SuccessfulDispatch)
                    {
                        WakeContinuousFarmScheduling();
                    }
                }
                return;
            }

            await RefreshFarmListsFromServerAsync(options, _loopController.AcquireSessionScopeToken());
        }
        catch (Exception ex)
        {
            AppendLog($"Farm list UI refresh after send failed: {ex.Message}");
        }
    }

    private async Task<FarmListsViewResult?> TryApplyFarmListsSnapshotAsync(BotOptions options)
    {
        var request = await CreateFarmListsViewRequestAsync(options);
        var result = await _farmListsWorkflow.RestoreAsync(
            request,
            DateTimeOffset.UtcNow,
            requireFreshSnapshot: true,
            _loopController.AcquireSessionScopeToken());
        if (result is null)
        {
            return null;
        }

        await ApplyFarmListsViewToUiAsync(result);
        return result;
    }

    private async Task RestoreFarmListsFromSnapshotForActiveAccount()
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var request = await CreateFarmListsViewRequestAsync(options);
        var result = await _farmListsWorkflow.RestoreAsync(
            request,
            DateTimeOffset.UtcNow,
            requireFreshSnapshot: false);
        if (result is null)
        {
            return;
        }

        await ApplyFarmListsViewToUiAsync(result);
        AppendLog($"[farm-list] restored {result.Lists.Count} saved farm list(s) from the last analysis.");
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
            var viewRequest = await CreateFarmListsViewRequestAsync(options);
            var addSession = _farmListsWorkflow.CreateAddSession(viewRequest, _travcoListStore.LoadAll);
            async Task<OfficialAddFarmsLoadResult> LoadOfficialAsync(CancellationToken cancellationToken)
            {
                await EnsureChromiumInstalledAsync();
                return await addSession.LoadAsync(cancellationToken);
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
                return await addSession.RunAsync(
                    plans,
                    useDefaultTroops,
                    troopType,
                    troopCount,
                    protection,
                    progress,
                    cancellationToken);
            }
            var villageOptions = GetFarmListCreationVillages()
                .Select(village => new OfficialAddFarmsWindow.AddFarmsVillageOption(
                    village.Name,
                    village.CoordX,
                    village.CoordY))
                .ToList();
            var dialogResult = _farmListsDialogs.ShowAddFarms(new OfficialAddFarmsDialogRequest(
                ResolveCurrentTribeForFarming(),
                LoadAddFarmsTroopCount(),
                LoadOfficialAsync,
                RunOfficialPlansAsync,
                addSession.BuildPlans,
                operationToken,
                addSession.ProtectionPreferences,
                addSession.PrepareTargetProtection,
                villageOptions,
                GetSelectedVillageName()));
            if (addSession.LatestView is { } latestView)
            {
                UpdateGoldClubInfo(latestView.IsAvailable);
                await ApplyFarmListsViewToUiAsync(latestView);
            }
            if (!dialogResult.Accepted || dialogResult.RunResult is null)
            {
                if (!string.IsNullOrWhiteSpace(dialogResult.LoadFailureMessage))
                {
                    AppDialog.Show(
                        this,
                        dialogResult.LoadFailureMessage,
                        "Add farms",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                CompleteOperation(
                    operationId,
                    operationSw,
                    string.IsNullOrWhiteSpace(dialogResult.LoadFailureMessage)
                        ? "Add farms canceled."
                        : dialogResult.LoadFailureMessage);
                return;
            }

            BusyOverlay.ShowCancel = false;
            ShowBusyOverlay("Adding farms", "Finalizing farm list updates...");
            var runResult = dialogResult.RunResult;
            HideBusyOverlay();

            // Single modern completion popup: the run summary as stat tiles, and — only when dead villages
            // were found — the "remove them from the Travco list?" question baked into the same dialog
            // (Keep / Remove them) so the user never sees two separate popups.
            var elapsed = dialogResult.RunDuration;
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
            var viewRequest = await CreateFarmListsViewRequestAsync(options);
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

            var createSession = _farmListsWorkflow.CreateCreateSession(viewRequest);
            async Task<FarmListCreateBatchResult> RunAsync(
                FarmListCreateRequest request,
                IProgress<FarmListCreateProgress> progress,
                CancellationToken cancellationToken)
            {
                await EnsureChromiumInstalledAsync();
                return await createSession.RunAsync(request, progress, cancellationToken);
            }

            var createResult = _farmListsDialogs.ShowCreateFarmLists(new CreateFarmListsDialogRequest(
                ResolveCurrentTribeForFarming(),
                villages,
                options.FarmListOnlyCreateReportsWithLosses,
                SaveFarmListOnlyCreateReportsWithLosses,
                RunAsync,
                operationToken));
            if (createResult is null)
            {
                CompleteOperation(operationId, operationSw, "Create farmlists canceled.");
                return;
            }

            if (createSession.LatestView is { } createdView)
            {
                UpdateGoldClubInfo(createdView.IsAvailable);
                await ApplyFarmListsViewToUiAsync(createdView);
            }

            var createdCount = createResult.CreatedCount;
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
                $"Created {createResult.CreatedCount}/{createResult.RequestedCount} farmlists.");
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
            row.VillageHeaderText = FarmListsWorkflow.BuildVillageHeader(row.VillageName, villageCoordsByName);
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
            if (await _farmListsWorkflow.DispatchOneAsync(options, list, operationToken))
            {
                WakeContinuousFarmScheduling();
            }
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
        if (sendToggled && !_farmLists.Any(row => IsRealFarmListRow(row) && row.IsEnabled))
        {
            AppendLog("[farm-list] Send all toggled: no farm lists are toggled on.");
            AppDialog.Show(this, "No farm lists are toggled on.", "Send farmlists", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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
            var viewRequest = await CreateFarmListsViewRequestAsync(options);
            var dispatch = await _farmListsWorkflow.DispatchManyAsync(
                viewRequest,
                _farmLists.ToList(),
                sendToggled,
                operationToken);
            UpdateGoldClubInfo(dispatch.View.IsAvailable);
            await ApplyFarmListsViewToUiAsync(dispatch.View);
            if (dispatch.SuccessfulDispatch)
            {
                WakeContinuousFarmScheduling();
            }
            CompleteOperation(operationId, operationSw, $"Sent {(sendToggled ? "toggled" : "all")} farmlists ({dispatch.SentCount} list(s)).");
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
            _farmListsWorkflow.SaveSelection(_farmLists);
        }
        else if (string.Equals(e.PropertyName, nameof(FarmListStatusRow.IntervalMinMinutesText), StringComparison.Ordinal) ||
                 string.Equals(e.PropertyName, nameof(FarmListStatusRow.IntervalMaxMinutesText), StringComparison.Ordinal))
        {
            if (!row.TryGetDispatchInterval(out var minMinutes, out var maxMinutes))
            {
                UpdateFarmingUiState();
                return;
            }

            if (_farmListsWorkflow.PersistDispatchInterval(
                    row,
                    minMinutes!.Value,
                    maxMinutes!.Value,
                    LoadBotOptions()))
            {
                WakeContinuousFarmScheduling();
            }
        }
        else
        {
            return;
        }

        UpdateAutomationLoopRunningIndicators();
        UpdateFarmingUiState();
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
            var saved = _farmListsWorkflow.SaveSettings(new FarmingPanelSettings(
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

        var loginValidation = _farmListsWorkflow.ValidateLossDestinationSetup(_isLoggedIn);
        if (!loginValidation.CanStart)
        {
            SetMoveLosses(isRed, false);
            AppendLog("[farm-list] loss destination setup blocked because the user is logged out.");
            AppDialog.Show(
                this,
                loginValidation.FailureMessage ?? "You must log in first.",
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

        IAsyncDisposable? automationPause = null;
        var operationToken = _loopController.StartOperation("loss-farmlist-destination");
        _farmLossDestinationSelectionInProgress = true;
        BeginManualFunctionPacingPause();
        BusyOverlay.ShowCancel = true;
        ShowBusyOverlay("Choose loss farmlist", "Pausing automation after the current action...");
        try
        {
            automationPause = await _farmListsWorkflow.AcquireAutomationPauseAsync(operationToken);

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
            if (automationPause is not null)
            {
                await automationPause.DisposeAsync();
            }
        }
    }

    private async Task CreateAndSelectFarmLossDestinationAsync(
        BotOptions options,
        FarmListLossColors lossColor,
        string listName,
        CancellationToken cancellationToken)
    {
        BusyOverlay.ShowCancel = true;
        ShowBusyOverlay("Creating loss farmlist", $"Creating '{listName}'...");
        await EnsureChromiumInstalledAsync();
        var viewRequest = await CreateFarmListsViewRequestAsync(options);
        var result = await _farmListsWorkflow.CreateLossDestinationAsync(
            viewRequest,
            GetFarmListCreationVillages(),
            ResolveCurrentTribeForFarming(),
            lossColor,
            listName,
            cancellationToken);
        UpdateGoldClubInfo(result.View.IsAvailable);
        await ApplyFarmListsViewToUiAsync(result.View);
        var isRed = lossColor == FarmListLossColors.Red;
        SetSelectedLossDestination(isRed, result.Destination);
        AppendLog($"[farm-list] created and selected '{result.Destination.Name}' as the {lossColor.ToString().ToLowerInvariant()} loss destination.");
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

}
