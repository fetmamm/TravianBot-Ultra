using TbotUltra.Core.Accounts;
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
    /// capacity, and only when gold spending is allowed and remains within the daily gold budget
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
            Notify($"NPC trade: skip at {buildingName}. Gold {gold.Value} would fall below minimum balance {_config.GoldLimit} (cost {NpcTradeGoldCost}).");
            return false;
        }

        if (!await TryReserveNpcTradeGoldAsync(buildingName, cancellationToken))
        {
            return false;
        }

        Notify($"NPC trade: triggering at {buildingName} for unit t{troopUnitId} ({triggered.Value.Key} at {triggered.Value.Percent:0.#}% >= {threshold}%, gold {gold.Value}).");

        return await ExecuteNpcTradeClicksAsync(troopUnitId, buildingName, cancellationToken);
    }

    /// <summary>
    /// Performs the in-building NPC exchange click sequence on the current page for the
    /// given unit: clicks the per-unit "NPC Trade" button, then "Distribute remaining
    /// resources.", then "Redeem" (costs 3 gold). Assumes the building page
    /// is already open and the gates have already been evaluated by the caller.
    /// </summary>
    private async Task<bool> ExecuteNpcTradeClicksAsync(int troopUnitId, string buildingName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exchangeButton = _page.Locator($"#button{troopUnitId}").First;
        if (await exchangeButton.CountAsync() <= 0)
        {
            // Official Travian (T4.6) has no per-unit NPC button; the training building page exposes
            // a general "Exchange resources" button (class "exchange") that opens the same NPC dialog.
            exchangeButton = _page.Locator("button.exchange[value='Exchange resources' i], div.npcMerchant button[value='Exchange resources' i], button[value='Exchange resources' i]").First;
            if (await exchangeButton.CountAsync() <= 0)
            {
                Notify($"NPC trade: skip. 'NPC Trade' button #button{troopUnitId} not found on the page.");
                return false;
            }
        }

        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        await exchangeButton.ClickAsync();

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

        if (_config.NpcTradeBuildTimeLimitEnabled && !bypassEnabledSetting)
        {
            var limitSeconds = NormalizeNpcTradeBuildTimeLimitSeconds(_config.NpcTradeBuildTimeLimitSeconds);
            var resourceWaitSeconds = await ReadConstructionResourceWaitSecondsAsync(cancellationToken);
            if (resourceWaitSeconds is int waitSeconds && waitSeconds > 0 && waitSeconds <= limitSeconds)
            {
                Notify($"NPC trade: skip at {label}. Resources will be available in {waitSeconds}s, within build time limit {limitSeconds}s.");
                return false;
            }
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
            Notify($"NPC trade: skip at {label}. Gold {gold.Value} would fall below minimum balance {_config.GoldLimit} (cost {NpcTradeGoldCost}).");
            return false;
        }

        if (!bypassGoldGates && !await TryReserveNpcTradeGoldAsync(label, cancellationToken))
        {
            return false;
        }

        var goldText = gold?.ToString() ?? "unknown";
        Notify($"NPC trade: triggering at {label} because the page indicates missing resources (gold {goldText}).");
        return await ExecuteConstructionNpcTradeClicksAsync(label, cancellationToken);
    }

    private async Task<bool> TryReserveNpcTradeGoldAsync(string label, CancellationToken cancellationToken)
    {
        try
        {
            var serverUtcOffset = _session.CachedServerUtcOffset;
            if (serverUtcOffset is null)
            {
                serverUtcOffset = await ReadProductionBonusServerUtcOffsetAsync(cancellationToken);
                if (serverUtcOffset is not null)
                {
                    _session.CachedServerUtcOffset = serverUtcOffset;
                }
            }

            var serverDate = DateOnly.FromDateTime(
                DateTimeOffset.UtcNow.ToOffset(serverUtcOffset ?? TimeSpan.Zero).Date);
            var path = AccountStoragePaths.DailySpendingStatePath(_projectRoot, _account.Name, _config.BaseUrl);
            var store = new DailySpendingStore(path);
            if (!store.TryReserveGold(serverDate, _config.DailyGoldSpendingLimit, NpcTradeGoldCost, out var spentAfterReservation))
            {
                Notify($"NPC trade: skip at {label}. Daily gold limit {_config.DailyGoldSpendingLimit} reached (spent {spentAfterReservation}, cost {NpcTradeGoldCost}); resets at 00:00 server time.");
                return false;
            }

            Notify($"NPC trade: reserved {NpcTradeGoldCost} gold for {label}; daily spent {spentAfterReservation}/{_config.DailyGoldSpendingLimit}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Notify($"NPC trade: skip at {label}. Could not update daily gold spending state: {ex.Message}");
            return false;
        }
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
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        await exchangeButton.ClickAsync();

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

        var distributeRemaining = _page.Locator("button[onclick*='exchangeResources.distribute'], button[value='Distribute remaining resources.' i]").First;
        if (!await TryClickDialogButtonAsync(distributeRemaining, "Distribute remaining resources", cancellationToken))
        {
            return false;
        }

        await Task.Delay(400, cancellationToken);

        // Official "Redeem" submit button is enabled only after the distribute step above.
        var distributeNow = _page.Locator("#npc_market_button").First;
        if (!await TryClickDialogButtonAsync(distributeNow, "Redeem", cancellationToken))
        {
            return false;
        }

        await Task.Delay(500, cancellationToken);
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
                // Official Travian (T4.6): the "Exchange resources" button (class "exchange") opens
                // the NPC merchant dialog and can appear outside the build area.
                const validOfficialExchange = hasExchangeClass && /exchange\s+resources/.test(combined);
                if (disabled || (!validSharingButton && !validNpcButton && !validOfficialExchange) || looksInstantOnly) {
                  continue;
                }

                matches.push({ index: i, rank: (validSharingButton ? 10 : 0) + (validOfficialExchange ? 8 : 0) + (hasSharingSignal ? 6 : 0) + (text.includes('npc') ? 4 : 0) + (onclick.includes('exchangeresources') ? 3 : 0) + (classes.includes('gold') ? 1 : 0) });
              }

              matches.sort((a, b) => b.rank - a.rank);
              return matches.length > 0 ? matches[0].index : null;
            }
            """);

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
              return !!document.querySelector('.upgradeBlocked .inlineIcon.resource.transfer, .inlineIcon.resource.transfer.fillUp')
                || /resources\s*will\s*be\s*available|enough\s*resources\s*on|not\s*enough|insufficient|missing\s*resources|requires\s*more|extend\s+(?:warehouse|granary|silo)|(?:warehouse|granary|silo)\s+first/.test(text);
            }
            """);

        return blocked;
    }

    private async Task<string?> ReadStorageCapacityBlockKindOnCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kind = await _page.EvaluateAsync<string?>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const nodes = Array.from(document.querySelectorAll(
                '.upgradeBlocked .errorMessage, .upgradeBlocked, .errorMessage, .error, .none'
              ));

              for (const node of nodes) {
                const text = clean(node.textContent || '');
                if (!text) {
                  continue;
                }

                const mentionsWarehouse = /\bwarehouse\b/.test(text);
                const mentionsGranary = /\bgranary\b|\bsilo\b/.test(text);
                if (!mentionsWarehouse && !mentionsGranary) {
                  continue;
                }

                const isStorageBlock =
                  /extend\s+(?:the\s+)?(?:warehouse|granary|silo)/.test(text)
                  || /(?:warehouse|granary|silo)(?:\s+and\s+(?:warehouse|granary|silo))?\s+first/.test(text)
                  || /(capacity|storage|level).*(warehouse|granary|silo)|(warehouse|granary|silo).*(capacity|storage|level)/.test(text);
                if (!isStorageBlock) {
                  continue;
                }

                return mentionsWarehouse ? 'warehouse' : 'granary';
              }

              return null;
            }
            """);

        return string.IsNullOrWhiteSpace(kind) ? null : kind;
    }

    private async Task<int?> ReadConstructionResourceWaitSecondsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var seconds = await _page.EvaluateAsync<int?>(
            """
            () => {
              const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();

              const parseDurationSeconds = (raw) => {
                const text = clean(raw);
                if (!text) return null;

                const full = text.match(/(\d{1,3})\s*:\s*(\d{1,2})\s*:\s*(\d{1,2})/);
                if (full) return Number(full[1]) * 3600 + Number(full[2]) * 60 + Number(full[3]);

                const short = text.match(/(^|[^\d])(\d{1,3})\s*:\s*(\d{1,2})([^\d]|$)/);
                if (short) return Number(short[2]) * 60 + Number(short[3]);

                const sec = text.match(/(\d{1,6})\s*s(?:ec|econd)?s?\b/i);
                if (sec) return Number(sec[1]);

                const min = text.match(/(\d{1,4})\s*m(?:in|inute)?s?\b/i);
                if (min) return Number(min[1]) * 60;

                const hour = text.match(/(\d{1,3})\s*h(?:our)?s?\b/i);
                if (hour) return Number(hour[1]) * 3600;

                return null;
              };

              const readServerNow = () => {
                const candidates = ['#servertime .timeStandard', '#servertime', '.serverTime'];
                for (const selector of candidates) {
                  const element = document.querySelector(selector);
                  const match = clean(element?.textContent || '').match(/(\d{1,2}):(\d{2}):(\d{2})/);
                  if (match) {
                    const now = new Date();
                    now.setHours(Number(match[1]), Number(match[2]), Number(match[3]), 0);
                    return now;
                  }
                }

                return new Date();
              };

              const parseClockTimeToSeconds = (raw) => {
                const text = clean(raw);
                if (!text) return null;
                const tomorrow = /(tomorrow|morgen|imorgon|i\s*morgon|demain|domani|manana|jutro)/i.test(text);
                const today = /(today|heute|idag|i\s*dag|aujourd|oggi|hoy|dzisiaj)/i.test(text);
                if (!today && !tomorrow) return null;

                const match = text.match(/(\d{1,2}):(\d{2}):(\d{2})/);
                if (!match) return null;

                const now = readServerNow();
                const target = new Date(now.getTime());
                target.setHours(Number(match[1]), Number(match[2]), Number(match[3]), 0);
                if (tomorrow) target.setDate(target.getDate() + 1);

                let diff = Math.round((target.getTime() - now.getTime()) / 1000);
                if (today && diff < 0) diff += 86400;
                return diff > 0 ? diff : null;
              };

              const sources = [];
              for (const node of document.querySelectorAll('span.none, div.none, .none, .contract, .contractWrapper, .build_details, .errorMessage, .error')) {
                const text = clean(node.textContent || '');
                if (/resources\s*will\s*be\s*available|not\s*enough|insufficient|missing\s*resources/i.test(text)) {
                  sources.push(text);
                }
              }

              const bodyText = clean(document.body?.innerText || document.body?.textContent || '');
              if (sources.length === 0 && /resources\s*will\s*be\s*available/i.test(bodyText)) {
                sources.push(bodyText);
              }

              for (const source of sources) {
                const clockSeconds = parseClockTimeToSeconds(source);
                if (clockSeconds && clockSeconds > 0) return Math.trunc(clockSeconds);

                const durationSeconds = parseDurationSeconds(source);
                if (durationSeconds && durationSeconds > 0) return Math.trunc(durationSeconds);
              }

              return null;
            }
            """);

        return seconds;
    }

    private static int NormalizeNpcTradeBuildTimeLimitSeconds(int seconds)
    {
        return seconds switch
        {
            30 or 60 or 300 or 1200 or 3600 => seconds,
            _ => 60,
        };
    }

    /// <summary>
    /// Manual test entry point: navigates to the given troop building, resolves its
    /// configured troop's unit id, and forces the NPC exchange click sequence for that
    /// unit — bypassing the threshold/gold gates so the click flow can be verified.
    /// </summary>
    public async Task<string> RunNpcTradeForBuildingTestAsync(TroopTrainingBuildingType buildingType, CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

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
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var traded = await ExecuteNpcTradeClicksAsync(troopUnitId.Value, queueStatus.BuildingName, cancellationToken);
        return traded
            ? $"NPC trade test: completed for {troopType} at {queueStatus.BuildingName}."
            : $"NPC trade test: could not complete NPC exchange for {troopType} at {queueStatus.BuildingName}.";
    }

    public async Task<string> RunNpcTradeForCurrentBuildingPageTestAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

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

        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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
