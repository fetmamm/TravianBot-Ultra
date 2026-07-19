using Microsoft.Playwright;
using System.Text.Json.Serialization;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int MarketplaceGid = 17;
    private const int ResourceTransferFallbackCooldownSeconds = 300;
    private static readonly string[] ResourceTransferKeys = ["wood", "clay", "iron", "crop"];

    public async Task<string> SendResourcesBetweenOwnVillagesAsync(CancellationToken cancellationToken = default)
    {
        Notify($"[transfer] starting — target='{_config.ResourceTransferTargetVillageName ?? "(unset)"}', sources={(_config.ResourceTransferSourceVillageNames?.Count ?? 0)}");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(_config.ResourceTransferTargetVillageName))
        {
            return "Resource transfer requires a target village.";
        }

        var sourceNames = (_config.ResourceTransferSourceVillageNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceNames.Count == 0)
        {
            return "Resource transfer requires at least one source village.";
        }

        var enabledResources = ResolveResourceTransferEnabledKeys(_config);
        if (enabledResources.Count == 0)
        {
            return "Resource transfer has no enabled resources.";
        }

        var villages = await ReadVillagesAsync(cancellationToken);
        var targetVillage = villages.FirstOrDefault(v =>
            string.Equals(v.Name, _config.ResourceTransferTargetVillageName, StringComparison.OrdinalIgnoreCase));
        if (targetVillage is null)
        {
            throw new InvalidOperationException($"Resource transfer target village '{_config.ResourceTransferTargetVillageName}' was not found.");
        }

        Notify($"Resource transfer: scanning target '{targetVillage.Name}'.");
        var targetStatus = await ReadResourceTransferVillageStatusAsync(targetVillage, cancellationToken);
        var targetStock = TryBuildResourceTransferStock(targetStatus);
        if (targetStock is null)
        {
            throw new InvalidOperationException($"Resource transfer target '{targetVillage.Name}' has incomplete storage data.");
        }

        var sentCount = 0;
        var skippedCount = 0;
        int? shortestMerchantWaitSeconds = null;
        foreach (var sourceName in sourceNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(sourceName, targetVillage.Name, StringComparison.OrdinalIgnoreCase))
            {
                skippedCount++;
                Notify($"Resource transfer: skip '{sourceName}' because it is the target village.");
                continue;
            }

            var sourceVillage = villages.FirstOrDefault(v =>
                string.Equals(v.Name, sourceName, StringComparison.OrdinalIgnoreCase));
            if (sourceVillage is null)
            {
                skippedCount++;
                Notify($"Resource transfer: source village '{sourceName}' was not found.");
                continue;
            }

            Notify($"Resource transfer: scanning source '{sourceVillage.Name}'.");
            var sourceStatus = await ReadResourceTransferVillageStatusAsync(sourceVillage, cancellationToken);
            var sourceStock = TryBuildResourceTransferStock(sourceStatus);
            if (sourceStock is null)
            {
                skippedCount++;
                Notify($"Resource transfer: skip '{sourceVillage.Name}' because storage data is incomplete.");
                continue;
            }

            var marketplace = FindMarketplaceBuilding(sourceStatus.Buildings);
            if (marketplace?.SlotId is not > 0)
            {
                skippedCount++;
                Notify($"Resource transfer: skip '{sourceVillage.Name}' because Marketplace was not found.");
                continue;
            }

            await GotoAsync(Paths.BuildBySlotTab(marketplace.SlotId.Value, 5), cancellationToken);
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);

            var merchantState = await ReadMarketplaceMerchantStateAsync(cancellationToken);
            if (merchantState.Available <= 0 || merchantState.TotalCapacity <= 0)
            {
                skippedCount++;
                shortestMerchantWaitSeconds = MinPositive(shortestMerchantWaitSeconds, merchantState.NextReturnSeconds);
                Notify($"Resource transfer: skip '{sourceVillage.Name}' because no merchant capacity is available.");
                continue;
            }

            var shipment = CalculateResourceTransferShipment(
                sourceStock.Resources,
                sourceStock.WarehouseCapacity,
                sourceStock.GranaryCapacity,
                targetStock.Resources,
                targetStock.WarehouseCapacity,
                targetStock.GranaryCapacity,
                enabledResources,
                _config.ResourceTransferSourceThresholdPercent,
                _config.ResourceTransferSourceKeepPercent,
                _config.ResourceTransferTargetFillPercent,
                merchantState.TotalCapacity);
            if (shipment.Total <= 0)
            {
                skippedCount++;
                Notify($"Resource transfer: skip '{sourceVillage.Name}' because no selected resource has transferable surplus.");
                continue;
            }

            var sent = await TrySendResourceTransferShipmentAsync(targetVillage, shipment, cancellationToken);
            if (!sent)
            {
                skippedCount++;
                Notify($"Resource transfer: could not send from '{sourceVillage.Name}'.");
                continue;
            }

            sentCount++;
            targetStock = targetStock.AddIncoming(shipment);
            Notify($"Resource transfer: sent from '{sourceVillage.Name}' to '{targetVillage.Name}' wood={shipment.Wood} clay={shipment.Clay} iron={shipment.Iron} crop={shipment.Crop}.");
            await ApplyActionDelayAsync(cancellationToken);
        }

        if (sentCount > 0)
        {
            return $"Resource transfer completed. Sent from {sentCount} source village(s), skipped {skippedCount}.";
        }

        var retrySeconds = shortestMerchantWaitSeconds is > 0
            ? shortestMerchantWaitSeconds.Value
            : ResourceTransferFallbackCooldownSeconds;
        throw new InvalidOperationException($"Resource transfer had no shipment to send. queue_wait_seconds={Math.Max(1, retrySeconds)}");
    }

    internal static ResourceTransferShipment CalculateResourceTransferShipment(
        IReadOnlyDictionary<string, long> sourceResources,
        long sourceWarehouseCapacity,
        long sourceGranaryCapacity,
        IReadOnlyDictionary<string, long> targetResources,
        long targetWarehouseCapacity,
        long targetGranaryCapacity,
        IReadOnlyCollection<string> enabledResources,
        int sourceThresholdPercent,
        int sourceKeepPercent,
        int targetFillPercent,
        long merchantCapacity)
    {
        if (merchantCapacity <= 0)
        {
            return ResourceTransferShipment.Empty;
        }

        var enabled = enabledResources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var threshold = Math.Clamp(sourceThresholdPercent, 0, 100);
        var keep = Math.Clamp(sourceKeepPercent, 0, 99);
        var fill = Math.Clamp(targetFillPercent, 0, 100);
        var amounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in ResourceTransferKeys)
        {
            if (!enabled.Contains(key))
            {
                amounts[key] = 0;
                continue;
            }

            var sourceCapacity = string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                ? sourceGranaryCapacity
                : sourceWarehouseCapacity;
            var targetCapacity = string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                ? targetGranaryCapacity
                : targetWarehouseCapacity;
            if (sourceCapacity <= 0 || targetCapacity <= 0)
            {
                amounts[key] = 0;
                continue;
            }

            var sourceCurrent = sourceResources.TryGetValue(key, out var rawSource) ? Math.Max(0, rawSource) : 0;
            var targetCurrent = targetResources.TryGetValue(key, out var rawTarget) ? Math.Max(0, rawTarget) : 0;
            var sourcePercent = sourceCurrent * 100.0 / sourceCapacity;
            if (sourcePercent < threshold)
            {
                amounts[key] = 0;
                continue;
            }

            var keepAmount = (long)Math.Floor(sourceCapacity * keep / 100.0);
            var targetMax = (long)Math.Floor(targetCapacity * fill / 100.0);
            var surplus = Math.Max(0, sourceCurrent - keepAmount);
            var targetFree = Math.Max(0, targetMax - targetCurrent);
            amounts[key] = Math.Min(surplus, targetFree);
        }

        var shipment = new ResourceTransferShipment(
            amounts.GetValueOrDefault("wood"),
            amounts.GetValueOrDefault("clay"),
            amounts.GetValueOrDefault("iron"),
            amounts.GetValueOrDefault("crop"));
        if (shipment.Total <= merchantCapacity)
        {
            return shipment;
        }

        var ratio = merchantCapacity / (double)shipment.Total;
        return new ResourceTransferShipment(
            (long)Math.Floor(shipment.Wood * ratio),
            (long)Math.Floor(shipment.Clay * ratio),
            (long)Math.Floor(shipment.Iron * ratio),
            (long)Math.Floor(shipment.Crop * ratio));
    }

    private static IReadOnlyCollection<string> ResolveResourceTransferEnabledKeys(TbotUltra.Core.Configuration.BotOptions options)
    {
        var keys = new List<string>();
        if (options.ResourceTransferSendWood)
        {
            keys.Add("wood");
        }
        if (options.ResourceTransferSendClay)
        {
            keys.Add("clay");
        }
        if (options.ResourceTransferSendIron)
        {
            keys.Add("iron");
        }
        if (options.ResourceTransferSendCrop)
        {
            keys.Add("crop");
        }
        return keys;
    }

    private async Task<VillageStatus> ReadResourceTransferVillageStatusAsync(Village village, CancellationToken cancellationToken)
    {
        await SwitchToVillageAsync(village.Name, village.Url, cancellationToken, skipFeatureRefresh: true);
        return await ReadVillageStatusAsync(cancellationToken);
    }

    private static ResourceTransferStock? TryBuildResourceTransferStock(VillageStatus status)
    {
        if (status.WarehouseCapacity is not > 0 || status.GranaryCapacity is not > 0)
        {
            return null;
        }

        var resources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ResourceTransferKeys)
        {
            if (!status.Resources.TryGetValue(key, out var raw) || TravianParsing.TryParseResourceValue(raw) is not { } parsed)
            {
                return null;
            }
            resources[key] = parsed;
        }

        return new ResourceTransferStock(status.ActiveVillage, resources, status.WarehouseCapacity.Value, status.GranaryCapacity.Value);
    }

    private static Building? FindMarketplaceBuilding(IReadOnlyList<Building> buildings)
    {
        return buildings.FirstOrDefault(building => building.Gid == MarketplaceGid)
            ?? buildings.FirstOrDefault(building =>
                building.SlotId is > 0
                && (building.Name.Contains("Marketplace", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(building.Name, "Market", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<ResourceTransferMerchantState> ReadMarketplaceMerchantStateAsync(CancellationToken cancellationToken)
    {
        var state = await _page.EvaluateAsync<ResourceTransferMerchantStateJs>(
            """
            () => {
              const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();
              const parseNumber = (value) => {
                const match = clean(value).match(/(\d[\d\s.,']*)/);
                if (!match) return null;
                const digits = match[1].replace(/[^\d]/g, '');
                if (!digits) return null;
                const parsed = Number.parseInt(digits, 10);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const parseDuration = (value) => {
                const text = clean(value).toLowerCase();
                const hms = text.match(/(\d{1,3})\s*:\s*(\d{1,2})(?:\s*:\s*(\d{1,2}))?/);
                if (hms) {
                  const a = Number(hms[1]);
                  const b = Number(hms[2]);
                  const c = hms[3] ? Number(hms[3]) : null;
                  return c === null ? a * 60 + b : a * 3600 + b * 60 + c;
                }
                return null;
              };

              const bodyText = clean(document.body?.innerText || document.body?.textContent || '');
              const availableNode = document.querySelector('#merchantsAvailable, .merchantsAvailable, [id*="merchant"][id*="Available" i], [class*="merchant"][class*="available" i]');
              let available = parseNumber(availableNode?.textContent || '');
              if (available === null) {
                const match = bodyText.match(/(?:merchants?|traders?)[^\d]*(\d+)\s*\/\s*(\d+)/i) || bodyText.match(/(\d+)\s*\/\s*(\d+)[^\n]{0,40}(?:merchants?|traders?)/i);
                if (match) available = Number.parseInt(match[1], 10);
              }

              let capacity = null;
              for (const selector of ['#merchantCapacity', '.merchantCapacity', '[id*="merchantCapacity" i]', '[class*="merchantCapacity" i]']) {
                const node = document.querySelector(selector);
                capacity = parseNumber(node?.textContent || node?.getAttribute('title') || '');
                if (capacity !== null) break;
              }
              if (capacity === null) {
                const match = bodyText.match(/(?:capacity|carry)[^\d]*(\d[\d\s.,']*)/i);
                capacity = match ? parseNumber(match[1]) : null;
              }

              let nextReturnSeconds = null;
              for (const node of document.querySelectorAll('.timer, .countdown, [counting="down"], [id^="timer"]')) {
                nextReturnSeconds = parseDuration(node.textContent || '');
                if (nextReturnSeconds !== null) break;
              }

              return { available, capacityPerMerchant: capacity, nextReturnSeconds };
            }
            """);

        var available = Math.Max(0, state.Available ?? 0);
        var perMerchant = Math.Max(0, state.CapacityPerMerchant ?? 0);
        return new ResourceTransferMerchantState(available, perMerchant, available * perMerchant, state.NextReturnSeconds);
    }

    private async Task<bool> TrySendResourceTransferShipmentAsync(Village targetVillage, ResourceTransferShipment shipment, CancellationToken cancellationToken)
    {
        if (!await FillMarketplaceResourceTransferFormAsync(targetVillage, shipment, cancellationToken))
        {
            return false;
        }

        if (!await ClickMarketplaceSendButtonAsync(cancellationToken))
        {
            return false;
        }

        await Task.Delay(300, cancellationToken);
        return await ClickMarketplaceConfirmButtonAsync(cancellationToken);
    }

    private async Task<bool> FillMarketplaceResourceTransferFormAsync(Village targetVillage, ResourceTransferShipment shipment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filled = await _page.EvaluateAsync<bool>(
            """
            ({ targetName, x, y, wood, clay, iron, crop }) => {
              const setValue = (el, value) => {
                if (!el) return false;
                el.focus();
                el.value = String(value);
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
              };
              const first = (selectors) => {
                for (const selector of selectors) {
                  const node = document.querySelector(selector);
                  if (node) return node;
                }
                return null;
              };

              // Official Travian (T4.6) names resource inputs lumber/clay/iron/crop
              // (wood is "lumber").
              const resourceOk =
                setValue(first(['input[name="lumber"]', 'input[name*="wood" i]', 'input[aria-label*="wood" i]']), wood) &&
                setValue(first(['input[name="clay"]', 'input[name*="clay" i]', 'input[aria-label*="clay" i]']), clay) &&
                setValue(first(['input[name="iron"]', 'input[name*="iron" i]', 'input[aria-label*="iron" i]']), iron) &&
                setValue(first(['input[name="crop"]', 'input[name*="crop" i]', 'input[aria-label*="crop" i]']), crop);
              if (!resourceOk) return false;

              let targetOk = false;
              if (Number.isFinite(x) && Number.isFinite(y)) {
                const xOk = setValue(first(['input[name="x"]', 'input#xCoordInput', 'input[name*="x" i][type="text"]']), x);
                const yOk = setValue(first(['input[name="y"]', 'input#yCoordInput', 'input[name*="y" i][type="text"]']), y);
                targetOk = xOk && yOk;
              }
              if (!targetOk && targetName) {
                targetOk = setValue(first(['input[name="dname"]', 'input[name="villageName"]', 'input[name*="village" i]', 'input[name*="name" i]']), targetName);
              }

              return targetOk;
            }
            """,
            new
            {
                targetName = targetVillage.Name,
                x = targetVillage.CoordX,
                y = targetVillage.CoordY,
                wood = shipment.Wood,
                clay = shipment.Clay,
                iron = shipment.Iron,
                crop = shipment.Crop,
            });

        return filled;
    }

    private async Task<bool> ClickMarketplaceSendButtonAsync(CancellationToken cancellationToken)
    {
        var selectors = new[]
        {
            "button[type='submit']",
            "input[type='submit']",
            "button:has-text('Send')",
            "button:has-text('Transport')",
            "a:has-text('Send')",
            ".button-container:has-text('Send')",
        };

        foreach (var selector in selectors)
        {
            var button = _page.Locator(selector).First;
            if (await button.CountAsync() <= 0)
            {
                continue;
            }

            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            await button.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
            return true;
        }

        return false;
    }

    private async Task<bool> ClickMarketplaceConfirmButtonAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selectors = new[]
            {
                "button:has-text('Confirm')",
                "input[type='submit'][value*='Confirm' i]",
                "button[type='submit']",
                ".button-container:has-text('Confirm')",
            };

            foreach (var selector in selectors)
            {
                var button = _page.Locator(selector).First;
                if (await button.CountAsync() <= 0)
                {
                    continue;
                }

                await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                await button.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private static int? MinPositive(int? current, int? candidate)
    {
        if (candidate is not > 0)
        {
            return current;
        }

        return current is > 0 ? Math.Min(current.Value, candidate.Value) : candidate.Value;
    }

    internal readonly record struct ResourceTransferShipment(long Wood, long Clay, long Iron, long Crop)
    {
        public static ResourceTransferShipment Empty { get; } = new(0, 0, 0, 0);
        public long Total => Math.Max(0, Wood) + Math.Max(0, Clay) + Math.Max(0, Iron) + Math.Max(0, Crop);
    }

    private sealed record ResourceTransferStock(string VillageName, IReadOnlyDictionary<string, long> Resources, long WarehouseCapacity, long GranaryCapacity)
    {
        public ResourceTransferStock AddIncoming(ResourceTransferShipment shipment)
        {
            var updated = new Dictionary<string, long>(Resources, StringComparer.OrdinalIgnoreCase)
            {
                ["wood"] = Resources.GetValueOrDefault("wood") + shipment.Wood,
                ["clay"] = Resources.GetValueOrDefault("clay") + shipment.Clay,
                ["iron"] = Resources.GetValueOrDefault("iron") + shipment.Iron,
                ["crop"] = Resources.GetValueOrDefault("crop") + shipment.Crop,
            };
            return this with { Resources = updated };
        }
    }

    private sealed record ResourceTransferMerchantState(int Available, int CapacityPerMerchant, long TotalCapacity, int? NextReturnSeconds);

    private sealed class ResourceTransferMerchantStateJs
    {
        [JsonPropertyName("available")]
        public int? Available { get; init; }

        [JsonPropertyName("capacityPerMerchant")]
        public int? CapacityPerMerchant { get; init; }

        [JsonPropertyName("nextReturnSeconds")]
        public int? NextReturnSeconds { get; init; }
    }
}
