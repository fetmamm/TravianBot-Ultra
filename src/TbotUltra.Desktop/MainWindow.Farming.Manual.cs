using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void StartManualFarmingButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (BlockIfSessionSleeping("Manual farming"))
        {
            return;
        }

        if (_farmingOperationBusy)
        {
            return;
        }

        _ = StartManualFarmingAsync();
    }

    private async Task StartManualFarmingAsync()
    {
        if (!_farmingFeaturesAvailable)
        {
            AppendLog("Start manual farming is unavailable while Gold Club farming is disabled.");
            return;
        }

        var currentOptions = LoadBotOptions();
        var activeAccountName = _accountStore.ActiveAccountName();
        var preferences = _manualFarmingPreferenceStore.Load(activeAccountName);
        var dialog = new ManualFarmingWindow(
            ResolveCurrentTribeForFarming(),
            currentOptions.NatarVillageSelection,
            preferences.TroopCount,
            preferences.VariancePercent)
        {
            Owner = this,
            PreferenceChanged = (troopCount, variancePercent) =>
            {
                _manualFarmingPreferenceStore.Save(activeAccountName, new ManualFarmingPreference(troopCount, variancePercent));
            },
        };

        if (dialog.ShowDialog() != true)
        {
            AppendLog("[farm-manual] canceled before start (dialog dismissed).");
            return;
        }

        var operationId = BeginOperation("Start Manual Farming");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        SetFarmingFunctionRunning(true);
        try
        {
            var options = ApplyManualFarmingSelectionToOptions(
                ApplySelectedVillageToOptions(currentOptions),
                dialog.NatarVillageSelection);
            await EnsureChromiumInstalledAsync();
            var runIndex = 0;
            while (true)
            {
                operationToken.ThrowIfCancellationRequested();
                runIndex++;
                AppendLog($"[farm-manual] loop {runIndex} started — troop='{dialog.SelectedTroopType}' count={dialog.TroopCount}±{dialog.TroopVariancePercent}% raid={dialog.IsRaid}");

                var result = await _botService.StartManualFarmingFromNatarsAsync(
                    options,
                    dialog.SelectedTroopType,
                    dialog.TroopCount,
                    dialog.TroopVariancePercent,
                    dialog.IsRaid,
                    AppendLog,
                    operationToken);
                SetNatarsProfileAnalyzed(result.TotalTargets > 0);

                if (result.StoppedByNoTroopsAlarm)
                {
                    AppDialog.Show(
                        this,
                        $"Manual farming stopped by alarm after loop {runIndex}. Sent: {result.SentCount}, Skipped: {result.SkippedCount}, Failed: {result.FailedCount}, Targets: {result.TotalTargets}.",
                        "Manual farming",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    CompleteOperation(operationId, operationSw, $"Manual farming stopped by troop alarm on loop {runIndex}.");
                    break;
                }

                AppendLog($"[farm-manual] loop {runIndex} done — sent={result.SentCount}, skipped={result.SkippedCount}, failed={result.FailedCount}, targets={result.TotalTargets}. Restarting...");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("[farm-manual] canceled by user.");
            CompleteOperation(operationId, operationSw, "Manual farming stopped by user.");
        }
        catch (Exception ex)
        {
            RefreshNatarsProfileAnalyzedFromCache();
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            SetFarmingFunctionRunning(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void SetFarmingOperationBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetFarmingOperationBusy(busy));
            return;
        }

        if (busy)
        {
            EnsureManualExecutionTracking();
        }

        _farmingOperationBusy = busy;
        try
        {
            SyncFarmingControlsEnabledState();
            UpdateExecutionStateIndicator();
        }
        finally
        {
            if (!busy)
            {
                CompleteManualExecutionTrackingIfNeeded();
            }
        }
    }

    private void SetFarmingFunctionRunning(bool running)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetFarmingFunctionRunning(running));
            return;
        }

        try
        {
            if (running)
            {
                EnsureManualExecutionTracking();
            }

            if (CancelFarmingOperationButton is not null)
            {
                CancelFarmingOperationButton.Visibility = running ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                CancelFarmingOperationButton.IsEnabled = running;
            }

            UpdateExecutionStateIndicator();
        }
        finally
        {
            if (!running)
            {
                CompleteManualExecutionTrackingIfNeeded();
            }
        }
    }

    private void CancelFarmingOperationButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CancelFarmingOperationButton is not null)
        {
            CancelFarmingOperationButton.IsEnabled = false;
        }

        AppendLog("Cancel requested for the running farming operation.");
        _operationCts?.Cancel();
    }

    private static BotOptions ApplyManualFarmingSelectionToOptions(BotOptions options, string natarVillageSelection)
    {
        return BotOptionsFactory.CloneWithOverrides(
            options,
            natarVillageSelectionOverride: natarVillageSelection);
    }

    private void UpdateManualFarmingRunningState()
    {
        if (StartManualFarmingButton is not null)
        {
            StartManualFarmingButton.Content = "Start manual farming";
            StartManualFarmingButton.IsEnabled = _farmingFeaturesAvailable && !_farmingOperationBusy;
        }

        if (ManualFarmingStateTextBlock is not null)
        {
            ManualFarmingStateTextBlock.Text = "State:";
            ManualFarmingStateTextBlock.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128));
        }

        if (ManualFarmingStateDot is not null)
        {
            ManualFarmingStateDot.Fill = _farmingOperationBusy
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175));
        }
    }

    private void UpdateManualFarmingExecutionCounter()
    {
        if (ManualFarmingExecutionCountTextBlock is null)
        {
            return;
        }

        ManualFarmingExecutionCountTextBlock.Text = _manualFarmSessionExecutionCount.ToString("N0");
    }
}
