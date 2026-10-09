using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

/// <summary>
/// Hero / Adventures host-side logic. After 3b5 the Hero TabItem is rendered
/// by <see cref="Views.HeroPanel"/>; <see cref="HeroPanelService"/> owns the
/// panel's persistence, payload creation, and Worker calls. This partial keeps
/// host-only lifecycle, cancellation, dialog, and dashboard integration.
///
/// Drag-and-drop scratch state and the drag handlers themselves live on
/// <see cref="Views.HeroPanel"/>.
/// </summary>
public partial class MainWindow
{
    private async Task RunHeroPanelOperationAsync(Func<Task> action)
    {
        await GuardUiAsync(async () =>
        {
            _heroViewModel.SetManualOperationRunning(true);
            try
            {
                await action();
            }
            finally
            {
                _heroViewModel.SetManualOperationRunning(false);
            }
        });
    }

    private void LoadHeroAttributeSnapshotForActiveAccount(string accountName)
    {
        try
        {
            var serverUrl = GetActiveAccountServerUrl();
            if (_heroAttributeSnapshotStore.TryLoad(accountName, serverUrl, out var snapshot)
                && snapshot is not null)
            {
                ApplyHeroSnapshotToUi(snapshot);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not load cached hero attributes: {ex.Message}");
        }
    }

    private void LoadHeroInventorySnapshotForActiveAccount(string accountName)
    {
        try
        {
            var serverUrl = GetActiveAccountServerUrl();
            _heroViewModel.SeedObservedInventory(accountName, serverUrl, null);
            if (_heroInventorySnapshotStore.TryLoad(accountName, serverUrl, out var resources)
                && resources is not null)
            {
                _heroViewModel.SeedObservedInventory(accountName, serverUrl, resources);
                _heroViewModel.ApplyInventory(resources);
                AppendLog(
                    $"Loaded cached hero inventory. wood={resources.Wood}, clay={resources.Clay}, "
                    + $"iron={resources.Iron}, crop={resources.Crop}.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not load cached hero inventory: {ex.Message}");
        }
    }

    /// <summary>
    /// Persists the current attribute priority order to the active account's settings overlay.
    /// Called from <see cref="Views.HeroPanel"/> after a drag-drop reorder.
    /// </summary>
    internal void PersistHeroPriorityToConfig()
    {
        try
        {
            _heroPanelService.PersistPriority(_heroViewModel);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save hero attribute priority: {ex.Message}");
        }
    }

    internal void PersistHeroSettingsToConfig()
    {
        try
        {
            _heroPanelService.PersistSettings(_heroViewModel);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save hero settings: {ex.Message}");
        }
    }

    private Dictionary<string, string> BuildHeroRuntimePayload()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(BuildHeroRuntimePayload);
        }

        return new HeroPayload(
            MinHpForAdventure: _heroViewModel.MinHpForAdventure,
            AutoRevive: _heroViewModel.AutoRevive,
            AutoAssignPoints: _heroViewModel.AutoAssignPoints,
            AutoUseOintments: _heroViewModel.AutoUseOintments,
            OintmentTargetHpPercent: _heroViewModel.OintmentTargetHpPercent,
            StatPriority: _heroViewModel.BuildPriorityPayload(),
            StatMaximums: _heroViewModel.BuildMaximumsPayload(),
            AdventurePickOrder: _heroViewModel.AdventurePickOrder,
            ContinuousAdventures: _heroViewModel.ContinuousAdventures)
            .ToDictionary();
    }

    /// <summary>
    /// Updates the adventure-count badge. A zero count should not disable the Hero group:
    /// if the user left it enabled, the continuous loop keeps polling and only queues work
    /// when adventures appear.
    /// </summary>
    internal void ApplyHeroAdventureAvailability(int? count)
    {
        if (count is null)
        {
            _heroViewModel.AdventureCountText = "?";
            return;
        }

        _heroViewModel.AdventureCountText = count.Value.ToString();
        if (count.Value > 0)
        {
            ClearHeroBlockedState();
            return;
        }

        if (string.Equals(_heroBlockedReasonKey, HeroBlockedReasonNoAdventures, StringComparison.OrdinalIgnoreCase))
        {
            ClearHeroBlockedState();
        }
    }

