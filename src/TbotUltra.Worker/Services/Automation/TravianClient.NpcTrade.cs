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
        var triggered = ResolveNpcTradeTriggerResource(
            resources,
            capacities,
            threshold,
            _config.NpcTradeAnalyzeWood,
            _config.NpcTradeAnalyzeClay,
            _config.NpcTradeAnalyzeIron,
            _config.NpcTradeAnalyzeCrop);
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

        if (!await CompleteNpcTradeDialogAsync(cancellationToken))
        {
            return false;
        }

        Notify($"NPC trade: completed at {buildingName} for unit t{troopUnitId}.");
        return true;
    }

    private async Task<bool> TryNpcTradeForConstructionAsync(
        string label,
        CancellationToken cancellationToken,
        bool bypassEnabledSetting = false,
        bool bypassGoldGates = false)
    {
        if (!_config.NpcTradeConstructionEnabled && !bypassEnabledSetting)
        {
            return false;
        }

        if (!await CurrentPageLooksBlockedByResourcesAsync(cancellationToken))
        {
            Notify($"NPC trade: skip at {label}. Page does not indicate missing resources.");
            return false;
        }

        var currency = await ReadCurrencyAsync(cancellationToken);
        return await TryNpcTradeForCurrentPageAsync(label, currency.Gold, cancellationToken, bypassGoldGates);
    }

    private async Task<bool> TryNpcTradeForCurrentPageAsync(
        string label,
        int? gold,
        CancellationToken cancellationToken,
        bool bypassGoldGates = false)
    {
        if (!_config.AllowGoldSpending && !bypassGoldGates)
        {
            Notify($"NPC trade: skip at {label}. Gold spending is disabled.");
            return false;
        }

        if (gold is null && !bypassGoldGates)
        {
            Notify($"NPC trade: skip at {label}. Current gold is unknown.");
            return false;
        }

        if (gold is not null && !bypassGoldGates && gold.Value - NpcTradeGoldCost < _config.GoldLimit)
        {
            Notify($"NPC trade: skip at {label}. Gold {gold.Value} would fall below limit {_config.GoldLimit} (cost {NpcTradeGoldCost}).");
            return false;
        }

        var goldText = gold?.ToString() ?? "unknown";
        Notify($"NPC trade: triggering at {label} because the page indicates missing resources (gold {goldText}).");
        return await ExecuteConstructionNpcTradeClicksAsync(label, cancellationToken);
    }

    private async Task<bool> ExecuteConstructionNpcTradeClicksAsync(string label, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidateIndex = await FindConstructionNpcTradeButtonIndexAsync(cancellationToken);
        if (candidateIndex is null)
        {
            Notify($"NPC trade: skip at {label}. NPC trade button not found on the page.");
            return false;
        }

        var exchangeButton = _page.Locator("button, input[type='submit'], input[type='button'], a, div.addHoverClick, div.button-container").Nth(candidateIndex.Value);
        await exchangeButton.ClickAsync();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening NPC trade.", cancellationToken);

        if (!await CompleteNpcTradeDialogAsync(cancellationToken, requirePositiveRemain: true))
        {
            return false;
        }

        Notify($"NPC trade: completed at {label}.");
        return true;
    }

    private async Task<bool> CompleteNpcTradeDialogAsync(CancellationToken cancellationToken, bool requirePositiveRemain = false)
    {
        if (requirePositiveRemain)
        {
            var remain = await ReadNpcTradeRemainAsync(cancellationToken);
            if (remain is not > 0)
            {
                Notify($"NPC trade: skip. Dialog remains value is {(remain?.ToString() ?? "unknown")}, so there are not enough total resources to exchange.");
                await TryCloseNpcTradeDialogAsync(cancellationToken);
                return false;
            }
        }

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
        return true;
    }

    private async Task<int?> FindConstructionNpcTradeButtonIndexAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = await _page.EvaluateAsync<int?>(
            """
            () => {
              const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();
              const candidates = Array.from(document.querySelectorAll(
                'button, input[type="submit"], input[type="button"], a, div.addHoverClick, div.button-container'
              ));
              const matches = [];

              for (let i = 0; i < candidates.length; i += 1) {
                const element = candidates[i];
                const text = clean(element.textContent || element.value || '').toLowerCase();
                const classes = clean(element.className || '').toLowerCase();
                const title = clean(element.getAttribute('title') || '').toLowerCase();
                const href = clean(element.getAttribute('href') || '').toLowerCase();
                const onclick = clean(element.getAttribute('onclick') || '').toLowerCase();
                const combined = `${text} ${classes} ${title} ${href} ${onclick}`;
                const disabled = !!(element.disabled || classes.includes('disabled') || element.getAttribute('aria-disabled') === 'true');
                const inBuildArea = !!element.closest('.upgradeBuilding, .contract, .contractWrapper, .build_details, .buildingWrapper, #contract, form[action*="build.php"]');
                const hasNpcSignal = combined.includes('npc') || combined.includes('exchange') || combined.includes('marketplace.exchangeresources');
                const hasSharingSignal = combined.includes('sharing resources') || combined.includes('share resources');
                const hasGoldSignal = classes.includes('gold') || combined.includes('gold');
                const hasExchangeClass = classes.includes('exchange');
                const looksInstantOnly = combined.includes('instant') && !hasNpcSignal && !hasSharingSignal;
                const validSharingButton = hasSharingSignal && (hasGoldSignal || hasExchangeClass);
                const validNpcButton = inBuildArea && hasNpcSignal && hasGoldSignal;
                if (disabled || (!validSharingButton && !validNpcButton) || looksInstantOnly) {
                  continue;
                }

                matches.push({ index: i, rank: (validSharingButton ? 10 : 0) + (hasSharingSignal ? 6 : 0) + (text.includes('npc') ? 4 : 0) + (onclick.includes('exchangeresources') ? 3 : 0) + (classes.includes('gold') ? 1 : 0) });
              }

              matches.sort((a, b) => b.rank - a.rank);
              return matches.length > 0 ? matches[0].index : null;
            }
            """);

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while finding NPC trade button.", cancellationToken);
        return index;
    }

    private async Task<long?> ReadNpcTradeRemainAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _page.Locator("#dialogContent #build.exchangeResources, #npc").First.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions
            {
                State = Microsoft.Playwright.WaitForSelectorState.Visible,
                Timeout = 5000,
            });
        }
        catch (TimeoutException)
        {
            Notify("NPC trade: dialog did not appear while reading remains value.");
            return null;
        }
        catch (Microsoft.Playwright.PlaywrightException)
        {
            Notify("NPC trade: dialog did not appear while reading remains value.");
            return null;
        }

        var remain = await _page.EvaluateAsync<long?>(
            """
            () => {
              const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();
              const parseNumber = (value) => {
                const text = clean(value);
                if (!text) return null;
                const match = text.match(/([+-]?\d[\d\s.,']*)/);
                if (!match) return null;
                const normalized = match[1].replace(/\s+/g, '').replace(/,/g, '').replace(/'/g, '');
                const parsed = Number(normalized);
                return Number.isFinite(parsed) ? Math.trunc(parsed) : null;
              };

              return parseNumber(document.querySelector('#remain')?.textContent);
            }
            """);

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading NPC trade remains value.", cancellationToken);
        return remain;
    }

    private async Task TryCloseNpcTradeDialogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var closeButton = _page.Locator("#dialogCancelButton, .mobile-npc-close, button[aria-label='Close']").First;
        if (await closeButton.CountAsync() <= 0)
        {
            return;
        }

        try
        {
            await closeButton.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Timeout = 1000 });
        }
        catch (Microsoft.Playwright.PlaywrightException ex)
        {
            Notify($"NPC trade: could not close dialog after skip: {ex.Message}");
        }
    }

    private async Task<bool> CurrentPageLooksBlockedByResourcesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blocked = await _page.EvaluateAsync<bool>(
            """
            () => {
              const text = (document.body?.innerText || document.body?.textContent || '').replace(/\s+/g, ' ').toLowerCase();
              return /resources\s*will\s*be\s*available|not\s*enough|insufficient|missing\s*resources|requires\s*more/.test(text);
            }
            """);

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while checking resource block state.", cancellationToken);
        return blocked;
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

    public async Task<string> RunNpcTradeForCurrentBuildingPageTestAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before current-page NPC trade test.", cancellationToken);

        var label = "current building page";
        var traded = await TryNpcTradeForConstructionAsync(label, cancellationToken, bypassEnabledSetting: true, bypassGoldGates: true);
        return traded
            ? "NPC trade building test: completed on current page."
            : "NPC trade building test: could not complete NPC exchange on current page.";
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
        int thresholdPercent,
        bool analyzeWood,
        bool analyzeClay,
        bool analyzeIron,
        bool analyzeCrop)
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

        Consider("wood", analyzeWood, capacities.WarehouseCapacity);
        Consider("clay", analyzeClay, capacities.WarehouseCapacity);
        Consider("iron", analyzeIron, capacities.WarehouseCapacity);
        Consider("crop", analyzeCrop, capacities.GranaryCapacity);

        return best;
    }
}
