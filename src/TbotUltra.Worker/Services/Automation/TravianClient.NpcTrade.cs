using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int NpcTradeGoldCost = 3;

    /// <summary>
    /// Performs the in-building "NPC Trade" exchange for the unit currently being trained,
    /// so all resources are redistributed optimally for that unit before training. Triggers
    /// only when a user-selected resource is at/above the configured percent of its storage
    /// capacity, and only when gold spending is allowed and stays at/above the gold limit
    /// (each NPC trade costs <see cref="NpcTradeGoldCost"/> gold). Returns true when an
    /// exchange was actually performed (caller should re-read resources afterwards).
    /// </summary>
    private async Task<bool> TryNpcTradeForUnitAsync(
        int troopUnitId,
        string buildingName,
        IReadOnlyDictionary<string, long> resources,
        ResourceCapacitySnapshot capacities,
        int? gold,
        CancellationToken cancellationToken)
    {
        if (!_config.NpcTradeEnabled)
        {
            return false;
        }

        var threshold = Math.Clamp(_config.NpcTradeThresholdPercent, 1, 100);
        var triggered = ResolveNpcTradeTriggerResource(resources, capacities, threshold);
        if (triggered is null)
        {
            Notify($"NPC trade: skip at {buildingName}. No selected resource at/above {threshold}% of capacity.");
            return false;
        }

        if (!_config.AllowGoldSpending)
        {
            Notify($"NPC trade: skip at {buildingName}. Gold spending is disabled.");
            return false;
        }

        if (gold is null)
        {
            Notify($"NPC trade: skip at {buildingName}. Current gold is unknown.");
            return false;
        }

        if (gold.Value - NpcTradeGoldCost < _config.GoldLimit)
        {
            Notify($"NPC trade: skip at {buildingName}. Gold {gold.Value} would fall below limit {_config.GoldLimit} (cost {NpcTradeGoldCost}).");
            return false;
        }

        Notify($"NPC trade: triggering at {buildingName} for unit t{troopUnitId} ({triggered.Value.Key} at {triggered.Value.Percent:0.#}% >= {threshold}%, gold {gold.Value}).");

        return await ExecuteNpcTradeClicksAsync(troopUnitId, buildingName, cancellationToken);
    }

    /// <summary>
    /// Performs the in-building NPC exchange click sequence on the current page for the
    /// given unit: clicks the per-unit "NPC Trade" button, then "distribution Resources
    /// remaining", then "distribution right now" (costs 3 gold). Assumes the building page
    /// is already open and the gates have already been evaluated by the caller.
    /// </summary>
    private async Task<bool> ExecuteNpcTradeClicksAsync(int troopUnitId, string buildingName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exchangeButton = _page.Locator($"#button{troopUnitId}").First;
        if (await exchangeButton.CountAsync() <= 0)
        {
            Notify($"NPC trade: skip. 'NPC Trade' button #button{troopUnitId} not found on the page.");
            return false;
        }

        await exchangeButton.ClickAsync();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening NPC trade.", cancellationToken);

        var distributeRemaining = _page.Locator("#mobile_npc_distribute, #submitText button, button:has-text('distribution Resources remaining')").First;
        if (!await TryClickDialogButtonAsync(distributeRemaining, "distribution Resources remaining", cancellationToken))
        {
            return false;
        }

        await Task.Delay(400, cancellationToken);

        var distributeNow = _page.Locator("#mobile_npc_confirm, #npc_market_button, button:has-text('distribution right now')").First;
        if (!await TryClickDialogButtonAsync(distributeNow, "distribution right now", cancellationToken))
        {
            return false;
        }

        await Task.Delay(500, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after NPC trade.", cancellationToken);
        await EnsureLoggedInAsync();
        Notify($"NPC trade: completed at {buildingName} for unit t{troopUnitId}.");
        return true;
    }

    /// <summary>
    /// Manual test entry point: navigates to the given troop building, resolves its
    /// configured troop's unit id, and forces the NPC exchange click sequence for that
    /// unit — bypassing the threshold/gold gates so the click flow can be verified.
    /// </summary>
    public async Task<string> RunNpcTradeForBuildingTestAsync(TroopTrainingBuildingType buildingType, CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();

        var status = await ReadVillageStatusAsync(cancellationToken);
        var troopType = buildingType switch
        {
            TroopTrainingBuildingType.Barracks => _config.TroopTrainingBarracksTroopType,
            TroopTrainingBuildingType.Stable => _config.TroopTrainingStableTroopType,
            TroopTrainingBuildingType.Workshop => _config.TroopTrainingWorkshopTroopType,
            _ => string.Empty,
        };
        if (string.IsNullOrWhiteSpace(troopType))
        {
            return $"NPC trade test: no troop configured for {buildingType}.";
        }

        var troopUnitId = TroopCatalog.ResolveTravianUnitId(status.Tribe, troopType);
        if (troopUnitId is null)
        {
            return $"NPC trade test: could not resolve unit id for '{troopType}' (tribe {status.Tribe}).";
        }

        var queueStatus = await ReadTroopTrainingQueueStatusAsync(status.Buildings, buildingType, cancellationToken);
        if (queueStatus.SlotId is not > 0)
        {
            return $"NPC trade test: {buildingType} not found in this village.";
        }

        Notify($"NPC trade test: navigating to {queueStatus.BuildingName} slot {queueStatus.SlotId.Value} for '{troopType}' (t{troopUnitId.Value}).");
        await GotoAsync(Paths.BuildBySlot(queueStatus.SlotId.Value), cancellationToken);
        await PauseForManualStepIfVisibleAsync($"Manual verification appeared while opening the {queueStatus.BuildingName}.", cancellationToken);
        await EnsureLoggedInAsync();

        var traded = await ExecuteNpcTradeClicksAsync(troopUnitId.Value, queueStatus.BuildingName, cancellationToken);
        return traded
            ? $"NPC trade test: completed for {troopType} at {queueStatus.BuildingName}."
            : $"NPC trade test: could not complete NPC exchange for {troopType} at {queueStatus.BuildingName}.";
    }

    private async Task<bool> TryClickDialogButtonAsync(Microsoft.Playwright.ILocator button, string label, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await button.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions
            {
                State = Microsoft.Playwright.WaitForSelectorState.Visible,
                Timeout = 5000,
            });
        }
        catch (TimeoutException)
        {
            Notify($"NPC trade: dialog button '{label}' did not appear.");
            return false;
        }
        catch (Microsoft.Playwright.PlaywrightException)
        {
            Notify($"NPC trade: dialog button '{label}' did not appear.");
            return false;
        }

        if (await button.CountAsync() <= 0)
        {
            Notify($"NPC trade: dialog button '{label}' not found.");
            return false;
        }

        await button.ClickAsync();
        Notify($"NPC trade: clicked '{label}'.");
        return true;
    }

    private (string Key, double Percent)? ResolveNpcTradeTriggerResource(
        IReadOnlyDictionary<string, long> resources,
        ResourceCapacitySnapshot capacities,
        int thresholdPercent)
    {
        (string Key, double Percent)? best = null;

        void Consider(string key, bool analyze, long? capacity)
        {
            if (!analyze || capacity is not > 0)
            {
                return;
            }

            if (!resources.TryGetValue(key, out var current) || current <= 0)
            {
                return;
            }

            var percent = Math.Clamp((double)current / capacity.Value * 100.0, 0.0, 100.0);
            if (percent >= thresholdPercent && (best is null || percent > best.Value.Percent))
            {
                best = (key, percent);
            }
        }

        Consider("wood", _config.NpcTradeAnalyzeWood, capacities.WarehouseCapacity);
        Consider("clay", _config.NpcTradeAnalyzeClay, capacities.WarehouseCapacity);
        Consider("iron", _config.NpcTradeAnalyzeIron, capacities.WarehouseCapacity);
        Consider("crop", _config.NpcTradeAnalyzeCrop, capacities.GranaryCapacity);

        return best;
    }
}
