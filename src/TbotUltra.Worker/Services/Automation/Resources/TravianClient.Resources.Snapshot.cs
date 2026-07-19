using Microsoft.Playwright;
using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Resource and production snapshots, diagnostics and per-village cache handling.
public sealed partial class TravianClient
{
    public async Task<VillageStatus> ReadVillageResourceStatusAsync(CancellationToken cancellationToken = default, bool allowNavigationToResourcePage = true)
    {
        if (allowNavigationToResourcePage && !IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

        await EnsureLoggedInAsync();
        if (allowNavigationToResourcePage)
        {
            await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
        }

        return await ReadCurrentVillageResourceStatusAsync(cancellationToken, allowNavigationToResourcePage);
    }

    public async Task<VillageStatus> ReadCurrentPageStorageStatusAsync(CancellationToken cancellationToken = default)
    {
        Notify("[resources:verbose] ReadCurrentPageStorageStatusAsync started");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var cachedSnapshot = TryGetCachedVillageResourceSnapshot(activeVillage);
        var snapshot = await ReadResourceSnapshotAsync(
            cancellationToken,
            allowRecovery: false,
            maxAttempts: 1);
        var resources = snapshot.Resources;
        var capacities = (
            Warehouse: snapshot.Capacities.Warehouse ?? cachedSnapshot?.WarehouseCapacity,
            Granary: snapshot.Capacities.Granary ?? cachedSnapshot?.GranaryCapacity);
        var productionByHour = ResourceSnapshotCalculator.MergeProductionByHour(snapshot.ProductionByHour, cachedSnapshot?.ProductionByHour);
        var forecasts = ResourceSnapshotCalculator.BuildStorageForecasts(resources, capacities.Warehouse, capacities.Granary, productionByHour);

        SaveCachedVillageResourceSnapshot(
            activeVillage,
            cachedSnapshot?.ResourceFields ?? [],
            capacities,
            productionByHour);

        Notify($"Storage read: village='{activeVillage}', storage wh={FormatResourceLogNumber(capacities.Warehouse)} gr={FormatResourceLogNumber(capacities.Granary)} | stock {BuildResourceValueLog(resources)} | prod {BuildProductionValueLog(productionByHour)}");

        var buildQueue = await ReadBuildQueueAsync(cancellationToken);
        var activeConstructions = await ReadActiveConstructionsAsync(
            cancellationToken,
            allowNavigationToBuildings: false,
            readMode: ActiveConstructionReadMode.CachedForObservation);
        var activeBuildCount = ConstructionSlots.ActiveBuildCount(buildQueue, activeConstructions);
        var remaining = TravianParsing.ResolveShortestQueueDurationSeconds(buildQueue);
        if (buildQueue.Count != activeConstructions.Count)
        {
            Notify(
                $"[construction-status:verbose] active count sources differ " +
                $"village='{activeVillage}' buildQueue={buildQueue.Count} " +
                $"activeConstructions={activeConstructions.Count} selected={activeBuildCount}");
        }

        var villageTribe = await ReadActiveVillageTribeAsync(cancellationToken);
        var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        return new VillageStatus(
            ActiveVillage: activeVillage,
            Villages: [],
            Resources: resources,
            ResourceFields: cachedSnapshot?.ResourceFields ?? [],
            Buildings: [],
            BuildQueue: buildQueue,
            Tribe: villageTribe,
            VillageCount: 0,
            IsBuildingInProgress: activeBuildCount > 0,
            ActiveBuildCount: activeBuildCount,
            BuildQueueRemainingSeconds: remaining,
            BuildQueueRemainingText: remaining is int left ? TravianParsing.FormatDuration(left) : string.Empty,
            IsCapital: TryGetCachedCapitalState(activeVillage),
            ServerTimeUtc: _serverTimeUtc,
            WarehouseCapacity: capacities.Warehouse,
            GranaryCapacity: capacities.Granary,
            ResourceStorageForecasts: forecasts,
            ActiveConstructions: activeConstructions,
            BuildQueueFinish: remaining is > 0 ? TimerSnapshot.FromRemaining(remaining.Value) : null,
            ActiveConstructionsFromOverview: _lastActiveConstructionsFromOverview,
            ActiveVillageCoordX: activeCoords.X,
            ActiveVillageCoordY: activeCoords.Y);
    }

    public async Task<IReadOnlyList<VillageStatus>> ReadAllVillageResourceStatusesAsync(CancellationToken cancellationToken = default)
    {
        Notify("[resources] all-village resource scan starting");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var villages = await ReadVillagesAsync(cancellationToken);
        if (villages.Count == 0)
        {
            return [await ReadVillageResourceStatusAsync(cancellationToken)];
        }

        var statuses = new List<VillageStatus>(villages.Count);
        foreach (var village in villages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SwitchToVillageAsync(village.Name, village.Url, cancellationToken, skipFeatureRefresh: true);
            statuses.Add(await ReadVillageResourceStatusAsync(cancellationToken));
        }

        Notify($"[resources] all-village resource scan finished — {statuses.Count} village(s)");
        return statuses;
    }

    public async Task NavigateToResourceFieldsAsync(CancellationToken cancellationToken = default)
    {
        Notify("[resources:verbose] NavigateToResourceFieldsAsync started");
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, double?>> ReadCurrentPageResourceProductionPerHourAsync(CancellationToken cancellationToken = default)
    {
        Notify("[ReadCurrentPageResourceProductionPerHourAsync] started");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            Notify("ReadCurrentPageResourceProductionPerHourAsync: current page is not dorf1, navigating to resource fields first.");
            await GotoAsync(Paths.Resources, cancellationToken);
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        }

        await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
        var production = await ReadResourceProductionPerHourAsync(cancellationToken);
        Notify($"ReadCurrentPageResourceProductionPerHourAsync: prod {BuildProductionValueLog(production)}");
        return production;
    }

    public async Task<PageHtmlCapture> ReadCurrentPageHtmlAsync(CancellationToken cancellationToken = default)
    {
        Notify("[ReadCurrentPageHtmlAsync] started");
        cancellationToken.ThrowIfCancellationRequested();
        var url = _page.Url;
        var html = await _page.ContentAsync();
        Notify($"ReadCurrentPageHtmlAsync: captured {html.Length} chars from url='{url}'.");
        return new PageHtmlCapture(url ?? string.Empty, html ?? string.Empty);
    }

    public async Task<PageHtmlCapture> NavigateToPageAndReadHtmlAsync(string pagePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePagePath(pagePath);
        Notify($"[NavigateToPageAndReadHtmlAsync] opening {normalizedPath}");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await GotoAsync(normalizedPath, cancellationToken);
        return await ReadCurrentPageHtmlAsync(cancellationToken);
    }

    private static string NormalizePagePath(string pagePath)
    {
        var value = (pagePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Page path is empty.");
        }

        return value;
    }

    private async Task NotifyCurrentResourceProductionForUiAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        try
        {
            Notify("Resource production update: start");
            var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
            var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
            var activeVillageKey = activeCoords.X.HasValue && activeCoords.Y.HasValue
                ? $"xy:{activeCoords.X.Value}|{activeCoords.Y.Value}"
                : null;
            IReadOnlyDictionary<string, double?> production;
            if (!forceRefresh
                && _productionUiSnapshot is not null
                && activeVillageKey is not null
                && string.Equals(_productionUiSnapshotVillageKey, activeVillageKey, StringComparison.OrdinalIgnoreCase))
            {
                production = _productionUiSnapshot;
                Notify($"Resource production update: reused current task snapshot for village='{activeVillage}'.");
            }
            else
            {
                production = await ReadCurrentPageResourceProductionPerHourAsync(cancellationToken);
                if (production.Count > 0 && production.Values.Any(value => value is not null))
                {
                    _productionUiSnapshotVillageKey = activeVillageKey;
                    _productionUiSnapshot = production;
                }
            }
            if (production.Count == 0 || production.Values.All(value => value is null))
            {
                Notify("Resource production update: skipped because no production values were read.");
                return;
            }

            var wood = production.TryGetValue("wood", out var woodValue) ? woodValue : null;
            var clay = production.TryGetValue("clay", out var clayValue) ? clayValue : null;
            var iron = production.TryGetValue("iron", out var ironValue) ? ironValue : null;
            var crop = production.TryGetValue("crop", out var cropValue) ? cropValue : null;
            Notify(
                $"Resource production update: wood={FormatProductionUpdateValue(wood)} clay={FormatProductionUpdateValue(clay)} iron={FormatProductionUpdateValue(iron)} crop={FormatProductionUpdateValue(crop)}");
        }
        catch (Exception ex)
        {
            Notify($"Resource production update: failed ({ex.Message})");
        }
    }

