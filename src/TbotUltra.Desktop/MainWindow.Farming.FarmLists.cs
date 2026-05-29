using System;
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
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
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
        var selectedFarmLists = LoadConfiguredContinuousFarmListNames();
        var mergedByName = new Dictionary<string, (int Active, int Total, int? RemainingSeconds)>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
        {
            if (list is null)
            {
                continue;
            }

            var normalizedName = string.IsNullOrWhiteSpace(list.Name) ? "Farm list" : list.Name.Trim();
            if (!mergedByName.TryGetValue(normalizedName, out var existing))
            {
                mergedByName[normalizedName] = (
                    Active: Math.Max(0, list.ActiveFarmCount),
                    Total: Math.Max(0, list.TotalFarmCount),
                    RemainingSeconds: list.RemainingSeconds is > 0 ? list.RemainingSeconds : null);
                continue;
            }

            var incomingRemaining = list.RemainingSeconds is > 0 ? list.RemainingSeconds : null;
            mergedByName[normalizedName] = (
                Active: Math.Max(existing.Active, Math.Max(0, list.ActiveFarmCount)),
                Total: Math.Max(existing.Total, Math.Max(0, list.TotalFarmCount)),
                RemainingSeconds: existing.RemainingSeconds is > 0
                    ? existing.RemainingSeconds
                    : incomingRemaining);
        }

        await Dispatcher.InvokeAsync(() =>
        {
            _suppressFarmListUiRefresh = true;
            try
            {
                _farmLists.Clear();
                var displayedRows = 0;
                foreach (var pair in mergedByName.OrderBy(pair => pair.Key))
                {
                    if (displayedRows >= MaxFarmListsShown)
                    {
                        break;
                    }

                    _farmLists.Add(new FarmListStatusRow
                    {
                        Name = pair.Key,
                        ActiveFarmCount = pair.Value.Active,
                        TotalFarmCount = pair.Value.Total,
                        IsEnabled = selectedFarmLists.Count <= 0 || selectedFarmLists.Contains(pair.Key),
                        RemainingSeconds = pair.Value.RemainingSeconds,
                    });
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

        return true;
    }

    private async void AnalyzeFarmListsButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("Analyze Farmlists");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        SetFarmingFunctionRunning(true);
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
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void AddFarmsToListButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_farmingFeaturesAvailable)
        {
            AppendLog("Add Farms to List is unavailable while Gold Club farming is disabled.");
            return;
        }

        var operationId = BeginOperation("Add Farms To List");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        SetFarmingFunctionRunning(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());

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
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void CreateFarmListButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_farmingFeaturesAvailable)
        {
            AppendLog("Create Farmlist is unavailable while Gold Club farming is disabled.");
            return;
        }

        AppendLog("Create Farmlist clicked. Wiring to farm page action is not connected yet.");
    }

    private async void FarmListSendNowButton_Click(object sender, RoutedEventArgs e)
    {
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
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
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
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void SyncFarmingControlsEnabledState()
    {
        var farmControlsEnabled = !_farmingOperationBusy && _farmingFeaturesAvailable;
        SetEnabled(AddFarmsToListButton, farmControlsEnabled);
        SetEnabled(CreateFarmListButton, farmControlsEnabled);
        SetEnabled(FarmListsItemsControl, farmControlsEnabled);
        SetEnabled(AnalyzeFarmListsButton, !_farmingOperationBusy);
        SetEnabled(AnalyzeNatarsProfileButton, !_farmingOperationBusy && _farmingFeaturesAvailable);
        SetEnabled(ShowNatarsListButton, !_farmingOperationBusy && _farmingFeaturesAvailable && _natarsProfileAnalyzed);
        SetEnabled(StartManualFarmingButton, _farmingFeaturesAvailable);
        SetEnabled(StartCatapultWavesButton, !_farmingOperationBusy && _farmingFeaturesAvailable);
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
            var selectedNames = _farmLists
                .Where(item => item.IsEnabled)
                .Select(item => item.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.ContinuousFarmListNames] = new JsonArray(selectedNames.Select(name => JsonValue.Create(name)!).ToArray());
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save selected farmlists: {ex.Message}");
        }
    }
}
