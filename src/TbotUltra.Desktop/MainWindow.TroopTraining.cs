using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

/// <summary>
/// Troops-tab host-side logic. After the TroopTraining MVVM migration the
/// row state (Buildings collection, suppression flag, per-option event
/// wiring, troop-dropdown rebuilds, queue-status apply, countdown tick,
/// payload serialize) lives on <see cref="ViewModels.TroopTrainingViewModel"/>.
/// What's kept here is purely the service-bound and UI-element-bound work
/// that needs MainWindow's private state:
///
///   - reading the active account's stored tribe (mixes <c>_accountStore</c>,
///     <c>_accountAnalysisStore</c> and the <c>TribeInfoTextBlock</c>
///     fallback)
///   - persisting the row state through <c>_botConfigStore</c>
///   - hitting the worker for build / queue status (<c>_botService</c>),
///     and surfacing the result back through the VM and the
///     <c>UpdateAutomationLoopRunningIndicators</c> badge.
/// </summary>
public partial class MainWindow
{
    private string ResolveStoredTroopTrainingTribe()
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (!string.IsNullOrWhiteSpace(accountName)
                && _accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl())
                && analysis is not null
                && !string.IsNullOrWhiteSpace(analysis.Tribe))
            {
                return analysis.Tribe;
            }
        }
        catch
        {
            // Ignore temporary analysis read failures.
        }

        return TribeInfoTextBlock?.Text?.Replace("Tribe:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim() ?? "Unknown";
    }

    private void OnTroopTrainingConfigChanged()
    {
        PersistTroopTrainingConfig();
        UpdateAutomationLoopRunningIndicators();
    }

    /// <summary>
    /// Queues an "upgrade troops at smithy" runtime task. Called by the
    /// Troops panel's Upgrade button.
    /// </summary>
    internal void OnTroopsUpgradeClicked()
    {
        EnqueueQuickTask("upgrade_troops_at_smithy", "Upgrade all troops at Smithy");
        _troopTrainingViewModel.InfoText = "Queued: upgrade all troops at Smithy.";
        AppendLog("Queued upgrade_troops_at_smithy task.");
    }

    /// <summary>
    /// Queues a one-shot "build troops" task. Called by the Troops panel's
    /// Build-now button.
    /// </summary>
    internal void OnTroopsBuildNowClicked()
    {
        EnqueueQuickTask("build_troops", "Build troops");
        _troopTrainingViewModel.InfoText = "Queued: build troops.";
        AppendLog("Queued build_troops task.");
    }

    /// <summary>
    /// Operation-bracketed refresh of troop queues. Called by the Troops
    /// panel's Refresh-queues button (the panel toggles its own IsEnabled
    /// around the call).
    /// </summary>
    internal async Task RefreshTroopQueuesCoreAsync()
    {
        var operationId = BeginOperation("Refresh troop queues");
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await RefreshTroopTrainingQueuesAsync(options, CancellationToken.None, _lastBuildingStatus?.Buildings, refreshBuildingsBeforeRead: true);
            _troopTrainingViewModel.InfoText = "Troop training queues refreshed.";
            AppendLog($"[{operationId}] Troop training queues refreshed.");
        }
        catch (Exception ex)
        {
            _troopTrainingViewModel.InfoText = $"Could not refresh troop queues: {ex.Message}";
            AppendLog($"[{operationId}] Troop queue refresh failed: {ex.Message}");
        }
    }

    private void PersistTroopTrainingConfig()
    {
        try
        {
            var config = _botConfigStore.Load();
            _troopTrainingViewModel.WriteToConfig(config);
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save troop training config: {ex.Message}");
        }
    }

    private async Task RefreshTroopTrainingQueuesAsync(
        BotOptions options,
        CancellationToken cancellationToken,
        IReadOnlyList<Building>? knownBuildings = null,
        bool refreshBuildingsBeforeRead = false)
    {
        IReadOnlyList<Building>? effectiveBuildings = knownBuildings;
        if (refreshBuildingsBeforeRead)
        {
            try
            {
                var refreshedStatus = await _botService.ReadBuildingsStatusAsync(options, AppendLog, cancellationToken);
                effectiveBuildings = refreshedStatus.Buildings;
                await Dispatcher.InvokeAsync(() =>
                {
                    _lastBuildingStatus = _lastBuildingStatus is null
                        ? refreshedStatus
                        : _lastBuildingStatus with
                        {
                            ActiveVillage = refreshedStatus.ActiveVillage,
                            Villages = refreshedStatus.Villages,
                            Tribe = refreshedStatus.Tribe,
                            Buildings = refreshedStatus.Buildings,
                            IsCapital = refreshedStatus.IsCapital,
                        };

                    _troopTrainingViewModel.ApplyStatus(_lastBuildingStatus, _lastBuildingStatus?.TroopTrainingQueues);
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Could not refresh troop building list before queue read: {ex.Message}");
            }
        }

        var queueStatuses = await _botService.ReadTroopTrainingQueuesAsync(options, AppendLog, effectiveBuildings, cancellationToken);
        await Dispatcher.InvokeAsync(() =>
        {
            var effectiveStatus = _lastBuildingStatus is null
                ? null
                : _lastBuildingStatus with { TroopTrainingQueues = queueStatuses };
            if (effectiveStatus is not null)
            {
                _lastBuildingStatus = effectiveStatus;
                _troopTrainingViewModel.ApplyStatus(effectiveStatus, queueStatuses);
            }
            else
            {
                _troopTrainingViewModel.ApplyStatus(new VillageStatus(
                    ActiveVillage: string.Empty,
                    Villages: [],
                    Resources: new Dictionary<string, string>(),
                    ResourceFields: [],
                    Buildings: effectiveBuildings?.ToList() ?? [],
                    BuildQueue: [],
                    TroopTrainingQueues: queueStatuses), queueStatuses);
            }

            UpdateAutomationLoopRunningIndicators();
        });
    }
}
