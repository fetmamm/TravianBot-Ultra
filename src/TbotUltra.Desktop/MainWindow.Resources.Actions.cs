using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            _resourcesViewModel.ClearPendingTargets();
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
        _resourceTestFunctionsWindow.ReadSmithyQueueTestRequested += TestReadSmithyQueueButton_Click;
        _resourceTestFunctionsWindow.ReinforcementsTestRequested += TestReinforcementsButton_Click;
        _resourceTestFunctionsWindow.SavePageHtmlRequested += SavePageHtmlButton_Click;
        _resourceTestFunctionsWindow.Closed += (_, _) =>
        {
            _resourceTestFunctionsWindow.ResourceProductionTestRequested -= TestResourceProductionButton_Click;
            _resourceTestFunctionsWindow.NavigateToBreweryTestRequested -= TestNavigateToBreweryButton_Click;
            _resourceTestFunctionsWindow.StartCelebrationTestRequested -= TestStartCelebrationButton_Click;
            _resourceTestFunctionsWindow.NpcTradeBarracksTestRequested -= TestNpcTradeBarracksButton_Click;
            _resourceTestFunctionsWindow.NpcTradeBuildingTestRequested -= TestNpcTradeBuildingButton_Click;
            _resourceTestFunctionsWindow.ReadSmithyQueueTestRequested -= TestReadSmithyQueueButton_Click;
            _resourceTestFunctionsWindow.ReinforcementsTestRequested -= TestReinforcementsButton_Click;
            _resourceTestFunctionsWindow.SavePageHtmlRequested -= SavePageHtmlButton_Click;
            _resourceTestFunctionsWindow = null;
        };

        _resourceTestFunctionsWindow.Show();
    }

    private const string SavePageHtmlDirectory = @"C:\Users\jespe\Documents\GitHub\Tbot_ultra_new\temp_build_out\DOM";

    private void SavePageHtmlButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSavePageHtmlWindow();
    }

    private void OpenSavePageHtmlWindow(Window? sourceWindow = null, bool closeSourceWindow = false)
    {
        if (_savePageHtmlWindow is not null)
        {
            if (!_savePageHtmlWindow.IsVisible)
            {
                _savePageHtmlWindow.Show();
            }

            _savePageHtmlWindow.Activate();
            return;
        }

        _savePageHtmlWindow = new SavePageHtmlWindow(SavePageHtmlDirectory)
        {
            Owner = sourceWindow?.Owner ?? _resourceTestFunctionsWindow ?? (Window)this,
        };
        _savePageHtmlWindow.SaveRequested += SavePageHtmlWindow_SaveRequested;
        _savePageHtmlWindow.BulkSaveRequested += SavePageHtmlWindow_BulkSaveRequested;
        _savePageHtmlWindow.Closed += (_, _) =>
        {
            if (_savePageHtmlWindow is not null)
            {
                _savePageHtmlWindow.SaveRequested -= SavePageHtmlWindow_SaveRequested;
                _savePageHtmlWindow.BulkSaveRequested -= SavePageHtmlWindow_BulkSaveRequested;
                _savePageHtmlWindow = null;
            }
        };
        _savePageHtmlWindow.Show();
        FinishPopupTransition(_savePageHtmlWindow, sourceWindow, closeSourceWindow);
    }

    private void SavePageHtmlWindow_BulkSaveRequested(object? sender, EventArgs e)
    {
        OpenBulkSavePageHtmlWindow(sender as Window, closeSourceWindow: true);
    }

    private void OpenBulkSavePageHtmlWindow(Window? sourceWindow = null, bool closeSourceWindow = false)
    {
        if (_bulkSavePageHtmlWindow is not null)
        {
            if (!_bulkSavePageHtmlWindow.IsVisible)
            {
                _bulkSavePageHtmlWindow.Show();
            }

            _bulkSavePageHtmlWindow.Activate();
            return;
        }

        _bulkSavePageHtmlWindow = new BulkSavePageHtmlWindow(SavePageHtmlDirectory)
        {
            Owner = sourceWindow?.Owner ?? _resourceTestFunctionsWindow ?? (Window)this,
        };
        _bulkSavePageHtmlWindow.SaveRequested += BulkSavePageHtmlWindow_SaveRequested;
        _bulkSavePageHtmlWindow.CancelRequested += BulkSavePageHtmlWindow_CancelRequested;
        _bulkSavePageHtmlWindow.Closed += (_, _) =>
        {
            if (_bulkSavePageHtmlWindow is not null)
            {
                _bulkSavePageHtmlWindow.SaveRequested -= BulkSavePageHtmlWindow_SaveRequested;
                _bulkSavePageHtmlWindow.CancelRequested -= BulkSavePageHtmlWindow_CancelRequested;
                _bulkSavePageHtmlWindow = null;
            }
        };
        _bulkSavePageHtmlWindow.Show();
        FinishPopupTransition(_bulkSavePageHtmlWindow, sourceWindow, closeSourceWindow);
    }

    private static void FinishPopupTransition(Window targetWindow, Window? sourceWindow, bool closeSourceWindow)
    {
        if (sourceWindow is not null)
        {
            targetWindow.Left = sourceWindow.Left + Math.Max(0, (sourceWindow.ActualWidth - targetWindow.Width) / 2d);
            targetWindow.Top = sourceWindow.Top + Math.Max(0, (sourceWindow.ActualHeight - targetWindow.Height) / 2d);
        }

        targetWindow.Activate();
        if (closeSourceWindow)
        {
            sourceWindow?.Close();
        }
    }

    private void BulkSavePageHtmlWindow_CancelRequested(object? sender, EventArgs e)
    {
        AppendLog("Cancel requested for bulk save.");
        try
        {
            _operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async void BulkSavePageHtmlWindow_SaveRequested(object? sender, IReadOnlyList<BulkSavePageRequest> pages)
    {
        if (_operationCts is not null)
        {
            AppendLog("Bulk save skipped: another operation is already running.");
            return;
        }

        var dialog = sender as BulkSavePageHtmlWindow;
        var operationId = BeginOperation("BulkSavePageHtml");
        var operationSw = Stopwatch.StartNew();
        _operationCts = _loopController.CreateCts("operation");
        var operationToken = _operationCts.Token;
        dialog?.SetSaveInProgress(true, $"Saving 0/{pages.Count}...");

        var saved = 0;
        var failed = 0;
        var finalDialogMessage = string.Empty;
        try
        {
            var options = LoadBotOptions();
            Directory.CreateDirectory(SavePageHtmlDirectory);

            for (var index = 0; index < pages.Count; index++)
            {
                operationToken.ThrowIfCancellationRequested();
                var request = pages[index];
                var page = request.Page;
                dialog?.SetSaveInProgress(true, $"Saving {index + 1}/{pages.Count}: {page}");
                AppendLog($"[{operationId}] opening {page} ({index + 1}/{pages.Count}).");

                try
                {
                    var capture = await _botService.NavigateToPageAndReadHtmlAsync(
                        options,
                        page,
                        AppendLog,
                        operationToken);
                    var nameSource = string.IsNullOrWhiteSpace(request.Alias) ? page : request.Alias;
                    var fileName = BuildBulkSaveHtmlFileName(nameSource, request.Prefix);
                    var filePath = Path.Combine(SavePageHtmlDirectory, $"{fileName}.txt");
                    var content = BuildSavedPageHtmlContent(capture, $"Bulk save source page: {page}\nAlias: {request.Alias}");
                    await File.WriteAllTextAsync(filePath, content, operationToken);
                    saved++;
                    AppendLog($"[{operationId}] saved {capture.Html.Length} chars from '{capture.Url}' to {filePath}.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    AppendLog($"[{operationId}] failed to save {page}: {ex.Message}");
                }
            }

            finalDialogMessage = $"Saved {saved}/{pages.Count}. Failed {failed}.";
            dialog?.SetSaveResult(finalDialogMessage);
            CompleteOperation(operationId, operationSw, $"Bulk saved {saved}/{pages.Count} HTML page(s). Failed: {failed}.");
            OpenSavePageHtmlWindow(dialog, closeSourceWindow: true);
            AppDialog.Show(
                _savePageHtmlWindow ?? (Window)this,
                finalDialogMessage,
                "Bulk save HTML",
                MessageBoxButton.OK,
                failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Bulk save page HTML canceled.");
            StatusTextBlock.Text = "Bulk save canceled.";
            finalDialogMessage = $"Canceled. Saved {saved}/{pages.Count}.";
            dialog?.SetSaveResult(finalDialogMessage);
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            finalDialogMessage = $"Bulk save failed: {ex.Message}";
            dialog?.SetSaveResult(finalDialogMessage);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            if (string.IsNullOrWhiteSpace(finalDialogMessage))
            {
                dialog?.SetSaveInProgress(false, $"Saved {saved}/{pages.Count}. Failed {failed}.");
            }
        }
    }

    private static string BuildBulkSaveHtmlFileName(string page, string? prefix = null)
    {
        var value = (page ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "page";
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = uri.PathAndQuery.TrimStart('/');
        }

        value = value.TrimStart('/');
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        value = value.Replace('/', '_').Replace('\\', '_').Replace('?', '_').Replace('&', '_').Replace('=', '_');
        value = string.IsNullOrWhiteSpace(value) ? "page" : value;
        var cleanPrefix = SanitizeFileNamePart(prefix);
        return string.IsNullOrWhiteSpace(cleanPrefix) ? value : $"{cleanPrefix}_{value}";
    }

    private static string SanitizeFileNamePart(string? value)
    {
        var result = (value ?? string.Empty).Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }

        return result.Replace('/', '_').Replace('\\', '_').Replace('?', '_').Replace('&', '_').Replace('=', '_');
    }

    private async void SavePageHtmlWindow_SaveRequested(object? sender, SavePageHtmlRequest request)
    {
        var dialog = sender as SavePageHtmlWindow;
        var operationId = BeginOperation("SavePageHtml");
        var operationSw = Stopwatch.StartNew();
        using var operationCts = new CancellationTokenSource();
        var operationToken = operationCts.Token;
        dialog?.SetSaveInProgress(true);
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] capturing current page HTML.");
            var capture = await _botService.ReadCurrentPageHtmlAsync(
                options,
                AppendLog,
                operationToken);

            Directory.CreateDirectory(SavePageHtmlDirectory);
            var filePath = Path.Combine(SavePageHtmlDirectory, $"{request.FileName}.txt");
            var content = BuildSavedPageHtmlContent(capture, request.Notes);
            await File.WriteAllTextAsync(filePath, content, operationToken);

            AppendLog($"[{operationId}] saved {capture.Html.Length} chars from '{capture.Url}' to {filePath}.");
            dialog?.SetSaveResult($"Saved {request.FileName}.txt");
            CompleteOperation(operationId, operationSw, $"Saved page HTML to {filePath}");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Save page HTML paused.");
            dialog?.SetSaveResult("Save paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            dialog?.SetSaveResult($"Save failed: {ex.Message}");
        }
        finally
        {
            dialog?.SetSaveInProgress(false);
        }
    }

    private static string BuildSavedPageHtmlContent(PageHtmlCapture capture, string notes)
    {
        var header = new System.Text.StringBuilder();
        header.AppendLine("<!--");
        header.AppendLine($"Saved: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        header.AppendLine($"URL: {capture.Url}");
        if (!string.IsNullOrWhiteSpace(notes))
        {
            header.AppendLine("Notes:");
            foreach (var line in notes.Replace("\r\n", "\n").Split('\n'))
            {
                header.AppendLine($"  {line}");
            }
        }

        header.AppendLine("-->");
        header.AppendLine();
        return header.ToString() + capture.Html;
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

    private async void TestReadSmithyQueueButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("TestReadSmithyQueue");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] reading Smithy queue from current page.");
            var result = await _botService.ReadSmithyQueueFromCurrentPageTestAsync(
                options,
                AppendLog,
                operationToken);
            AppendLog($"[{operationId}] Smithy queue result: {result}");
            CompleteOperation(operationId, operationSw, result);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Smithy queue test paused.";
            AppendLog("Smithy queue test paused.");
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

    private async void TestReinforcementsButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("TestReinforcements");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            PersistReinforcementSettings();
            var options = BotOptionsPayloadApplier.Apply(
                LoadBotOptions(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [BotOptionPayloadKeys.ReinforcementsTroopRules] = System.Text.Json.JsonSerializer.Serialize(BuildReinforcementRulesForRun()),
                });
            AppendLog($"[{operationId}] running reinforcements test with saved settings.");
            var result = await _botService.RunReinforcementsTestAsync(
                options,
                AppendLog,
                operationToken);
            AppendLog($"[{operationId}] reinforcements test result: {result}");
            CompleteOperation(operationId, operationSw, result);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Reinforcements test paused.";
            AppendLog("Reinforcements test paused.");
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
        var targetLevel = _resourcesViewModel.SelectedTargetLevel;
        if (targetLevel <= 0)
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
        _resourcesViewModel.ActionsEnabled = enabled;
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