    private static string FormatProductionUpdateValue(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "-";
        }

        return Math.Round(value.Value, MidpointRounding.AwayFromZero)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<VillageStatus> ReadCurrentVillageResourceStatusAsync(CancellationToken cancellationToken, bool allowNavigationToResourcePage)
    {
        var villages = await ReadVillagesPreferCacheAsync(cancellationToken);
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        // Read the hero adventure indicator from the current page (cheap, no navigation) so the
        // periodic refresh keeps the dashboard/hero adventure count in sync with the live game.
        var heroSidebar = await ReadHeroSidebarStatusAsync(cancellationToken);
        int? adventureCount = heroSidebar.AdventureFound ? Math.Max(0, heroSidebar.AdventureCount) : null;
        // Read Travian's own in-progress construction list from the current page so the buildings /
        // resources UI keeps showing upgrades started outside the program (target level in parentheses).
        var activeConstructions = await ReadActiveConstructionsAsync(
            cancellationToken,
            allowNavigationToBuildings: false,
            readMode: ActiveConstructionReadMode.CachedForObservation);
        var buildQueue = await ReadBuildQueueAsync(cancellationToken);
        var activeBuildCount = ConstructionSlots.ActiveBuildCount(buildQueue, activeConstructions);
        if (buildQueue.Count != activeConstructions.Count)
        {
            Notify(
                $"[construction-status:verbose] active count sources differ " +
                $"village='{activeVillage}' buildQueue={buildQueue.Count} " +
                $"activeConstructions={activeConstructions.Count} selected={activeBuildCount}");
        }
        var remaining = TravianParsing.ResolveShortestQueueDurationSeconds(buildQueue);
        var currency = await ReadCurrencyAsync(cancellationToken);
        var unreadInbox = await ReadUnreadInboxCountsAsync(cancellationToken);
        if (allowNavigationToResourcePage)
        {
            await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
        }

        var snapshot = await ReadResourceSnapshotAsync(
            cancellationToken,
            allowRecovery: allowNavigationToResourcePage,
            maxAttempts: allowNavigationToResourcePage ? 4 : 1);
        var cachedSnapshot = TryGetCachedVillageResourceSnapshot(activeVillage);
        var resources = snapshot.Resources;
        var capacities = (
            Warehouse: snapshot.Capacities.Warehouse ?? cachedSnapshot?.WarehouseCapacity,
            Granary: snapshot.Capacities.Granary ?? cachedSnapshot?.GranaryCapacity);
        var productionByHour = ResourceSnapshotCalculator.MergeProductionByHour(snapshot.ProductionByHour, cachedSnapshot?.ProductionByHour);
        var forecasts = ResourceSnapshotCalculator.BuildStorageForecasts(resources, capacities.Warehouse, capacities.Granary, productionByHour);
        var usingCachedProduction = !HasAnyProduction(snapshot.ProductionByHour) && HasAnyProduction(cachedSnapshot?.ProductionByHour);
        NotifyResourceRead($"Resource read: storage wh={FormatResourceLogNumber(capacities.Warehouse)} gr={FormatResourceLogNumber(capacities.Granary)} | stock {BuildResourceValueLog(resources)} | prod {BuildProductionValueLog(productionByHour)}{(usingCachedProduction ? " (cached production)" : string.Empty)}");

        var liveResourceFields = await ReadResourceFieldsAsync(cancellationToken);
        var liveResourceFieldsComplete = HasCompleteResourceFieldSnapshot(liveResourceFields);

        var resourceFields = liveResourceFieldsComplete
            ? liveResourceFields
            : cachedSnapshot?.ResourceFields ?? liveResourceFields;

        // Fast capital detection: non-capital villages are capped at level 10
        var cachedIsCapital = TryGetCachedCapitalState(activeVillage);
        if (cachedIsCapital != true && resourceFields.Any(f => f.Level > 10))
        {
            Notify($"Fast capital detection: resource field above level 10 found — '{activeVillage}' is capital.");
            SaveCachedVillageState(activeVillage, true, null, null);
            cachedIsCapital = true;
            // Update the in-memory list with the new capital flag instead of refetching
            // from spieler.php — that re-fetch would cause a visible page navigation.
            villages = villages
                .Select(v => string.Equals(v.Name, activeVillage, StringComparison.Ordinal)
                    ? v with { IsCapital = true }
                    : v)
                .ToList();
            UpdateCachedVillages(villages);
        }

        SaveCachedVillageResourceSnapshot(
            activeVillage,
            resourceFields,
            capacities,
            productionByHour);

        var villageTribe = await ReadActiveVillageTribeAsync(cancellationToken);
        villages = EnrichActiveVillageTribe(villages, activeVillage, villageTribe);
        var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        return new VillageStatus(
            ActiveVillage: activeVillage,
            Villages: villages,
            Resources: resources,
            ResourceFields: resourceFields,
            Buildings: [],
            BuildQueue: buildQueue,
            Tribe: villageTribe,
            VillageCount: villages.Count,
            Gold: currency.Gold,
            Silver: currency.Silver,
            IsBuildingInProgress: activeBuildCount > 0,
            ActiveBuildCount: activeBuildCount,
            BuildQueueRemainingSeconds: remaining,
            BuildQueueRemainingText: remaining is int left ? TravianParsing.FormatDuration(left) : string.Empty,
            IsCapital: cachedIsCapital,
            ServerTimeUtc: _serverTimeUtc,
            UnreadMessages: unreadInbox.UnreadMessages,
            UnreadReports: unreadInbox.UnreadReports,
            WarehouseCapacity: capacities.Warehouse,
            GranaryCapacity: capacities.Granary,
            ResourceStorageForecasts: forecasts,
            AdventureCount: adventureCount,
            ActiveConstructions: activeConstructions,
            BuildQueueFinish: remaining is > 0 ? TimerSnapshot.FromRemaining(remaining.Value) : null,
            ActiveConstructionsFromOverview: _lastActiveConstructionsFromOverview,
            ActiveVillageCoordX: activeCoords.X,
            ActiveVillageCoordY: activeCoords.Y);
    }

