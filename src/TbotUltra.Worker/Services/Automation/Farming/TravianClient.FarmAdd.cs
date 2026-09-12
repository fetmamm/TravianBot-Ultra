using Microsoft.Playwright;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int AddTargetLookupMaxAttempts = 2;

    public async Task<FarmAddBatchResult> AddFarmsFromCoordinatesAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates,
        bool useDefaultTroops = false,
        IProgress<FarmAddProgress>? progress = null,
        FarmTargetProtectionContext? protection = null,
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

        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        try
        {
            return await AddFarmsFromCoordinatesCoreAsync(
                farmListName,
                troopType,
                troopCount,
                requestedCount,
                coordinates,
                progress,
                useDefaultTroops,
                protection,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _session.FarmListReloadRequiredBeforeAdd = true;
            Notify("[farm-list] Add farms was cancelled; the next add run will reload Farm Lists before reading targets.");
            throw;
        }
        catch
        {
            _session.FarmListReloadRequiredBeforeAdd = true;
            Notify("[farm-list] Add farms failed; the next add run will reload Farm Lists before reading targets.");
            throw;
        }
    }

    private async Task<FarmAddBatchResult> AddFarmsFromCoordinatesCoreAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates,
        IProgress<FarmAddProgress>? progress,
        bool useDefaultTroops,
        FarmTargetProtectionContext? protection,
        CancellationToken cancellationToken)
    {
        if (_session.FarmListReloadRequiredBeforeAdd)
        {
            Notify("[farm-list] Reloading Farm Lists after an interrupted Add farms run to discard stale page state.");
            await ReloadOrGotoAsync(Paths.RallyPointFarmLists, cancellationToken);
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);
            await WaitForOfficialFarmListRenderAsync(cancellationToken);
            _session.FarmListReloadRequiredBeforeAdd = false;
        }
        else
        {
            await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        }

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
        var excludedPlayers = 0;
        var excludedAlliances = 0;
        var identityUnavailable = 0;
        var attempted = 0;
        var invalidCoordinates = new List<FarmCoordinate>();
        void ReportProgress(FarmCoordinate? invalidCoordinate = null) => progress?.Report(new FarmAddProgress(
            farmListName,
            attempted,
            targetAddedCount,
            added,
            notFound,
            invalidCoordinate,
            occupiedSkipped,
            excludedPlayers,
            excludedAlliances,
            identityUnavailable));
        // When a coordinate is skipped before Save the Add-target form is left open (see TryFillAddRaidFormAndSaveAsync),
        // so the next coordinate is typed straight into it instead of closing + reopening the dialog every miss.
        var reuseOpenForm = false;
        var reuseOpenFormAfterInvalidCoordinates = false;
        // Consecutive fill/save exceptions. A single failure is skipped, but a run of them means the session
        // itself is broken, so the batch aborts instead of churning through every remaining candidate.
        var consecutiveFillExceptions = 0;
        for (var i = 0; i < coordinates.Count && added < targetAddedCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The farm count stays authoritative without re-reading the page every loop: it starts from the
            // initial read above and each verified Added below increments it by exactly one. The save
            // confirmation (WaitForFunction + ReadAddTargetSaveStateAsync) is what proves an add really
            // happened, so this 100/100 capacity guard is just as safe while reusing the count we already
            // have instead of an extra round-trip between saving and opening the next Add target form.
            if (!reuseOpenForm && currentFarmCount.Value >= OfficialFarmListCapacity)
            {
                Notify($"[farm-list] '{farmListName}' reached 100/100; stopping before another Add target attempt.");
                break;
            }

            var coordinate = coordinates[i];
            attempted++;
            var stepPrefix = $"[checked={attempted}, added={added}/{targetAddedCount}]";

            if (protection is not null
                && protection.TryGetCachedDecision(coordinate.X, coordinate.Y, out var cachedDecision)
                && cachedDecision is FarmTargetProtectionDecision.ExcludedPlayer or FarmTargetProtectionDecision.ExcludedAlliance)
            {
                if (cachedDecision == FarmTargetProtectionDecision.ExcludedPlayer)
                {
                    excludedPlayers++;
                }
                else
                {
                    excludedAlliances++;
                }

                Notify($"{stepPrefix} Skipped ({coordinate.X}|{coordinate.Y}): cached {cachedDecision} protection decision.");
                ReportProgress();
                continue;
            }

            if (!reuseOpenForm)
            {
                await OpenAddRaidFormAsync(lid, cancellationToken);
            }

            AddRaidSaveOutcome saveOutcome;
            try
            {
                var reuseAfterInvalidCoordinates = reuseOpenFormAfterInvalidCoordinates;
                saveOutcome = AddRaidSaveOutcome.Failed;
                for (var lookupAttempt = 1; lookupAttempt <= AddTargetLookupMaxAttempts; lookupAttempt++)
                {
                    saveOutcome = await TryFillAddRaidFormAndSaveAsync(
                        farmListName,
                        troopType.Trim(),
                        troopCount,
                        coordinate.X,
                        coordinate.Y,
                        lid,
                        useDefaultTroops,
                        coordinate.RequireUnoccupiedOasis,
                        coordinate.IsOasis,
                        reuseAfterInvalidCoordinates: reuseAfterInvalidCoordinates,
                        protection,
                        cancellationToken);
                    if (saveOutcome is not (AddRaidSaveOutcome.LookupTimedOut or AddRaidSaveOutcome.IdentityUnavailable)
                        || lookupAttempt >= AddTargetLookupMaxAttempts)
                    {
                        break;
                    }

                    Notify(
                        $"{stepPrefix} Retrying Add target lookup for ({coordinate.X}|{coordinate.Y}) " +
                        $"after Travian left the form unresolved (attempt {lookupAttempt + 1}/{AddTargetLookupMaxAttempts}).");
                    await DismissAddTargetDialogAsync($"retrying unresolved lookup for ({coordinate.X}|{coordinate.Y})");
                    await Task.Delay(Random.Shared.Next(1200, 2500), cancellationToken);
                    await OpenAddRaidFormAsync(lid, cancellationToken);
                    reuseAfterInvalidCoordinates = false;
                }

                consecutiveFillExceptions = 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // One target failing to fill/save (e.g. an overlay intercepting the coordinate click) must not
                // abort the whole batch: close whatever is open so the next open starts clean, count this one
                // as failed, and continue. Too many in a row means the session is broken, so bail out then.
                failed++;
                reuseOpenForm = false;
                reuseOpenFormAfterInvalidCoordinates = false;
                Notify($"{stepPrefix} Add target for ({coordinate.X}|{coordinate.Y}) failed and was skipped: {ex.Message}");
                ReportProgress();
                await CloseAnyOpenAddTargetDialogAsync(cancellationToken);
                if (++consecutiveFillExceptions >= 5)
                {
                    throw new InvalidOperationException(
                        $"Add farms aborted after {consecutiveFillExceptions} consecutive Add target failures.", ex);
                }

                continue;
            }

            // Saved/already/failed outcomes close or abandon the form. Pre-save skips keep it open for the next attempt.
            reuseOpenForm = false;
            reuseOpenFormAfterInvalidCoordinates = false;

            if (saveOutcome == AddRaidSaveOutcome.Added)
            {
                added++;
                // Carry the confirmed add forward instead of re-reading the page: a verified save added
                // exactly one farm, so the maintained count stays correct for the next capacity check.
                currentFarmCount = currentFarmCount.Value + 1;
                Notify($"{stepPrefix} Added farm ({coordinate.X}|{coordinate.Y}) to '{farmListName}'.");
                ReportProgress();
                continue;
            }

            if (saveOutcome is AddRaidSaveOutcome.AlreadyInList or AddRaidSaveOutcome.AlreadyInListFormOpen)
            {
                alreadyInList++;
                Notify($"{stepPrefix} Farm ({coordinate.X}|{coordinate.Y}) is already in '{farmListName}' (This village is already in the selected farm list.).");
                ReportProgress();
                reuseOpenForm = saveOutcome == AddRaidSaveOutcome.AlreadyInListFormOpen;
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.InvalidCoordinates)
            {
                failed++;
                notFound++;
                invalidCoordinates.Add(coordinate);
                Notify($"{stepPrefix} Skipped ({coordinate.X}|{coordinate.Y}): there is no village at these coordinates.");
                ReportProgress(coordinate);
                // Keep the open form and type the next coordinate straight into it.
                reuseOpenForm = true;
                reuseOpenFormAfterInvalidCoordinates = true;
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.OccupiedOasisSkipped)
            {
                occupiedSkipped++;
                Notify($"{stepPrefix} Skipped occupied oasis ({coordinate.X}|{coordinate.Y}) for '{farmListName}'.");
                ReportProgress();
                // Keep the open form and type the next coordinate straight into it.
                reuseOpenForm = true;
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.ExcludedPlayer)
            {
                excludedPlayers++;
                Notify($"{stepPrefix} Skipped ({coordinate.X}|{coordinate.Y}): player is protected by the Add farms filters.");
                ReportProgress();
                reuseOpenForm = true;
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.ExcludedAlliance)
            {
                excludedAlliances++;
                Notify($"{stepPrefix} Skipped ({coordinate.X}|{coordinate.Y}): alliance is protected by the Add farms filters.");
                ReportProgress();
                reuseOpenForm = true;
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.IdentityUnavailable)
            {
                failed++;
                identityUnavailable++;
                Notify(
                    $"{stepPrefix} Skipped ({coordinate.X}|{coordinate.Y}): target identity was unavailable " +
                    $"after {AddTargetLookupMaxAttempts} attempts; Save was not attempted.");
                ReportProgress();
                await DismissAddTargetDialogAsync($"unresolved target identity for ({coordinate.X}|{coordinate.Y})");
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.LookupTimedOut)
            {
                failed++;
                Notify(
                    $"{stepPrefix} Failed to validate farm ({coordinate.X}|{coordinate.Y}) in '{farmListName}' " +
                    $"after {AddTargetLookupMaxAttempts} attempts because Travian left the Add target form unresolved; " +
                    "Save was not attempted.");
                ReportProgress();
                await DismissAddTargetDialogAsync($"unresolved lookup for ({coordinate.X}|{coordinate.Y})");
                continue;
            }

            failed++;
            Notify($"{stepPrefix} Failed to save farm ({coordinate.X}|{coordinate.Y}) in '{farmListName}'.");
            ReportProgress();
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
            occupiedSkipped,
            excludedPlayers,
            excludedAlliances,
            identityUnavailable);
    }

    private async Task OpenAddRaidFormAsync(string lid, CancellationToken cancellationToken)
    {
        if (!await IsFarmListPageAsync(cancellationToken))
        {
            await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        }

        // Never stack Add-target dialogs. A previous run that was canceled or failed mid-form can leave a
        // dialog open; the dispatch below fires even behind an overlay, so it would open a SECOND dialog and
        // the top #dialogOverlay then intercepts every click on the new form's inputs (the coordinate click
        // times out). Close any lingering dialog first so exactly one is ever open.
        await CloseAnyOpenAddTargetDialogAsync(cancellationToken);

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

        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
    }

    // Closes any Add-target dialog / modal overlay that is already open, so opening a new one can never stack
    // a second dialog on top of it. A leftover dialog (e.g. after a canceled or failed run) otherwise makes
    // the next open produce two stacked dialogs whose top #dialogOverlay intercepts every click on the form's
    // inputs, timing out the coordinate field. Safe to call when nothing is open — it no-ops.
    private async Task CloseAnyOpenAddTargetDialogAsync(CancellationToken cancellationToken)
    {
        // One dismiss per iteration collapses one layer of a (usually single) leftover dialog; the loop
        // handles the rare case where a previous failure stacked more than one. Reuses the existing dismiss
        // path so no new raw DOM click is introduced.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anyOpen = await _page.EvaluateAsync<bool>(
                """
                () => !!document.querySelector('#farmListTargetForm')
                  || Array.from(document.querySelectorAll('.dialogOverlay.dialogVisible'))
                      .some(node => node.getClientRects().length > 0)
                """).WaitAsync(cancellationToken);
            if (!anyOpen)
            {
                return;
            }

            Notify($"[farm-list] closing a lingering Add target dialog before opening a new one (attempt {attempt}/3).");
            await DismissAddTargetDialogAsync($"clearing a lingering dialog before opening a new Add target (attempt {attempt})");
        }

        Notify("[farm-list] a lingering Add target dialog could not be fully closed before opening a new one.");
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
        bool isOasis,
        bool reuseAfterInvalidCoordinates,
        FarmTargetProtectionContext? protection,
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

        if (!reuseAfterInvalidCoordinates)
        {
            await Task.Delay(Random.Shared.Next(200, 400), cancellationToken);

            // The Add-target box is now open. Pause with the action-pacing click delay before typing the
            // coordinates, so the bot doesn't fill the freshly-loaded form instantly (more human-like).
            await DelayBeforeClickAsync(cancellationToken, "add farm: enter coordinates");
        }

        if (!await FillAndVerifyFarmCoordinatesAsync(x, y, farmListName, cancellationToken))
        {
            return AddRaidSaveOutcome.Failed;
        }

        var validationTriggered = await _page.EvaluateAsync<bool>(
            """
            () => {
              const form = document.querySelector('#farmListTargetForm');
              if (!form) return false;

              const active = document.activeElement;
              if (active instanceof HTMLElement) {
                active.blur();
              }

              // Neutralise any previous target's resolved state so the post-coordinate wait reacts only to
              // the NEW lookup: disable Save and clear the stale error. Travian re-enables Save / re-flags
              // the error once its async lookup resolves for the freshly entered coordinates.
              const staleSave = form.querySelector('button.save, button[type="submit"]');
              if (staleSave) staleSave.disabled = true;
              const staleWrapper = form.querySelector('.targetSelectionResultWrapper');
              if (staleWrapper) staleWrapper.classList.remove('hasError');

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
        // Functional wait (not pacing): after the coordinates are entered Travian runs an async lookup that
        // loads the target's village/owner, briefly showing "no village" before it resolves. Proceed as soon
        // as that lookup resolves (Save re-enabled = valid target, or the error persists = truly no village)
        // instead of blocking on the full click-pacing, so a high click-pacing setting no longer slows every
        // add. A short settle lets the lookup start after the state was neutralised above.
        await Task.Delay(Random.Shared.Next(200, 350), cancellationToken);
        await WaitForAddTargetLookupResolvedAsync(cancellationToken);

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
            return AddRaidSaveOutcome.LookupTimedOut;
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

        FarmTargetIdentity? targetIdentity = null;
        if (protection is not null
            && (!protection.TryGetCachedDecision(x, y, out var cachedDecision)
                || cachedDecision != FarmTargetProtectionDecision.Allowed))
        {
            targetIdentity = await ReadAddTargetIdentityAsync(cancellationToken);
            var decision = protection.EvaluateAndCache(x, y, isOasis, targetIdentity);
            if (decision == FarmTargetProtectionDecision.IdentityUnavailable)
            {
                Notify($"[farm-list] Target identity was unavailable for ({x}|{y}) in '{farmListName}'.");
                return AddRaidSaveOutcome.IdentityUnavailable;
            }

            if (decision == FarmTargetProtectionDecision.ExcludedPlayer)
            {
                Notify($"[farm-list] Protected player '{targetIdentity.PlayerName}' at ({x}|{y}) skipped before Save.");
                return AddRaidSaveOutcome.ExcludedPlayer;
            }

            if (decision == FarmTargetProtectionDecision.ExcludedAlliance)
            {
                Notify($"[farm-list] Protected alliance '{targetIdentity.Alliance}' at ({x}|{y}) skipped before Save.");
                return AddRaidSaveOutcome.ExcludedAlliance;
            }
        }

        if (requireUnoccupiedOasis)
        {
            targetIdentity ??= await ReadAddTargetIdentityAsync(cancellationToken);
            var ownerText = targetIdentity.PlayerName;
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

        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

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

    private async Task<FarmTargetIdentity> ReadAddTargetIdentityAsync(CancellationToken cancellationToken)
    {
        return await _page.EvaluateAsync<FarmTargetIdentity>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const readValue = (node, label) => {
                const preferred = node?.querySelector('a, .value') || node;
                const text = clean(preferred?.textContent)
                  .replace(new RegExp(`^${label}:\\s*`, 'i'), '')
                  .trim();
                return !text || text === '-' || text === '–' || text === '—' ? null : text;
              };
              const wrapper = document.querySelector(
                '#farmListTargetForm .targetSelectionResultWrapper .targetWrapper');
              const player = wrapper?.querySelector('.player');
              const alliance = wrapper?.querySelector('.alliance');
              return {
                isResolved: !!wrapper && !!player && !!alliance,
                playerName: readValue(player, 'Player'),
                alliance: readValue(alliance, 'Alliance')
              };
            }
            """).WaitAsync(cancellationToken)
            ?? new FarmTargetIdentity(false, null, null);
    }

    // Reused Add-target forms can restore the previous React-controlled value immediately after a normal
    // clear. Type through the traced input path, then read both fresh fields back as one operation. Never
    // continue to target validation unless the form contains exactly the requested pair.
    private async Task<bool> FillAndVerifyFarmCoordinatesAsync(
        int x,
        int y,
        string farmListName,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xInput = _page.Locator(
                "#farmListTargetForm input[name=\"x\"], " +
                "#farmListTargetForm input[name=\"xCoord\"], " +
                "#farmListTargetForm input[id*=\"xCoord\" i]").First;
            var yInput = _page.Locator(
                "#farmListTargetForm input[name=\"y\"], " +
                "#farmListTargetForm input[name=\"yCoord\"], " +
                "#farmListTargetForm input[id*=\"yCoord\" i]").First;

            var expectedX = x.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var expectedY = y.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await TypeHumanlyAsync(xInput, expectedX, cancellationToken);
            await Task.Delay(Random.Shared.Next(90, 220), cancellationToken);
            await TypeHumanlyAsync(yInput, expectedY, cancellationToken);

            // Give React one turn to commit its controlled state, then query fresh elements in case the
            // coordinate inputs were re-rendered by either input event.
            await Task.Delay(60, cancellationToken);
            xInput = _page.Locator(
                "#farmListTargetForm input[name=\"x\"], " +
                "#farmListTargetForm input[name=\"xCoord\"], " +
                "#farmListTargetForm input[id*=\"xCoord\" i]").First;
            yInput = _page.Locator(
                "#farmListTargetForm input[name=\"y\"], " +
                "#farmListTargetForm input[name=\"yCoord\"], " +
                "#farmListTargetForm input[id*=\"yCoord\" i]").First;
            var actualX = await xInput.InputValueAsync(new LocatorInputValueOptions { Timeout = _config.TimeoutMs })
                .WaitAsync(cancellationToken);
            var actualY = await yInput.InputValueAsync(new LocatorInputValueOptions { Timeout = _config.TimeoutMs })
                .WaitAsync(cancellationToken);
            var verified = string.Equals(actualX, expectedX, StringComparison.Ordinal)
                && string.Equals(actualY, expectedY, StringComparison.Ordinal);

            if (verified)
            {
                Notify($"[farm-list] Add target coordinates verified as ({x}|{y}) for '{farmListName}'.");
                return true;
            }

            Notify($"[farm-list] coordinate input mismatch for ({x}|{y}) in '{farmListName}' " +
                $"(attempt {attempt}/{maxAttempts}); replacing both fields again.");
        }

        Notify($"[farm-list] Could not verify coordinate inputs for ({x}|{y}) in '{farmListName}'; Save was not attempted.");
        return false;
    }

    // Waits for Travian's async Add-target lookup to resolve after the coordinates were entered. Returns as
    // soon as a valid target is loaded (Save re-enabled without an error) or the error state has persisted
    // long enough to be a real "no village" result rather than the transient pre-lookup flash. Event-driven,
    // so it never blocks on the configured click-pacing; bounded by a timeout after which the caller's own
    // Save-ready check (WaitForFunctionAsync below) and invalid-coordinate probe make the final decision.
    private async Task WaitForAddTargetLookupResolvedAsync(CancellationToken cancellationToken)
    {
        const int timeoutMs = 5000;
        const int pollIntervalMs = 60;
        var stableError = TimeSpan.FromMilliseconds(300);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        DateTime? errorSince = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await _page.EvaluateAsync<string>(
                """
                () => {
                  const form = document.querySelector('#farmListTargetForm');
                  if (!form) return 'gone';
                  const wrapper = form.querySelector('.targetSelectionResultWrapper');
                  const save = form.querySelector('button.save, button[type="submit"]');
                  const saveReady = !!save && !save.disabled;
                  const errorEl = wrapper?.querySelector('.targetSelectionValidation.show, .customValidationRenderElement');
                  const hasError = !!(wrapper && wrapper.classList.contains('hasError'))
                    && ((errorEl?.textContent || '').replace(/\s+/g, ' ').trim().length > 0);
                  if (saveReady && !hasError) return 'valid';
                  if (hasError) return 'error';
                  return 'pending';
                }
                """).WaitAsync(cancellationToken);

            if (state is "valid" or "gone")
            {
                return;
            }

            if (state == "error")
            {
                errorSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - errorSince.Value >= stableError)
                {
                    return;
                }
            }
            else
            {
                errorSince = null;
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }
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
        LookupTimedOut = 6,
        ExcludedPlayer = 7,
        ExcludedAlliance = 8,
        IdentityUnavailable = 9,
    }
}