    private void ApplyHeroSnapshotToUi(HeroAttributeSnapshot snapshot, string? adventureStatusText = null)
    {
        _heroViewModel.ApplyAttributeSnapshot(snapshot);
        var heroReviving = string.Equals(snapshot.HeroState, "Reviving", StringComparison.OrdinalIgnoreCase);
        var heroDead = string.Equals(snapshot.HeroState, "Dead", StringComparison.OrdinalIgnoreCase);
        // SetHeroState keeps the last-known home village when the name is null (hero away/dead pages may not
        // name a village), so this safely updates away/dead/reviving colouring without a name.
        SetHeroState(
            snapshot.HomeVillageName,
            snapshot.HomeVillageHeroAway,
            heroDead,
            heroReviving,
            snapshot.HomeVillageCoordX,
            snapshot.HomeVillageCoordY);
        if (snapshot.AdventureCount is not null)
        {
            ApplyHeroAdventureAvailability(snapshot.AdventureCount.Value);
        }

        if (!string.IsNullOrWhiteSpace(adventureStatusText))
        {
            _heroViewModel.AdventureStatusText = adventureStatusText;
        }

        ApplyHeroReturnTimer(snapshot);
    }

    private void ApplyHeroReturnTimer(HeroAttributeSnapshot snapshot)
    {
        if (!snapshot.HomeVillageHeroAway || snapshot.SecondsUntilReturn is not > 0)
        {
            return;
        }

        var remainingSeconds = snapshot.SecondsUntilReturn.Value;
        var movement = string.IsNullOrWhiteSpace(snapshot.MovementState) ? "Hero away" : snapshot.MovementState.Trim();
        _heroViewModel.AdventureStatusText = $"{movement}: {FormatCountdown(remainingSeconds)}";
        if (_heroViewModel.HeroLoopTask is not null)
        {
            _heroViewModel.HeroLoopTask.RemainingSeconds = remainingSeconds;
        }

        if (!_isLoggedIn)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var updated = 0;
        foreach (var item in _botService.GetQueueItemsForDisplay()
                     .Where(item => string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase))
                     .Where(item => item.Status == QueueStatus.Pending)
                     .Where(item => item.NextAttemptAt <= now.AddSeconds(5)
                         || item.Payload.TryGetValue(HeroDeferReasonKey, out var reason)
                         && string.Equals(reason, HeroDeferReasonAway, StringComparison.OrdinalIgnoreCase)))
        {
            var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
            {
                [HeroDeferReasonKey] = HeroDeferReasonAway,
            };
            if (_botService.UpdateDeferredQueueItem(item.Id, payload, TimeSpan.FromSeconds(remainingSeconds)))
            {
                updated++;
            }
        }

