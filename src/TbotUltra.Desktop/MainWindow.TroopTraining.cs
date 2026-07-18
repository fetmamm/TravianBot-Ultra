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
    private bool _lastAutoCelebrationEnabledForChangeTracking;

    private static bool IsTeutonsTribe(string? tribe)
    {
        return string.Equals(tribe?.Trim(), "Teutons", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveStoredTroopTrainingTribe()
    {
        var selectedName = GetSelectedVillageName();
        return ResolveVillageTribeByName(selectedName);
    }

    private string ResolveVillageTribeByName(string? villageName)
    {
        var normalizedName = NormalizeVillageName(villageName);
        if (normalizedName is not null
            && _villageStatusCache.TryGetByName(normalizedName, out var status)
            && TroopCatalog.IsKnownTribe(status.Tribe))
        {
            return status.Tribe;
        }

        var item = (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)?
            .FirstOrDefault(candidate => string.Equals(
                NormalizeVillageName(candidate.Name),
                normalizedName,
                StringComparison.OrdinalIgnoreCase));
        if (TroopCatalog.IsKnownTribe(item?.Tribe))
        {
            return item!.Tribe;
        }

        return "Unknown";
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
                Tribe: ResolveTribeForSnapshotWrite(existing?.Tribe),
                GoldClubEnabled: existing?.GoldClubEnabled ?? false,
                BuildingCatalog: existing?.BuildingCatalog ?? [],
                AutoCelebrationEnabled: enabled,
                AutomationLoopEnabledGroups: existing?.AutomationLoopEnabledGroups,
                AutomationLoopVisibleGroups: existing?.AutomationLoopVisibleGroups,
                WorldUid: existing?.WorldUid);
            _accountAnalysisStore.Save(snapshot);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save auto celebration preference: {ex.Message}");
        }
    }

    private bool _troopTribeUnknownSkipLogged;

    // Account analysis stores the permanent avatar tribe. Never fill it from the selected village:
    // mixed-tribe servers allow those values to differ.
    private string ResolveTribeForSnapshotWrite(string? existingTribe)
    {
        if (TroopCatalog.IsKnownTribe(existingTribe))
        {
            return existingTribe!;
        }

        return string.Empty;
    }

    private void ApplyTroopTrainingTribeState(string? tribe)
    {
        // Village-status reads sometimes carry Tribe="Unknown"/empty (same reason SetTribeText is
        // hardened). Rebuilding from an unknown tribe swaps the dropdowns to the generic fallback
        // list AND persists fallback troop names into the village override — so re-resolve from the
        // stored analysis, and keep the current lists when no real tribe is known. The lists are
        // only empty before the very first apply; in that case fall through so a fresh install
        // still gets the generic list.
        if (!TroopCatalog.IsKnownTribe(tribe))
        {
            var storedTribe = ResolveStoredTroopTrainingTribe();
            if (!TroopCatalog.IsKnownTribe(storedTribe)
                && _troopTrainingViewModel.Buildings.Any(option => option.TroopOptions.Count > 0))
            {
                if (!_troopTribeUnknownSkipLogged)
                {
                    _troopTribeUnknownSkipLogged = true;
                    AppendLog($"[troops] ignored unknown tribe '{tribe}' from status read — kept current troop options.");
                }

                return;
            }

            tribe = storedTribe;
        }
        else
        {
            _troopTribeUnknownSkipLogged = false;
        }

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
        var autoCelebrationTurnedOn = !_lastAutoCelebrationEnabledForChangeTracking
            && _troopTrainingViewModel.AutoCelebrationEnabled;
        _lastAutoCelebrationEnabledForChangeTracking = _troopTrainingViewModel.AutoCelebrationEnabled;

        // Account-wide settings (NPC trade, gold, celebration) go to the account config; the per-building
        // training rules go to the selected village's override so the Troops tab and the per-village
        // "Troop settings" popup edits the same data.
        PersistTroopTrainingConfig();
        PersistTroopTrainingForSelectedVillage();
        PersistAutoCelebrationPreferenceForActiveAccount(_troopTrainingViewModel.AutoCelebrationEnabled);
        UpdateAutomationLoopRunningIndicators();
        if (autoCelebrationTurnedOn
            && _troopTrainingViewModel.IsAutoCelebrationAvailableForCurrentTribe)
        {
            WakeBreweryCelebrationAutomation("Auto celebration enabled");
        }

        if (_lastResourceStatusForUi is not null)
        {
            _troopTrainingDeferredRefreshDebounceTimer.Stop();
            _troopTrainingDeferredRefreshDebounceTimer.Start();
        }
    }

    private VillageSelectionItem? GetCapitalVillageSelectionSnapshot()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(GetCapitalVillageSelectionSnapshot);
        }

        var source = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? Enumerable.Empty<VillageSelectionItem>();

        return source.FirstOrDefault(v =>
            v.IsCapital
            && !string.IsNullOrWhiteSpace(v.Name)
            && !string.Equals(v.Name, "-", StringComparison.Ordinal));
    }

    private BotOptions ApplyCapitalVillageToOptions(BotOptions source, VillageSelectionItem capital)
    {
        var options = BotOptionsPayloadApplier.Apply(source, BuildVillageRuntimePayload(capital));
        return ApplyHeroResourceSettingsForVillage(options, GetVillageKey(capital), capital.Name);
    }

    private VillageStatus ResolveCapitalBreweryStatusSeed(VillageSelectionItem capital)
    {
        var capitalName = NormalizeVillageName(capital.Name);
        if (capitalName is not null
            && _villageStatusCache.TryGetByName(capitalName, out var cached))
        {
            return cached;
        }

        if (_lastBuildingStatus is not null
            && (_lastBuildingStatus.IsCapital == true
                || string.Equals(NormalizeVillageName(_lastBuildingStatus.ActiveVillage), capitalName, StringComparison.OrdinalIgnoreCase)))
        {
            return _lastBuildingStatus;
        }

        return new VillageStatus(
            capital.Name,
            [new Village(capital.Name, capital.Url, true, capital.CoordX, capital.CoordY, capital.Population)],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [],
            [],
            [],
            Tribe: ResolveVillageTribeByName(capital.Name),
            VillageCount: 1,
            IsCapital: true);
    }

    private void WakeBreweryCelebrationAutomation(string reason)
    {
        var capital = GetCapitalVillageSelectionSnapshot();
        if (capital is null)
        {
            AppendLog($"Brewery celebration: {reason}, but no capital village is loaded.");
            return;
        }

        if (!IsGroupEnabledForVillage(GetVillageKey(capital), QueueGroup.BreweryCelebration))
        {
            return;
        }

        var continuousLoopRunning = IsContinuousLoopRunning();
        if (continuousLoopRunning || _autoQueueRunning)
        {
            Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
            if (_autoQueueRunning && !continuousLoopRunning)
            {
                _startContinuousLoopAfterQueueStop = true;
                _loopController.RequestQueueStop();
                AppendLog($"Brewery celebration: {reason}. Queue wait will stop and continuous loop will check it now.");
            }
            else
            {
                AppendLog($"Brewery celebration: {reason}. Continuous loop will check it now.");
            }
        }
        else
        {
            TriggerBreweryCelebrationVerificationRefresh();
        }
    }

    private void EnsureAutoCelebrationEnabledForBreweryGroup()
    {
        if (!_troopTrainingViewModel.IsAutoCelebrationAvailableForCurrentTribe
            || _troopTrainingViewModel.AutoCelebrationEnabled)
        {
            return;
        }

        var capital = GetCapitalVillageSelectionSnapshot();
        if (capital is null
            || !IsGroupEnabledForVillage(GetVillageKey(capital), QueueGroup.BreweryCelebration))
        {
            return;
        }

        _troopTrainingViewModel.AutoCelebrationEnabled = true;
        AppendLog("Brewery Celebration group is enabled for the capital. Auto celebration was off and has been enabled.");
    }

    private void PersistBreweryGroupForCapital(bool enabled)
    {
        var capital = GetCapitalVillageSelectionSnapshot();
        if (capital is null)
        {
            AppendLog("Brewery celebration: could not sync group setting to capital because no capital village is loaded.");
            return;
        }

        PersistAutomationGroupEnabledForVillage(
            BuildVillageKeyInfo(capital),
            enabled,
            QueueGroupCatalog.GetKey(QueueGroup.BreweryCelebration));
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

        // Don't wipe the running countdown here — local refreshes fire every ~20s while
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
            && _villageStatusCache.TryGetByName(villageName, out var cached))
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
        if (IsMainTabSelected(DashboardTabItem))
        {
            RefreshVillageActivityIndicatorsOnDashboard();
        }
        else if (IsMainTabSelected(QueueTabItem))
        {
            RefreshTravianSmithyQueueUi();
        }
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
        // This method runs on background threads too (queue-loop refreshes), so all viewmodel/UI
        // updates must go through the dispatcher — direct calls threw
        // "The calling thread cannot access this object because a different thread owns it".
        if (status is null)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _troopTrainingViewModel.ResetBreweryCelebrationStatus();
                UpdateAutomationLoopRunningIndicators();
            });
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            ApplyLocalBreweryCelebrationStatus(status);
            UpdateAutomationLoopRunningIndicators();
        });

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
            var capital = GetCapitalVillageSelectionSnapshot();
            var options = capital is null
                ? ApplySelectedVillageToOptions(LoadBotOptions())
                : ApplyCapitalVillageToOptions(LoadBotOptions(), capital);
            var status = capital is null
                ? _lastBuildingStatus
                : ResolveCapitalBreweryStatusSeed(capital);
            AppendLog($"[{operationId}] Manual celebration check requested.");
            await RefreshBreweryCelebrationStatusAsync(options, status, _loopController.AcquireSessionScopeToken());
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
        var villageInfo = GetSelectedVillageKeyInfoOrNull();
        if (villageInfo is null)
        {
            _troopTrainingViewModel.InfoText = "Select a village on the Dashboard first to configure its Smithy upgrades.";
            AppendLog("Smithy upgrade options: no village selected.");
            return;
        }

        OpenSmithyUpgradeOptionsForVillage(villageInfo);
    }

    // Opens the Smithy "Upgrade options" popup for a SPECIFIC village and saves against ITS key,
    // independent of which village the bot/UI is currently on. Used by the Troops panel button (selected
    // village) and the Village settings per-row gear (that row's village).
    private void OpenSmithyUpgradeOptionsForVillage(VillageSettingsStore.VillageKeyInfo villageInfo)
    {
        var account = _accountStore.ActiveAccountName();
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
            var villageInfos = GetAllVillageKeyInfos();
            var keys = villageInfos.Select(info => info.Key).ToList();
            SmithyUpgradeTargetsStore.SaveForVillages(_projectRoot, account, keys, window.Result);
            // Selecting troops re-enables each village's Upgrade Troops group (any may have been
            // auto-disabled on a previous "All done"). No selection leaves the toggles untouched.
            if (window.Result.Count > 0)
            {
                var troopsGroupKey = QueueGroupCatalog.GetKey(QueueGroup.Troops);
                foreach (var info in villageInfos)
                {
                    PersistAutomationGroupEnabledForVillage(info, enabled: true, troopsGroupKey);
                }
            }
            // Drop every village's stale smithy queue item so the loop re-enqueues with the synced targets.
            var removedAll = RemoveSmithyQueueItemsForVillage(null);
            _troopTrainingViewModel.InfoText = $"Synced Smithy upgrade options to {keys.Count} village(s).";
            AppendLog($"Synced Smithy upgrade options from '{villageInfo.Name}' to {keys.Count} village(s) "
                + $"({window.Result.Count} troop(s)). Cleared {removedAll} queued smithy task(s) to apply the change.");
            return;
        }

        SmithyUpgradeTargetsStore.Save(_projectRoot, account, villageInfo.Key, window.Result);
        // Selecting troops re-enables this village's Upgrade Troops group (it may have been auto-disabled
        // when a previous run reported "All done"). No selection leaves the toggle untouched.
        if (window.Result.Count > 0)
        {
            PersistAutomationGroupEnabledForVillage(villageInfo, enabled: true, QueueGroupCatalog.GetKey(QueueGroup.Troops));
        }
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

    // Village settings "Upgrade troops" gear: opens the Smithy "Upgrade options" popup for the village on
    // the clicked ROW (the overview is per-row), regardless of which village the bot is currently on.
    private void OpenSmithyUpgradeSettingsFromVillageSettings(VillageSettingsRow villageSettingsRow)
    {
        if (villageSettingsRow?.KeyInfo is null)
        {
            AppendLog("Smithy upgrade options: village row has no key.");
            return;
        }

        OpenSmithyUpgradeOptionsForVillage(villageSettingsRow.KeyInfo);
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
            var tribe = ResolveVillageTribeByName(village.Name);
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

        var sourceTribe = ResolveStoredTroopTrainingTribe();
        if (!TroopCatalog.IsKnownTribe(sourceTribe))
        {
            _troopTrainingViewModel.InfoText = "Scan the selected village before syncing troop training.";
            AppendLog("Sync troop settings: selected village tribe is unknown.");
            return;
        }

        var allVillages = GetAllVillageKeyInfos();
        var matchingVillages = allVillages
            .Where(info => string.Equals(ResolveVillageTribeByName(info.Name), sourceTribe, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var skippedVillages = allVillages
            .Where(info => !matchingVillages.Contains(info))
            .Select(info => info.Name)
            .ToList();
        var keys = matchingVillages.Select(info => info.Key).ToList();
        if (keys.Count == 0)
        {
            _troopTrainingViewModel.InfoText = "No villages with the selected village's tribe are loaded.";
            AppendLog($"Sync troop settings: no villages loaded for tribe '{sourceTribe}'.");
            return;
        }

        var confirm = AppDialog.Show(
            this,
            $"Sync the troop-training settings shown here to {keys.Count} {sourceTribe} village(s)? "
            + $"{skippedVillages.Count} village(s) with another or unknown tribe will be skipped.",
            "Sync troop settings",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        TroopTrainingSettingsStore.SaveForVillages(_projectRoot, account, keys, _troopTrainingViewModel.BuildVillageTrainingPayload());
        var removed = matchingVillages.Sum(village => RemoveTroopTrainingQueueItemsForVillage(village.Name));
        _troopTrainingViewModel.InfoText = $"Synced troop-training settings to {keys.Count} {sourceTribe} village(s); skipped {skippedVillages.Count}.";
        AppendLog($"Synced troop-training settings to {keys.Count} village(s). "
            + $"Skipped {skippedVillages.Count}: {string.Join(", ", skippedVillages)}. "
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

    private IReadOnlyList<TroopTrainingQueueStatus>? ResolveTroopTrainingQueuesForStatus(VillageStatus status)
    {
        if (status.TroopTrainingQueues is not null)
        {
            return status.TroopTrainingQueues;
        }

        var statusName = NormalizeVillageName(status.ActiveVillage);
        if (statusName is null)
        {
            return null;
        }

        if (_villageStatusCache.TryGetByName(statusName, out var cached))
        {
            return cached.TroopTrainingQueues;
        }

        return _lastBuildingStatus is not null
            && string.Equals(
                NormalizeVillageName(_lastBuildingStatus.ActiveVillage),
                statusName,
                StringComparison.OrdinalIgnoreCase)
            ? _lastBuildingStatus.TroopTrainingQueues
            : null;
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
        var payload = _troopTrainingViewModel.BuildVillageTrainingPayload().ToDictionary();
        EnqueueQuickTask("build_troops", "Build troops", payload);
        _troopTrainingViewModel.InfoText = "Queued: build troops.";
        AppendLog("Queued build_troops task with selected village troop-training settings.");
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
            await RefreshTroopTrainingQueuesAsync(options, _loopController.AcquireSessionScopeToken(), _lastBuildingStatus?.Buildings, refreshBuildingsBeforeRead: true);
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
        bool refreshBuildingsBeforeRead = false,
        bool includeSmithyStatus = true)
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
                    var troopTrainingQueues = ResolveTroopTrainingQueuesForStatus(refreshedStatus);
                    var refreshedStatusWithQueues = refreshedStatus with { TroopTrainingQueues = troopTrainingQueues };
                    CacheVillageStatus(refreshedStatusWithQueues);
                    _lastBuildingStatus = _lastBuildingStatus is null
                        ? refreshedStatusWithQueues
                        : _lastBuildingStatus with
                        {
                            ActiveVillage = refreshedStatus.ActiveVillage,
                            Villages = refreshedStatus.Villages,
                            Tribe = refreshedStatus.Tribe,
                            Buildings = refreshedStatus.Buildings,
                            IsCapital = refreshedStatus.IsCapital,
                            TroopTrainingQueues = troopTrainingQueues,
                        };

                    if (IsStatusForSelectedVillage(_lastBuildingStatus))
                    {
                        _troopTrainingViewModel.ApplyStatus(_lastBuildingStatus, troopTrainingQueues);
                    }
                    else
                    {
                        AppendLog($"[troop-training-ui] skipped queue repaint: status is for '{_lastBuildingStatus.ActiveVillage}', another village is selected. Cache updated.");
                    }
                });
                await RefreshBreweryCelebrationStatusAsync(options, refreshedStatus, cancellationToken);
            }
            catch (Exception ex)
            {
                AppendLog($"Could not refresh troop building list before queue read: {ex.Message}");
            }
        }

        var queueStatuses = await _botService.ReadTroopTrainingQueuesAsync(options, AppendLog, effectiveBuildings, cancellationToken);
        SmithyUpgradeStatus? smithyStatus = null;
        if (includeSmithyStatus)
        {
            smithyStatus = await _botService.ReadSmithyUpgradeStatusAsync(options, AppendLog, effectiveBuildings, cancellationToken);
        }

        await Dispatcher.InvokeAsync(() =>
        {
            // The in-building training queue is the source of truth: keep a still-ticking queue from the
            // per-village cache when this read came back empty (a partial / off-village read), so the
            // dashboard timer doesn't vanish. Mirrors ApplySmithyUpgradeStatus.
            var queueVillageName = NormalizeVillageName(options.TargetVillageName)
                ?? NormalizeVillageName(_activeWorkingVillageName)
                ?? NormalizeVillageName(GetSelectedVillageName());
            if (queueVillageName is not null
                && _villageStatusCache.TryGetByName(queueVillageName, out var cachedVillageStatus))
            {
                queueStatuses = TroopTrainingQueueState.PreserveKnownActiveQueue(
                    queueStatuses, cachedVillageStatus.TroopTrainingQueues, GetServerNow());
            }

            var effectiveStatus = _lastBuildingStatus is null
                ? null
                : _lastBuildingStatus with { TroopTrainingQueues = queueStatuses };
            if (queueVillageName is not null
                && (effectiveStatus is null
                    || !string.Equals(
                        NormalizeVillageName(effectiveStatus.ActiveVillage),
                        queueVillageName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                effectiveStatus = _villageStatusCache.TryGetByName(queueVillageName, out var queueVillageStatus)
                    ? queueVillageStatus with { TroopTrainingQueues = queueStatuses }
                    : new VillageStatus(
                        ActiveVillage: queueVillageName,
                        Villages: [],
                        Resources: new Dictionary<string, string>(),
                        ResourceFields: [],
                        Buildings: effectiveBuildings?.ToList() ?? [],
                        BuildQueue: [],
                        TroopTrainingQueues: queueStatuses);
            }

            if (effectiveStatus is not null)
            {
                _lastBuildingStatus = effectiveStatus;
                CacheVillageStatus(effectiveStatus);
                if (IsStatusForSelectedVillage(effectiveStatus))
                {
                    _troopTrainingViewModel.ApplyStatus(effectiveStatus, queueStatuses);
                }
                else
                {
                    AppendLog($"[troop-training-ui] skipped queue repaint: status is for '{effectiveStatus.ActiveVillage}', another village is selected. Cache updated.");
                }

                UpdateCachedTimerStatus(effectiveStatus.ActiveVillage, cached => cached with
                {
                    TroopTrainingQueues = queueStatuses,
                    SmithyUpgradeStatus = smithyStatus ?? cached.SmithyUpgradeStatus,
                });
            }
            else
            {
                var statusWithoutVillage = new VillageStatus(
                    ActiveVillage: string.Empty,
                    Villages: [],
                    Resources: new Dictionary<string, string>(),
                    ResourceFields: [],
                    Buildings: effectiveBuildings?.ToList() ?? [],
                    BuildQueue: [],
                    TroopTrainingQueues: queueStatuses);
                if (IsStatusForSelectedVillage(statusWithoutVillage))
                {
                    _troopTrainingViewModel.ApplyStatus(statusWithoutVillage, queueStatuses);
                }
                else
                {
                    AppendLog("[troop-training-ui] skipped queue repaint: village could not be matched to the selected village.");
                }
            }

            if (smithyStatus is not null)
            {
                ApplySmithyUpgradeStatus(smithyStatus);
            }

            UpdateAutomationLoopRunningIndicators();
            // Repaint the dashboard village list so the per-village Troops B/S/W icon reflects the
            // just-read training queue (it renders from the per-village cache updated above).
            RefreshVillageActivityIndicatorsOnDashboard();
        });
    }

    private async Task RefreshTroopTrainingUiAfterBuildAsync(QueueItem item, BotOptions options, CancellationToken cancellationToken)
    {
        var itemOptions = ApplyHeroResourceSettingsForQueueItem(
            BotOptionsPayloadApplier.Apply(options, item.Payload),
            item);
        // No dorf2 buildings re-read here: build_troops doesn't change the building list, and the queue
        // read below reuses the statuses the task just read (session snapshot) instead of re-navigating.
        await RefreshTroopTrainingQueuesAsync(
            itemOptions,
            cancellationToken,
            _lastBuildingStatus?.Buildings,
            refreshBuildingsBeforeRead: false,
            includeSmithyStatus: false);

        try
        {
            await RefreshResourceSnapshotForUiAsync(itemOptions, cancellationToken, currentPageOnly: true);
        }
        catch (Exception ex)
        {
            AppendLog($"Troop build current-page resource refresh failed, falling back: {ex.Message}");
            await RefreshResourceSnapshotForUiAsync(itemOptions, cancellationToken);
        }
    }
}
