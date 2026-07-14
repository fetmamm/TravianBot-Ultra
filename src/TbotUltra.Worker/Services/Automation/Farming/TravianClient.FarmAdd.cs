using Microsoft.Playwright;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
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

        var currentFarmCount = await ReadOfficialFarmListFarmCountAsync(lid, cancellationToken);
        if (!currentFarmCount.HasValue)
        {
            throw new InvalidOperationException($"Could not read the current farm count for '{farmListName}'.");
        }

        var capacityLimit = Math.Max(0, OfficialFarmListCapacity - currentFarmCount.Value);
        if (capacityLimit <= 0)
        {
            throw new InvalidOperationException($"Farm list '{farmListName}' is full (100/100).");
        }

        var targetAddedCount = Math.Min(requestedCount, capacityLimit);
        Notify($"Starting add farms batch: target={targetAddedCount}, available={coordinates.Count}.");
        var added = 0;
        var alreadyInList = 0;
        var failed = 0;
        var notFound = 0;
        var occupiedSkipped = 0;
        var attempted = 0;
        var invalidCoordinates = new List<FarmCoordinate>();
        // When a coordinate is skipped before Save the Add-target form is left open (see TryFillAddRaidFormAndSaveAsync),
        // so the next coordinate is typed straight into it instead of closing + reopening the dialog every miss.
        var reuseOpenForm = false;
        for (var i = 0; i < coordinates.Count && added < targetAddedCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // On a reused (already-open) form the farm count cannot have changed since the last miss, so skip
            // the re-read — it would otherwise query the list sitting behind the open dialog.
            if (!reuseOpenForm)
            {
                currentFarmCount = await ReadOfficialFarmListFarmCountAsync(lid, cancellationToken);
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

            if (!reuseOpenForm)
            {
                await OpenAddRaidFormAsync(lid, cancellationToken);
            }

            var saveOutcome = await TryFillAddRaidFormAndSaveAsync(
                farmListName,
                troopType.Trim(),
                troopCount,
                coordinate.X,
                coordinate.Y,
                lid,
                useDefaultTroops,
                coordinate.RequireUnoccupiedOasis,
                cancellationToken);

            // Saved/already/failed outcomes close or abandon the form. Pre-save skips keep it open for the next attempt.
            reuseOpenForm = false;

            if (saveOutcome == AddRaidSaveOutcome.Added)
            {
                added++;
                Notify($"{stepPrefix} Added farm ({coordinate.X}|{coordinate.Y}) to '{farmListName}'.");
                progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound, OccupiedOasisSkippedCount: occupiedSkipped));
                continue;
            }

            if (saveOutcome is AddRaidSaveOutcome.AlreadyInList or AddRaidSaveOutcome.AlreadyInListFormOpen)
            {
                alreadyInList++;
                Notify($"{stepPrefix} Farm ({coordinate.X}|{coordinate.Y}) is already in '{farmListName}' (This village is already in the selected farm list.).");
                progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound, OccupiedOasisSkippedCount: occupiedSkipped));
                reuseOpenForm = saveOutcome == AddRaidSaveOutcome.AlreadyInListFormOpen;
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.InvalidCoordinates)
            {
                failed++;
                notFound++;
                invalidCoordinates.Add(coordinate);
                Notify($"{stepPrefix} Skipped ({coordinate.X}|{coordinate.Y}): there is no village at these coordinates.");
                progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound, coordinate, occupiedSkipped));
                // Keep the open form and type the next coordinate straight into it.
                reuseOpenForm = true;
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.OccupiedOasisSkipped)
            {
                occupiedSkipped++;
                Notify($"{stepPrefix} Skipped occupied oasis ({coordinate.X}|{coordinate.Y}) for '{farmListName}'.");
                progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound, OccupiedOasisSkippedCount: occupiedSkipped));
                // Keep the open form and type the next coordinate straight into it.
                reuseOpenForm = true;
                continue;
            }

            failed++;
            Notify($"{stepPrefix} Failed to save farm ({coordinate.X}|{coordinate.Y}) in '{farmListName}'.");
            progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound, OccupiedOasisSkippedCount: occupiedSkipped));
            await DismissAddTargetDialogAsync($"failed save for ({coordinate.X}|{coordinate.Y})");
        }

        if (reuseOpenForm)
        {
            await DismissAddTargetDialogAsync("finishing add farms batch");
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
            invalidCoordinates,
            occupiedSkipped);
    }

    private async Task OpenAddRaidFormAsync(string lid, CancellationToken cancellationToken)
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
                    new PageWaitForFunctionOptions { Timeout = 15000 });
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
        bool requireUnoccupiedOasis,
        CancellationToken cancellationToken)
    {
        var troopIndex = TroopCatalog.ResolveTroopIndex(troopType);
        var filled = await _page.EvaluateAsync<bool>(
            """
            (args) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const norm = (value) => normalize(value).toLowerCase();

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

              return true;
            }
            """,
            new
            {
                farmListName,
                lid,
            });

        if (!filled)
        {
            return AddRaidSaveOutcome.Failed;
        }

        await Task.Delay(Random.Shared.Next(200, 400), cancellationToken);

        // The Add-target box is now open. Pause with the action-pacing click delay before typing the
        // coordinates, so the bot doesn't fill the freshly-loaded form instantly (more human-like).
        await DelayBeforeClickAsync(cancellationToken, "add farm: enter coordinates");

        var xInput = _page.Locator(
            "#farmListTargetForm input[name=\"x\"], " +
            "#farmListTargetForm input[name=\"xCoord\"], " +
            "#farmListTargetForm input[id*=\"xCoord\" i]").First;
        var yInput = _page.Locator(
            "#farmListTargetForm input[name=\"y\"], " +
            "#farmListTargetForm input[name=\"yCoord\"], " +
            "#farmListTargetForm input[id*=\"yCoord\" i]").First;

        await TypeHumanlyAsync(xInput, x.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        Notify($"[farm-list] Add target X filled with {x} for '{farmListName}'.");
        await Task.Delay(Random.Shared.Next(90, 220), cancellationToken);

        await TypeHumanlyAsync(yInput, y.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        Notify($"[farm-list] Add target Y filled with {y} for '{farmListName}'.");
        await Task.Delay(Random.Shared.Next(90, 220), cancellationToken);

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
        // Functional wait (not just pacing): after the coordinates are entered Travian runs an async
        // lookup that loads the target's village name / owner. Without this pause the form is read before
        // that lookup resolves, so every target wrongly comes back as "no village at these coordinates".
        await DelayBeforeClickAsync(cancellationToken, "add farm: load target data");

        if (!useDefaultTroops)
        {
            if (troopIndex is null)
            {
                return AddRaidSaveOutcome.Failed;
            }

            var troopInput = _page.Locator(
                $"#farmListTargetForm input.unitAmount[name=\"t{troopIndex.Value}\"], " +
                $"#farmListTargetForm input[name=\"t{troopIndex.Value}\"]").First;
            await TypeHumanlyAsync(troopInput, troopCount.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            await Task.Delay(Random.Shared.Next(90, 220), cancellationToken);
        }

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
            // Leave the Add-target form open so the caller can type the next coordinate straight into it
            // (TypeHumanlyAsync clears each field first), instead of closing and reopening for every miss.
            return AddRaidSaveOutcome.InvalidCoordinates;
        }

        if (requireUnoccupiedOasis)
        {
            var ownerText = await _page.EvaluateAsync<string?>(
                """
                () => {
                  const form = document.querySelector('#farmListTargetForm');
                  const player = form?.querySelector('.targetSelectionResultWrapper .targetWrapper .player');
                  const owner =
                    player?.querySelector('a[href*="/profile"], a[href*="spieler.php"]') ||
                    player?.querySelector('.value') ||
                    player;
                  const text = (owner?.textContent || '').replace(/\s+/g, ' ').trim().replace(/^Player:\s*/i, '').trim();
                  if (!text || text === '-' || text === '–' || text === '—') return null;
                  return text;
                }
                """);
            if (!string.IsNullOrWhiteSpace(ownerText))
            {
                Notify($"[farm-list] Occupied oasis ({x}|{y}) owned by '{ownerText}' skipped before Save.");
                return AddRaidSaveOutcome.OccupiedOasisSkipped;
            }
        }

        var stateBeforeSave = await ReadFarmListTargetStateAsync(lid, x, y, cancellationToken);
        await DelayBeforeClickAsync(cancellationToken, "add farm: save target");
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

        try
        {
            await _page.WaitForFunctionAsync(
                """
                (args) => {
                  const clean = (value) => (value || '')
                    .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                    .replace(/\s+/g, ' ')
                    .trim();
                  const readWrapper = () => Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                    .find(node => node.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') === String(args.lid));
                  const readCount = (wrapper) => {
                    const match = clean(wrapper?.querySelector('td.addTarget')?.textContent).match(/(\d+)\s*\/\s*(\d+)/);
                    return match ? Number(match[1]) : null;
                  };
                  const hasCoordinate = (wrapper) => {
                    if (!wrapper) return false;
                    for (const link of wrapper.querySelectorAll('tbody tr.slot td.target a[href*="karte.php"]')) {
                      const href = link.getAttribute('href') || '';
                      try {
                        const url = new URL(href, document.baseURI);
                        if (Number(url.searchParams.get('x')) === Number(args.x) &&
                            Number(url.searchParams.get('y')) === Number(args.y)) {
                          return true;
                        }
                      } catch (_) {
                        const match = href.match(/[?&]x=(-?\d+).*?[?&]y=(-?\d+)/i);
                        if (match && Number(match[1]) === Number(args.x) && Number(match[2]) === Number(args.y)) {
                          return true;
                        }
                      }
                    }
                    return false;
                  };
                  const bodyText = clean(document.body?.innerText).toLowerCase();
                  const duplicateDialog = Array.from(document.querySelectorAll('.confirmationDialog, #dialogContent, .dialogWrapper'))
                    .some(node => clean(node.textContent).toLowerCase().includes('already on the farm list'));
                  if (duplicateDialog ||
                      bodyText.includes('already on the farm list') ||
                      bodyText.includes('already in the selected farm list')) return true;

                  const form = document.querySelector('#farmListTargetForm');
                  const targetError = form?.querySelector(
                    '.targetSelectionResultWrapper.hasError .targetSelectionValidation.show, ' +
                    '.targetSelectionResultWrapper.hasError .customValidationRenderElement');
                  if (targetError && clean(targetError.textContent).length > 0) return true;

                  const wrapper = readWrapper();
                  const count = readCount(wrapper);
                  return !form && ((!args.beforeHasCoordinate && hasCoordinate(wrapper)) ||
                    (count !== null && args.beforeCount !== null && count > Number(args.beforeCount)));
                }
                """,
                new { lid, x, y, beforeCount = stateBeforeSave.Count, beforeHasCoordinate = stateBeforeSave.HasCoordinate },
                new PageWaitForFunctionOptions { Timeout = 7000 });
        }
        catch (TimeoutException)
        {
            Notify($"[farm-list] Add target save did not finish for ({x}|{y}).");
            return AddRaidSaveOutcome.Failed;
        }

        await EnsureLoggedInAsync();

        var saveState = await ReadAddTargetSaveStateAsync(lid, x, y, stateBeforeSave, cancellationToken);

        if (string.Equals(saveState, "duplicate", StringComparison.OrdinalIgnoreCase))
        {
            var formStillOpen = await DismissDuplicateConfirmationAsync(cancellationToken);
            return formStillOpen
                ? AddRaidSaveOutcome.AlreadyInListFormOpen
                : AddRaidSaveOutcome.AlreadyInList;
        }

        if (string.Equals(saveState, "saved", StringComparison.OrdinalIgnoreCase))
        {
            return AddRaidSaveOutcome.Added;
        }

        Notify($"[farm-list] Add target save ended in unexpected state '{saveState}' for ({x}|{y}) in '{farmListName}'.");
        return AddRaidSaveOutcome.Failed;
    }

    private async Task<FarmListTargetStateJs> ReadFarmListTargetStateAsync(string lid, int x, int y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<FarmListTargetStateJs>(
            """
            (args) => {
              const clean = (value) => (value || '')
                .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                .replace(/\s+/g, ' ')
                .trim();
              const wrapper = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                .find(node => node.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') === String(args.lid));
              const match = clean(wrapper?.querySelector('td.addTarget')?.textContent).match(/(\d+)\s*\/\s*(\d+)/);
              let hasCoordinate = false;
              if (wrapper) {
                for (const link of wrapper.querySelectorAll('tbody tr.slot td.target a[href*="karte.php"]')) {
                  const href = link.getAttribute('href') || '';
                  try {
                    const url = new URL(href, document.baseURI);
                    hasCoordinate = Number(url.searchParams.get('x')) === Number(args.x) &&
                      Number(url.searchParams.get('y')) === Number(args.y);
                  } catch (_) {
                    const hrefMatch = href.match(/[?&]x=(-?\d+).*?[?&]y=(-?\d+)/i);
                    hasCoordinate = !!hrefMatch &&
                      Number(hrefMatch[1]) === Number(args.x) &&
                      Number(hrefMatch[2]) === Number(args.y);
                  }

                  if (hasCoordinate) break;
                }
              }

              return {
                count: match ? Number(match[1]) : null,
                hasCoordinate
              };
            }
            """,
            new { lid, x, y }).WaitAsync(cancellationToken);
    }

    private async Task<string> ReadAddTargetSaveStateAsync(string lid, int x, int y, FarmListTargetStateJs beforeState, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<string>(
            """
            (args) => {
              const clean = (value) => (value || '')
                .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                .replace(/\s+/g, ' ')
                .trim();
              const wrapper = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                .find(node => node.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') === String(args.lid));
              const countMatch = clean(wrapper?.querySelector('td.addTarget')?.textContent).match(/(\d+)\s*\/\s*(\d+)/);
              const count = countMatch ? Number(countMatch[1]) : null;
              const hasCoordinate = (() => {
                if (!wrapper) return false;
                for (const link of wrapper.querySelectorAll('tbody tr.slot td.target a[href*="karte.php"]')) {
                  const href = link.getAttribute('href') || '';
                  try {
                    const url = new URL(href, document.baseURI);
                    if (Number(url.searchParams.get('x')) === Number(args.x) &&
                        Number(url.searchParams.get('y')) === Number(args.y)) {
                      return true;
                    }
                  } catch (_) {
                    const match = href.match(/[?&]x=(-?\d+).*?[?&]y=(-?\d+)/i);
                    if (match && Number(match[1]) === Number(args.x) && Number(match[2]) === Number(args.y)) {
                      return true;
                    }
                  }
                }
                return false;
              })();
              const bodyText = clean(document.body?.innerText).toLowerCase();
              const duplicateDialog = Array.from(document.querySelectorAll('.confirmationDialog, #dialogContent, .dialogWrapper'))
                .some(node => clean(node.textContent).toLowerCase().includes('already on the farm list'));
              if (duplicateDialog ||
                  bodyText.includes('already on the farm list') ||
                  bodyText.includes('already in the selected farm list')) return 'duplicate';

              const form = document.querySelector('#farmListTargetForm');
              const targetError = form?.querySelector(
                '.targetSelectionResultWrapper.hasError .targetSelectionValidation.show, ' +
                '.targetSelectionResultWrapper.hasError .customValidationRenderElement');
              if (targetError && clean(targetError.textContent).length > 0) return 'invalid';
              if (!form && ((!args.beforeHasCoordinate && hasCoordinate) ||
                  (count !== null && args.beforeCount !== null && count > Number(args.beforeCount)))) return 'saved';
              if (!form) return 'closed_unknown';
              return 'pending';
            }
            """,
            new { lid, x, y, beforeCount = beforeState.Count, beforeHasCoordinate = beforeState.HasCoordinate }).WaitAsync(cancellationToken);
    }

    private async Task<bool> DismissDuplicateConfirmationAsync(CancellationToken cancellationToken)
    {
        var clicked = await _page.EvaluateAsync<bool>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const dialogs = Array.from(document.querySelectorAll('.confirmationDialog, #dialogContent, .dialogWrapper'));
              const dialog = dialogs.find(node => clean(node.textContent).includes('already on the farm list'));
              const cancel = Array.from(dialog?.querySelectorAll('button, .dialogCancelButton') || [])
                .find(node => clean(node.textContent) === 'cancel' || clean(node.getAttribute('class')).includes('cancel'));
              if (!cancel) return false;
              cancel.click();
              return true;
            }
            """).WaitAsync(cancellationToken);

        if (clicked)
        {
            Notify("[farm-list] Duplicate Add target confirmation cancelled; keeping Add target form for next coordinate.");
            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    () => !Array.from(document.querySelectorAll('.confirmationDialog, #dialogContent, .dialogWrapper'))
                      .some(node => (node.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase().includes('already on the farm list'))
                    """,
                    null,
                    new PageWaitForFunctionOptions { Timeout = 3000 });
            }
            catch (TimeoutException)
            {
                Notify("[farm-list] Duplicate Add target confirmation remained open after Cancel.");
            }
        }

        return await IsAddTargetFormOpenAsync(cancellationToken);
    }

    private async Task<bool> IsAddTargetFormOpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            "() => !!document.querySelector('#farmListTargetForm')").WaitAsync(cancellationToken);
    }

    private async Task DismissAddTargetDialogAsync(string reason)
    {
        await _page.EvaluateAsync<bool>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const duplicateDialog = Array.from(document.querySelectorAll('.confirmationDialog, #dialogContent, .dialogWrapper'))
                .find(node => clean(node.textContent).includes('already on the farm list'));
              const duplicateCancel = Array.from(duplicateDialog?.querySelectorAll('button, .dialogCancelButton') || [])
                .find(node => clean(node.textContent) === 'cancel' || clean(node.getAttribute('class')).includes('cancel'));
              if (duplicateCancel) {
                duplicateCancel.click();
                return true;
              }

              const form = document.querySelector('#farmListTargetForm');
              const cancel = form?.querySelector('.actionButtons button.cancel')
                || document.querySelector('.dialogCancelButton');
              if (!cancel) return false;
              cancel.click();
              return true;
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
            Notify($"[farm-list] Add target dialog remained open after {reason}.");
        }
    }

    private enum AddRaidSaveOutcome
    {
        Failed = 0,
        Added = 1,
        AlreadyInList = 2,
        InvalidCoordinates = 3,
        OccupiedOasisSkipped = 4,
        AlreadyInListFormOpen = 5,
    }
}