        if (updated > 0)
        {
            AppendLog($"[hero] Attributes return timer applied after login: {remainingSeconds}s; deferred {updated} hero task(s) without another page visit.");
            RequestQueueUiRefresh();
            UpdateAutomationLoopRunningIndicators();
        }
    }

    /// <summary>
    /// Operation-bracketed refresh of hero attributes. Called by the panel's
    /// Refresh-hero-stats button (the panel toggles its own IsEnabled around
    /// the call).
    /// </summary>
    internal async Task RefreshHeroStatsCoreAsync()
    {
        if (BlockIfSessionSleeping("Refresh hero stats"))
        {
            return;
        }

        var operationId = BeginOperation("Refresh hero stats");
        var operationSw = Stopwatch.StartNew();

        try
        {
            await EnsureChromiumInstalledAsync();
            var snapshot = await RefreshHeroStatsAsync(_loopController.AcquireSessionScopeToken());
            CompleteOperation(operationId, operationSw, $"Hero stats refreshed. Free points: {snapshot.FreePoints}.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            _heroViewModel.AttributesStatusText = $"Hero stats refresh failed: {ex.Message}";
        }
    }

    private async Task<HeroAttributeSnapshot> RefreshHeroStatsAsync(CancellationToken cancellationToken)
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var snapshot = await _heroPanelService.ReadAttributesAsync(options, AppendLog, cancellationToken);
        ApplyHeroSnapshotToUi(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Validates form input and queues one or more
    /// <c>hero_manage</c> task(s). Called by the panel's Hero-adventure
    /// button.
    /// </summary>
    internal void QueueHeroAdventure()
    {
        var minHp = _heroViewModel.MinHpForAdventure;
        if (minHp < 1 || minHp > 100)
        {
            BuildingsInfoTextBlock.Text = "Hero minimum HP must be an integer 1-100.";
            return;
        }

        var hasAvailableAdventures = int.TryParse(_heroViewModel.AdventureCountText.Trim(), out var available);
        var payloads = _heroPanelService.CreateAdventurePayloads(_heroViewModel, hasAvailableAdventures ? available : 0);
        foreach (var payload in payloads)
        {
            EnqueueQuickTask("hero_manage", "Hero adventure (with revive/points checks)", payload);
        }

        var copies = payloads.Count;
        BuildingsInfoTextBlock.Text = _heroViewModel.ContinuousAdventures && copies > 1
            ? $"Queued {copies} hero adventures."
            : "Queued hero adventure.";
    }

    /// <summary>
    /// Operation-bracketed refresh of the available-adventures count.
    /// Called by the panel's Refresh-adventures button (the panel toggles
    /// its own IsEnabled around the call).
    /// </summary>
    internal async Task RefreshAdventuresCoreAsync()
    {
        if (BlockIfSessionSleeping("Refresh adventures"))
        {
            return;
        }

        var operationId = BeginOperation("Refresh adventures");
        var operationSw = Stopwatch.StartNew();
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var count = await _heroPanelService.ReadAdventureCountAsync(
                options,
                AppendLog,
                _loopController.AcquireSessionScopeToken());
            if (count is null)
            {
                ApplyHeroAdventureAvailability(null);
                _heroViewModel.AdventureStatusText = "Adventures not found on current page.";
            }
            else
            {
                ApplyHeroAdventureAvailability(count.Value);
                _heroViewModel.AdventureStatusText = $"Adventures available: {count.Value}.";
            }

            CompleteOperation(operationId, operationSw, $"Refresh adventures: {(count?.ToString() ?? "not found")}.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            _heroViewModel.AdventureStatusText = $"Refresh failed: {ex.Message}";
        }
    }

    private void StartAdventureDebugButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        QueueHeroAdventure();
    }

    /// <summary>Reads hero HP from the current page's top-bar SVG without navigation.</summary>
    internal async Task RefreshHeroHpCoreAsync()
    {
        if (BlockIfSessionSleeping("Refresh hero HP"))
        {
            return;
        }

        var operationId = BeginOperation("Refresh hero HP");
        var operationSw = Stopwatch.StartNew();
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var hpPercent = await ReadHeroHpFromCurrentPageForUiAsync(
                options,
                _loopController.AcquireSessionScopeToken());
            CompleteOperation(
                operationId,
                operationSw,
                hpPercent is null
                    ? "Hero HP was not available on the current page."
                    : $"Hero HP refreshed: {hpPercent}%.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            _heroViewModel.HeroHpText = "?";
        }
    }

    private async Task<int?> ReadHeroHpFromCurrentPageForUiAsync(
        BotOptions options,
        CancellationToken cancellationToken)
    {
        var hpPercent = await _heroPanelService.ReadHpAsync(
            options,
            AppendLog,
            cancellationToken);
        if (hpPercent is not null)
        {
            _heroViewModel.HeroHpText = $"{Math.Clamp(hpPercent.Value, 0, 100)}%";
        }

        // An authoritative current-page HP read releases a low-HP adventure defer as soon as HP reaches
        // the threshold. Doing it here — the single helper used by login, the manual "Refresh hero HP"
        // button and the background tick — means the hero timer stops showing a stale regen-estimate
        // countdown the moment HP has already recovered, instead of only after the next background tick.
        TryReleaseLowHpHeroManageDefer(options, hpPercent);

        return hpPercent;
    }

    /// <summary>
    /// Subscribes to the worker's hero-inventory cache updates so the Hero-tab fields reflect
    /// reads and transfers that happen during automated runs (not just manual refreshes).
    /// Unsubscribed in <see cref="OnClosed"/> to avoid leaking via the static event.
    /// </summary>
    private void SubscribeToHeroInventoryUpdates()
    {
        TravianClient.HeroInventoryUpdated += OnWorkerHeroInventoryUpdated;
        TravianClient.HeroHpUpdated += OnWorkerHeroHpUpdated;
        TravianClient.HeroStatusUpdated += OnWorkerHeroStatusUpdated;
    }

    protected override void OnClosed(EventArgs e)
    {
        _automationDesk.Updated -= AutomationDesk_Updated;
        _botService.FarmLossDestinationChanged -= OnFarmLossDestinationChanged;
        _botService.ActiveVillageVerified -= OnActiveVillageVerified;
        _botService.ConstructionQueueObserved -= OnConstructionQueueObserved;
        _botService.WatchtowerStatusObserved -= OnWatchtowerStatusObserved;
        TravianClient.HeroInventoryUpdated -= OnWorkerHeroInventoryUpdated;
        TravianClient.HeroHpUpdated -= OnWorkerHeroHpUpdated;
        TravianClient.HeroStatusUpdated -= OnWorkerHeroStatusUpdated;
        base.OnClosed(e);
    }

    private void OnWorkerHeroInventoryUpdated(string accountName, HeroInventoryResources resources)
    {
        // Ignore updates for an account other than the one currently shown.
        if (!string.Equals(accountName, _accountStore.ActiveAccountName(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RunOrPostToUi(() =>
        {
            var serverUrl = GetActiveAccountServerUrl();
            var inventoryIncreased = _heroViewModel.ApplyObservedInventory(accountName, serverUrl, resources);
            if (inventoryIncreased)
            {
                ReleaseDeferredConstructionResourceHeadsNow("hero inventory increased");
            }
        });
    }

    private void OnWorkerHeroHpUpdated(string accountName, int hpPercent)
    {
        if (!string.Equals(accountName, _accountStore.ActiveAccountName(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.InvokeAsync(() => _heroViewModel.HeroHpText = $"{Math.Clamp(hpPercent, 0, 100)}%");
    }

    private void OnWorkerHeroStatusUpdated(string accountName, HeroRuntimeStatus status)
    {
        if (!string.Equals(accountName, _accountStore.ActiveAccountName(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RunOrPostToUi(() =>
        {
            SetHeroState(null, status.IsAway, status.IsDead, status.IsReviving);
            _heroViewModel.HeroStatusText = status.DisplayText;
        });
    }

    /// <summary>
    /// Operation-bracketed refresh of the hero inventory resources. Navigates to the hero
    /// inventory page, reads the four resource amounts and updates the bound UI fields.
    /// Called by the panel's Refresh-hero-inventory button (the panel toggles its own
    /// IsEnabled around the call).
    /// </summary>
    internal async Task RefreshHeroInventoryCoreAsync()
    {
        if (BlockIfSessionSleeping("Refresh hero inventory"))
        {
            return;
        }

        var operationId = BeginOperation("Refresh hero inventory");
        var operationSw = Stopwatch.StartNew();
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var resources = await _heroPanelService.ReadInventoryAsync(
                options,
                AppendLog,
                _loopController.AcquireSessionScopeToken());
            _heroViewModel.ApplyInventory(resources);
            CompleteOperation(operationId, operationSw,
                $"Hero inventory refreshed. wood={resources.Wood}, clay={resources.Clay}, iron={resources.Iron}, crop={resources.Crop}.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            _heroViewModel.HeroInventoryStatusText = $"Hero inventory refresh failed: {ex.Message}";
        }
    }
}
