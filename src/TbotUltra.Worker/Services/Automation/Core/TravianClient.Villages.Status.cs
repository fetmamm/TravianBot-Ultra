using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Configuration;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task<IReadOnlyList<VillageStatus>> ReadAllVillageStatusesAsync(CancellationToken cancellationToken = default)
    {
        Notify("[scan] all-village status scan starting");
        var returnVillageName = await TryReadActiveVillageNameSafeAsync(cancellationToken);
        await GotoAsync(Paths.Resources, cancellationToken);
        await EnsureLoggedInAsync();

        var villages = await ReadVillagesAsync(cancellationToken);
        if (villages.Count == 0)
        {
            return [await ReadCurrentVillageStatusAsync(cancellationToken)];
        }

        var statuses = new List<VillageStatus>();
        try
        {
            var scanIndex = 0;
            foreach (var village in villages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanIndex++;
                Notify($"[scan:verbose] reading village '{village.Name}' ({scanIndex}/{villages.Count})");

                if (!string.IsNullOrWhiteSpace(village.Url))
                {
                    await GotoAsync(village.Url, cancellationToken);
                }
                else
                {
                    await GotoAsync(Paths.Resources, cancellationToken);
                }

                await EnsureLoggedInAsync();
                await ApplyActionDelayAsync(cancellationToken);
                statuses.Add(await ReadCurrentVillageStatusAsync(cancellationToken));
            }
        }
        finally
        {
            Notify($"[scan] all-village status scan finished — read {statuses.Count}/{villages.Count} village(s)");
            if (!string.IsNullOrWhiteSpace(returnVillageName))
            {
                try
                {
                    await SwitchToVillageAsync(returnVillageName, cancellationToken: cancellationToken, skipFeatureRefresh: true);
                    await GotoAsync(Paths.Resources, cancellationToken);
                    Notify($"[scan:verbose] returned to original village '{returnVillageName}' on dorf1");
                }
                catch (Exception ex)
                {
                    Notify($"[scan] could not return to original village '{returnVillageName}': {ex.Message}");
                }
            }
        }

        return statuses;
    }

    private async Task<VillageStatus> ReadCurrentVillageStatusAsync(
        CancellationToken cancellationToken,
        IReadOnlyList<Village>? knownVillages = null,
        IReadOnlyList<Building>? knownBuildings = null)
    {
        // Read the village list from the sidebar/cache instead of navigating to the profile (spieler.php).
        // On a village switch we only need dorf1/dorf2 of the target village for status; the profile was
        // only used to enumerate villages and re-check the capital — capital comes from cache here. This
        // avoids the extra (slow) profile navigation on every switch/status read.
        var villages = knownVillages is { Count: > 0 }
            ? knownVillages
            : await ReadVillagesPreferCacheAsync(cancellationToken);
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var buildQueue = await ReadBuildQueueAsync(cancellationToken);
        var remaining = TravianParsing.ResolveShortestQueueDurationSeconds(buildQueue);
        var currency = await ReadCurrencyAsync(cancellationToken);
        var unreadInbox = await ReadUnreadInboxCountsAsync(cancellationToken);
        await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
        var snapshot = await ReadResourceSnapshotAsync(cancellationToken);
        var resources = snapshot.Resources;
        var cachedSnapshot = TryGetCachedVillageResourceSnapshot(activeVillage);
        var capacities = (
            Warehouse: snapshot.Capacities.Warehouse ?? cachedSnapshot?.WarehouseCapacity,
            Granary: snapshot.Capacities.Granary ?? cachedSnapshot?.GranaryCapacity);
        var productionByHour = ResourceSnapshotCalculator.MergeProductionByHour(snapshot.ProductionByHour, cachedSnapshot?.ProductionByHour);
        var forecasts = ResourceSnapshotCalculator.BuildStorageForecasts(resources, capacities.Warehouse, capacities.Granary, productionByHour);
        var usingCachedProduction = !HasAnyProduction(snapshot.ProductionByHour) && HasAnyProduction(cachedSnapshot?.ProductionByHour);
        NotifyResourceRead($"Resource read: storage wh={FormatResourceLogNumber(capacities.Warehouse)} gr={FormatResourceLogNumber(capacities.Granary)} | stock {BuildResourceValueLog(resources)} | prod {BuildProductionValueLog(productionByHour)}{(usingCachedProduction ? " (cached production)" : string.Empty)}");

        var resourceFields = await ReadResourceFieldsAsync(cancellationToken);

        // Persist the per-village resource snapshot here too. This full status read runs on dorf1 right
        // after a village switch and is often the only place production is read for a freshly-switched
        // village. Without saving it, later current-page reads (on dorf2/build pages, where production is
        // not present) found an empty cache and showed "@-/h"/"not filling". SaveCached keeps existing
        // values when the new read is empty, so it never overwrites good data with blanks.
        SaveCachedVillageResourceSnapshot(activeVillage, resourceFields, capacities, productionByHour);

        var buildings = knownBuildings is { Count: > 0 }
            ? knownBuildings
            : await ReadBuildingsAsync(cancellationToken);
        // Read Travian's own in-progress construction list so the UI can show upgrades that were
        // started outside the program (e.g. manually before login) with the target level in
        // parentheses. We are on dorf1/dorf2 after the reads above, both of which carry the list,
        // so no extra navigation is needed.
        var activeConstructions = await ReadActiveConstructionsAsync(cancellationToken, allowNavigationToBuildings: false);
        var heroStatus = await ReadHeroStatusAsync(cancellationToken);
        var activeBuildCount = ConstructionSlots.ActiveBuildCount(buildQueue, activeConstructions);
        if (buildQueue.Count != activeConstructions.Count)
        {
            Notify(
                $"[construction-status:verbose] active count sources differ " +
                $"village='{activeVillage}' buildQueue={buildQueue.Count} " +
                $"activeConstructions={activeConstructions.Count} selected={activeBuildCount}");
        }

        return new VillageStatus(
            ActiveVillage: activeVillage,
            Villages: villages,
            Resources: resources,
            ResourceFields: resourceFields,
            Buildings: buildings,
            BuildQueue: buildQueue,
            Tribe: await ReadTribeAsync(cancellationToken),
            VillageCount: villages.Count,
            Gold: currency.Gold,
            Silver: currency.Silver,
            IsBuildingInProgress: activeBuildCount > 0,
            ActiveBuildCount: activeBuildCount,
            BuildQueueRemainingSeconds: remaining,
            BuildQueueRemainingText: remaining is int left ? TravianParsing.FormatDuration(left) : string.Empty,
            IsCapital: TryGetCachedCapitalState(activeVillage),
            ServerTimeUtc: _serverTimeUtc,
            UnreadMessages: unreadInbox.UnreadMessages,
            UnreadReports: unreadInbox.UnreadReports,
            WarehouseCapacity: capacities.Warehouse,
            GranaryCapacity: capacities.Granary,
            ResourceStorageForecasts: forecasts,
            ActiveConstructions: activeConstructions,
            BuildQueueFinish: remaining is > 0 ? TimerSnapshot.FromRemaining(remaining.Value) : null,
            HeroStatus: heroStatus,
            ActiveConstructionsFromOverview: _lastActiveConstructionsFromOverview);
    }

    private async Task<(long? Warehouse, long? Granary)> ReadStorageCapacitiesAsync(CancellationToken cancellationToken)
    {
        var raw = await _page.EvaluateAsync<Dictionary<string, string>>(
            """
            () => {
              const readFirst = (selectors) => {
                for (const selector of selectors) {
                  for (const node of document.querySelectorAll(selector)) {
                    const value =
                      node.getAttribute('data-value')
                      || node.getAttribute('data-max')
                      || node.getAttribute('data-capacity')
                      || node.getAttribute('title')
                      || node.getAttribute('aria-label')
                      || node.textContent
                      || '';
                    const text = String(value).trim();
                    if (text) return text;
                  }
                }
                return null;
              };

              return {
                warehouse: readFirst([
                  '#warehouse .value',
                  '#warehouse',
                  '[id*="warehouse" i][data-max]',
                  '[class*="warehouse" i]'
                ]),
                granary: readFirst([
                  '#granary .value',
                  '#granary',
                  '#silo .value',
                  '#silo',
                  '[id*="granary" i][data-max]',
                  '[id*="silo" i][data-max]',
                  '[class*="granary" i]',
                  '[class*="silo" i]'
                ])
              };
            }
            """);

        if (raw is null)
        {
            return (null, null);
        }

        raw.TryGetValue("warehouse", out var warehouseRaw);
        raw.TryGetValue("granary", out var granaryRaw);
        return (TravianParsing.TryParseResourceValue(warehouseRaw), TravianParsing.TryParseResourceValue(granaryRaw));
    }


}

