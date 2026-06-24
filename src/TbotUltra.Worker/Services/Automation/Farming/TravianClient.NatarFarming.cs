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
    public async Task<FarmAddResult> AddSingleFarmFromNatarsAsync(
        string farmListName,
        string troopType,
        int troopCount,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (string.IsNullOrWhiteSpace(farmListName))
        {
            throw new InvalidOperationException("Farm list name is required.");
        }

        if (string.IsNullOrWhiteSpace(troopType))
        {
            throw new InvalidOperationException("Troop type is required.");
        }

        if (troopCount <= 0)
        {
            throw new InvalidOperationException("Troop count must be greater than 0.");
        }

        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        var coordinate = await ReadFirstNatarFarmCoordinateAsync(cancellationToken);
        if (coordinate is null || coordinate.X is null || coordinate.Y is null)
        {
            throw new InvalidOperationException("Could not read any Natar farm coordinates from Natars profile.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        var lid = await TryResolveFarmListSlotIdByNameAsync(farmListName, cancellationToken);
        if (string.IsNullOrWhiteSpace(lid))
        {
            throw new InvalidOperationException($"Could not find farm list '{farmListName}' on farm page.");
        }

        await GotoAsync(Paths.FarmListBySlotId(lid), cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Add Raid form.", cancellationToken);
        await EnsureLoggedInAsync();

        var saveOutcome = await TryFillAddRaidFormAndSaveAsync(
            farmListName,
            troopType.Trim(),
            troopCount,
            coordinate.X.Value,
            coordinate.Y.Value,
            lid,
            false,
            cancellationToken);
        if (saveOutcome == AddRaidSaveOutcome.Failed)
        {
            throw new InvalidOperationException("Could not fill Add Raid form or click Save.");
        }
        if (saveOutcome == AddRaidSaveOutcome.AlreadyInList)
        {
            throw new InvalidOperationException("This village is already in the selected farm list.");
        }
        Notify($"Added 1 farm to '{farmListName}' at ({coordinate.X}|{coordinate.Y}) with {troopCount} {troopType}.");
        return new FarmAddResult(farmListName, coordinate.X.Value, coordinate.Y.Value, troopType.Trim(), troopCount);
    }

    public async Task<FarmAddBatchResult> AddFarmsFromNatarsAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IProgress<int>? addedProgress = null,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (string.IsNullOrWhiteSpace(farmListName))
        {
            throw new InvalidOperationException("Farm list name is required.");
        }

        if (string.IsNullOrWhiteSpace(troopType))
        {
            throw new InvalidOperationException("Troop type is required.");
        }

        if (troopCount <= 0)
        {
            throw new InvalidOperationException("Troop count must be greater than 0.");
        }

        if (requestedCount <= 0)
        {
            throw new InvalidOperationException("Requested farm count must be greater than 0.");
        }

        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        var coordinates = await ReadNatarFarmCoordinatesCachedAsync(cancellationToken);
        if (coordinates.Count <= 0)
        {
            throw new InvalidOperationException("Could not read any 'Natar farm village' coordinates from Natars profile.");
        }

        return await AddFarmsFromCoordinatesCoreAsync(
            farmListName,
            troopType,
            troopCount,
            requestedCount,
            coordinates
                .Where(coordinate => coordinate.X.HasValue && coordinate.Y.HasValue)
                .Select(coordinate => new FarmCoordinate(coordinate.X!.Value, coordinate.Y!.Value))
                .ToList(),
            addedProgress,
            null,
            false,
            cancellationToken);
    }

    public async Task<FarmAddBatchResult> AddFarmsFromCoordinatesAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates,
        bool useDefaultTroops = false,
        IProgress<FarmAddProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (_config.IsPrivateServer)
        {
            throw new InvalidOperationException("Travco farm lists are only available on official servers.");
        }

        if (string.IsNullOrWhiteSpace(farmListName))
        {
            throw new InvalidOperationException("Farm list name is required.");
        }

        if (!useDefaultTroops && string.IsNullOrWhiteSpace(troopType))
        {
            throw new InvalidOperationException("Troop type is required.");
        }

        if (!useDefaultTroops && troopCount <= 0)
        {
            throw new InvalidOperationException("Troop count must be greater than 0.");
        }

        if (coordinates.Count <= 0)
        {
            throw new InvalidOperationException("At least one farm coordinate is required.");
        }

        if (requestedCount <= 0)
        {
            throw new InvalidOperationException("Requested farm count must be greater than 0.");
        }

        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        return await AddFarmsFromCoordinatesCoreAsync(
            farmListName,
            troopType,
            troopCount,
            requestedCount,
            coordinates,
            null,
            progress,
            useDefaultTroops,
            cancellationToken);
    }

    private async Task<FarmAddBatchResult> AddFarmsFromCoordinatesCoreAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates,
        IProgress<int>? addedProgress,
        IProgress<FarmAddProgress>? progress,
        bool useDefaultTroops,
        CancellationToken cancellationToken)
    {
        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        var lid = await TryResolveFarmListSlotIdByNameAsync(farmListName, cancellationToken);
        if (string.IsNullOrWhiteSpace(lid))
        {
            throw new InvalidOperationException($"Could not find farm list '{farmListName}' on farm page.");
        }

        var capacityLimit = requestedCount;
        if (!_config.IsPrivateServer)
        {
            var currentFarmCount = await ReadOfficialFarmListFarmCountAsync(lid, cancellationToken);
            if (!currentFarmCount.HasValue)
            {
                throw new InvalidOperationException($"Could not read the current farm count for '{farmListName}'.");
            }

            capacityLimit = Math.Max(0, OfficialFarmListCapacity - currentFarmCount.Value);
            if (capacityLimit <= 0)
            {
                throw new InvalidOperationException($"Farm list '{farmListName}' is full (100/100).");
            }
        }

        var targetAddedCount = Math.Min(requestedCount, capacityLimit);
        Notify($"Starting add farms batch: target={targetAddedCount}, available={coordinates.Count}.");
        var added = 0;
        var alreadyInList = 0;
        var failed = 0;
        var notFound = 0;
        var attempted = 0;
        var invalidCoordinates = new List<FarmCoordinate>();
        for (var i = 0; i < coordinates.Count && added < targetAddedCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_config.IsPrivateServer)
            {
                var currentFarmCount = await ReadOfficialFarmListFarmCountAsync(lid, cancellationToken);
                if (!currentFarmCount.HasValue)
                {
                    throw new InvalidOperationException($"Could not verify the current farm count for '{farmListName}'.");
                }

                if (currentFarmCount.Value >= OfficialFarmListCapacity)
                {
                    Notify($"[farm-list] '{farmListName}' reached 100/100; stopping before another Add target attempt.");
                    break;
                }
            }

            var coordinate = coordinates[i];
            attempted++;
            var stepPrefix = $"[checked={attempted}, added={added}/{targetAddedCount}]";

            await OpenAddRaidFormAsync(lid, cancellationToken);

            var saveOutcome = await TryFillAddRaidFormAndSaveAsync(
                farmListName,
                troopType.Trim(),
                troopCount,
                coordinate.X,
                coordinate.Y,
                lid,
                useDefaultTroops,
                cancellationToken);

            if (saveOutcome == AddRaidSaveOutcome.Added)
            {
                added++;
                addedProgress?.Report(added);
                Notify($"{stepPrefix} Added farm ({coordinate.X}|{coordinate.Y}) to '{farmListName}'.");
                progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound));
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.AlreadyInList)
            {
                alreadyInList++;
                Notify($"{stepPrefix} Farm ({coordinate.X}|{coordinate.Y}) is already in '{farmListName}' (This village is already in the selected farm list.).");
                progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound));
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.InvalidCoordinates)
            {
                failed++;
                notFound++;
                invalidCoordinates.Add(coordinate);
                Notify($"{stepPrefix} Skipped ({coordinate.X}|{coordinate.Y}): there is no village at these coordinates.");
                progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound));
                continue;
            }

            failed++;
            Notify($"{stepPrefix} Failed to save farm ({coordinate.X}|{coordinate.Y}) in '{farmListName}'.");
            progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound));
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        return new FarmAddBatchResult(
            farmListName,
            requestedCount,
            attempted,
            added,
            alreadyInList,
            failed,
            notFound,
            invalidCoordinates);
    }

    public async Task<int> EnsureNatarFarmCacheAndReturnToFarmListAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        var coordinates = await ReadNatarFarmCoordinatesCachedAsync(cancellationToken, forceRefresh);
        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        return coordinates.Count;
    }

    public async Task<ManualFarmRunResult> StartManualFarmingFromNatarsAsync(
        string troopType,
        long troopCount,
        int troopVariancePercent,
        bool raidAttack,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (string.IsNullOrWhiteSpace(troopType))
        {
            throw new InvalidOperationException("Troop type is required.");
        }

        if (troopCount <= 0)
        {
            throw new InvalidOperationException("Troop count must be greater than 0.");
        }

        var troopIndex = TroopCatalog.ResolveTroopIndex(troopType.Trim());
        if (troopIndex is null)
        {
            throw new InvalidOperationException($"Could not resolve troop slot for '{troopType}'.");
        }

        await EnsureLoggedInAsync();
        var coordinates = await ReadNatarFarmCoordinatesCachedAsync(cancellationToken);
        if (coordinates.Count <= 0)
        {
            throw new InvalidOperationException("Could not read any Natar farm coordinates from Natars profile.");
        }

        var sent = 0;
        var skipped = 0;
        var failed = 0;
        var attempted = 0;
        var consecutiveLowTroopSkips = 0;
        var stoppedByNoTroopsAlarm = false;
        for (var i = 0; i < coordinates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coordinate = coordinates[i];
            if (coordinate.X is null || coordinate.Y is null)
            {
                continue;
            }

            attempted++;
            var stepPrefix = $"[farm-manual] [{attempted}/{coordinates.Count}]";
            await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: attempted > 1);
            var sendResult = await TrySendManualAttackAsync(
                troopType.Trim(),
                troopIndex.Value,
                troopCount,
                troopVariancePercent,
                coordinate.X.Value,
                coordinate.Y.Value,
                raidAttack,
                cancellationToken);

            if (sendResult.Status == ManualAttackSendStatus.Sent)
            {
                consecutiveLowTroopSkips = 0;
                sent++;
                var normalizedVariancePercent = troopVariancePercent switch
                {
                    0 or 5 or 10 or 20 or 50 => troopVariancePercent,
                    _ => 10,
                };
                Notify($"{stepPrefix} Sent {(raidAttack ? "raid" : "normal attack")} to ({coordinate.X}|{coordinate.Y}) with {sendResult.SentTroopCount}/{troopCount}±{normalizedVariancePercent}% {troopType.Trim()} (Available: {FormatLargeCount(sendResult.AvailableTroopCount)}).");
                
                
                continue;
            }

            if (sendResult.Status == ManualAttackSendStatus.SkippedLowTroops)
            {
                skipped++;
                consecutiveLowTroopSkips++;
                Notify($"{stepPrefix} Skipped ({coordinate.X}|{coordinate.Y}). Available {troopType.Trim()}: {FormatLargeCount(sendResult.AvailableTroopCount)}, required minimum: {FormatLargeCount(sendResult.MinimumAcceptedTroopCount)}.");
                if (consecutiveLowTroopSkips >= ManualFarmingMaxConsecutiveLowTroopSkips)
                {
                    Notify($"[farm-manual] stopping — {consecutiveLowTroopSkips} consecutive low-troop skips reached");
                    break;
                }

                continue;
            }

            if (sendResult.Status == ManualAttackSendStatus.StoppedByNoTroopsAlarm)
            {
                stoppedByNoTroopsAlarm = true;
                failed++;
                Notify($"ALARM: manual farming stopped because available {troopType.Trim()} stayed at 0 after {ManualFarmingNoTroopsRetryAttempts} retries.");
                break;
            }

            consecutiveLowTroopSkips = 0;
            failed++;
            Notify($"{stepPrefix} Failed to send {(raidAttack ? "raid" : "normal attack")} to ({coordinate.X}|{coordinate.Y}).");
        }

        return new ManualFarmRunResult(
            coordinates.Count,
            attempted,
            sent,
            skipped,
            failed,
            stoppedByNoTroopsAlarm,
            troopType.Trim(),
            troopCount,
            raidAttack ? "Raid" : "Normal attack");
    }

    private async Task<NatarCoordinateJs?> ReadFirstNatarFarmCoordinateAsync(CancellationToken cancellationToken)
    {
        await GotoAsync(Paths.PlayerProfileNatars, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Natars profile.", cancellationToken);
        await EnsureLoggedInAsync();

        var coordinates = await ReadNatarFarmCoordinatesFromCurrentPageAsync(cancellationToken);
        if (coordinates.Length > 0)
        {
            return coordinates[0];
        }

        await GotoAsync(Paths.Statistics100, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening statistics farms page.", cancellationToken);
        await EnsureLoggedInAsync();

        await _page.EvaluateAsync(
            """
            () => {
              const links = Array.from(document.querySelectorAll('a[href*="spieler.php?uid=3"]'));
              const link = links.find(node => ((node.textContent || '').toLowerCase().includes('natar'))) || links[0];
              if (link) {
                link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              }
            }
            """);
        await Task.Delay(Random.Shared.Next(300, 500), cancellationToken); // Random wait
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while navigating to Natars profile.", cancellationToken);

        coordinates = await ReadNatarFarmCoordinatesFromCurrentPageAsync(cancellationToken);
        return coordinates.FirstOrDefault();
    }

    private async Task<List<NatarCoordinateJs>> ReadNatarFarmCoordinatesCachedAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var selectionMode = ResolveNatarVillageSelectionMode();
        var cacheKey = $"{ServerUrl}::{AccountName}::{selectionMode}";
        HashSet<string>? persistedEnabledKeys = null;
        if (!forceRefresh)
        {
            if (_natarFarmCacheStore.TryLoad(AccountName, out var persisted, ServerUrl, selectionMode)
                && persisted is not null
                && persisted.Coordinates.Count > 0)
            {
                var restored = persisted.Coordinates
                    .Where(item => item.Enabled)
                    .Select(item => new NatarCoordinateJs { X = item.X, Y = item.Y, VillageName = item.VillageName })
                    .ToList();
                lock (NatarCacheSync)
                {
                    CachedNatarCoordinatesByAccount[cacheKey] = [.. restored];
                }

                Notify($"[farm-natar] using persisted farms list — {restored.Count} enabled target(s)");
                return restored;
            }

            lock (NatarCacheSync)
            {
                if (CachedNatarCoordinatesByAccount.TryGetValue(cacheKey, out var existing) && existing.Count > 0)
                {
                    Notify($"[farm-natar] using in-memory cached farms list — {existing.Count} target(s)");
                    return [.. existing];
                }
            }
        }
        else if (_natarFarmCacheStore.TryLoad(AccountName, out var persisted, ServerUrl, selectionMode)
            && persisted is not null
            && persisted.Coordinates.Count > 0)
        {
            persistedEnabledKeys = persisted.Coordinates
                .Where(item => item.Enabled)
                .Select(item => NatarFarmCacheStore.BuildCoordinateKey(item.X, item.Y))
                .ToHashSet(StringComparer.Ordinal);
        }

        await GotoAsync(Paths.PlayerProfileNatars, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Natars profile.", cancellationToken);
        await EnsureLoggedInAsync();

        var coordinates = await ReadNatarFarmCoordinatesFromCurrentPageAsync(cancellationToken);
        if (coordinates.Length <= 0)
        {
            await GotoAsync(Paths.Statistics100, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening statistics farms page.", cancellationToken);
            await EnsureLoggedInAsync();
            await _page.EvaluateAsync(
                """
                () => {
                  const links = Array.from(document.querySelectorAll('a[href*="spieler.php?uid=3"]'));
                  const link = links.find(node => ((node.textContent || '').toLowerCase().includes('natar'))) || links[0];
                  if (link) {
                    link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                  }
                }
                """);
            await Task.Delay(Random.Shared.Next(300, 500), cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while navigating to Natars profile.", cancellationToken);
            coordinates = await ReadNatarFarmCoordinatesFromCurrentPageAsync(cancellationToken);
        }

        var cached = coordinates
            .Where(item => item.X.HasValue && item.Y.HasValue)
            .GroupBy(item => $"{item.X}|{item.Y}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.X)
            .ThenBy(item => item.Y)
            .ToList();
        lock (NatarCacheSync)
        {
            CachedNatarCoordinatesByAccount[cacheKey] = [.. cached];
        }

        if (cached.Count > 0)
        {
            var changed = _natarFarmCacheStore.Save(new NatarFarmCacheSnapshot(
                SchemaVersion: 1,
                AnalyzedAtUtc: DateTimeOffset.UtcNow,
                AccountName: AccountName,
                ServerUrl: ServerUrl,
                SelectionMode: selectionMode,
                Coordinates: cached
                    .Where(item => item.X.HasValue && item.Y.HasValue)
                    .Select(item =>
                    {
                        var key = NatarFarmCacheStore.BuildCoordinateKey(item.X!.Value, item.Y!.Value);
                        var enabled = persistedEnabledKeys is null || persistedEnabledKeys.Contains(key);
                        return new NatarFarmCoordinate(item.X!.Value, item.Y!.Value, item.VillageName, enabled);
                    })
                    .ToList()));

            Notify(changed
                ? $"[farm-natar] scanned profile — saved {cached.Count} target(s) to cache"
                : $"[farm-natar] scanned profile — confirmed {cached.Count} existing cached target(s)");
        }
        else
        {
            Notify("[farm-natar] scanned profile but found no targets (empty Natar list?)");
        }

        return cached;
    }

    private async Task<NatarCoordinateJs[]> ReadNatarFarmCoordinatesFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading Natar coordinates.", cancellationToken);
        var includeAllVillages = string.Equals(ResolveNatarVillageSelectionMode(), "all_villages", StringComparison.OrdinalIgnoreCase);
        return await _page.EvaluateAsync<NatarCoordinateJs[]>(
            """
            (includeAllVillages) => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const targetVillageName = 'Natar farm village';
              const parsePair = (text) => {
                const normalized = clean(text);
                if (!normalized) return null;
                const match = normalized.match(/(-?\d+)\s*[|/]\s*(-?\d+)/);
                if (!match) return null;
                const x = Number.parseInt(match[1], 10);
                const y = Number.parseInt(match[2], 10);
                if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
                return { x, y };
              };

              const seen = new Set();
              const rows = [];
              for (const row of document.querySelectorAll('tr, li, article, section, .row')) {
                const hasVillageAnchor = !!row.querySelector('a[href*="karte.php"]');
                if (!hasVillageAnchor) continue;
                const villageNameNode =
                  row.querySelector('td.vil a, .vil a, .village a, a[href*="dorf1.php"], a[href*="newdid="], a[href*="profile.php"]');
                const villageNameFromRow = clean(villageNameNode?.textContent || '');
                const names = Array.from(row.querySelectorAll('a, .name, .village, .vil, td, span'))
                  .map(node => clean(node.textContent || ''))
                  .filter(Boolean);
                const hasTargetVillageName = names.some(text => text === targetVillageName);
                if (!includeAllVillages && !hasTargetVillageName) continue;
                const rowText = clean(row.textContent || '');
                const parsed = parsePair(rowText);
                if (!parsed) continue;
                const key = `${parsed.x}|${parsed.y}`;
                if (seen.has(key)) continue;
                seen.add(key);
                const villageName = villageNameFromRow
                  || (hasTargetVillageName
                    ? targetVillageName
                    : (names.find(text => text !== `${parsed.x}|${parsed.y}` && text !== `${parsed.x}/${parsed.y}`) || ''));
                rows.push({ x: parsed.x, y: parsed.y, villageName });
              }

              if (rows.length > 0) return rows;

              for (const anchor of document.querySelectorAll('a[href*="karte.php"]')) {
                const row = anchor.closest('tr, li, article, section, .row');
                const rowText = clean(row?.textContent || '');
                if (!includeAllVillages && !rowText.includes(targetVillageName)) continue;
                const parsed = parsePair(anchor.textContent || '');
                if (!parsed) continue;
                const key = `${parsed.x}|${parsed.y}`;
                if (seen.has(key)) continue;
                seen.add(key);
                const villageName = includeAllVillages ? (anchor.textContent || '').trim() : targetVillageName;
                rows.push({ x: parsed.x, y: parsed.y, villageName });
              }

              return rows;
            }
            """,
            includeAllVillages);
    }

    private string ResolveNatarVillageSelectionMode()
    {
        return string.Equals(_config.NatarVillageSelection, "all_villages", StringComparison.OrdinalIgnoreCase)
            ? "all_villages"
            : "farm_villages";
    }

    private async Task OpenAddRaidFormAsync(string lid, CancellationToken cancellationToken)
    {
        if (_config.IsPrivateServer)
        {
            await GotoAsync(Paths.FarmListBySlotId(lid), cancellationToken);
        }
        else
        {
            if (!await IsFarmListPageAsync(cancellationToken))
            {
                await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
            }

            var clicked = await _page.EvaluateAsync<bool>(
                """
                (listId) => {
                  const wrapper = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                    .find(node => node.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') === String(listId));
                  const button = wrapper?.querySelector('td.addTarget a, td.addTarget button');
                  if (!button) return false;
                  button.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                  return true;
                }
                """,
                lid);
            if (!clicked)
            {
                throw new InvalidOperationException($"Could not open Add target for farm list id {lid}.");
            }


            // Wait for the Add target form (X/Y inputs) to render. Retry up to 3 times with a 10s
            // timeout each in case the page is slow to mount the dialog.
            const int formRenderAttempts = 3;
            var formRendered = false;
            for (var attempt = 1; attempt <= formRenderAttempts; attempt++)
            {
                try
                {
                    await _page.WaitForFunctionAsync(
                        """
                        () => {
                          const form = document.querySelector('#farmListTargetForm');
                          return !!form?.querySelector('input[name="x"]')
                            && !!form?.querySelector('input[name="y"]');
                        }
                        """,
                        null,
                        new PageWaitForFunctionOptions { Timeout = 10000 });
                    formRendered = true;
                    break;
                }
                catch (TimeoutException)
                {
                    Notify($"[farm-list] Add target dialog for farm list id {lid} not rendered (attempt {attempt}/{formRenderAttempts}).");
                }
            }

            if (!formRendered)
            {
                throw new InvalidOperationException($"Add target dialog for farm list id {lid} did not render after {formRenderAttempts} attempts.");
            }
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Add Raid form.", cancellationToken);
        await EnsureLoggedInAsync();
    }

    private async Task<AddRaidSaveOutcome> TryFillAddRaidFormAndSaveAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int x,
        int y,
        string lid,
        bool useDefaultTroops,
        CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before filling Add Raid form.", cancellationToken);
        var troopIndex = TroopCatalog.ResolveTroopIndex(troopType);
        var filled = await _page.EvaluateAsync<bool>(
            """
            (args) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const norm = (value) => normalize(value).toLowerCase();
              const setValue = (input, value) => {
                if (!input) return false;
                input.focus();
                input.value = String(value);
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
              };

              const root = document.querySelector('#farmListTargetForm') || document;
              const selects = Array.from(root.querySelectorAll('select'));
              const textInputs = Array.from(root.querySelectorAll('input:not([type="hidden"])'));

              const findInput = (patterns, fallback = null) => {
                for (const pattern of patterns) {
                  const candidate = textInputs.find(node => {
                    const id = (node.id || '').toLowerCase();
                    const name = (node.getAttribute('name') || '').toLowerCase();
                    const type = (node.getAttribute('type') || 'text').toLowerCase();
                    if (type !== 'text' && type !== 'number' && type !== '') return false;
                    return id.includes(pattern) || name === pattern || name.includes(pattern);
                  });
                  if (candidate) return candidate;
                }
                return fallback;
              };

              const xInput =
                root.querySelector('input[name="x"], input[name="xCoord"], input[id*="xCoord" i]') ||
                findInput(['xcoord', 'coordx', 'x']);
              const yInput =
                root.querySelector('input[name="y"], input[name="yCoord"], input[id*="yCoord" i]') ||
                findInput(['ycoord', 'coordy', 'y']);
              if (!xInput || !yInput) return false;

              const listSelect = root.querySelector('select[name="listId"]') ||
                selects.find(select => Array.from(select.options || []).some(option => option.value === String(args.lid)));
              if (listSelect && listSelect.value !== String(args.lid)) {
                const option = Array.from(listSelect.options || []).find(opt => opt.value === String(args.lid));
                if (option) {
                  listSelect.value = option.value;
                  listSelect.dispatchEvent(new Event('input', { bubbles: true }));
                  listSelect.dispatchEvent(new Event('change', { bubbles: true }));
                }
              }

              const listSelectByName = selects.find(select => Array.from(select.options || []).some(option => norm(option.textContent || '') === norm(args.farmListName)));
              if (!listSelect && listSelectByName) {
                const option = Array.from(listSelectByName.options || []).find(opt => norm(opt.textContent || '') === norm(args.farmListName));
                if (option) {
                  listSelectByName.value = option.value;
                  listSelectByName.dispatchEvent(new Event('change', { bubbles: true }));
                }
              }

              if (args.pacedCoordinateInputs) {
                return true;
              }

              if (!setValue(xInput, args.x)) return false;
              if (!setValue(yInput, args.y)) return false;

              if (args.useDefaultTroops) {
                return true;
              }

              const troopSelect = selects.find(select => Array.from(select.options || []).some(option => norm(option.textContent || '') === norm(args.troopType)));
              if (troopSelect) {
                const option = Array.from(troopSelect.options || []).find(opt => norm(opt.textContent || '') === norm(args.troopType));
                if (option) {
                  troopSelect.value = option.value;
                  troopSelect.dispatchEvent(new Event('change', { bubbles: true }));
                }
              }

              let countInput = textInputs.find(node => {
                if (node === xInput || node === yInput) return false;
                const id = (node.id || '').toLowerCase();
                const name = (node.getAttribute('name') || '').toLowerCase();
                return (args.troopIndex && (name === `t${args.troopIndex}` || id === `t${args.troopIndex}`))
                  || id.includes('count') || id.includes('amount') || name.includes('count') || name.includes('amount');
              });
              if (!countInput) {
                countInput = textInputs.find(node => node !== xInput && node !== yInput);
              }
              if (!countInput) return false;
              if (!setValue(countInput, args.troopCount)) return false;

              return true;
            }
            """,
            new
            {
                farmListName,
                troopType,
                troopCount,
                troopIndex,
                lid,
                useDefaultTroops,
                pacedCoordinateInputs = !_config.IsPrivateServer,
                x,
                y,
            });

        if (!filled)
        {
            return AddRaidSaveOutcome.Failed;
        }

        if (!_config.IsPrivateServer)
        {
            // Short "reading" pause before touching the row, then type the coordinates like a person
            // (focus, clear, key-by-key) instead of pasting them instantly. See TypeHumanlyAsync.
            await Task.Delay(Random.Shared.Next(200, 400), cancellationToken); // Random wait

            var xInput = _page.Locator(
                "#farmListTargetForm input[name=\"x\"], " +
                "#farmListTargetForm input[name=\"xCoord\"], " +
                "#farmListTargetForm input[id*=\"xCoord\" i]").First;
            await TypeHumanlyAsync(xInput, x.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            Notify($"[farm-list] Add target X filled with {x} for '{farmListName}'.");
            await Task.Delay(Random.Shared.Next(90, 220), cancellationToken); // Random wait

            var yInput = _page.Locator(
                "#farmListTargetForm input[name=\"y\"], " +
                "#farmListTargetForm input[name=\"yCoord\"], " +
                "#farmListTargetForm input[id*=\"yCoord\" i]").First;
            await TypeHumanlyAsync(yInput, y.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            Notify($"[farm-list] Add target Y filled with {y} for '{farmListName}'.");
            await Task.Delay(Random.Shared.Next(90, 220), cancellationToken); // Random wait

            if (useDefaultTroops)
            {
                var validationTriggered = await _page.EvaluateAsync<bool>(
                    """
                    () => {
                      const form = document.querySelector('#farmListTargetForm');
                      if (!form) return false;

                      const active = document.activeElement;
                      if (active instanceof HTMLElement) {
                        active.blur();
                      }

                      const clickTarget = form.querySelector(
                        '.targetSelection, .targetSelectionResultWrapper, .troopSelection, .actionButtons')
                        || form;
                      clickTarget.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
                      clickTarget.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
                      clickTarget.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                      return true;
                    }
                    """);
                if (!validationTriggered)
                {
                    Notify($"[farm-list] Could not trigger Add target validation for ({x}|{y}) in '{farmListName}'.");
                    return AddRaidSaveOutcome.Failed;
                }

                Notify($"[farm-list] Add target validation triggered after coordinates for ({x}|{y}) in '{farmListName}'.");
                await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            }
            else
            {
                if (troopIndex is null)
                {
                    return AddRaidSaveOutcome.Failed;
                }

                var troopInput = _page.Locator(
                    $"#farmListTargetForm input.unitAmount[name=\"t{troopIndex.Value}\"], " +
                    $"#farmListTargetForm input[name=\"t{troopIndex.Value}\"]").First;
                await TypeHumanlyAsync(troopInput, troopCount.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
                await Task.Delay(Random.Shared.Next(90, 220), cancellationToken); // Random wait
            }
        }

        if (!_config.IsPrivateServer)
        {
            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    (lid) => {
                      const form = document.querySelector('#farmListTargetForm');
                      const save = form?.querySelector('button.save, button[type="submit"]');
                      const list = form?.querySelector('select[name="listId"]');
                      const targetError = form?.querySelector(
                        '.targetSelectionResultWrapper.hasError .targetSelectionValidation.show, ' +
                        '.targetSelectionResultWrapper.hasError .customValidationRenderElement');
                      const invalidCoordinates = !!targetError
                        && (targetError.textContent || '').replace(/\s+/g, ' ').trim().length > 0;
                      return invalidCoordinates || (!!save && !save.disabled && (!list || list.value === String(lid)));
                    }
                    """,
                    lid,
                    new PageWaitForFunctionOptions { Timeout = 5000 });
            }
            catch (TimeoutException)
            {
                Notify($"[farm-list] Add target form did not become ready for ({x}|{y}) in '{farmListName}'.");
                return AddRaidSaveOutcome.Failed;
            }
        }

        if (!_config.IsPrivateServer)
        {
            var invalidCoordinates = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const form = document.querySelector('#farmListTargetForm');
                  const targetError = form?.querySelector(
                    '.targetSelectionResultWrapper.hasError .targetSelectionValidation.show, ' +
                    '.targetSelectionResultWrapper.hasError .customValidationRenderElement');
                  return !!targetError
                    && (targetError.textContent || '').replace(/\s+/g, ' ').trim().length > 0;
                }
                """);
            if (invalidCoordinates)
            {
                await _page.EvaluateAsync(
                    """
                    () => {
                      const form = document.querySelector('#farmListTargetForm');
                      const cancel = form?.querySelector('.actionButtons button.cancel')
                        || document.querySelector('.dialogCancelButton');
                      if (cancel) cancel.click();
                    }
                    """);
                try
                {
                    await _page.WaitForFunctionAsync(
                        "() => !document.querySelector('#farmListTargetForm')",
                        null,
                        new PageWaitForFunctionOptions { Timeout = 3000 });
                }
                catch (TimeoutException)
                {
                    Notify($"[farm-list] Invalid Add target dialog remained open after skipping ({x}|{y}).");
                }

                return AddRaidSaveOutcome.InvalidCoordinates;
            }
        }

        await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
        var clicked = await _page.EvaluateAsync<bool>(
            """
            () => {
              const form = document.querySelector('#farmListTargetForm') || document;
              const save = form.querySelector('button.save, button[type="submit"]');
              if (!save || save.disabled) return false;
              save.click();
              return true;
            }
            """);
        if (!clicked)
        {
            Notify($"[farm-list] Add target Save button was unavailable for ({x}|{y}) in '{farmListName}'.");
            return AddRaidSaveOutcome.Failed;
        }

        if (!_config.IsPrivateServer)
        {
            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    () => {
                      const body = (document.body?.innerText || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      if (body.includes('already in the selected farm list')) return true;
                      return !document.querySelector('#farmListTargetForm');
                    }
                    """,
                    null,
                    new PageWaitForFunctionOptions { Timeout = 5000 });
            }
            catch (TimeoutException)
            {
                Notify($"[farm-list] Add target save did not finish for ({x}|{y}).");
                return AddRaidSaveOutcome.Failed;
            }
        }
        else
        {
            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared after saving new raid.", cancellationToken);
        await EnsureLoggedInAsync();

        var saveState = await _page.EvaluateAsync<string>(
            """
            () => {
              const text = (document.body?.innerText || '').replace(/\s+/g, ' ').trim();
              if (text.includes('This village is already in the selected farm list.')) return 'already';
              if (text.toLowerCase().includes('success') || text.toLowerCase().includes('saved')) return 'saved';
              return 'unknown';
            }
            """);

        if (string.Equals(saveState, "already", StringComparison.OrdinalIgnoreCase))
        {
            return AddRaidSaveOutcome.AlreadyInList;
        }

        return AddRaidSaveOutcome.Added;
    }

    private enum AddRaidSaveOutcome
    {
        Failed = 0,
        Added = 1,
        AlreadyInList = 2,
        InvalidCoordinates = 3,
    }

}