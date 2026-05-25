using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private async void LoadResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("LoadResources");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] INFO server={options.ServerName}, headless={options.Headless}");
            await EnsureChromiumInstalledAsync();
            var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: true, forceCurrentVillage: true);
            _resourcePendingTargetBySlot.Clear();
            var rows = ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: false);
            _inboxAutoEnabled = true;
            UpdateInboxButtons(status.UnreadMessages, status.UnreadReports);
            AppendLog($"Resources loaded: {rows.Count} fields.");
            CompleteOperation(operationId, operationSw, $"Loaded {rows.Count} fields.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Load resources paused.";
            AppendLog("Load resources paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void OpenResourceTestFunctionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_resourceTestFunctionsWindow is not null)
        {
            if (!_resourceTestFunctionsWindow.IsVisible)
            {
                _resourceTestFunctionsWindow.Show();
            }

            _resourceTestFunctionsWindow.Activate();
            return;
        }

        _resourceTestFunctionsWindow = new FunctionTestWindow
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        _resourceTestFunctionsWindow.ResourceProductionTestRequested += TestResourceProductionButton_Click;
        _resourceTestFunctionsWindow.NavigateToBreweryTestRequested += TestNavigateToBreweryButton_Click;
        _resourceTestFunctionsWindow.StartCelebrationTestRequested += TestStartCelebrationButton_Click;
        _resourceTestFunctionsWindow.NpcTradeBarracksTestRequested += TestNpcTradeBarracksButton_Click;
        _resourceTestFunctionsWindow.NpcTradeBuildingTestRequested += TestNpcTradeBuildingButton_Click;
        _resourceTestFunctionsWindow.Closed += (_, _) =>
        {
            _resourceTestFunctionsWindow.ResourceProductionTestRequested -= TestResourceProductionButton_Click;
            _resourceTestFunctionsWindow.NavigateToBreweryTestRequested -= TestNavigateToBreweryButton_Click;
            _resourceTestFunctionsWindow.StartCelebrationTestRequested -= TestStartCelebrationButton_Click;
            _resourceTestFunctionsWindow.NpcTradeBarracksTestRequested -= TestNpcTradeBarracksButton_Click;
            _resourceTestFunctionsWindow.NpcTradeBuildingTestRequested -= TestNpcTradeBuildingButton_Click;
            _resourceTestFunctionsWindow = null;
        };

        _resourceTestFunctionsWindow.Show();
    }

    private async void TestResourceProductionButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("TestResourceProduction");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] testing production DOM read on current page.");
            var productionByHour = await _botService.ReadCurrentPageResourceProductionPerHourAsync(
                options,
                AppendLog,
                operationToken);

            var summary = string.Join(", ", new[] { "wood", "clay", "iron", "crop" }
                .Select(key =>
                {
                    productionByHour.TryGetValue(key, out var value);
                    var formatted = value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                    return $"{key}={formatted}/h";
                }));

            AppendLog($"[{operationId}] production DOM read result: {summary}");
            if (productionByHour.Count > 0 && productionByHour.Values.Any(value => value is not null))
            {
                ApplyResourceProductionOnlyToUi(productionByHour);
                AppendLog($"[{operationId}] applied production DOM read to UI.");
            }
            else
            {
                AppendLog($"[{operationId}] no production values returned from DOM read.");
            }

            CompleteOperation(operationId, operationSw, $"Production DOM read finished: {summary}");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Test production paused.";
            AppendLog("Test production paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void TestNavigateToBreweryButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("TestNavigateToBrewery");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] navigating to brewery (same path as auto celebration).");
            var status = await _botService.ReadBreweryCelebrationStatusAsync(
                options,
                AppendLog,
                null,
                operationToken);

            AppendLog($"[{operationId}] brewery status: {status.StatusText}");
            CompleteOperation(operationId, operationSw, $"Navigate to brewery finished: {status.StatusText}");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Navigate to brewery paused.";
            AppendLog("Navigate to brewery paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void TestStartCelebrationButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("TestStartCelebration");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] starting brewery celebration (same path as auto celebration).");
            var result = await _botService.RunBreweryCelebrationAsync(
                options,
                AppendLog,
                operationToken);

            AppendLog($"[{operationId}] celebration result: {result}");
            CompleteOperation(operationId, operationSw, $"Start celebration finished: {result}");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Start celebration paused.";
            AppendLog("Start celebration paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void TestNpcTradeBarracksButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("TestNpcTradeBarracks");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] running NPC trade test for Barracks.");
            var result = await _botService.RunNpcTradeForBuildingTestAsync(
                options,
                AppendLog,
                TbotUltra.Core.Travian.TroopTrainingBuildingType.Barracks,
                operationToken);
            AppendLog($"[{operationId}] NPC trade test result: {result}");
            CompleteOperation(operationId, operationSw, result);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "NPC trade test paused.";
            AppendLog("NPC trade test paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void TestNpcTradeBuildingButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("TestNpcTradeBuilding");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] running NPC trade test on current building page.");
            var result = await _botService.RunNpcTradeForCurrentBuildingPageTestAsync(
                options,
                AppendLog,
                operationToken);
            AppendLog($"[{operationId}] NPC trade building test result: {result}");
            CompleteOperation(operationId, operationSw, result);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "NPC trade building test paused.";
            AppendLog("NPC trade building test paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void UpgradeAllResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("UpgradeAllResources");
        if (ResourceTargetLevelComboBox.SelectedItem is not int targetLevel)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | No target level selected.");
            return;
        }

        try
        {
            QueueUpgradeAllResources(operationId, targetLevel);
        }
        catch (Exception ex)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | {FormatExceptionForLog(ex)}");
        }
    }

    private void UpgradeAllResourcesToMaxButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("UpgradeAllResourcesToMax");
        try
        {
            QueueUpgradeAllResources(operationId, ResolveSelectedVillageResourceMaxLevel());
        }
        catch (Exception ex)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | {FormatExceptionForLog(ex)}");
        }
    }

    private void ToggleResourceTabActionsBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ToggleResourceTabActionsBusy(busy));
            return;
        }

        var enabled = !busy;
        SetEnabled(LoadResourcesButton, enabled);
        SetEnabled(ResourceTargetLevelComboBox, enabled);
        SetEnabled(UpgradeAllResourcesButton, enabled);
        SetEnabled(UpgradeAllResourcesToMaxButton, enabled);
        if (_resourceTestFunctionsWindow is not null)
        {
            _resourceTestFunctionsWindow.IsEnabled = enabled;
        }
    }

    private void QueueUpgradeAllResources(string operationId, int targetLevel)
    {
        var requestedTargetLevel = Math.Clamp(targetLevel, 1, ResolveSelectedVillageResourceMaxLevel());
        var buildStrategy = _resourcesViewModel.BuildStrategy;
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = requestedTargetLevel.ToString(),
            [BotOptionPayloadKeys.ResourceBuildStrategy] = buildStrategy,
        };

        var item = _botService.Enqueue("upgrade_all_resources_to_level", payload, priority: 0, maxRetries: 3);
        RequestQueueUiRefresh(selectId: item.Id);
        TriggerQueueAutoRunFromEnqueue();
        var strategyText = string.Equals(buildStrategy, "smart", StringComparison.OrdinalIgnoreCase)
            ? "the field of the resource with the least in storage first"
            : "the lowest resource field first";
        AppendLog($"[{operationId}] OK 0.0s | Queued upgrade-all toward level {requestedTargetLevel}. The worker will upgrade {strategyText}.");
    }

    private int ResolveSelectedVillageResourceMaxLevel()
    {
        if (VillageComboBox.SelectedItem is VillageSelectionItem selectedVillage)
        {
            return selectedVillage.IsCapital ? ResourceFieldMaxLevel : NonCapitalResourceMaxLevel;
        }

        return Math.Clamp(_activeVillageResourceMaxLevel, NonCapitalResourceMaxLevel, ResourceFieldMaxLevel);
    }

    private void ResourceBuildStrategyRadio_Click(object sender, RoutedEventArgs e)
    {
        PersistResourceBuildStrategyToConfig();
    }

    private void PersistResourceBuildStrategyToConfig()
    {
        try
        {
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.ResourceBuildStrategy] = _resourcesViewModel.BuildStrategy;
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save resource build strategy: {ex.Message}");
        }
    }

    private async Task LoadResourcesAfterUpgradeAsync(CancellationToken cancellationToken = default, bool resourceOnly = false)
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var status = await ReadVillageStatusWithRetryAsync(options, cancellationToken, resourceOnly);
        await Dispatcher.InvokeAsync(() =>
        {
            ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
        });
    }
}
