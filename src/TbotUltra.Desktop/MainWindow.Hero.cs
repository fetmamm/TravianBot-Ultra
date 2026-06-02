using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

/// <summary>
/// Hero / Adventures host-side logic. After 3b5 the Hero TabItem is rendered
/// by <see cref="Views.HeroPanel"/>; this partial holds the methods that
/// panel calls back into for service-bound work (refresh stats, refresh
/// adventures, queue adventure, hide-mode push) plus the
/// host-only helpers (priority persist, blocked-state interplay) that need
/// access to MainWindow's private state.
///
/// Drag-and-drop scratch state and the drag handlers themselves live on
/// <see cref="Views.HeroPanel"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Persists the current attribute priority order to <c>bot.json</c>.
    /// Called from <see cref="Views.HeroPanel"/> after a drag-drop reorder.
    /// </summary>
    internal void PersistHeroPriorityToConfig()
    {
        try
        {
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.HeroStatPriority] = _heroViewModel.BuildPriorityPayload();
            _botConfigStore.Save(config);
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
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.HeroMinHpForAdventure] = _heroViewModel.MinHpForAdventure;
            config[BotOptionPayloadKeys.HeroHpRegenPerDayPercent] = _heroViewModel.HeroHpRegenPerDayPercent;
            config[BotOptionPayloadKeys.HeroAutoRevive] = _heroViewModel.AutoRevive;
            config[BotOptionPayloadKeys.HeroAutoAssignPoints] = _heroViewModel.AutoAssignPoints;
            config[BotOptionPayloadKeys.HeroAutoUseOintments] = _heroViewModel.AutoUseOintments;
            config[BotOptionPayloadKeys.HeroStatPriority] = _heroViewModel.BuildPriorityPayload();
            config[BotOptionPayloadKeys.HeroAdventurePickOrder] = _heroViewModel.AdventurePickOrder;
            config[BotOptionPayloadKeys.HeroHideModeEnabled] = _heroViewModel.HideModeControlEnabled;
            config[BotOptionPayloadKeys.HeroHideMode] = _heroViewModel.HideMode;
            config[BotOptionPayloadKeys.HeroContinuousAdventures] = _heroViewModel.ContinuousAdventures;
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save hero settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the adventure-count badge and the Hero blocked-state flag
    /// based on the latest adventure count. Stays on MainWindow because
    /// SetHeroBlockedState / ClearHeroBlockedState share state with the
    /// other group-blocked indicators.
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

        if (!string.Equals(_heroBlockedReasonKey, HeroBlockedReasonNoAdventures, StringComparison.OrdinalIgnoreCase))
        {
            SetHeroBlockedState(HeroBlockedReasonNoAdventures, "No adventures");
        }
    }

    private void ApplyHeroSnapshotToUi(HeroAttributeSnapshot snapshot, string? adventureStatusText = null)
    {
        _heroViewModel.ApplyAttributeSnapshot(snapshot);
        ApplyHeroHideModeSnapshotToUi(snapshot.HideMode);
        if (snapshot.AdventureCount is not null)
        {
            ApplyHeroAdventureAvailability(snapshot.AdventureCount.Value);
        }

        if (!string.IsNullOrWhiteSpace(adventureStatusText))
        {
            _heroViewModel.AdventureStatusText = adventureStatusText;
        }
    }

    private void ApplyHeroHideModeSnapshotToUi(string? hideMode)
    {
        if (string.IsNullOrWhiteSpace(hideMode))
        {
            return;
        }

        var normalized = string.Equals(hideMode, "fight", StringComparison.OrdinalIgnoreCase) ? "fight" : "hide";
        if (string.Equals(_heroViewModel.HideMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _suppressHeroHideModeApply = true;
        try
        {
            if (string.Equals(normalized, "fight", StringComparison.OrdinalIgnoreCase))
            {
                _heroViewModel.IsHideModeFight = true;
            }
            else
            {
                _heroViewModel.IsHideModeHide = true;
            }

            PersistHeroSettingsToConfig();
            AppendLog($"Hero hide mode synced from Travian: {normalized}.");
        }
        finally
        {
            _suppressHeroHideModeApply = false;
        }
    }

    /// <summary>
    /// Operation-bracketed refresh of hero attributes. Called by the panel's
    /// Refresh-hero-stats button (the panel toggles its own IsEnabled around
    /// the call).
    /// </summary>
    internal async Task RefreshHeroStatsCoreAsync()
    {
        var operationId = BeginOperation("Refresh hero stats");
        var operationSw = Stopwatch.StartNew();

        try
        {
            await EnsureChromiumInstalledAsync();
            var snapshot = await RefreshHeroStatsAsync(CancellationToken.None);
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
        var snapshot = await _botService.ReadHeroAttributesAsync(options, AppendLog, cancellationToken);
        ApplyHeroSnapshotToUi(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Pushes the current hide-mode (read from <see cref="_heroViewModel"/>)
    /// to the worker as a <c>hero_set_hide_mode</c> task. The
    /// <see cref="_suppressHeroHideModeApply"/> guard prevents the call
    /// while LoadConfigToUi is replaying persisted state into the bound
    /// radio buttons.
    /// </summary>
    internal void OnHeroHideModeChanged()
    {
        if (_suppressHeroHideModeApply || !IsLoaded)
        {
            return;
        }

        PersistHeroSettingsToConfig();
        if (!_heroViewModel.HideModeControlEnabled)
        {
            return;
        }

        var mode = _heroViewModel.HideMode;
        var payload = new HeroPayload(HideModeEnabled: true, HideMode: mode).ToDictionary();
        EnqueueQuickTask("hero_set_hide_mode", $"Set hero hide mode to '{mode}'", payload);
    }

    /// <summary>
    /// Validates form input and queues one (or up to 20 with continuous mode)
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

        var payload = new HeroPayload(
            MinHpForAdventure: minHp,
            AutoRevive: _heroViewModel.AutoRevive,
            AutoAssignPoints: _heroViewModel.AutoAssignPoints,
            AutoUseOintments: _heroViewModel.AutoUseOintments,
            StatPriority: _heroViewModel.BuildPriorityPayload(),
            AdventurePickOrder: _heroViewModel.AdventurePickOrder,
            HideModeEnabled: _heroViewModel.HideModeControlEnabled,
            HideMode: _heroViewModel.HideMode).ToDictionary();

        var continuous = _heroViewModel.ContinuousAdventures;
        var copies = 1;
        if (continuous && int.TryParse(_heroViewModel.AdventureCountText.Trim(), out var available) && available > 1)
        {
            copies = Math.Min(available, 20); // hard cap to avoid runaway queues if count is wrong
        }

        for (var i = 0; i < copies; i++)
        {
            EnqueueQuickTask("hero_manage", "Hero adventure (with revive/points checks)", payload);
        }
        BuildingsInfoTextBlock.Text = continuous && copies > 1
            ? $"Queued {copies} hero adventures."
            : "Queued hero adventure.";
    }

    /// <summary>
    /// Refreshes the adventure count after a successful login. Called from
    /// the post-login flow (not from the panel).
    /// </summary>
    private async Task RefreshAdventureCountAfterLoginAsync(BotOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var count = await _botService.RefreshAdventureCountAsync(options, AppendLog, cancellationToken);
            if (count is null)
            {
                ApplyHeroAdventureAvailability(null);
                AppendLog("Adventure count: not found on current page.");
            }
            else
            {
                ApplyHeroAdventureAvailability(count.Value);
                AppendLog($"Adventure count after login: {count.Value}.");
            }
        }
        catch (OperationCanceledException)
        {
            // Login flow was cancelled — leave the count unchanged.
        }
        catch (Exception ex)
        {
            AppendLog($"Adventure count refresh after login failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Operation-bracketed refresh of the available-adventures count.
    /// Called by the panel's Refresh-adventures button (the panel toggles
    /// its own IsEnabled around the call).
    /// </summary>
    internal async Task RefreshAdventuresCoreAsync()
    {
        var operationId = BeginOperation("Refresh adventures");
        var operationSw = Stopwatch.StartNew();
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var count = await _botService.RefreshAdventureCountAsync(options, AppendLog, CancellationToken.None);
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

    /// <summary>
    /// Subscribes to the worker's hero-inventory cache updates so the Hero-tab fields reflect
    /// reads and transfers that happen during automated runs (not just manual refreshes).
    /// Unsubscribed in <see cref="OnClosed"/> to avoid leaking via the static event.
    /// </summary>
    private void SubscribeToHeroInventoryUpdates()
    {
        TravianClient.HeroInventoryUpdated += OnWorkerHeroInventoryUpdated;
    }

    protected override void OnClosed(EventArgs e)
    {
        TravianClient.HeroInventoryUpdated -= OnWorkerHeroInventoryUpdated;
        base.OnClosed(e);
    }

    private void OnWorkerHeroInventoryUpdated(string accountName, HeroInventoryResources resources)
    {
        // Ignore updates for an account other than the one currently shown.
        if (!string.Equals(accountName, _accountStore.ActiveAccountName(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.InvokeAsync(() => _heroViewModel.ApplyInventory(resources));
    }

    /// <summary>
    /// Operation-bracketed refresh of the hero inventory resources. Navigates to the hero
    /// inventory page, reads the four resource amounts and updates the bound UI fields.
    /// Called by the panel's Refresh-hero-inventory button (the panel toggles its own
    /// IsEnabled around the call).
    /// </summary>
    internal async Task RefreshHeroInventoryCoreAsync()
    {
        var operationId = BeginOperation("Refresh hero inventory");
        var operationSw = Stopwatch.StartNew();
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var resources = await _botService.RefreshHeroInventoryAsync(options, AppendLog, CancellationToken.None);
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
