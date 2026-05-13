using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Accounts;
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
    private static bool IsTeutonsTribe(string? tribe)
    {
        return string.Equals(tribe?.Trim(), "Teutons", StringComparison.OrdinalIgnoreCase);
    }

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

    private (bool? Value, bool HasValue) TryGetStoredAutoCelebrationPreference()
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (!string.IsNullOrWhiteSpace(accountName)
                && _accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl())
                && analysis is not null
                && analysis.AutoCelebrationEnabled.HasValue)
            {
                return (analysis.AutoCelebrationEnabled.Value, true);
            }
        }
        catch
        {
            // Ignore temporary account analysis read failures.
        }

        return (null, false);
    }

    private void PersistAutoCelebrationPreferenceForActiveAccount(bool enabled)
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (string.IsNullOrWhiteSpace(accountName))
            {
                return;
            }

            var serverUrl = GetActiveAccountServerUrl();
            _accountAnalysisStore.TryLoad(accountName, out var existing, serverUrl);
            var snapshot = new AccountAnalysisSnapshot(
                SchemaVersion: AccountAnalysisConstants.CurrentSchemaVersion,
                AnalyzedAtUtc: DateTimeOffset.UtcNow,
                AccountName: string.IsNullOrWhiteSpace(existing?.AccountName) ? accountName : existing.AccountName,
                ServerUrl: string.IsNullOrWhiteSpace(existing?.ServerUrl) ? serverUrl ?? string.Empty : existing.ServerUrl,
                Tribe: string.IsNullOrWhiteSpace(existing?.Tribe) ? ResolveStoredTroopTrainingTribe() : existing.Tribe,
                GoldClubEnabled: existing?.GoldClubEnabled ?? false,
                BuildingCatalog: existing?.BuildingCatalog ?? [],
                AutoCelebrationEnabled: enabled);
            _accountAnalysisStore.Save(snapshot);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save auto celebration preference: {ex.Message}");
        }
    }

    private void ApplyTroopTrainingTribeState(string? tribe)
    {
        var troopOptionsChanged = _troopTrainingViewModel.UpdateTroopOptions(tribe);
        var celebrationChanged = _troopTrainingViewModel.UpdateAutoCelebrationAvailability(tribe);
        if (troopOptionsChanged || celebrationChanged)
        {
            PersistTroopTrainingConfig();
            if (celebrationChanged)
            {
                PersistAutoCelebrationPreferenceForActiveAccount(_troopTrainingViewModel.AutoCelebrationEnabled);
            }
        }

        SyncTeutonsOnlyAutomationGroups(tribe, persistChanges: true);
    }

    private void OnTroopTrainingConfigChanged()
    {
        PersistTroopTrainingConfig();
        PersistAutoCelebrationPreferenceForActiveAccount(_troopTrainingViewModel.AutoCelebrationEnabled);
        UpdateAutomationLoopRunningIndicators();
        if (_lastResourceStatusForUi is not null)
        {
            _troopTrainingDeferredRefreshDebounceTimer.Stop();
            _troopTrainingDeferredRefreshDebounceTimer.Start();
        }
    }

    private static bool TryResolveBrewerySlotId(VillageStatus status, out int slotId)
    {
        slotId = status.Buildings
            .FirstOrDefault(item =>
                item.SlotId is > 0
                && (item.Gid == 35 || string.Equals(item.Name, "Brewery", StringComparison.OrdinalIgnoreCase)))
            ?.SlotId ?? 0;
        return slotId > 0;
    }

    private void ApplyLocalBreweryCelebrationStatus(VillageStatus status)
    {
        if (!IsTeutonsTribe(status.Tribe))
        {
            ClearBreweryBlockedState();
            _troopTrainingViewModel.ApplyBreweryCelebrationStatus(new BreweryCelebrationStatus(
                false,
                status.IsCapital,
                false,
                null,
                false,
                null,
                "N/A",
                "Teutons only."));
            return;
        }

        if (status.IsCapital == false)
        {
            ClearBreweryBlockedState();
            _troopTrainingViewModel.ApplyBreweryCelebrationStatus(new BreweryCelebrationStatus(
                true,
                false,
                TryResolveBrewerySlotId(status, out var nonCapitalBrewerySlot) && nonCapitalBrewerySlot > 0,
                nonCapitalBrewerySlot > 0 ? nonCapitalBrewerySlot : null,
                false,
                null,
                "N/A",
                "Capital village required."));
            return;
        }

        if (!TryResolveBrewerySlotId(status, out var brewerySlotId))
        {
            if (!string.Equals(_breweryBlockedReasonKey, BreweryBlockedReasonMissing, StringComparison.OrdinalIgnoreCase))
            {
                SetBreweryBlockedState(BreweryBlockedReasonMissing, "Brewery missing");
            }

            _troopTrainingViewModel.ApplyBreweryCelebrationStatus(new BreweryCelebrationStatus(
                true,
                status.IsCapital,
                false,
                null,
                false,
                null,
                "N/A",
                "Brewery not found."));
            return;
        }

        if (string.Equals(_breweryBlockedReasonKey, BreweryBlockedReasonMissing, StringComparison.OrdinalIgnoreCase))
        {
            ClearBreweryBlockedState();
            AppendLog("Brewery celebration group re-enabled: Brewery detected after building refresh.");
        }

        _troopTrainingViewModel.ResetBreweryCelebrationStatus(
            _troopTrainingViewModel.AutoCelebrationEnabled
                ? "Reading celebration status..."
                : "Disabled.");
    }

    private async Task RefreshBreweryCelebrationStatusAsync(BotOptions options, VillageStatus? status, CancellationToken cancellationToken)
    {
        if (status is null)
        {
            _troopTrainingViewModel.ResetBreweryCelebrationStatus();
            UpdateAutomationLoopRunningIndicators();
            return;
        }

        ApplyLocalBreweryCelebrationStatus(status);
        UpdateAutomationLoopRunningIndicators();

        if (!IsTeutonsTribe(status.Tribe)
            || status.IsCapital == false
            || !TryResolveBrewerySlotId(status, out _))
        {
            return;
        }

        try
        {
            var celebrationStatus = await _botService.ReadBreweryCelebrationStatusAsync(options, AppendLog, status.Buildings, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                _troopTrainingViewModel.ApplyBreweryCelebrationStatus(celebrationStatus);
                UpdateAutomationLoopRunningIndicators();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _troopTrainingViewModel.ResetBreweryCelebrationStatus($"Could not read celebration status: {ex.Message}");
                UpdateAutomationLoopRunningIndicators();
            });
        }
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
                await RefreshBreweryCelebrationStatusAsync(options, refreshedStatus, cancellationToken);
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

    private async Task RefreshTroopTrainingUiAfterBuildAsync(BotOptions options, CancellationToken cancellationToken)
    {
        await RefreshTroopTrainingQueuesAsync(options, cancellationToken, _lastBuildingStatus?.Buildings, refreshBuildingsBeforeRead: true);

        try
        {
            await RefreshResourceSnapshotForUiAsync(options, cancellationToken, currentPageOnly: true);
        }
        catch (Exception ex)
        {
            AppendLog($"Troop build current-page resource refresh failed, falling back: {ex.Message}");
            await RefreshResourceSnapshotForUiAsync(options, cancellationToken);
        }
    }
}