    private async Task<(
        IReadOnlyDictionary<string, string> Resources,
        (long? Warehouse, long? Granary) Capacities,
        IReadOnlyDictionary<string, double?> ProductionByHour)> ReadResourceSnapshotAsync(
            CancellationToken cancellationToken,
            bool allowRecovery = true,
            int maxAttempts = 4)
    {
        var attempts = Math.Max(1, maxAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = await _page.EvaluateAsync<ResourceSnapshotDomReadResult>(
                """
                () => {
                  const clean = (value) => String(value || '').replace(/[\u202A-\u202E\u2066-\u2069]/g, '').replace(/\s+/g, ' ').trim();
                  const compact = (value) => clean(value).replace(/\s+/g, '');
                  const parseNumber = (value) => {
                    const text = clean(value).replace(/[\u2212\u2012\u2013\u2014]/g, '-');
                    if (!text) return null;
                    const match = text.match(/([+-]?\d[\d\s.,']*)/);
                    if (!match) return null;
                    const normalized = match[1].replace(/\s+/g, '').replace(/,/g, '').replace(/'/g, '');
                    const parsed = Number(normalized);
                    return Number.isFinite(parsed) ? parsed : null;
                  };

                  const readFirstText = (selectors) => {
                    for (const selector of selectors) {
                      const node = document.querySelector(selector);
                      if (!node) continue;
                      const values = [
                        node.getAttribute?.('data-value'),
                        node.getAttribute?.('data-amount'),
                        node.getAttribute?.('data-max'),
                        node.getAttribute?.('data-capacity'),
                        node.innerText,
                        node.textContent,
                        node.querySelector?.('.value')?.innerText,
                        node.querySelector?.('.value')?.textContent
                      ];
                      for (const value of values) {
                        const text = compact(value);
                        if (text) return text;
                      }
                    }
                    return null;
                  };

                  const readProduction = (resourceClass) => {
                    const row = document.querySelector(`#production i.${resourceClass}`)?.closest('tr');
                    if (!row) return null;
                    const valueCell = row.querySelector('td.num');
                    return parseNumber(valueCell?.innerText || valueCell?.textContent || row.innerText || row.textContent || '');
                  };

                  return {
                    wood: readFirstText(['#l1']),
                    clay: readFirstText(['#l2']),
                    iron: readFirstText(['#l3']),
                    crop: readFirstText(['#l4']),
                    warehouse: readFirstText(['#warehouse', '#warehouse .value', '.warehouse .capacity .value', '.warehouse .value']),
                    granary: readFirstText(['#granary', '#granary .value', '#silo', '#silo .value', '.granary .capacity .value', '.granary .value']),
                    woodProduction: readProduction('r1'),
                    clayProduction: readProduction('r2'),
                    ironProduction: readProduction('r3'),
                    cropProduction: readProduction('r4'),
                    diagnostics: [
                      `url=${location.pathname}${location.search}`,
                      `ready=${document.readyState}`,
                      `l1=${readFirstText(['#l1']) || '-'}`,
                      `warehouseNode=${document.querySelector('.warehouse .capacity .value, #warehouse') ? 'yes' : 'no'}`,
                      `granaryNode=${document.querySelector('.granary .capacity .value, #granary, #silo') ? 'yes' : 'no'}`,
                      `productionNode=${document.querySelector('#production') ? 'yes' : 'no'}`,
                      `productionText=${String(readProduction('r1') ?? '-')}`,
                      `bodyClass=${document.body?.className || '-'}`
                    ].join(', ')
                  };
                }
                """);

            var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddResourceIfPresent(resources, "wood", snapshot?.Wood);
            AddResourceIfPresent(resources, "clay", snapshot?.Clay);
            AddResourceIfPresent(resources, "iron", snapshot?.Iron);
            AddResourceIfPresent(resources, "crop", snapshot?.Crop);

            var productionByHour = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wood"] = snapshot?.WoodProduction,
                ["clay"] = snapshot?.ClayProduction,
                ["iron"] = snapshot?.IronProduction,
                ["crop"] = snapshot?.CropProduction,
            };
            var capacities = (
                Warehouse: TravianParsing.TryParseResourceValue(snapshot?.Warehouse),
                Granary: TravianParsing.TryParseResourceValue(snapshot?.Granary));

            var hasResources = resources.Count > 0;
            var hasCapacity = capacities.Warehouse is not null || capacities.Granary is not null;
            var hasProduction = productionByHour.Values.Any(value => value is not null);
            if (hasResources || hasCapacity || hasProduction)
            {
                if (attempt > 1)
                {
                    Notify($"Resource read recovered on attempt {attempt}/4: {snapshot?.Diagnostics ?? "-"}");
                }

                return (resources, capacities, productionByHour);
            }

            Notify($"Resource read incomplete {attempt}/{attempts}: {snapshot?.Diagnostics ?? "-"}");
            if (!allowRecovery)
            {
                continue;
            }

            if (attempt == 2)
            {
                try
                {
                    await ReloadPageTracedAsync(
                        _page,
                        "incomplete resource snapshot",
                        new PageReloadOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = _config.TimeoutMs,
                        },
                        cancellationToken);
                    await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
                    await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
                }
                catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
                {
                    Notify($"Resource snapshot reload hit transient navigation context: {ex.Message}");
                }
                catch (TimeoutException)
                {
                    Notify("Resource snapshot reload timed out while waiting for DOMContentLoaded.");
                }
            }

