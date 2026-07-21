using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void StartCatapultWavesButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockIfSessionSleeping("Catapult waves"))
        {
            return;
        }

        if (_farmingOperationBusy)
        {
            return;
        }

        _backgroundTasks.Track(StartCatapultWavesAsync());
    }

    private async Task StartCatapultWavesAsync()
    {
        var operationId = BeginOperation("Catapult Waves");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        SetFarmingFunctionRunning(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            SetCatapultWavesStatus("Reading troops from Rally Point...");

            // Open the window immediately; it shows its own busy overlay and loads the troops via
            // InitialLoadRequested, so the popup never appears empty while the Rally Point is read.
            var dialog = new CatapultWaveWindow(ResolveCurrentTribeForFarming())
            {
                Owner = this,
                InitialLoadRequested = async (status, token) =>
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken, token);
                    var initialSetupInfo = await _botService.ReadCatapultWaveSetupInfoAsync(
                        options,
                        message =>
                        {
                            AppendLog(message);
                            status(message);
                        },
                        forceRefresh: false,
                        linkedCts.Token);
                    SetCatapultWavesStatus("Troops loaded from Rally Point.");
                    return initialSetupInfo;
                },
                RefreshRequested = async (status, token) =>
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken, token);
                    status("Refreshing troops from Rally Point...");
                    SetCatapultWavesStatus("Refreshing troops from Rally Point...");
                    var refreshedSetupInfo = await _botService.ReadCatapultWaveSetupInfoAsync(
                        options,
                        message =>
                        {
                            AppendLog(message);
                            status(message);
                        },
                        forceRefresh: true,
                        linkedCts.Token);
                    SetCatapultWavesStatus("Troops refreshed from Rally Point.");
                    return refreshedSetupInfo;
                },
                StartRequested = async (request, status, token) =>
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken, token);
                    status("Preparing catapult waves...");
                    SetCatapultWavesStatus("Preparing catapult waves...");
                    var result = await _botService.StartCatapultWavesAsync(
                        options,
                        request,
                        message =>
                        {
                            AppendLog(message);
                            status(message);
                        },
                        linkedCts.Token);

                    var attackMode = request.RaidAttack ? "raid" : "normal attack";
                    var message = $"Sent {result.SentCount}/{result.TotalAttacks} {attackMode}(s) to ({result.X}|{result.Y}).";
                    SetCatapultWavesStatus(message);
                    CompleteOperation(operationId, operationSw, message);
                    return result;
                },
            };

            if (dialog.ShowDialog() != true)
            {
                AppendLog("Catapult waves canceled.");
                CompleteOperation(operationId, operationSw, "Catapult waves canceled before sending.");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            SetCatapultWavesStatus("Catapult waves canceled.");
            AppendLog("Catapult waves canceled.");
            CompleteOperation(operationId, operationSw, "Catapult waves stopped by user.");
        }
        catch (Exception ex)
        {
            SetCatapultWavesStatus($"Catapult waves failed: {ex.Message}");
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            SetFarmingFunctionRunning(false);
            DisposeOperationCts();
        }
    }

    private void SetCatapultWavesStatus(string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetCatapultWavesStatus(status));
            return;
        }

        if (CatapultWavesStatusTextBlock is not null)
        {
            CatapultWavesStatusTextBlock.Text = status;
        }
    }
}
