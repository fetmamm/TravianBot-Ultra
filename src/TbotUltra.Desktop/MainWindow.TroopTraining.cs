using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

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
                AutoCelebrationEnabled: enabled,
                AutomationLoopEnabledGroups: existing?.AutomationLoopEnabledGroups,
                AutomationLoopVisibleGroups: existing?.AutomationLoopVisibleGroups);
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
        RefreshReinforcementTroopRules(tribe);
        if (troopOptionsChanged)
        {
            // A tribe change can swap an invalid troop selection for a fallback in the building rows —
            // persist that to the selected village's override.
            PersistTroopTrainingForSelectedVillage();
        }

        if (celebrationChanged)
        {
            PersistTroopTrainingConfig();
            PersistAutoCelebrationPreferenceForActiveAccount(_troopTrainingViewModel.AutoCelebrationEnabled);
        }

        SyncTeutonsOnlyAutomationGroups(tribe, persistChanges: true);
    }

    private void OnTroopTrainingConfigChanged()
    {
        // Account-wide settings (NPC trade, gold, celebration) go to the account config; the per-building
        // training rules go to the selected village's override so the Troops tab and the per-village
        // "Troop settings" popup edits the same data.
        PersistTroopTrainingConfig();
        PersistTroopTrainingForSelectedVillage();
        PersistAutoCelebrationPreferenceForActiveAccount(_troopTrainingViewModel.AutoCelebrationEnabled);
        UpdateAutomationLoopRunningIndicators();
        if (_lastResourceStatusForUi is not null)
        {
            _troopTrainingDeferredRefreshDebounceTimer.Stop();
            _troopTrainingDeferredRefreshDebounceTimer.Start();
        }
    }

    private static bool TryResolveBrewerySlotIdFromStatus(VillageStatus status, out int slotId)
    {
        slotId = status.Buildings
            .FirstOrDefault(item =>
                item.SlotId is > 0
                && (item.Gid == 35 || string.Equals(item.Name, "Brewery", StringComparison.OrdinalIgnoreCase)))
            ?.SlotId ?? 0;
        return slotId > 0;
    }

    /// <summary>
    /// Returns the brewery slot id for the village in <paramref name="status"/>, preferring
    /// the live scan but falling back to the per-village cache when the scan came back
    /// without gid=35. Partial dorf2 scans (and reads taken from non-dorf2 pages) often
    /// miss buildings even when they exist — without the cache fallback every such read
    /// would flip the celebration card to "Brewery missing".
    /// </summary>
    /// <remarks>
    /// Cache invalidation policy: only a high-confidence full dorf2 read can declare
    /// the brewery actually gone. Lower-confidence reads keep the cached slot id. So
    /// when the user demolishes the brewery, the next successful dorf2 scan will
    /// invalidate the cache; transient partial scans (or status snapshots taken from
    /// dorf1 / build pages) will not.
    /// </remarks>
    private bool TryResolveBrewerySlotId(VillageStatus status, out int slotId)
    {
        var villageKey = status.ActiveVillage;
        var hasVillageKey = !string.IsNullOrWhiteSpace(villageKey);

        if (TryResolveBrewerySlotIdFromStatus(status, out slotId))
        {
            if (hasVillageKey)
            {
                _knownBrewerySlotByVillage[villageKey!] = slotId;
            }
            return true;
        }

        // Brewery not present in this status. Only invalidate the cache if we trust
        // the absence — i.e. the scan looks like a complete dorf2 read.
        if (IsHighConfidenceBuildingsScan(status))
        {
            if (hasVillageKey && _knownBrewerySlotByVillage.Remove(villageKey!))
            {
                AppendLog("Brewery celebration: cache cleared — high-confidence dorf2 scan reports brewery absent.");
            }
            slotId = 0;
            return false;
        }

        // Low/partial scan — trust the cache.
        if (hasVillageKey
            && _knownBrewerySlotByVillage.TryGetValue(villageKey!, out var cachedSlot)
            && cachedSlot > 0)
        {
            slotId = cachedSlot;
            return true;
        }

        slotId = 0;
        return false;
    }

    /// <summary>
    /// Heuristic for "this status came from a full dorf2 buildings scan". Requires
    /// most slots to have a known gid plus the Main Building to be present, which
    /// effectively excludes partial scans (where hydration races leave most slots
    /// without gid classes) and status snapshots that don't include real building
    /// data at all (Buildings empty / very few entries).
    /// </summary>
    private static bool IsHighConfidenceBuildingsScan(VillageStatus status)
    {
        var buildings = status.Buildings;
        if (buildings.Count < 18)
        {
            return false;
        }

        var withGid = 0;
        var hasMainBuilding = false;
        foreach (var building in buildings)
        {
            if (building.Gid is > 0)
            {
                withGid += 1;
                if (building.Gid == 15)
                {
                    hasMainBuilding = true;
                }
            }
        }

        // Require Main Building visible plus the bulk of slots resolved. With 22 slots
        // typical, 18 with-gid keeps headroom for one or two stubborn img-hydration
        // misses while still excluding the partial-scan case (where ~7 of 22 carry gids).
        return hasMainBuilding && withGid >= 18;
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
            _troopTrainingViewModel.MarkBreweryNonCapital();
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

        // Brewery confirmed (either by scan or by per-village cache hit). Sync the
        // troops-tab Y/N indicator so it doesn't lag behind the dashboard knowledge.
        _troopTrainingViewModel.MarkBreweryExists(true);

        // Don't wipe the running countdown here — local refreshes fire every ~16s while
        // idle and would otherwise erase a running celebration's RemainingSeconds before
        // the next remote read can repopulate it. We just update the status text; the
        // 1Hz TickCountdowns keeps the cached seconds in sync until a remote read
        // confirms or refreshes the value.
        _troopTrainingViewModel.UpdateBreweryStatusTextOnly(
            _troopTrainingViewModel.AutoCelebrationEnabled
                ? (_troopTrainingViewModel.AutoCelebrationRemainingSeconds is > 0
                    ? "Celebration running."
                    : "Brewery ready.")
                : "Disabled.");
    }

    private void ApplySmithyUpgradeStatus(SmithyUpgradeStatus status)
    {
        var villageName = NormalizeVillageName(GetSelectedVillageName())
            ?? NormalizeVillageName(_activeWorkingVillageName);
        if (villageName is not null
            && _villageStatusCacheByName.TryGetValue(villageName, out var cached))
        {
            status = SmithyQueueState.PreserveKnownActiveQueue(
                status,
                cached.SmithyUpgradeStatus,
                DateTimeOffset.UtcNow);
        }

        _smithyUpgradeRemainingSeconds = SmithyQueueState.ResolveActiveUpgrades(status, DateTimeOffset.UtcNow)
            .Where(entry => entry.TimeLeftSeconds is > 0)
            .Select(entry => entry.TimeLeftSeconds!.Value)
            .ToList();
        UpdateAutomationLoopRunningIndicators();
        RefreshTravianSmithyQueueUi();
    }

    private int? ResolveSmithyUpgradeGroupRemainingSeconds()
    {
        var villageName = NormalizeVillageName(GetSelectedVillageName())
            ?? NormalizeVillageName(_activeWorkingVillageName);
        return ResolveActiveSmithyQueue(villageName).FirstOrDefault()?.TimeLeftSeconds;
    }

    private int ResolveSmithyUpgradeActiveCount()
    {
        var villageName = NormalizeVillageName(GetSelectedVillageName())
            ?? NormalizeVillageName(_activeWorkingVillageName);
        return ResolveActiveSmithyQueue(villageName).Count;
    }

    private void TickSmithyUpgradeCountdown()
    {
        var villageName = NormalizeVillageName(GetSelectedVillageName())
            ?? NormalizeVillageName(_activeWorkingVillageName);
        _smithyUpgradeRemainingSeconds = ResolveActiveSmithyQueue(villageName)
            .Where(entry => entry.TimeLeftSeconds is > 0)
            .Select(entry => entry.TimeLeftSeconds!.Value)
            .ToList();
        UpdateAutomationLoopRunningIndicators();
        RefreshVillageActivityIndicatorsOnDashboard();
        RefreshTravianSmithyQueueUi();
    }

    private void TriggerSmithyUpgradeStatusRefresh(IReadOnlyList<Building>? knownBuildings, string source)
    {
        _ = source;
        if (_smithyUpgradeStatusRefreshRunning)
        {
            _pendingSmithyUpgradeStatusBuildings = knownBuildings?.ToList();
            return;
        }

        _smithyUpgradeStatusRefreshRunning = true;
        _backgroundTasks.Run(async cancellationToken =>
        {
            try
            {
                var options = ApplySelectedVillageToOptions(LoadBotOptions());
                var smithyStatus = await _botService.ReadSmithyUpgradeStatusAsync(options, AppendLog, knownBuildings, cancellationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    ApplySmithyUpgradeStatus(smithyStatus);
                    UpdateCachedTimerStatus(GetSelectedVillageName(), status => status with { SmithyUpgradeStatus = smithyStatus });
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"Could not refresh Smithy upgrade status: {ex.Message}");
            }
            finally
            {
                _smithyUpgradeStatusRefreshRunning = false;
                if (!cancellationToken.IsCancellationRequested
                    && _pendingSmithyUpgradeStatusBuildings is not null)
                {
                    var pendingBuildings = _pendingSmithyUpgradeStatusBuildings;
                    _pendingSmithyUpgradeStatusBuildings = null;
                    TriggerSmithyUpgradeStatusRefresh(pendingBuildings, "pending_refresh");
                }
            }
        });
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

        // Bail only when we can be certain a remote read is pointless.
        // Crucially, do NOT skip just because the local buildings scan missed gid=35 —
        // the remote read has a dedicated DOM fallback probe that catches breweries the
        // dorf2 scan misses (e.g. async-hydrated V3 layouts). Skipping here keeps the
        // "Brewery missing" blocked state stuck even though the building exists.
        if (!IsTeutonsTribe(status.Tribe) || status.IsCapital == false)
        {
            return;
        }

        try
        {
            var celebrationStatus = await _botService.ReadBreweryCelebrationStatusAsync(options, AppendLog, status.Buildings, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                // Seed the per-village brewery cache from authoritative remote reads so
                // later partial dorf2 scans don't regress to "Brewery missing".
                if (celebrationStatus.BreweryExists
                    && celebrationStatus.BrewerySlotId is > 0
                    && !string.IsNullOrWhiteSpace(status.ActiveVillage))
                {
                    _knownBrewerySlotByVillage[status.ActiveVillage] = celebrationStatus.BrewerySlotId.Value;
                }

                if (celebrationStatus.BreweryExists
                    && string.Equals(_breweryBlockedReasonKey, BreweryBlockedReasonMissing, StringComparison.OrdinalIgnoreCase))
                {
                    ClearBreweryBlockedState();
                    AppendLog("Brewery celebration group re-enabled: Brewery detected via remote probe.");
                }

                _troopTrainingViewModel.ApplyBreweryCelebrationStatus(celebrationStatus);
                UpdateCachedTimerStatus(status.ActiveVillage, cached => cached with { BreweryCelebrationStatus = celebrationStatus });
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
    /// Pulls the brewery celebration timer out of a run_brewery_celebration queue defer
    /// message and pushes it onto the troops-tab badge so it tracks the dashboard.
    /// Recognised defer messages:
    ///   - "Brewery celebration running. queue_wait_seconds=N"   → N is the celebration timer
    ///   - "Brewery celebration started. queue_wait_seconds=N"   → N is the celebration timer
    /// Other defer reasons (Teutons only, capital required, retry-after-no-button etc.)
    /// carry a queue retry interval, not the brewery timer, so we ignore them here.
    /// </summary>
    private void ApplyBreweryCelebrationDeferSignal(string? message, TimeSpan queueWaitDelay)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var seconds = (int)Math.Ceiling(queueWaitDelay.TotalSeconds);
        if (seconds <= 0)
        {
            return;
        }

        var lower = message.ToLowerInvariant();
        string? statusText = null;
        if (lower.Contains("brewery celebration running"))
        {
            statusText = "Celebration running.";
        }
        else if (lower.Contains("brewery celebration started"))
        {
            statusText = "Celebration started.";
        }
        else
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            _troopTrainingViewModel.PushBreweryCelebrationRemainingSeconds(seconds, statusText);
        });
    }

    /// <summary>
    /// Manual "Check celebration" button on the troops tab. Navigates to the brewery,
    /// reads the live celebration status, and updates the UI (timer + Brewery-found
    /// indicator). Bypasses the queue so it works even when no continuous loop is
    /// running.
    /// </summary>
    internal async Task OnCheckCelebrationClickedAsync()
    {
        if (BlockIfSessionSleeping("Check celebration"))
        {
            return;
        }

        if (!_isLoggedIn)
        {
            _troopTrainingViewModel.InfoText = "Log in first to check celebration status.";
            return;
        }

        var operationId = BeginOperation("Check celebration");
        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            AppendLog($"[{operationId}] Manual celebration check requested.");
            await RefreshBreweryCelebrationStatusAsync(options, _lastBuildingStatus, CancellationToken.None);
            _troopTrainingViewModel.InfoText = "Celebration status refreshed.";
            CompleteOperation(operationId, sw, "Celebration check completed.");
        }
        catch (Exception ex)
        {
            _troopTrainingViewModel.InfoText = $"Celebration check failed: {ex.Message}";
            FailOperation(operationId, sw, ex);
        }
    }

    /// <summary>
    /// Opens the Smithy upgrade-options popup for the SELECTED village, seeding it from the tribe troop
    /// catalog merged with that village's saved selection. On Save it persists the village's selection; on
    /// "Sync to all villages" it copies the selection to every village. Called by the Troops panel's
    /// "Upgrade options" button.
    /// </summary>
    internal void OnTroopsUpgradeOptionsClicked()
    {
        var account = _accountStore.ActiveAccountName();
        var villageInfo = GetSelectedVillageKeyInfoOrNull();
        if (villageInfo is null)
        {
            _troopTrainingViewModel.InfoText = "Select a village on the Dashboard first to configure its Smithy upgrades.";
            AppendLog("Smithy upgrade options: no village selected.");
            return;
        }

        var options = BuildSmithyTroopOptions(account, villageInfo.Key);
        if (options.Count == 0)
        {
            _troopTrainingViewModel.InfoText = "Could not determine the tribe's troops yet. Scan the account first.";
            AppendLog("Smithy upgrade options: no troops resolved for the current tribe.");
            return;
        }

        var window = new SmithyUpgradeOptionsWindow(options, villageInfo.Name) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        if (window.SyncRequested)
        {
            var keys = GetAllVillageKeyInfos().Select(info => info.Key).ToList();
            SmithyUpgradeTargetsStore.SaveForVillages(_projectRoot, account, keys, window.Result);
            // Drop every village's stale smithy queue item so the loop re-enqueues with the synced targets.
            var removedAll = RemoveSmithyQueueItemsForVillage(null);
            _troopTrainingViewModel.InfoText = $"Synced Smithy upgrade options to {keys.Count} village(s).";
            AppendLog($"Synced Smithy upgrade options from '{villageInfo.Name}' to {keys.Count} village(s) "
                + $"({window.Result.Count} troop(s)). Cleared {removedAll} queued smithy task(s) to apply the change.");
            return;
        }

        SmithyUpgradeTargetsStore.Save(_projectRoot, account, villageInfo.Key, window.Result);
        // Drop this village's stale smithy queue item (old troop snapshot) so the loop re-enqueues with the
        // new selection — otherwise the dedup keeps the old targets running until the stale item completes.
        var removed = RemoveSmithyQueueItemsForVillage(villageInfo.Name);
        _troopTrainingViewModel.InfoText = window.Result.Count > 0
            ? $"Saved Smithy upgrade options for '{villageInfo.Name}' ({window.Result.Count} troop(s))."
            : $"Saved Smithy upgrade options for '{villageInfo.Name}': no troops selected.";
        AppendLog($"Saved Smithy upgrade options for '{villageInfo.Name}' ({window.Result.Count} troop(s) selected)."
            + (removed > 0 ? $" Cleared {removed} queued smithy task(s) to apply the change." : string.Empty));
    }

    /// <summary>
    /// Opens the quick per-village troop-training settings popup for all known villages. The popup only
    /// edits each building's enabled/troop choice and preserves advanced settings from the village override
    /// or global defaults. Stale build_troops queue items are dropped so the loop re-enqueues fresh payloads.
    /// Called by the Troops panel's "Troop settings" button.
    /// </summary>
    // Top-bar "Troop settings" button (always visible). Opens the same per-village training overview as
    // the Troops panel's "Troop settings" button.
    private void TroopSettingsButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        OnTroopsTrainingOptionsClicked();
    }

    internal void OnTroopsTrainingOptionsClicked()
    {
        OpenTroopSettingsWindow(null);
    }

    private void OpenTroopSettingsFromVillageSettings(IReadOnlyList<VillageSettingsRow> villageSettingsRows)
    {
        OpenTroopSettingsWindow(villageSettingsRows);
    }

    private void OpenTroopSettingsWindow(IReadOnlyList<VillageSettingsRow>? villageSettingsRows)
    {
        var account = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(account))
        {
            _troopTrainingViewModel.InfoText = "Select an account before configuring troop training.";
            AppendLog("Troop settings: no active account.");
            return;
        }

        var tribe = ResolveStoredTroopTrainingTribe();
        var troopOptions = TroopCatalog.ResolveTroopTypesForTribe(tribe);
        if (troopOptions.Count == 0)
        {
            _troopTrainingViewModel.InfoText = "Could not determine the tribe's troops yet. Scan the account first.";
            AppendLog("Troop settings: no troops resolved for the current tribe.");
            return;
        }

        var villages = GetAllVillageKeyInfos();
        if (villages.Count == 0)
        {
            _troopTrainingViewModel.InfoText = "Load villages before configuring troop training.";
            AppendLog("Troop settings: no villages loaded.");
            return;
        }

        var globalOptions = LoadBotOptions();
        var troopTrainingGroupKey = QueueGroupCatalog.GetKey(QueueGroup.TroopTraining);
        var rows = new List<TroopTrainingQuickVillageRow>(villages.Count);
        foreach (var village in villages)
        {
            var saved = TroopTrainingSettingsStore.Load(_projectRoot, account, village.Key);
            // When the village has an override, overlay it on the global options so the row opens on that
            // village's own settings; otherwise it opens on the global defaults.
            var effectiveOptions = saved is null
                ? globalOptions
                : BotOptionsPayloadApplier.Apply(globalOptions, saved.ToDictionary());
            rows.Add(new TroopTrainingQuickVillageRow(
                village.Key,
                village.Name,
                ResolveBuildTroopsEnabledForVillage(village, villageSettingsRows, troopTrainingGroupKey),
                TroopTrainingQuickSettings.FromOptions(effectiveOptions),
                tribe));
        }

        var window = new TroopTrainingOptionsWindow(rows) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        foreach (var result in window.Results)
        {
            TroopTrainingSettingsStore.Save(_projectRoot, account, result.VillageKey, result.Settings);
            var village = villages.FirstOrDefault(v => string.Equals(v.Key, result.VillageKey, StringComparison.OrdinalIgnoreCase))
                ?? new VillageSettingsStore.VillageKeyInfo(result.VillageKey, result.VillageName, null, null, false);
            PersistBuildTroopsGroupEnabled(village, result.IsBuildTroopsEnabled, troopTrainingGroupKey);
            UpdateVillageSettingsBuildTroopsRow(villageSettingsRows, result, troopTrainingGroupKey);
        }

        var removed = RemoveTroopTrainingQueueItemsForVillage(null);
        // Reload the Troops tab from the (possibly just-changed) selected village's override so the
        // two surfaces stay in sync.
        ApplyTroopTrainingForSelectedVillage();
        _troopTrainingViewModel.InfoText = $"Saved troop-training settings for {window.Results.Count} village(s).";
        AppendLog($"Saved troop-training settings for {window.Results.Count} village(s). "
            + $"Cleared {removed} queued build_troops task(s) to apply the change.");
    }

    private bool ResolveBuildTroopsEnabledForVillage(
        VillageSettingsStore.VillageKeyInfo village,
        IReadOnlyList<VillageSettingsRow>? villageSettingsRows,
        string troopTrainingGroupKey)
    {
        var row = FindVillageSettingsRow(villageSettingsRows, village);
        var rowToggle = row?.GroupToggles.FirstOrDefault(toggle =>
            string.Equals(toggle.GroupKey, troopTrainingGroupKey, StringComparison.OrdinalIgnoreCase));
        if (rowToggle is not null)
        {
            return rowToggle.IsEnabled;
        }

        var groups = _villageSettingsStore.GetEnabledGroups(village)
            ?? VillageSettingsStore.DefaultEnabledGroups;
        return groups.Contains(troopTrainingGroupKey, StringComparer.OrdinalIgnoreCase);
    }

    private void PersistBuildTroopsGroupEnabled(
        VillageSettingsStore.VillageKeyInfo village,
        bool enabled,
        string troopTrainingGroupKey)
    {
        PersistAutomationGroupEnabledForVillage(village, enabled, troopTrainingGroupKey);
    }

    private static void UpdateVillageSettingsBuildTroopsRow(
        IReadOnlyList<VillageSettingsRow>? villageSettingsRows,
        TroopTrainingQuickVillageResult result,
        string troopTrainingGroupKey)
    {
        UpdateVillageSettingsGroupRow(
            villageSettingsRows,
            result.VillageKey,
            result.VillageName,
            troopTrainingGroupKey,
            result.IsBuildTroopsEnabled);
    }

    private static VillageSettingsRow? FindVillageSettingsRow(
        IReadOnlyList<VillageSettingsRow>? rows,
        VillageSettingsStore.VillageKeyInfo village)
    {
        return rows?.FirstOrDefault(row =>
            row.KeyInfo is not null
            && (string.Equals(row.KeyInfo.Key, village.Key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.KeyInfo.Name, village.Name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Troops panel's "Sync settings" button. After a confirmation, copies the settings currently shown on
    /// the Troops tab (the building rules / troop / amount / run trigger / checks / fallback) to EVERY
    /// village's per-village override, and drops stale build_troops queue items so the loop re-enqueues with
    /// the synced settings.
    /// </summary>
    internal void OnTroopsSyncSettingsClicked()
    {
        var account = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(account))
        {
            _troopTrainingViewModel.InfoText = "Select an account before syncing troop training.";
            AppendLog("Sync troop settings: no active account.");
            return;
        }

        var keys = GetAllVillageKeyInfos().Select(info => info.Key).ToList();
        if (keys.Count == 0)
        {
            _troopTrainingViewModel.InfoText = "Load villages before syncing troop training.";
            AppendLog("Sync troop settings: no villages loaded.");
            return;
        }

        var confirm = AppDialog.Show(
            this,
            $"Sync the troop-training settings shown here to all {keys.Count} village(s)? This overwrites each village's build-troops settings.",
            "Sync troop settings",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        TroopTrainingSettingsStore.SaveForVillages(_projectRoot, account, keys, _troopTrainingViewModel.BuildVillageTrainingPayload());
        var removed = RemoveTroopTrainingQueueItemsForVillage(null);
        _troopTrainingViewModel.InfoText = $"Synced troop-training settings to {keys.Count} village(s).";
        AppendLog($"Synced troop-training settings to {keys.Count} village(s). "
            + $"Cleared {removed} queued build_troops task(s) to apply the change.");
    }

    // Loads the selected village's per-village troop-training override into the Troops tab's building
    // rows (or the account-wide defaults when the village has no override). Keeps the tab and the
    // "Troop settings" popup showing the same per-village settings. Does not persist.
    private void ApplyTroopTrainingForSelectedVillage()
    {
        var account = _accountStore.ActiveAccountName();
        var key = GetSelectedVillageKey();
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var payload = TroopTrainingSettingsStore.Load(_projectRoot, account, key)
            ?? TroopTrainingQuickSettings.FromOptions(LoadBotOptions());
        _troopTrainingViewModel.ApplyVillageTrainingPayload(payload);
    }

    // Persists the Troops tab's building rules (enable/troop/amount/run trigger/checks/fallback) as the
    // selected village's per-village override. Account-wide settings (NPC trade, gold, celebration) are
    // saved separately by PersistTroopTrainingConfig. Falls back to the account-wide config when no
    // village is selected so a stray edit before login still lands somewhere sensible.
    private void PersistTroopTrainingForSelectedVillage()
    {
        var account = _accountStore.ActiveAccountName();
        var key = GetSelectedVillageKey();
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(key))
        {
            PersistTroopTrainingConfig();
            return;
        }

        try
        {
            TroopTrainingSettingsStore.Save(_projectRoot, account, key, _troopTrainingViewModel.BuildVillageTrainingPayload());
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save troop training for selected village: {ex.Message}");
        }
    }

    // Builds the troop rows for the upgrade-options popup: the tribe's improvable troops (combat + siege,
    // excluding the expansion units that the Smithy never lists) merged with the village's saved selection so
    // the popup reopens with the user's prior choices. Troops are keyed by Travian unit id ("u21") when the
    // tribe is known, falling back to the troop slot ("t1") otherwise.
    private List<SmithyTroopOption> BuildSmithyTroopOptions(string? account, string? villageKey)
    {
        var tribe = ResolveStoredTroopTrainingTribe();
        var troopNames = TroopCatalog.ResolveTroopTypesForTribe(tribe);
        if (troopNames.Count == 0)
        {
            return [];
        }

        var saved = SmithyUpgradeTargetsStore.Load(_projectRoot, account, villageKey)
            .ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

        // The Smithy improves combat units, rams and siege — but not the two expansion units (Chief/Senator
        // and Settler) at the end of every tribe list.
        var improvable = troopNames.Take(Math.Min(8, troopNames.Count)).ToList();

        var options = new List<SmithyTroopOption>(improvable.Count);
        for (var index = 0; index < improvable.Count; index++)
        {
            var name = improvable[index];
            var unitId = TroopCatalog.ResolveTravianUnitId(tribe, name);
            var key = unitId.HasValue ? $"u{unitId.Value}" : $"t{index + 1}";
            var enabled = saved.TryGetValue(key, out var savedSelection);
            var targetLevel = enabled ? savedSelection!.TargetLevel : SmithyUpgradeOptionsWindow.MaxLevel;
            options.Add(new SmithyTroopOption(key, name, enabled, targetLevel));
        }

        return options;
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
        if (BlockIfSessionSleeping("Refresh troop queues"))
        {
            return;
        }

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
        var smithyStatus = await _botService.ReadSmithyUpgradeStatusAsync(options, AppendLog, effectiveBuildings, cancellationToken);
        await Dispatcher.InvokeAsync(() =>
        {
            // The in-building training queue is the source of truth: keep a still-ticking queue from the
            // per-village cache when this read came back empty (a partial / off-village read), so the
            // dashboard timer doesn't vanish. Mirrors ApplySmithyUpgradeStatus.
            var queueVillageName = NormalizeVillageName(GetSelectedVillageName())
                ?? NormalizeVillageName(_activeWorkingVillageName);
            if (queueVillageName is not null
                && _villageStatusCacheByName.TryGetValue(queueVillageName, out var cachedVillageStatus))
            {
                queueStatuses = TroopTrainingQueueState.PreserveKnownActiveQueue(
                    queueStatuses, cachedVillageStatus.TroopTrainingQueues, GetServerNow());
            }

            var effectiveStatus = _lastBuildingStatus is null
                ? null
                : _lastBuildingStatus with { TroopTrainingQueues = queueStatuses };
            if (effectiveStatus is not null)
            {
                _lastBuildingStatus = effectiveStatus;
                _troopTrainingViewModel.ApplyStatus(effectiveStatus, queueStatuses);
                UpdateCachedTimerStatus(effectiveStatus.ActiveVillage, cached => cached with
                {
                    TroopTrainingQueues = queueStatuses,
                    SmithyUpgradeStatus = smithyStatus,
                });
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

            ApplySmithyUpgradeStatus(smithyStatus);
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
