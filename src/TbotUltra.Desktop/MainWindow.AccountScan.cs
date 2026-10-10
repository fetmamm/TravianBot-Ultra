using System.Diagnostics;
using TbotUltra.Desktop.Services.Orchestration;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private bool _accountScanInProgress;

    private async void AccountScanButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(() => AccountScanButtonClickAsync(sender, e));

    private async Task AccountScanButtonClickAsync(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        if (_accountScanInProgress || BlockIfSessionSleeping("Account scan"))
        {
            return;
        }

        if (!_isLoggedIn)
        {
            AppendLog("[account-scan] Active Travian login required.");
            return;
        }

        var scanSelection = ShowAccountScanSelectionDialog();
        if (scanSelection is null)
        {
            return;
        }

        var resumeContinuous = IsContinuousLoopRunning() || _startContinuousLoopAfterQueueStop;
        var resumeQueue = !resumeContinuous && _autoQueueRunning;
        var safeToResume = true;
        _accountScanInProgress = true;
        AccountScanButton.IsEnabled = false;

        try
        {
            await PauseAutomationForAccountScanAsync(resumeContinuous || resumeQueue);
            safeToResume = await RunAccountScanAsync(scanSelection);
        }
        catch (Exception ex)
        {
            safeToResume = false;
            AppendLog($"[account-scan] Failed before scan could run: {ex.Message}");
            StatusTextBlock.Text = "Account scan failed.";
        }
        finally
        {
            _accountScanInProgress = false;
            AccountScanButton.IsEnabled = _isLoggedIn && !IsSessionSleeping;
            if (safeToResume)
            {
                await ResumeAutomationAfterAccountScanAsync(resumeContinuous, resumeQueue);
            }
            else if (resumeContinuous || resumeQueue)
            {
                AppendLog("[account-scan] Automation remains paused because browser restoration was not confirmed.");
            }
        }
    }

    private AccountScanSelection? ShowAccountScanSelectionDialog()
    {
        var dorf1CheckBox = new CheckBox
        {
            Content = "Dorf1",
            IsChecked = true,
            Margin = new Thickness(0, 4, 16, 4),
            ToolTip = "Resources, production, storage, population, construction queue, tasks and daily quests.",
        };
        var dorf2CheckBox = new CheckBox
        {
            Content = "Dorf2",
            IsChecked = true,
            Margin = new Thickness(0, 4, 16, 4),
            ToolTip = "Buildings in the village center.",
        };
        var smithyCheckBox = CreateAccountScanCheckBox("Smithy");
        var barracksCheckBox = CreateAccountScanCheckBox("Barracks");
        var stableCheckBox = CreateAccountScanCheckBox("Stable");
        var workshopCheckBox = CreateAccountScanCheckBox("Workshop");
        var townHallCheckBox = CreateAccountScanCheckBox("Town Hall");
        var breweryCheckBox = CreateAccountScanCheckBox("Brewery", "Teutons only.");
        var dorf2Details = new[]
        {
            smithyCheckBox,
            barracksCheckBox,
            stableCheckBox,
            workshopCheckBox,
            townHallCheckBox,
            breweryCheckBox,
        };

        void UpdateDorf2Details()
        {
            var enabled = dorf2CheckBox.IsChecked == true;
            foreach (var checkBox in dorf2Details)
            {
                checkBox.IsEnabled = enabled;
            }
        }

        dorf2CheckBox.Checked += (_, _) => UpdateDorf2Details();
        dorf2CheckBox.Unchecked += (_, _) => UpdateDorf2Details();

        var basePagesPanel = new WrapPanel();
        basePagesPanel.Children.Add(dorf1CheckBox);
        basePagesPanel.Children.Add(dorf2CheckBox);

        var detailPagesPanel = new WrapPanel
        {
            Margin = new Thickness(0, 2, 0, 0),
        };
        foreach (var checkBox in dorf2Details)
        {
            detailPagesPanel.Children.Add(checkBox);
        }

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "Analyze every village now?",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
        });
        content.Children.Add(new TextBlock
        {
            Text = "Select what to read. Villages are scanned in random order. Active automation pauses after its current action and resumes from the final scanned village.",
            Margin = new Thickness(0, 8, 0, 10),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
        });
        content.Children.Add(basePagesPanel);
        content.Children.Add(detailPagesPanel);
        UpdateDorf2Details();

        var choice = AppDialog.ShowCustomContent(
            this,
            content,
            "Account scan",
            [
                ("Start scan", MessageBoxResult.Yes),
                ("Cancel", MessageBoxResult.Cancel),
            ],
            MessageBoxImage.Question,
            MessageBoxResult.Yes,
            MessageBoxResult.Cancel,
            successResult: MessageBoxResult.Yes,
            width: 560);
        if (choice != MessageBoxResult.Yes)
        {
            return null;
        }

        if (dorf1CheckBox.IsChecked != true && dorf2CheckBox.IsChecked != true)
        {
            AppDialog.Show(
                this,
                "Select Dorf1 or Dorf2 before starting the account scan.",
                "Account scan",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        var dorf2Enabled = dorf2CheckBox.IsChecked == true;
        return new AccountScanSelection(
            Dorf1: dorf1CheckBox.IsChecked == true,
            Dorf2: dorf2Enabled,
            Smithy: dorf2Enabled && smithyCheckBox.IsChecked == true,
            Barracks: dorf2Enabled && barracksCheckBox.IsChecked == true,
            Stable: dorf2Enabled && stableCheckBox.IsChecked == true,
            Workshop: dorf2Enabled && workshopCheckBox.IsChecked == true,
            TownHall: dorf2Enabled && townHallCheckBox.IsChecked == true,
            Brewery: dorf2Enabled && breweryCheckBox.IsChecked == true);
    }

    private static CheckBox CreateAccountScanCheckBox(string content, string? toolTip = null) =>
        new()
        {
            Content = content,
            Margin = new Thickness(0, 4, 16, 4),
            ToolTip = toolTip,
        };

    private async Task PauseAutomationForAccountScanAsync(bool automationWasRunning)
    {
        if (!automationWasRunning)
        {
            return;
        }

        _startContinuousLoopAfterQueueStop = false;
        _restartContinuousLoopAfterStop = false;
        RequestAutomationStop(AutomationStopMode.AfterCurrentAction);
        UpdateExecutionStateIndicator();
        AppendLog("[account-scan] Pause requested; waiting for current action to finish.");

        var gracefulDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < gracefulDeadline)
        {
            if (!_autoQueueRunning
                && !IsContinuousLoopRunning()
                && !_uiBusy)
            {
                AppendLog("[account-scan] Automation paused.");
                return;
            }

            await Task.Delay(Random.Shared.Next(150, 350)); // Random wait
        }

        AppendLog("[account-scan] Graceful pause timed out; canceling current action.");
        _loopController.CancelOperation();
        RequestAutomationStop(AutomationStopMode.CancelCurrentAction);

        var cancelDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < cancelDeadline)
        {
            if (!_autoQueueRunning
                && !IsContinuousLoopRunning()
                && !_uiBusy)
            {
                AppendLog("[account-scan] Automation stopped after cancellation.");
                return;
            }

            await Task.Delay(Random.Shared.Next(150, 350)); // Random wait
        }

        throw new InvalidOperationException("Could not pause active automation.");
    }

    private async Task<bool> RunAccountScanAsync(AccountScanSelection scanSelection)
    {
        var operationId = BeginOperation("AccountScan");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("account-scan");
        var selectedVillageName = GetSelectedVillageName();
        var loaded = 0;
        var failed = 0;
        var scanCompleted = false;

        ToggleUiBusy(true);
        ShowBusyOverlay("Account scan", "Reading current village list...");
        try
        {
            var options = LoadBotOptions() with
            {
                VillageStatusSweepDorf1Enabled = scanSelection.Dorf1,
                VillageStatusSweepDorf2Enabled = scanSelection.Dorf2,
                VillageStatusSweepSmithyEnabled = scanSelection.Smithy,
                VillageStatusSweepBarracksEnabled = scanSelection.Barracks,
                VillageStatusSweepStableEnabled = scanSelection.Stable,
                VillageStatusSweepWorkshopEnabled = scanSelection.Workshop,
                VillageStatusSweepTownHallEnabled = scanSelection.TownHall,
                VillageStatusSweepBreweryEnabled = scanSelection.Brewery,
            };
            var snapshot = await _botService.ReadAccountSnapshotForScanAsync(
                options,
                AppendLog,
                operationToken);
            var villages = snapshot.Villages
                .Where(village => !string.IsNullOrWhiteSpace(village.Name))
                .GroupBy(
                    village => GetVillageKey(village.Url, village.CoordX, village.CoordY, village.Name),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(_ => Random.Shared.Next())
                .ToList();
            if (villages.Count == 0)
            {
                throw new InvalidOperationException("No villages were found.");
            }

            SyncDashboardVillageUiFromVillages(
                villages,
                snapshot.ActiveVillage,
                selectedVillageName,
                snapshot.ActiveVillageCoordX,
                snapshot.ActiveVillageCoordY);
            ReconcileConfirmedVillageList(villages, "account_scan");
            AppendLog(
                $"[account-scan] Started in randomized order. villages={villages.Count}, "
                + $"scope={scanSelection.ToLogText()}.");

            for (var index = 0; index < villages.Count; index++)
            {
                operationToken.ThrowIfCancellationRequested();
                var village = villages[index];
                BusyOverlay.Text = $"Analyzing village {index + 1}/{villages.Count}: {village.Name}";
                try
                {
                    var status = await ReadAccountScanVillageWithRetryAsync(
                        options,
                        village,
                        operationToken);
                    CacheVillageStatus(status, village.Name);
                    ReconcilePendingBuildingQueueWithLiveStatus(status);
                    SetActiveWorkingVillageFromStatus(status);
                    SyncDashboardVillageUiFromVillages(
                        villages,
                        status.ActiveVillage,
                        selectedVillageName,
                        status.ActiveVillageCoordX,
                        status.ActiveVillageCoordY);
                    RefreshQueueUi();
                    loaded++;
                    AppendLog(
                        $"[account-scan] {index + 1}/{villages.Count} complete: "
                        + $"village='{village.Name}', fields={status.ResourceFields.Count}, "
                        + $"buildings={status.Buildings.Count}, construction={status.ActiveConstructions?.Count ?? status.BuildQueue.Count}, "
                        + $"smithy={status.SmithyUpgradeStatus?.ActiveUpgradeCount ?? 0}.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppendLog(
                        $"[account-scan] {index + 1}/{villages.Count} failed for "
                        + $"'{village.Name}': {ex.Message}");
                }
            }

            scanCompleted = true;
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{operationId}] INFO account scan canceled.");
            StatusTextBlock.Text = "Account scan canceled.";
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            StatusTextBlock.Text = "Account scan failed.";
        }
        finally
        {
            if (scanCompleted)
            {
                CompleteOperation(
                    operationId,
                    operationSw,
                    $"Account scan completed: {loaded} village(s), failed={failed}; browser left on final scanned village.");
                StatusTextBlock.Text = failed == 0
                    ? $"Account scan completed: {loaded} village(s)."
                    : $"Account scan completed with {failed} failure(s).";
            }
            RefreshSelectedVillageAfterAccountScan();
            HideBusyOverlay();
            ToggleUiBusy(false);
            DisposeOperationCts();
        }

        return scanCompleted;
    }

    private async Task<VillageStatus> ReadAccountScanVillageWithRetryAsync(
        BotOptions options,
        Village village,
        CancellationToken cancellationToken,
        bool requireCompleteStructure = false)
    {
        const int maxAttempts = 3;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var selectionItem = new VillageSelectionItem
                {
                    Name = village.Name,
                    Url = village.Url ?? string.Empty,
                    CoordX = village.CoordX,
                    CoordY = village.CoordY,
                    Population = village.Population,
                    Tribe = village.Tribe,
                };
                var status = await ReadVillageStatusSweepBaseStatusAsync(
                    options,
                    selectionItem,
                    cancellationToken);
                status = await RefreshVillageStatusSweepOptionalStatusesAsync(
                    options,
                    selectionItem,
                    status,
                    cancellationToken,
                    "[account-scan]");
                var hasExpectedDorf1 = !options.VillageStatusSweepDorf1Enabled
                    || (requireCompleteStructure
                        ? VillageStatusCompleteness.HasCompleteResourceFieldSnapshot(status.ResourceFields)
                        : status.ResourceFields.Count > 0);
                var hasExpectedDorf2 = !options.VillageStatusSweepDorf2Enabled
                    || (requireCompleteStructure
                        ? BuildingUpgradeSlotRebindPlanner.HasCompleteBuildingOverview(status)
                        : status.Buildings.Count > 0);
                if (hasExpectedDorf1 && hasExpectedDorf2)
                {
                    return status;
                }

                lastError = new InvalidOperationException(
                    $"Incomplete status: fields={status.ResourceFields.Count}, buildings={status.Buildings.Count}.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AccountAccessException)
            {
                // Account access signals are terminal safety events, not transient scan failures.
                // Let the caller establish the account hold immediately without retry navigation.
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (attempt < maxAttempts)
            {
                AppendLog(
                    $"[account-scan] Retry {attempt + 1}/{maxAttempts} for "
                    + $"'{village.Name}': {lastError?.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(750 * attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Village '{village.Name}' did not return the selected scan scope after {maxAttempts} attempts.",
            lastError);
    }

    private void RefreshSelectedVillageAfterAccountScan()
    {
        if (VillageComboBox.SelectedItem is VillageSelectionItem selected)
        {
            ShowSelectedVillageFromCache(selected);
        }
        else
        {
            RefreshVillageActivityIndicatorsOnDashboard();
            RefreshQueueUi();
        }
    }

    private async Task ResumeAutomationAfterAccountScanAsync(
        bool resumeContinuous,
        bool resumeQueue)
    {
        if (_loopController.IsClosing || !_isLoggedIn || IsSessionSleeping)
        {
            return;
        }

        if (resumeContinuous
            && !IsContinuousLoopRunning())
        {
            AppendLog("[account-scan] Resuming continuous loop.");
            StartContinuousLoopRunner();
            return;
        }

        if (resumeQueue && !_autoQueueRunning)
        {
            AppendLog("[account-scan] Resuming queue auto-run.");
            await TriggerQueueAutoRunAsync();
        }
    }

    private sealed record AccountScanSelection(
        bool Dorf1,
        bool Dorf2,
        bool Smithy,
        bool Barracks,
        bool Stable,
        bool Workshop,
        bool TownHall,
        bool Brewery)
    {
        public string ToLogText()
        {
            var selected = new List<string>();
            if (Dorf1) selected.Add("dorf1");
            if (Dorf2) selected.Add("dorf2");
            if (Smithy) selected.Add("smithy");
            if (Barracks) selected.Add("barracks");
            if (Stable) selected.Add("stable");
            if (Workshop) selected.Add("workshop");
            if (TownHall) selected.Add("town_hall");
            if (Brewery) selected.Add("brewery");
            return string.Join(',', selected);
        }
    }
}
