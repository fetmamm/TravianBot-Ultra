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
        _resourceTestFunctionsWindow.Closed += (_, _) =>
        {
            _resourceTestFunctionsWindow.ResourceProductionTestRequested -= TestResourceProductionButton_Click;
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

    private async void UpgradeAllResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("UpgradeAllResources");
        if (ResourceTargetLevelComboBox.SelectedItem is not int targetLevel)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | No target level selected.");
            return;
        }

        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            await QueueUpgradeAllResourcesAsync(operationId, operationToken, targetLevel);
        }
        catch (OperationCanceledException)
        {
            ClearPendingResourceLevelsFromUi();
            StatusTextBlock.Text = "Upgrade all resources paused.";
            AppendLog("Upgrade all resources paused.");
        }
        catch (Exception ex)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | {FormatExceptionForLog(ex)}");
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void UpgradeAllResourcesToMaxButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("UpgradeAllResourcesToMax");
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            await QueueUpgradeAllResourcesAsync(operationId, operationToken, null);
        }
        catch (OperationCanceledException)
        {
            ClearPendingResourceLevelsFromUi();
            StatusTextBlock.Text = "Upgrade all resources paused.";
            AppendLog("Upgrade all resources paused.");
        }
        catch (Exception ex)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | {FormatExceptionForLog(ex)}");
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
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

    private async Task QueueUpgradeAllResourcesAsync(string operationId, CancellationToken operationToken, int? targetLevel)
    {
        await EnsureChromiumInstalledAsync();
        var options = LoadBotOptions();
        var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: true, forceCurrentVillage: true);
        var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);
        var requestedTargetLevel = targetLevel.HasValue
            ? Math.Min(targetLevel.Value, resourceMaxLevel)
            : resourceMaxLevel;
        _resourcePendingTargetBySlot.Clear();
        var rows = ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: false);

        var orderedUpgrades = rows
            .Where(row => (row.Level ?? 0) < requestedTargetLevel)
            .OrderBy(row => row.Level ?? 0)
            .ThenBy(row => row.SlotId)
            .ToList();

        if (orderedUpgrades.Count == 0)
        {
            AppendLog($"[{operationId}] OK 0.0s | All resource fields are already at or above level {requestedTargetLevel}.");
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = requestedTargetLevel.ToString(),
        };

        var item = _botService.Enqueue("upgrade_all_resources_to_level", payload, priority: 0, maxRetries: 3);
        RequestQueueUiRefresh(selectId: item.Id);
        TriggerQueueAutoRunFromEnqueue();
        AppendLog($"[{operationId}] OK 0.0s | Queued upgrade-all toward level {requestedTargetLevel}. The worker will upgrade the lowest resource field first.");
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