            await Task.Delay(250 * attempt, cancellationToken);
        }

        return (
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            (null, null),
            new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase));
    }

    private static bool HasAnyProduction(IReadOnlyDictionary<string, double?>? productionByHour)
        => productionByHour is not null && productionByHour.Values.Any(value => value is not null);

    private static void AddResourceIfPresent(IDictionary<string, string> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }

    private static string BuildResourceValueLog(IReadOnlyDictionary<string, string> resources)
    {
        return string.Join(" ", new[] { "wood", "clay", "iron", "crop" }
            .Select(key => $"{key[0]}={FormatResourceLogNumber(TravianParsing.TryParseResourceValue(resources.TryGetValue(key, out var raw) ? raw : null))}"));
    }

    private static string BuildProductionValueLog(IReadOnlyDictionary<string, double?> productionByHour)
    {
        return string.Join(" ", new[] { "wood", "clay", "iron", "crop" }
            .Select(key =>
            {
                productionByHour.TryGetValue(key, out var value);
                return $"{key[0]}={FormatResourceLogNumber(value)}";
            }));
    }

    private static string FormatResourceLogNumber(long? value)
    {
        if (value is null)
        {
            return "-";
        }

        return value.Value.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", " ");
    }

    private static string FormatResourceLogNumber(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "-";
        }

        return Math.Round(value.Value, MidpointRounding.AwayFromZero)
            .ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)
            .Replace(",", " ");
    }

    private async Task WaitForResourceSnapshotWidgetsAsync(CancellationToken cancellationToken)
    {
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    () => {
                      const hasResourceValue = !!document.querySelector('#l1');
                      const hasCapacity = !!document.querySelector('.warehouse .capacity .value, .granary .capacity .value');
                      const hasProduction = !!document.querySelector('#production td.num, #production tbody tr');
                      return hasResourceValue && hasCapacity && hasProduction;
                    }
                    """,
                    new PageWaitForFunctionOptions
                    {
                        Timeout = 1500,
                    }).WaitAsync(cancellationToken);

                if (attempt > 1)
                {
                    Notify($"Resource widgets became available on attempt {attempt}.");
                }

                return;
            }
            catch (TimeoutException)
            {
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
            }

            var diagnostics = await ReadResourceSnapshotDiagnosticsAsync(cancellationToken);
            Notify($"Resource widget wait attempt {attempt}/4: {diagnostics}");

            if (attempt >= 4)
            {
                return;
            }

            if (attempt == 2)
            {
                try
                {
                    await ReloadPageTracedAsync(
                        _page,
                        "resource widgets not ready",
                        new PageReloadOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = _config.TimeoutMs,
                        },
                        cancellationToken);
                    await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
                    continue;
                }
                catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
                {
                    Notify($"Resource widget reload hit transient navigation context: {ex.Message}");
                }
                catch (TimeoutException)
                {
                    Notify("Resource widget reload timed out while waiting for DOMContentLoaded.");
                }
            }

            await Task.Delay(300 * attempt, cancellationToken);
        }
    }

    private async Task<string> ReadResourceSnapshotDiagnosticsAsync(CancellationToken cancellationToken)
    {

        try
        {
            return await _page.EvaluateAsync<string>(
                """
                () => {
                  const text = (selector) => {
                    const node = document.querySelector(selector);
                    if (!node) return '-';
                    return (node.textContent || '').replace(/\s+/g, ' ').trim() || '(empty)';
                  };

                  return [
                    `url=${location.pathname}${location.search}`,
                    `ready=${document.readyState}`,
                    `l1=${document.querySelector('#l1') ? 'yes' : 'no'}:${text('#l1')}`,
                    `warehouse=${document.querySelector('.warehouse .capacity .value') ? 'yes' : 'no'}:${text('.warehouse .capacity .value')}`,
                    `granary=${document.querySelector('.granary .capacity .value') ? 'yes' : 'no'}:${text('.granary .capacity .value')}`,
                    `production=${document.querySelector('#production') ? 'yes' : 'no'}`,
                    `villageMap=${document.querySelector('#village_map') ? 'yes' : 'no'}`,
                    `bodyClass=${document.body?.className || '-'}`
                  ].join(', ');
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return $"diagnostics unavailable: {ex.Message}";
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadResourcesAsync(CancellationToken cancellationToken)
    {
        using var trace = _browserTrace.BeginOperation("READ", "stock-bar-resources", "scope=current-page source=live");
        var raw = await _page.EvaluateAsync<Dictionary<string, string>>(
            """
            () => {
              const readValue = (selector) => {
                const element = document.querySelector(selector);
                if (!element) return null;
                const candidates = [
                  element.getAttribute('data-value'),
                  element.getAttribute('data-amount'),
                  element.getAttribute('aria-label'),
                  element.getAttribute('title'),
                  element.textContent,
                  element.querySelector?.('.value')?.textContent || ''
                ];
                for (const candidate of candidates) {
                  const value = String(candidate || '').replace(/\s+/g, '').trim();
                  if (value) return value;
                }
                return null;
              };

              const ids = {
                wood: ['#l1'],
                clay: ['#l2'],
                iron: ['#l3'],
                crop: ['#l4']
              };
              const resources = {};

              for (const [name, selectors] of Object.entries(ids)) {
                for (const selector of selectors) {
                  const value = readValue(selector);
                  if (value) {
                    resources[name] = value;
                    break;
                  }
                }
              }

              return resources;
            }
            """);
        var result = raw ?? new Dictionary<string, string>();
        if (result.Count == 0)
        {
            Notify("[resources:verbose] ReadResourcesAsync read 0 values (page may not be dorf1/dorf2 or stock bar not loaded)");
        }
        else
        {
            Notify($"[resources:verbose] ReadResourcesAsync — wood={result.GetValueOrDefault("wood", "-")} clay={result.GetValueOrDefault("clay", "-")} iron={result.GetValueOrDefault("iron", "-")} crop={result.GetValueOrDefault("crop", "-")}");
        }
        trace.Complete(
            "success",
            $"count={result.Count} wood={result.GetValueOrDefault("wood", "-")} clay={result.GetValueOrDefault("clay", "-")} iron={result.GetValueOrDefault("iron", "-")} crop={result.GetValueOrDefault("crop", "-")}");
        return result;
    }

    private sealed class ResourceSnapshotDomReadResult
    {
        public string? Wood { get; set; }
        public string? Clay { get; set; }
        public string? Iron { get; set; }
        public string? Crop { get; set; }
        public string? Warehouse { get; set; }
        public string? Granary { get; set; }
        public double? WoodProduction { get; set; }
        public double? ClayProduction { get; set; }
        public double? IronProduction { get; set; }
        public double? CropProduction { get; set; }
        public string? Diagnostics { get; set; }
    }

    private async Task<IReadOnlyDictionary<string, double?>> ReadResourceProductionPerHourAsync(CancellationToken cancellationToken)
    {
        const int attempts = 4;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = await _page.EvaluateAsync<ResourceSnapshotDomReadResult>(
                """
                () => {
                  const clean = (value) => String(value || '').replace(/[\u202A-\u202E\u2066-\u2069]/g, '').replace(/\s+/g, ' ').trim();
                  const parseNumber = (value) => {
                    const text = clean(value).replace(/[\u2212\u2012\u2013\u2014]/g, '-');
                    if (!text) return null;
                    const match = text.match(/([+-]?\d[\d\s.,']*)/);
                    if (!match) return null;
                    const normalized = match[1].replace(/\s+/g, '').replace(/,/g, '').replace(/'/g, '');
                    const parsed = Number(normalized);
                    return Number.isFinite(parsed) ? parsed : null;
                  };

                  const readProduction = (resourceClass) => {
                    const row = document.querySelector(`#production i.${resourceClass}`)?.closest('tr');
                    if (!row) return null;
                    const valueCell = row.querySelector('td.num');
                    return parseNumber(valueCell?.innerText || valueCell?.textContent || row.innerText || row.textContent || '');
                  };

                  return {
                    woodProduction: readProduction('r1'),
                    clayProduction: readProduction('r2'),
                    ironProduction: readProduction('r3'),
                    cropProduction: readProduction('r4'),
                    diagnostics: [
                      `url=${location.pathname}${location.search}`,
                      `ready=${document.readyState}`,
                      `productionNode=${document.querySelector('#production') ? 'yes' : 'no'}`,
                      `productionText=${String(readProduction('r1') ?? '-')}`,
                      `bodyClass=${document.body?.className || '-'}`
                    ].join(', ')
                  };
                }
                """);

            var productionByHour = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wood"] = snapshot?.WoodProduction,
                ["clay"] = snapshot?.ClayProduction,
                ["iron"] = snapshot?.IronProduction,
                ["crop"] = snapshot?.CropProduction,
            };

            if (productionByHour.Values.Any(value => value is not null))
            {
                if (attempt > 1)
                {
                    Notify($"Production read recovered on attempt {attempt}/{attempts}: {snapshot?.Diagnostics ?? "-"}");
                }

                return productionByHour;
            }

            Notify($"Production read incomplete {attempt}/{attempts}: {snapshot?.Diagnostics ?? "-"}");

            if (attempt == 2)
            {
                try
                {
                    await ReloadPageTracedAsync(
                        _page,
                        "incomplete resource production read",
                        new PageReloadOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = _config.TimeoutMs,
                        },
                        cancellationToken);
                    await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
                    await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
                }
                catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
                {
                    Notify($"Production read reload hit transient navigation context: {ex.Message}");
                }
                catch (TimeoutException)
                {
                    Notify("Production read reload timed out while waiting for DOMContentLoaded.");
                }
            }

            await Task.Delay(250 * attempt, cancellationToken);
        }

        return new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasCompleteResourceFieldSnapshot(IReadOnlyList<ResourceField> fields)
    {
        var bySlot = fields
            .Where(field => field.SlotId is >= 1 and <= 18)
            .GroupBy(field => field.SlotId!.Value)
            .ToList();
        if (bySlot.Count != 18)
        {
            return false;
        }

        return bySlot.All(group =>
        {
            var field = group.First();
            return field.Level is >= 0
                && IsKnownResourceFieldType(field.FieldType);
        });
    }

    private static bool IsKnownResourceFieldType(string? fieldType)
        => string.Equals(fieldType, "wood", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fieldType, "clay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fieldType, "iron", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fieldType, "crop", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownVillageName(string villageName)
        => !string.IsNullOrWhiteSpace(villageName)
            && !string.Equals(villageName.Trim(), "Unknown village", StringComparison.OrdinalIgnoreCase);

    private string? BuildVillageResourceCacheKey(string villageName)
    {
        var normalizedName = villageName.Trim();
        var matches = _cachedVillages?
            .Where(village => VillageIdentityReconciler.IsSameName(village.Name, normalizedName))
            .ToList() ?? [];
        if (matches.Count > 1)
        {
            return null;
        }

        var villageIdentity = matches.Count == 1
            && matches[0].CoordX is int x
            && matches[0].CoordY is int y
                ? $"xy:{x}|{y}"
                : $"name:{normalizedName}";
        return $"{_account.Name}|{_config.BaseUrl.TrimEnd('/')}|{villageIdentity}";
    }

    private CachedVillageResourceSnapshot? TryGetCachedVillageResourceSnapshot(string villageName)
    {
        if (!IsKnownVillageName(villageName))
        {
            return null;
        }

        var key = BuildVillageResourceCacheKey(villageName);
        if (key is null)
        {
            return null;
        }
        lock (ResourceStatusCacheSync)
        {
            return CachedVillageResourceSnapshotsByKey.TryGetValue(key, out var snapshot)
                ? snapshot
                : null;
        }
    }

    private void SaveCachedVillageResourceSnapshot(
        string villageName,
        IReadOnlyList<ResourceField> resourceFields,
        (long? Warehouse, long? Granary) capacities,
        IReadOnlyDictionary<string, double?> productionByHour)
    {
        if (!IsKnownVillageName(villageName))
        {
            return;
        }

        var key = BuildVillageResourceCacheKey(villageName);
        if (key is null)
        {
            Notify($"[resource-cache] skipped ambiguous village name '{villageName}'; stable coordinates are required.");
            return;
        }
        lock (ResourceStatusCacheSync)
        {
            CachedVillageResourceSnapshotsByKey.TryGetValue(key, out var existing);

            var fieldsToStore = HasCompleteResourceFieldSnapshot(resourceFields)
                ? resourceFields.Select(field => field with { }).ToList()
                : existing?.ResourceFields ?? [];

            var productionToStore = HasAnyProduction(productionByHour)
                ? new Dictionary<string, double?>(productionByHour, StringComparer.OrdinalIgnoreCase)
                : existing?.ProductionByHour ?? new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);

            var warehouse = capacities.Warehouse ?? existing?.WarehouseCapacity;
            var granary = capacities.Granary ?? existing?.GranaryCapacity;

            if (fieldsToStore.Count == 0
                && productionToStore.Count == 0
                && warehouse is null
                && granary is null)
            {
                return;
            }

            CachedVillageResourceSnapshotsByKey[key] = new CachedVillageResourceSnapshot
            {
                ResourceFields = fieldsToStore,
                ProductionByHour = productionToStore,
                WarehouseCapacity = warehouse,
                GranaryCapacity = granary,
            };
        }
    }

}
