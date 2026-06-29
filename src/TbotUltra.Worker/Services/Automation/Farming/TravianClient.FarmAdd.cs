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
        var attempted = 0;
        var invalidCoordinates = new List<FarmCoordinate>();
        for (var i = 0; i < coordinates.Count && added < targetAddedCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                coordinate.RequireUnoccupiedOasis,
                cancellationToken);

            if (saveOutcome == AddRaidSaveOutcome.Added)
            {
                added++;
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

            if (saveOutcome == AddRaidSaveOutcome.OccupiedOasisSkipped)
            {
                Notify($"{stepPrefix} Skipped occupied oasis ({coordinate.X}|{coordinate.Y}) for '{farmListName}'.");
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

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Add target form.", cancellationToken);
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
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before filling Add target form.", cancellationToken);
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
        await TypeHumanlyAsync(xInput, x.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        Notify($"[farm-list] Add target X filled with {x} for '{farmListName}'.");
        await Task.Delay(Random.Shared.Next(90, 220), cancellationToken);

        var yInput = _page.Locator(
            "#farmListTargetForm input[name=\"y\"], " +
            "#farmListTargetForm input[name=\"yCoord\"], " +
            "#farmListTargetForm input[id*=\"yCoord\" i]").First;
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
        await DelayBeforeClickAsync(cancellationToken);

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
            await DismissAddTargetDialogAsync($"skipping invalid coordinates ({x}|{y})");
            return AddRaidSaveOutcome.InvalidCoordinates;
        }

        if (requireUnoccupiedOasis)
        {
            var ownerText = await _page.EvaluateAsync<string?>(
                """
                () => {
                  const form = document.querySelector('#farmListTargetForm');
                  const owner = form?.querySelector(
                    '.targetSelectionResultWrapper .targetWrapper .player a[href*="/profile"], ' +
                    '.targetSelectionResultWrapper .targetWrapper .player a[href*="spieler.php"], ' +
                    '.targetSelectionResultWrapper .targetWrapper .player');
                  const text = (owner?.textContent || '').replace(/\s+/g, ' ').trim();
                  return text.length > 0 ? text.replace(/^Player:\s*/i, '').trim() : null;
                }
                """);
            if (!string.IsNullOrWhiteSpace(ownerText))
            {
                Notify($"[farm-list] Occupied oasis ({x}|{y}) owned by '{ownerText}' skipped before Save.");
                await DismissAddTargetDialogAsync($"skipping occupied oasis ({x}|{y})");
                return AddRaidSaveOutcome.OccupiedOasisSkipped;
            }
        }

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

        await PauseForManualStepIfVisibleAsync("Manual verification appeared after saving new target.", cancellationToken);
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

    private async Task DismissAddTargetDialogAsync(string reason)
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
    }
}
