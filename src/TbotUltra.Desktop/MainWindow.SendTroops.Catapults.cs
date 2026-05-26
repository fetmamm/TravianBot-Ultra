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
        if (_farmingOperationBusy)
        {
            return;
        }

        _ = StartCatapultWavesAsync();
    }

    private async Task StartCatapultWavesAsync()
    {
        if (!_farmingFeaturesAvailable)
        {
            AppendLog("Catapult waves are unavailable while Gold Club farming is disabled.");
            return;
        }

        var operationId = BeginOperation("Catapult Waves");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        SetFarmingFunctionRunning(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            SetCatapultWavesStatus("Reading troops from Rally Point...");
            var setupInfo = await _botService.ReadCatapultWaveSetupInfoAsync(
                options,
                AppendLog,
                forceRefresh: false,
                operationToken);

            var dialog = new CatapultWaveWindow(ResolveCurrentTribeForFarming(), setupInfo.AvailableTroops, setupInfo.RallyPointLevel)
            {
                Owner = this,
                RefreshRequested = async (status, token) =>
                {
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
                        operationToken);
                    SetCatapultWavesStatus("Troops refreshed from Rally Point.");
                    return refreshedSetupInfo;
                },
                StartRequested = async (request, status, token) =>
                {
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
                        operationToken);

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
            _operationCts?.Dispose();
            _operationCts = null;
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
