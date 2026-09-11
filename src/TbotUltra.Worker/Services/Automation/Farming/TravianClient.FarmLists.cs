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

// Farming surface of the TravianClient facade. The interface list is declared
// on this partial to co-locate the contract with the domain it covers.
public sealed partial class TravianClient : IFarmingClient
{
    public async Task<IReadOnlyList<FarmListOverview>> ReadFarmListsOverviewAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var goldClubEnabled = await ReadGoldClubEnabledAsync(cancellationToken);
        if (!goldClubEnabled)
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        // The farm list page is React-rendered, so the wrappers can be missing on the first read
        // right after navigation (a known race that "worked on retry"). Wait for them to render and,
        // on Official, retry the whole open/expand/read once if the page still yielded zero lists.
        const int maxAttempts = 2;
        IReadOnlyList<FarmListOverview> rows = [];
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await EnsureRallyPointAndOpenFarmListPageAsync(
                cancellationToken,
                refreshCurrentPage: attempt == 1);
            await DismissDeactivatedTargetsNoticeAsync(cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            var confirmedEmpty = await WaitForFarmListsRenderedAsync(cancellationToken);
            await EnsureOfficialFarmListsExpandedAsync(cancellationToken);
            rows = await ReadFarmListsFromCurrentPageAsync(cancellationToken);
            if (confirmedEmpty || rows.Count > 0 || attempt == maxAttempts)
            {
                break;
            }

            Notify($"[farm-list] no farm lists read on attempt {attempt}/{maxAttempts}; reopening the farm page and retrying.");
            await Task.Delay(Random.Shared.Next(600, 800), cancellationToken); // Random wait
        }

        Notify($"[farm-list] read {rows.Count} farm list(s) from rally point");
        return rows;
    }

    // Waits for either list wrappers or Travian's explicit zero-count marker. Missing wrappers
    // alone are not enough to prove that React finished rendering an empty account.
    private async Task<bool> WaitForFarmListsRenderedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                    const root = document.querySelector('#rallyPointFarmList');
                    if (!root) return false;
                    if (root.querySelector('.farmListWrapper')) return true;

                    const count = root.querySelector('.farmListCount .nominator')?.textContent ?? '';
                    return count.replace(/[^0-9]/g, '') === '0';
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 8000 }).WaitAsync(cancellationToken);

            var confirmedEmpty = await _page.EvaluateAsync<bool>(
                """
                () => {
                    const root = document.querySelector('#rallyPointFarmList');
                    const count = root?.querySelector('.farmListCount .nominator')?.textContent ?? '';
                    return !!root
                        && !root.querySelector('.farmListWrapper')
                        && count.replace(/[^0-9]/g, '') === '0';
                }
                """).WaitAsync(cancellationToken);
            if (confirmedEmpty)
            {
                Notify("[farm-list] page rendered with an explicit zero farm-list count.");
            }

            return confirmedEmpty;
        }
        catch (TimeoutException)
        {
            Notify("[farm-list] neither farm list wrappers nor an explicit zero count rendered within 8 seconds.");
        }
        catch (PlaywrightException ex)
        {
            Notify($"[farm-list] waiting for farm list wrappers failed: {ex.Message}");
        }

        return false;
    }

    public async Task<int?> SendFarmListNowAsync(string farmListName, CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (string.IsNullOrWhiteSpace(farmListName))
        {
            throw new InvalidOperationException("Farm list name is required.");
        }

        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        await DismissDeactivatedTargetsNoticeAsync(cancellationToken);
        await WaitForDispatchLimitToClearAsync(cancellationToken);

        var clicked = await TryClickFarmListSendNowAsync(farmListName, cancellationToken);
        if (!clicked)
        {
            throw new InvalidOperationException($"Could not find clickable Start Raid button for farm list '{farmListName}'.");
        }

        await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
        var remaining = await ReadFarmListTimerSecondsByNameAsync(farmListName, cancellationToken);
        Notify($"[farm-list] '{farmListName}' sent — next ready in {(remaining is > 0 ? TravianParsing.FormatDuration(remaining.Value) : "now")}");
        return remaining;
    }

    public async Task<int> SendAllFarmListsNowAsync(CancellationToken cancellationToken = default)
        => (await SendFarmListsSequentiallyAsync(selectedNames: null, selectedIds: null, throwIfNoneSendable: true, cancellationToken)).SentCount;

    public Task<FarmListSendBatchResult> SendSelectedFarmListsNowAsync(
        IReadOnlyCollection<string> selectedNames,
        IReadOnlyCollection<string> selectedIds,
        CancellationToken cancellationToken = default)
        => SendFarmListsSequentiallyAsync(
            selectedNames: new HashSet<string>(selectedNames ?? [], StringComparer.OrdinalIgnoreCase),
            selectedIds: new HashSet<string>(selectedIds ?? [], StringComparer.OrdinalIgnoreCase),
            throwIfNoneSendable: false,
            cancellationToken);

    // Core sequential send: opens the farm page, resolves every list with an enabled Start button (optionally
    // filtered to the toggled/selected lists), then clicks each Start ONE AT A TIME and waits for that list's
    // "being raided" counter to rise (Travian's live confirmation the raids were dispatched) before the next
    // click. Clicking every list at once — or the single "start all" button — is unsafe: a list can silently
    // fail to send with no per-list feedback. The wait between each click is the "Send farmlists" pacing.
    private async Task<FarmListSendBatchResult> SendFarmListsSequentiallyAsync(
        IReadOnlySet<string>? selectedNames,
        IReadOnlySet<string>? selectedIds,
        bool throwIfNoneSendable,
        CancellationToken cancellationToken)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        await DismissDeactivatedTargetsNoticeAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await WaitForFarmListsRenderedAsync(cancellationToken);
        await WaitForDispatchLimitToClearAsync(cancellationToken);

        var sendable = await ReadSendableFarmListsAsync(cancellationToken);
        if (selectedNames is not null || selectedIds is not null)
        {
            sendable = sendable
                .Where(entry => MatchesFarmListSelection(entry.Lid, entry.Name, selectedNames, selectedIds))
                .ToList();
        }

        if (sendable.Count <= 0)
        {
            if (throwIfNoneSendable)
            {
                throw new InvalidOperationException("No farm lists were found for start-all farming.");
            }

            Notify("[farm-list] send: none of the selected farm lists is ready to send right now.");
            return new FarmListSendBatchResult([], []);
        }

        Notify($"[farm-list] send: sending {sendable.Count} list(s) one at a time.");
        var attempted = sendable
            .Select(entry => new FarmListSendEntry(entry.Name, entry.Lid))
            .ToList();
        var sent = new List<FarmListSendEntry>();
        for (var index = 0; index < sendable.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = sendable[index];

            // "Send farmlists" action pacing: a small randomized wait between each list's Start click.
            if (index > 0)
            {
                await ApplyPacingDelayAsync(
                    _config.FarmListStepDelayMinSeconds,
                    _config.FarmListStepDelayMaxSeconds,
                    "send-farmlists-pacing",
                    $"Send farmlists: before '{entry.Name}'",
                    cancellationToken);
            }

            var beingRaidedBefore = await ReadFarmListBeingRaidedCountAsync(entry.Lid, cancellationToken);
            var clicked = await TryClickFarmListStartByLidAsync(entry.Lid, entry.Name, cancellationToken);
            if (!clicked)
            {
                Notify($"[farm-list] send: could not click Start for '{entry.Name}' (lid {entry.Lid}); skipping.");
                continue;
            }

            if (await WaitForFarmListRaidConfirmedAsync(entry.Lid, beingRaidedBefore, cancellationToken))
            {
                sent.Add(new FarmListSendEntry(entry.Name, entry.Lid));
                Notify($"[farm-list] send: '{entry.Name}' dispatched (confirmed — being raided rose from {beingRaidedBefore}).");
            }
            else
            {
                Notify($"[farm-list] send: '{entry.Name}' Start click not confirmed as raided within timeout; continuing.");
            }
        }

        Notify($"[farm-list] send completed: {sent.Count}/{sendable.Count} list(s) confirmed dispatched.");
        return new FarmListSendBatchResult(attempted, sent);
    }

    /// <summary>
    /// Uses Travian's stable list id whenever the selection has ids. Names are only a legacy fallback:
    /// two villages may legitimately contain identically named lists.
    /// </summary>
    internal static bool MatchesFarmListSelection(
        string? listId,
        string? listName,
        IReadOnlySet<string>? selectedNames,
        IReadOnlySet<string>? selectedIds)
    {
        if (selectedIds is { Count: > 0 })
        {
            return !string.IsNullOrWhiteSpace(listId) && selectedIds.Contains(listId);
        }

        return selectedNames is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(listName)
            && selectedNames.Contains(listName);
    }

    // Sends every farm list in one action by clicking Travian's own "Start all farm lists" button
    // (button.startAllFarmLists) — the fast "send everything at once" path, in contrast to the sequential
    // per-list send above. Returns the number of lists that had an enabled Start button when clicked.
    public async Task<int> SendAllFarmListsViaStartAllButtonAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        await DismissDeactivatedTargetsNoticeAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await WaitForFarmListsRenderedAsync(cancellationToken);
        await WaitForDispatchLimitToClearAsync(cancellationToken);

        var sendable = await ReadSendableFarmListsAsync(cancellationToken);
        var clicked = await TryRealClickFarmButtonAsync(
            _page.Locator("#rallyPointFarmList button.startAllFarmLists, button.startAllFarmLists").First,
            JsDispatchStartAllFarmListsAsync,
            "start all farm lists",
            cancellationToken);
        if (!clicked)
        {
            throw new InvalidOperationException("Could not click Travian's 'Start all farm lists' button.");
        }

        Notify($"[farm-list] clicked 'Start all farm lists' ({sendable.Count} list(s) had an enabled Start button).");
        return sendable.Count;
    }

    // Synthetic-dispatch fallback for the start-all button, used only when the real Playwright click is not
    // actionable (covered/detached), so the send never silently regresses.
    private async Task<bool> JsDispatchStartAllFarmListsAsync()
    {
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const allButton = document.querySelector('#rallyPointFarmList button.startAllFarmLists, button.startAllFarmLists');
              if (!allButton) return false;
              const cls = (allButton.getAttribute('class') || '').toLowerCase();
              if (allButton.disabled || allButton.getAttribute('disabled') !== null || cls.includes('disabled')) return false;
              allButton.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
              allButton.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
              allButton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """);
    }

    // Reads the lid + name of every farm list whose Start button is currently enabled — the lists that a
    // sequential send should actually send. Disabled buttons (empty list or on cooldown) are excluded.
    private async Task<IReadOnlyList<(string Lid, string Name)>> ReadSendableFarmListsAsync(CancellationToken cancellationToken)
    {
        var entries = await _page.EvaluateAsync<SendableFarmListJs[]>(
            """
            () => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const isDisabled = (node) => {
                const cls = (node?.getAttribute('class') || '').toLowerCase();
                return !node || node.disabled || node.getAttribute('disabled') !== null || cls.includes('disabled');
              };
              const out = [];
              for (const wrapper of document.querySelectorAll('#rallyPointFarmList .farmListWrapper')) {
                const button = wrapper.querySelector('button.startFarmList');
                if (isDisabled(button)) continue;
                const lid =
                  wrapper.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') ||
                  wrapper.querySelector('[data-farm-list-id]')?.getAttribute('data-farm-list-id') || '';
                if (!lid) continue;
                out.push({ lid, name: normalize(wrapper.querySelector('.farmListName .name')?.textContent) || 'Farm list' });
              }
              return out;
            }
            """).WaitAsync(cancellationToken);

        return (entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Lid))
            .Select(entry => (entry.Lid!.Trim(), string.IsNullOrWhiteSpace(entry.Name) ? "Farm list" : entry.Name!.Trim()))
            .ToList();
    }

    // Reads the "being raided" numerator ("N/M being raided") from a list's status, used as the before/after
    // signal that its Start click actually dispatched raids. Returns 0 when the list/status is not present.
    private async Task<int> ReadFarmListBeingRaidedCountAsync(string lid, CancellationToken cancellationToken)
    {
        return await _page.EvaluateAsync<int>(
            """
            (listId) => {
              const clean = (value) => (value || '')
                .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                .replace(/\s+/g, ' ')
                .trim();
              const wrapper = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                .find(node => node.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') === String(listId));
              const match = clean(wrapper?.querySelector('.farmListStatus')?.textContent).match(/(\d+)\s*\/\s*(\d+)/);
              return match ? Number(match[1]) : 0;
            }
            """,
            lid).WaitAsync(cancellationToken);
    }

    // Real, trusted click of a single list's Start button, resolved by its stable lid. Falls back to the
    // name-based synthetic-dispatch path only when the real click is not actionable, matching the other sends.
    private async Task<bool> TryClickFarmListStartByLidAsync(string lid, string name, CancellationToken cancellationToken)
    {
        var button = _page
            .Locator($"#rallyPointFarmList .farmListWrapper:has(.dragAndDrop[data-list='{lid}']) button.startFarmList")
            .First;
        return await TryRealClickFarmButtonAsync(
            button,
            () => JsDispatchFarmListSendNowAsync(name),
            $"send farm list '{name}'",
            cancellationToken);
    }

    // Waits for Travian's live confirmation that a list's raids were dispatched: its "being raided" count
    // rises above the pre-click value, or its Start button becomes disabled (nothing left ready to send).
    // Bounded so a list that shows no visible change never blocks the rest of the sequential send.
    private async Task<bool> WaitForFarmListRaidConfirmedAsync(string lid, int beingRaidedBefore, CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                """
                ([listId, before]) => {
                  const clean = (value) => (value || '')
                    .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                    .replace(/\s+/g, ' ')
                    .trim();
                  const wrapper = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                    .find(node => node.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') === String(listId));
                  if (!wrapper) return false;
                  const match = clean(wrapper.querySelector('.farmListStatus')?.textContent).match(/(\d+)\s*\/\s*(\d+)/);
                  const nowRaided = match ? Number(match[1]) : 0;
                  if (nowRaided > before) return true;
                  const button = wrapper.querySelector('button.startFarmList');
                  const cls = (button?.getAttribute('class') || '').toLowerCase();
                  return !button || button.disabled || button.getAttribute('disabled') !== null || cls.includes('disabled');
                }
                """,
                new object[] { lid, beingRaidedBefore },
                new PageWaitForFunctionOptions { Timeout = 5000 }).WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    public async Task<FarmListLossDeactivationResult> DeactivateFarmListLossTargetsAsync(
        bool includeUnoccupiedOasis,
        CancellationToken cancellationToken = default)
    {
        return await DeactivateRequestedFarmListLossTargetsAsync(
            new FarmListLossHandlingRequest(includeUnoccupiedOasis, false, string.Empty, string.Empty, string.Empty),
            cancellationToken);
    }

    private async Task<FarmListLossDeactivationResult> DeactivateRequestedFarmListLossTargetsAsync(
        FarmListLossHandlingRequest request,
        CancellationToken cancellationToken)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        await DismissDeactivatedTargetsNoticeAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await WaitForFarmListsRenderedAsync(cancellationToken);
        await EnsureOfficialFarmListsExpandedAsync(cancellationToken);

        var initialRows = await ReadFarmListLossRowsFromCurrentPageAsync(cancellationToken);
        var lossRows = initialRows.Where(row => MatchesRequestedFarmListLossColor(row, request)).ToList();
        var skippedOasisRows = lossRows.Count(row =>
            !request.IncludeUnoccupiedOasis && FarmListLossStateClassifier.IsUnoccupiedOasis(row.TargetName));
        var unknownRaidClasses = initialRows
            .Where(row => !string.IsNullOrWhiteSpace(row.RaidClass))
            .Where(row => row.RaidClass!.Contains("attack_", StringComparison.OrdinalIgnoreCase))
            .Where(row => FarmListLossStateClassifier.Classify(row.RaidClass) == FarmListLossState.Unknown)
            .Select(row => row.RaidClass!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        if (unknownRaidClasses.Count > 0)
        {
            Notify($"[farm-list] red/yellow scan saw unknown raid state class(es): {string.Join(", ", unknownRaidClasses)}");
        }

        Notify($"[farm-list] {DescribeLossColors(request.LossColors)} loss scan found {lossRows.Count} active row(s); skipped oasis={skippedOasisRows}; includeOasis={request.IncludeUnoccupiedOasis}.");

        var deactivated = 0;
        for (var attempt = 1; attempt <= MaxFarmsPerFarmList * 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = attempt == 1
                ? initialRows
                : await ReadFarmListLossRowsFromCurrentPageAsync(cancellationToken);
            var candidate = rows.FirstOrDefault(row => IsRequestedFarmListLossRow(row, request));
            if (candidate is null)
            {
                break;
            }

            var clicked = await TryDeactivateFarmListLossRowAsync(candidate, cancellationToken);
            if (!clicked)
            {
                Notify($"[farm-list] could not deactivate loss row target='{candidate.TargetName}' slot='{candidate.SlotId}' list='{candidate.ListName}'.");
                break;
            }

            deactivated++;
            Notify($"[farm-list] deactivated loss row target='{candidate.TargetName}' slot='{candidate.SlotId}' list='{candidate.ListName}' state='{candidate.RaidClass}'.");
            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
        }

        Notify($"[farm-list] {DescribeLossColors(request.LossColors)} loss deactivation done: found={lossRows.Count}, deactivated={deactivated}, skippedOasis={skippedOasisRows}.");
        return new FarmListLossDeactivationResult(lossRows.Count, deactivated, skippedOasisRows, LossColors: request.LossColors);
    }

    public async Task<FarmListLossDeactivationResult> HandleFarmListLossTargetsAsync(
        FarmListLossHandlingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.MoveLosses)
        {
            return await DeactivateRequestedFarmListLossTargetsAsync(request, cancellationToken);
        }

        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        await DismissDeactivatedTargetsNoticeAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await WaitForFarmListsRenderedAsync(cancellationToken);
        await EnsureOfficialFarmListsExpandedAsync(cancellationToken);

        LossDestinationResolution destination;
        try
        {
            destination = await ResolveLossDestinationAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify($"ALARM: [farm-list] loss destination could not be prepared: {ex.Message}. Falling back to deactivation only.");
            var fallback = await DeactivateRequestedFarmListLossTargetsAsync(request with { MoveLosses = false }, cancellationToken);
            return fallback with { MoveFailures = fallback.RowsDeactivated };
        }

        var initialRows = await ReadFarmListLossRowsFromCurrentPageAsync(cancellationToken);
        var lossRows = initialRows.Where(row => MatchesRequestedFarmListLossColor(row, request)).ToList();
        var skippedOasisRows = lossRows.Count(row =>
            !request.IncludeUnoccupiedOasis && FarmListLossStateClassifier.IsUnoccupiedOasis(row.TargetName));
        var deactivated = 0;
        var moved = 0;
        var moveFailures = 0;
        var ignoredRows = new HashSet<string>(StringComparer.Ordinal);
        string? unavailableDestinationReason = null;
        var handledTargets = 0;
        var maxTargets = request.MaxTargets is > 0 ? request.MaxTargets.Value : int.MaxValue;
        Notify($"[farm-list] combined loss handling started: found={lossRows.Count}; destination='{destination.Name}' ({destination.Id}).");

        for (var attempt = 1; attempt <= MaxFarmsPerFarmList * 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = attempt == 1
                ? initialRows
                : await ReadFarmListLossRowsFromCurrentPageAsync(cancellationToken);
            var candidate = rows.FirstOrDefault(row =>
                IsRequestedFarmListLossRow(row, request)
                && !ignoredRows.Contains(FarmListLossRowKey(row)));
            if (candidate is null)
            {
                break;
            }

            var isOasis = FarmListLossStateClassifier.IsUnoccupiedOasis(candidate.TargetName);
            if (isOasis)
            {
                if (!request.IncludeUnoccupiedOasis)
                {
                    ignoredRows.Add(FarmListLossRowKey(candidate));
                    continue;
                }

                if (await TryDeactivateFarmListLossRowAsync(candidate, cancellationToken))
                {
                    deactivated++;
                    Notify($"[farm-list] deactivated oasis loss target='{candidate.TargetName}' slot='{candidate.SlotId}'; oasis was not moved.");
                }
                else
                {
                    ignoredRows.Add(FarmListLossRowKey(candidate));
                    Notify($"ALARM: [farm-list] could not deactivate oasis loss target='{candidate.TargetName}' slot='{candidate.SlotId}'.");
                }
                continue;
            }

            if (handledTargets >= maxTargets)
            {
                break;
            }
            handledTargets++;

            if (string.Equals(candidate.ListId, destination.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (await TryDeactivateFarmListLossRowAsync(candidate, cancellationToken))
                {
                    deactivated++;
                }
                else
                {
                    ignoredRows.Add(FarmListLossRowKey(candidate));
                    Notify($"ALARM: [farm-list] target='{candidate.TargetName}' is already in destination '{destination.Name}' but could not be deactivated.");
                }
                continue;
            }

            if (unavailableDestinationReason is null && destination.TotalFarmCount >= destination.Capacity)
            {
                try
                {
                    destination = await CreateNextLossDestinationAsync(request, destination, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    unavailableDestinationReason = ex.Message;
                    Notify($"ALARM: [farm-list] loss destination rollover failed: {ex.Message}. Remaining loss targets will only be deactivated.");
                }
            }

            if (unavailableDestinationReason is not null)
            {
                moveFailures++;
                if (await TryDeactivateFarmListLossRowAsync(candidate, cancellationToken))
                {
                    deactivated++;
                }
                else
                {
                    ignoredRows.Add(FarmListLossRowKey(candidate));
                    Notify($"ALARM: [farm-list] fallback deactivation failed target='{candidate.TargetName}' slot='{candidate.SlotId}' after rollover error='{unavailableDestinationReason}'.");
                }
                continue;
            }

            var combinedSucceeded = false;
            for (var moveAttempt = 1; moveAttempt <= 2 && !combinedSucceeded; moveAttempt++)
            {
                combinedSucceeded = await TryMoveAndDeactivateFarmListLossRowAsync(candidate, destination, cancellationToken);
                if (!combinedSucceeded)
                {
                    Notify($"[farm-list] combined move/deactivate retry {moveAttempt}/2 failed target='{candidate.TargetName}' slot='{candidate.SlotId}' destination='{destination.Name}'.");
                    await CloseFarmListSlotDialogIfVisibleAsync(cancellationToken);
                }
            }

            if (combinedSucceeded)
            {
                deactivated++;
                moved++;
                destination = destination with { TotalFarmCount = destination.TotalFarmCount + 1 };
                Notify($"[farm-list] moved and deactivated target='{candidate.TargetName}' slot='{candidate.SlotId}' to '{destination.Name}'.");
                continue;
            }

            moveFailures++;
            var fallbackDeactivated = await TryDeactivateFarmListLossRowAsync(candidate, cancellationToken);
            if (fallbackDeactivated)
            {
                deactivated++;
            }
            else
            {
                ignoredRows.Add(FarmListLossRowKey(candidate));
            }
            Notify($"ALARM: [farm-list] could not move loss target='{candidate.TargetName}' slot='{candidate.SlotId}' to '{destination.Name}' after 2 attempts; fallbackDeactivated={fallbackDeactivated}.");
        }

        Notify($"[farm-list] combined loss handling done: found={lossRows.Count}, deactivated={deactivated}, moved={moved}, moveFailures={moveFailures}, skippedOasis={skippedOasisRows}.");
        return new FarmListLossDeactivationResult(
            lossRows.Count,
            deactivated,
            skippedOasisRows,
            moved,
            moveFailures,
            destination.Id,
            destination.Name,
            destination.Changed,
            request.LossColors);
    }

    private async Task<LossDestinationResolution> ResolveLossDestinationAsync(
        FarmListLossHandlingRequest request,
        CancellationToken cancellationToken)
    {
        var overview = await ReadFarmListsFromCurrentPageAsync(cancellationToken);
        var destination = overview.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(request.DestinationListId)
                && string.Equals(item.ListId, request.DestinationListId, StringComparison.OrdinalIgnoreCase))
            ?? overview.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(request.DestinationListName)
                && string.Equals(item.Name, request.DestinationListName, StringComparison.OrdinalIgnoreCase));
        if (destination is null)
        {
            if (request.CreateTemplate is null || string.IsNullOrWhiteSpace(request.DestinationListName))
            {
                throw new InvalidOperationException("The configured loss destination list is missing and no create template is available.");
            }

            await CreateFarmListsAsync(
                request.CreateTemplate with { Names = [request.DestinationListName.Trim()] },
                cancellationToken: cancellationToken);
            await EnsureOfficialFarmListsExpandedAsync(cancellationToken);
            overview = await ReadFarmListsFromCurrentPageAsync(cancellationToken);
            destination = overview.FirstOrDefault(item =>
                string.Equals(item.Name, request.DestinationListName, StringComparison.OrdinalIgnoreCase));
            if (destination is null)
            {
                throw new InvalidOperationException($"Recreated loss destination '{request.DestinationListName}' was not found.");
            }
        }

        var resolved = ToLossDestination(destination,
            !string.Equals(destination.ListId, request.DestinationListId, StringComparison.OrdinalIgnoreCase));
        if (resolved.TotalFarmCount >= resolved.Capacity)
        {
            return await CreateNextLossDestinationAsync(request, resolved, cancellationToken);
        }

        return resolved;
    }

    private async Task<LossDestinationResolution> CreateNextLossDestinationAsync(
        FarmListLossHandlingRequest request,
        LossDestinationResolution current,
        CancellationToken cancellationToken)
    {
        if (request.CreateTemplate is null)
        {
            throw new InvalidOperationException($"Loss destination '{current.Name}' is full and no create template is available.");
        }

        var overview = await ReadFarmListsFromCurrentPageAsync(cancellationToken);
        var baseName = string.IsNullOrWhiteSpace(request.DestinationBaseName)
            ? current.Name
            : request.DestinationBaseName.Trim();
        var nextName = FarmLossListNaming.NextAvailable(baseName, overview.Select(item => item.Name));
        Notify($"[farm-list] loss destination '{current.Name}' is full; creating rollover '{nextName}'.");
        await CreateFarmListsAsync(
            request.CreateTemplate with { Names = [nextName] },
            cancellationToken: cancellationToken);
        await EnsureOfficialFarmListsExpandedAsync(cancellationToken);
        overview = await ReadFarmListsFromCurrentPageAsync(cancellationToken);
        var created = overview.FirstOrDefault(item => string.Equals(item.Name, nextName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Rollover loss destination '{nextName}' was not found after creation.");
        return ToLossDestination(created, changed: true);
    }

    private static LossDestinationResolution ToLossDestination(FarmListOverview item, bool changed)
    {
        if (string.IsNullOrWhiteSpace(item.ListId))
        {
            throw new InvalidOperationException($"Loss destination '{item.Name}' has no stable list id.");
        }

        return new LossDestinationResolution(
            item.ListId.Trim(),
            item.Name.Trim(),
            Math.Max(0, item.TotalFarmCount),
            item.Capacity is > 0 ? item.Capacity.Value : MaxFarmsPerFarmList,
            changed);
    }

    private async Task<bool> TryMoveAndDeactivateFarmListLossRowAsync(
        FarmListLossRowJs row,
        LossDestinationResolution destination,
        CancellationToken cancellationToken)
    {
        var editEntry = await TryOpenFarmListEditMenuAsync(row, cancellationToken);
        if (editEntry is null)
        {
            return false;
        }

        if (!await TryRealClickFarmButtonAsync(
                editEntry,
                JsClickVisibleFarmListEditEntryAsync,
                $"edit loss row slot '{row.SlotId}'",
                cancellationToken))
        {
            return false;
        }

        var dialog = _page.Locator(".dialog.basic.slotDialog:visible").First;
        try
        {
            await dialog.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = _config.TimeoutMs,
            }).WaitAsync(cancellationToken);
            var listSelect = dialog.Locator("select[name='listId']").First;
            await SelectFarmListOptionAsync(listSelect, destination.Id, "move loss target: destination list", cancellationToken);
            var deactivate = dialog.Locator("input[name='isActive'][type='checkbox']").First;
            if (!await deactivate.IsCheckedAsync())
            {
                if (!await TryRealClickFarmButtonAsync(
                        deactivate,
                        () => Task.FromResult(false),
                        "move loss target: deactivate checkbox",
                        cancellationToken))
                {
                    return false;
                }

                if (!await deactivate.IsCheckedAsync())
                {
                    Notify($"[farm-list] deactivate checkbox did not become checked for slot '{row.SlotId}'.");
                    return false;
                }
            }

            var save = dialog.Locator("button.save[type='submit'], button.save").First;
            if (!await TryRealClickFarmButtonAsync(save, () => Task.FromResult(false), $"save moved loss row slot '{row.SlotId}'", cancellationToken))
            {
                return false;
            }

            return await WaitForFarmListMoveWithDuplicateOverrideAsync(row, destination, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify($"[farm-list] combined move/deactivate did not confirm target='{row.TargetName}' slot='{row.SlotId}': {ex.Message}");
            return false;
        }
    }

    private async Task<ILocator?> TryOpenFarmListEditMenuAsync(
        FarmListLossRowJs row,
        CancellationToken cancellationToken)
    {
        var editEntry = _page
            .Locator(".contextMenu.from.defaultButtons:visible button.entry.edit[title='Edit target']")
            .First;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var menuTriggered = attempt == 1
                ? await TryRealClickFarmButtonAsync(
                    ResolveFarmListLossRowLocator(row)
                        .Locator("td.openContextMenu a, td.openContextMenu button, td.openContextMenu")
                        .First,
                    () => JsOpenFarmListRowMenuAsync(row),
                    $"open loss-row menu for combined move slot '{row.SlotId}'",
                    cancellationToken)
                : await JsOpenFarmListRowMenuAsync(row);
            if (!menuTriggered)
            {
                Notify($"[farm-list] combined move: menu trigger failed for slot '{row.SlotId}' attempt {attempt}/2.");
                continue;
            }

            try
            {
                await editEntry.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = Math.Min(_config.TimeoutMs, 3000),
                }).WaitAsync(cancellationToken);
                Notify($"[farm-list] combined move: verified menu for slot '{row.SlotId}' on attempt {attempt}/2; selecting Edit target.");
                return editEntry;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException ex)
            {
                Notify($"[farm-list] combined move: Edit target timed out for slot '{row.SlotId}' attempt {attempt}/2: {ex.Message}");
            }
            catch (PlaywrightException ex)
            {
                Notify($"[farm-list] combined move: Edit target was not actionable for slot '{row.SlotId}' attempt {attempt}/2: {ex.Message}");
            }
        }

        return null;
    }

    // Wait for the ordinary completed move OR Travian's duplicate-target confirmation. Both are
    // observed in the same bounded loop, so a normal move is not delayed while a late dialog can
    // still appear and be confirmed before the overall move-verification deadline expires.
    private async Task<bool> WaitForFarmListMoveWithDuplicateOverrideAsync(
        FarmListLossRowJs row,
        LossDestinationResolution destination,
        CancellationToken cancellationToken)
    {
        const string duplicateMessage = "The target is already on the farm list. Override existing farm list target?";
        var confirmation = _page
            .Locator(".dialogContainer:visible .confirmationDialogContent")
            .Filter(new LocatorFilterOptions { HasTextString = duplicateMessage })
            .First;
        var duplicateConfirmed = false;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    (candidate) => {
                      const duplicateShown = Array.from(document.querySelectorAll('.dialogContainer .confirmationDialogContent'))
                        .some(dialog => dialog.closest('.dialogContainer')?.getClientRects().length > 0
                          && (dialog.textContent || '').includes(candidate.duplicateMessage));
                      if (duplicateShown) return true;
                      const wrapper = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                        .find(item => item.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') === candidate.destinationId);
                      const input = wrapper?.querySelector(`tr.slot input[data-slot-id="${CSS.escape(candidate.slotId)}"]`);
                      const movedRow = input?.closest('tr.slot');
                      return !!movedRow && movedRow.classList.contains('disabled');
                    }
                    """,
                    new
                    {
                        slotId = row.SlotId ?? string.Empty,
                        destinationId = destination.Id,
                        duplicateMessage,
                    },
                    new PageWaitForFunctionOptions { Timeout = 5000 }).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                continue;
            }

            if (await IsFarmListMoveConfirmedOnCurrentPageAsync(row, destination))
            {
                return true;
            }

            if (await confirmation.CountAsync() > 0 && await confirmation.IsVisibleAsync())
            {
                if (duplicateConfirmed)
                {
                    Notify($"[farm-list] duplicate target override is still closing for slot '{row.SlotId}'; waiting without clicking it again.");
                    try
                    {
                        await confirmation.WaitForAsync(new LocatorWaitForOptions
                        {
                            State = WaitForSelectorState.Hidden,
                            Timeout = 3000,
                        }).WaitAsync(cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                        // The final refresh below is authoritative when the React dialog remains stale.
                    }
                    catch (PlaywrightException)
                    {
                        // The final refresh below is authoritative when the React dialog is detached mid-wait.
                    }
                    continue;
                }

                var confirmButton = confirmation
                    .Locator("button.textButtonV2.buttonFramed.rectangle.withText.green")
                    .First;
                if (!await TryRealClickFarmButtonAsync(
                        confirmButton,
                        () => Task.FromResult(false),
                        $"confirm duplicate farm-list target override for slot '{row.SlotId}'",
                        cancellationToken))
                {
                    Notify($"[farm-list] duplicate target override could not be confirmed target='{row.TargetName}' slot='{row.SlotId}'.");
                    return false;
                }
                duplicateConfirmed = true;
                Notify($"[farm-list] confirmed duplicate target override target='{row.TargetName}' slot='{row.SlotId}'.");
                try
                {
                    await confirmation.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Hidden,
                        Timeout = 3000,
                    }).WaitAsync(cancellationToken);
                }
                catch (TimeoutException)
                {
                    Notify($"[farm-list] duplicate target override dialog remained visible for slot '{row.SlotId}'; continuing observation without another click.");
                }
                catch (PlaywrightException ex)
                {
                    Notify($"[farm-list] duplicate target override dialog changed while closing for slot '{row.SlotId}': {ex.Message}");
                }
                continue;
            }
        }

        return await VerifyFarmListMoveAfterRefreshAsync(row, destination, cancellationToken);
    }

    private async Task<bool> VerifyFarmListMoveAfterRefreshAsync(
        FarmListLossRowJs row,
        LossDestinationResolution destination,
        CancellationToken cancellationToken)
    {
        Notify($"[farm-list] move confirmation remained ambiguous for target='{row.TargetName}' slot='{row.SlotId}'; refreshing once before any mutation retry.");
        await ReloadOrGotoAsync(Paths.RallyPointFarmLists, cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await WaitForFarmListsRenderedAsync(cancellationToken);
        await EnsureOfficialFarmListsExpandedAsync(cancellationToken);
        var confirmed = await IsFarmListMoveConfirmedOnCurrentPageAsync(row, destination);
        Notify(confirmed
            ? $"[farm-list] move confirmed after refresh target='{row.TargetName}' slot='{row.SlotId}' destination='{destination.Name}'."
            : $"[farm-list] move still unconfirmed after refresh target='{row.TargetName}' slot='{row.SlotId}' destination='{destination.Name}'.");
        return confirmed;
    }

    private async Task<bool> IsFarmListMoveConfirmedOnCurrentPageAsync(
        FarmListLossRowJs row,
        LossDestinationResolution destination)
    {
        return await _page.EvaluateAsync<bool>(
            """
            (candidate) => {
              const wrapper = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                .find(item => item.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') === candidate.destinationId);
              const input = wrapper?.querySelector(`tr.slot input[data-slot-id="${CSS.escape(candidate.slotId)}"]`);
              const movedRow = input?.closest('tr.slot');
              return !!movedRow && movedRow.classList.contains('disabled');
            }
            """,
            new
            {
                slotId = row.SlotId ?? string.Empty,
                destinationId = destination.Id,
            });
    }

    private ILocator ResolveFarmListLossRowLocator(FarmListLossRowJs row)
    {
        var rows = _page.Locator("#rallyPointFarmList tr.slot, tr.slot");
        return !string.IsNullOrWhiteSpace(row.SlotId) && row.SlotId.All(char.IsAsciiDigit)
            ? _page.Locator($"#rallyPointFarmList tr.slot:has(input[data-slot-id='{row.SlotId}']), tr.slot:has(input[data-slot-id='{row.SlotId}'])").First
            : rows.Nth(row.RowIndex);
    }

    private async Task<bool> JsClickVisibleFarmListEditEntryAsync()
    {
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const clean = value => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const entries = Array.from(document.querySelectorAll('button.entry.edit[title="Edit target"]'));
              const entry = entries.find(node => node.getClientRects().length > 0 && clean(node.textContent).includes('edit'));
              if (!entry) return false;
              entry.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
              entry.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
              entry.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """);
    }

    private async Task CloseFarmListSlotDialogIfVisibleAsync(CancellationToken cancellationToken)
    {
        var cancel = _page.Locator(".dialog.basic.slotDialog:visible button.cancel").First;
        if (await cancel.CountAsync() > 0 && await cancel.IsVisibleAsync())
        {
            await TryRealClickFarmButtonAsync(cancel, () => Task.FromResult(false), "cancel failed loss-row edit", cancellationToken);
        }
    }

    private async Task SelectFarmListOptionAsync(
        ILocator select,
        string value,
        string reason,
        CancellationToken cancellationToken)
    {
        Notify($"[farm-list:verbose] selecting option value='{value}' reason='{reason}'.");
        await select.SelectOptionAsync(value).WaitAsync(cancellationToken);
    }

    private sealed record LossDestinationResolution(
        string Id,
        string Name,
        int TotalFarmCount,
        int Capacity,
        bool Changed);

    private async Task DismissDeactivatedTargetsNoticeAsync(CancellationToken cancellationToken)
    {
        var notice = _page.Locator("#rallyPointFarmList .noticeBox.deactivatedTargets").First;
        if (await notice.CountAsync() == 0 || !await notice.IsVisibleAsync())
        {
            return;
        }

        var closeButton = notice.Locator("svg.close").First;
        var clicked = await TryRealClickFarmButtonAsync(
            closeButton,
            () => Task.FromResult(false),
            "dismiss deactivated-targets notice",
            cancellationToken);
        if (!clicked)
        {
            Notify("[farm-list] could not dismiss the deactivated-targets notice; continuing.");
            return;
        }

        try
        {
            await notice.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = _config.TimeoutMs,
            }).WaitAsync(cancellationToken);
            Notify("[farm-list] dismissed the deactivated-targets notice.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlaywrightException ex)
        {
            Notify($"[farm-list] deactivated-targets notice remained visible after close: {ex.Message}");
        }
    }

    private async Task<int?> ReadOfficialFarmListFarmCountAsync(string lid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<int?>(
            """
            (listId) => {
              const clean = (value) => (value || '')
                .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                .replace(/\s+/g, ' ')
                .trim();
              const wrapper = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                .find(node => node.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') === String(listId));
              const match = clean(wrapper?.querySelector('td.addTarget')?.textContent).match(/(\d+)\s*\/\s*(\d+)/);
              return match ? Number(match[1]) : null;
            }
            """,
            lid);
    }

    private async Task EnsureRallyPointAndOpenFarmListPageAsync(
        CancellationToken cancellationToken,
        bool refreshCurrentPage = false)
    {
        if (!refreshCurrentPage && await CanReuseCurrentFarmListPageAsync(cancellationToken))
        {
            Notify("[farm-list:verbose] reusing the current hydrated farm list page.");
            return;
        }

        if (refreshCurrentPage)
        {
            Notify("[farm-list] refreshing Farm Lists before reading current values.");
            await ReloadOrGotoAsync(Paths.RallyPointFarmLists, cancellationToken);
        }
        else
        {
            await GotoAsync(Paths.RallyPointFarmLists, cancellationToken);
        }

        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await WaitForOfficialFarmListRenderAsync(cancellationToken);
        if (await IsFarmListPageAsync(cancellationToken))
        {
            return;
        }

        // Farm lists require a built Rally Point. When it is still level 0 (not built) the rally point
        // page shows the construct view instead of the farm lists — abort with a clear message rather
        // than auto-building it, so the user decides when to build it.
        if (await IsRallyPointLevelZeroAsync(cancellationToken))
        {
            throw new InvalidOperationException("Rally Point is level 0 (not built) in this village. Build the Rally Point before using farm lists.");
        }

        await GotoAsync(Paths.FarmListFastUp, cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        try
        {
            var constructResult = await ConstructBuildingAsync(39, 16, "Rally Point", cancellationToken);
            Notify($"Rally Point ensure result: {constructResult}");
        }
        catch (Exception ex)
        {
            Notify($"Could not auto-construct Rally Point on slot 39: {ex.Message}");
        }

        await GotoAsync(Paths.RallyPointFarmLists, cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await WaitForOfficialFarmListRenderAsync(cancellationToken);

        if (!await IsFarmListPageAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Could not open farm list page at {Paths.RallyPointFarmLists}. Farmlists may be unavailable on this account/server.");
        }
    }

    private async Task<bool> CanReuseCurrentFarmListPageAsync(CancellationToken cancellationToken)
    {
        if (!IsOfficialFarmListUrl(_page.Url))
        {
            return false;
        }

        try
        {
            return await _page.Locator("#rallyPointFarmList .farmListWrapper").CountAsync() > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    internal static bool IsOfficialFarmListUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.AbsolutePath.EndsWith("/build.php", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .ToList();
        return query.Any(part => part[0].Equals("id", StringComparison.OrdinalIgnoreCase) && part[1] == "39")
            && query.Any(part => part[0].Equals("tt", StringComparison.OrdinalIgnoreCase) && part[1] == "99");
    }

    private async Task WaitForOfficialFarmListRenderAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                "() => !!document.querySelector('#rallyPointFarmList')",
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 })
                .WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            Notify("[farm-list] Official farm list root did not render within 5 seconds; continuing with page checks.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    // Predicate (run in the page) that is true only when every farm list is expanded AND has rendered
    // at least as many slot rows as it claims to hold, i.e. all target coordinates are in the DOM.
    private const string FarmListsFullyRenderedScript =
        """
        () => {
          const clean = (value) => (value || '')
            .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
            .replace(/\s+/g, ' ')
            .trim();
          return Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
            .every(list => {
              if (list.classList.contains('collapsed')) return false;
              const match = clean(list.querySelector('td.addTarget')?.textContent).match(/(\d+)\s*\/\s*(\d+)/);
              const expectedRows = match ? Number(match[1]) : 0;
              return list.querySelectorAll('tbody tr.slot').length >= expectedRows;
            });
        }
        """;

    private async Task EnsureOfficialFarmListsExpandedAsync(CancellationToken cancellationToken)
    {
        // Expand every collapsed list and scroll each into view so Travian lazy-renders its slot rows
        // (which carry the target coordinates). A single pass can leave large/slow lists half-rendered,
        // so retry the expand+scroll a few rounds until every list reports all of its rows.
        const int maxRounds = 4;
        for (var round = 1; round <= maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var collapsedCount = await _page.EvaluateAsync<int>(
                """
                () => {
                  const wrappers = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'));
                  let collapsed = 0;
                  for (const list of wrappers) {
                    if (list.classList.contains('collapsed')) {
                      collapsed++;
                      const toggle = list.querySelector('.farmListHeader .expandCollapse');
                      toggle?.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                    }
                    try { list.scrollIntoView({ block: 'center' }); } catch (_) {}
                  }
                  return collapsed;
                }
                """);

            if (round == 1 && collapsedCount > 0)
            {
                Notify($"[farm-list] expanding {collapsedCount} Official farm list(s) to read target coordinates");
            }

            try
            {
                await _page.WaitForFunctionAsync(
                    FarmListsFullyRenderedScript,
                    null,
                    new PageWaitForFunctionOptions { Timeout = 6000 })
                    .WaitAsync(cancellationToken);
                if (round > 1)
                {
                    Notify($"[farm-list] all farm lists fully expanded after {round} round(s)");
                }

                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            catch (TimeoutException)
            {
                if (round < maxRounds)
                {
                    Notify($"[farm-list] expansion round {round}/{maxRounds} incomplete; retrying expand+scroll");
                    await Task.Delay(Random.Shared.Next(500, 800), cancellationToken);
                }
            }
        }

        Notify("[farm-list] some Official farm lists did not fully expand after retries; "
            + "reading available targets (duplicate check may be incomplete).");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<bool> IsFarmListPageAsync(CancellationToken cancellationToken)
    {
        var isFarmListPage = await _page.EvaluateAsync<bool>(
            """
            () => {
              if (document.querySelector('span[id^="timerTop"]')) return true;
              if (document.querySelector('.farmList, .farmlist, [class*="farm" i][class*="list" i]')) return true;

              const body = (document.body?.innerText || '').toLowerCase();
              return body.includes('start raid') || body.includes('farm list') || body.includes('farmlist');
            }
            """);
        return isFarmListPage;
    }

    private async Task<IReadOnlyList<FarmListOverview>> ReadFarmListsFromCurrentPageAsync(CancellationToken cancellationToken)
    {

        var rawRows = await _page.EvaluateAsync<FarmListRowJs[]>(
            """
            () => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const cleanNumericText = (value) =>
                normalize(value).replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '');

              // Official Travian (T4.6) keeps list metadata rendered even when lists are collapsed.
              const officialCandidates = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'));
              // Ordinal of each owning .villageWrapper, so two villages that happen to share a display
              // name stay as separate groups (the page exposes no village id/coordinates on the wrapper).
              const villageWrappers = Array.from(document.querySelectorAll('#rallyPointFarmList .villageWrapper'));
              if (officialCandidates.length > 0) {
                return officialCandidates.slice(0, 200).map((candidate) => {
                  const name =
                    normalize(candidate.querySelector('.farmListName .name')?.textContent) ||
                    'Farm list';
                  const lid =
                    candidate.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') ||
                    candidate.querySelector('[data-farm-list-id]')?.getAttribute('data-farm-list-id') ||
                    '';

                  // Owning village name + ordinal from the wrapping .villageWrapper header. Used only to
                  // group lists under a village heading in the UI; empty/-1 when there is no wrapper.
                  const villageWrapper = candidate.closest('.villageWrapper');
                  const villageName = normalize(villageWrapper?.querySelector('.villageHeader .villageName')?.textContent);
                  const villageIndex = villageWrapper ? villageWrappers.indexOf(villageWrapper) : -1;

                  const statusText = cleanNumericText(candidate.querySelector('.farmListStatus')?.textContent);
                  const statusMatch = statusText.match(/(\d+)\s*\/\s*(\d+)/);
                  const running = statusMatch ? Number(statusMatch[1]) : 0;
                  const statusTotal = statusMatch ? Number(statusMatch[2]) : 0;

                  const capacityText = cleanNumericText(candidate.querySelector('td.addTarget')?.textContent);
                  const capacityMatch = capacityText.match(/(\d+)\s*\/\s*(\d+)/);
                  let total = capacityMatch ? Number(capacityMatch[1]) : statusTotal;
                  let capacity = capacityMatch ? Number(capacityMatch[2]) : total;

                  const startButton = candidate.querySelector('button.startFarmList');
                  const startText = cleanNumericText(startButton?.textContent);
                  const startCountMatch = startText.match(/start\s*\((\d+)\)/i);
                  const startCount = startCountMatch ? Number(startCountMatch[1]) : null;
                  const startButtonDisabled =
                    !startButton
                    || startButton.disabled
                    || startButton.getAttribute('disabled') !== null
                    || (startButton.className || '').toLowerCase().includes('disabled');
                  const renderedSlots = Array.from(candidate.querySelectorAll('tbody tr.slot'));
                  let active = startCountMatch
                    ? startCount
                    : renderedSlots.filter((row) => !row.classList.contains('disabled')).length;

                  if (!Number.isFinite(total) || total < 0) total = 0;
                  if (!Number.isFinite(capacity) || capacity < total) capacity = total;
                  if (!Number.isFinite(active) || active < 0) active = 0;
                  if (renderedSlots.length === 0 && !startCountMatch && startButton && !startButton.classList.contains('disabled')) {
                    active = total;
                  }
                  if (total > 0 && active > total) active = total;

                  const farmCoordinates = [];
                  const seenCoordinates = new Set();
                  for (const link of candidate.querySelectorAll('tbody tr.slot td.target a[href*="karte.php"]')) {
                    const href = link.getAttribute('href') || '';
                    const match = href.match(/[?&]x=(-?\d+).*?[?&]y=(-?\d+)/i);
                    if (!match) continue;
                    const key = `${Number(match[1])}|${Number(match[2])}`;
                    if (seenCoordinates.has(key)) continue;
                    seenCoordinates.add(key);
                    farmCoordinates.push(key);
                  }

                  return {
                    name,
                    activeFarmCount: active,
                    totalFarmCount: total,
                    capacity,
                    farmCoordinates,
                    timerText: '',
                    // "Not ready" is the Start button's own state, NOT how many farms are currently out
                    // raiding. A list with some targets still being raided ("22/37 being raided") keeps a
                    // green, clickable "Start (N)" button for the N targets that ARE ready — it must still
                    // be sendable. Only treat it as not-ready when that button is missing/disabled or 0.
                    disabled: startButtonDisabled || startCount === 0,
                    lid,
                    villageName,
                    villageIndex
                  };
                });
              }

              const candidates = new Set();
              document.querySelectorAll('.listTitle').forEach((node) => candidates.add(node));
              if (candidates.size === 0) {
                document.querySelectorAll('.farmList, .farmlist').forEach((node) => candidates.add(node));
              }

              const rows = [];
              const seenByName = new Map();
              for (const candidate of candidates) {
                if (!candidate) continue;
                const titleTextNode = candidate.querySelector('.listTitleText') || candidate;
                const whole = normalize(titleTextNode.textContent);
                if (!whole) continue;
                if (whole.length > 300) continue;

                // True farm list title rows contain a delete icon button.
                if (!candidate.querySelector('img.del')) continue;

                const lowerWhole = whole.toLowerCase();
                if (lowerWhole.includes('building plans will be released') || lowerWhole.startsWith('server time')) {
                  continue;
                }

                let name =
                  normalize(candidate.querySelector('h1, h2, h3, h4, .title, .name, strong')?.textContent) ||
                  normalize(whole.split('\n')[0] || '') ||
                  whole;
                name = name
                  .replace(/\bdelete\b/ig, '')
                  .replace(/\(\d+\s*farms?\)/i, '')
                  .replace(/\s*start raid.*$/i, '')
                  .trim();
                if (!name) name = 'Farm list';
                if (name.length > 120) continue;

                const slashCountMatch = whole.match(/(\d+)\s*\/\s*(\d+)\s*farm/i);
                const parenCountMatch = whole.match(/\((\d+)\s*farms?\)/i);

                let active = 0;
                let total = 0;
                if (slashCountMatch) {
                  active = Number(slashCountMatch[1]);
                  total = Number(slashCountMatch[2]);
                } else if (parenCountMatch) {
                  active = Number(parenCountMatch[1]);
                  total = 120;
                }
                if (!Number.isFinite(active) || active < 0) active = 0;
                if (!Number.isFinite(total) || total < 0) total = 0;
                active = Math.min(active, 120);
                total = Math.min(total, 120);
                if (total > 0 && active > total) active = total;

                const container =
                  candidate.closest('.raidList, .listEntry, tr, li, article, section, .box') ||
                  candidate.parentElement ||
                  candidate;

                // Resolve the farm list id (lid). The Start Raid button id encodes the lid
                // (startRaidBtnTop<lid>), but the countdown span id (timerTop<n>) uses an
                // unrelated sequential index, so we must read the timer from the button itself.
                const tryReadListId = (root) => {
                  if (!root) return null;
                  const markAll = root.querySelector('input[id^="raidListMarkAll"]');
                  const markAllMatch = (markAll?.id || '').match(/raidListMarkAll(\d+)/i);
                  if (markAllMatch) return markAllMatch[1];
                  const btn = root.querySelector('button[id^="startRaidBtnTop"], button.startRaidButton[data-lid]');
                  const btnIdMatch = (btn?.id || '').match(/startRaidBtnTop(\d+)/i);
                  if (btnIdMatch) return btnIdMatch[1];
                  if (btn?.getAttribute('data-lid')) return btn.getAttribute('data-lid');
                  const switchNode = root.querySelector('.openedClosedSwitch[onclick*="toggleList"]');
                  const switchMatch = (switchNode?.getAttribute('onclick') || '').match(/toggleList\((\d+)\)/i);
                  if (switchMatch) return switchMatch[1];
                  return null;
                };

                const lid =
                  tryReadListId(candidate) ||
                  tryReadListId(container) ||
                  tryReadListId(candidate.closest('.listTitle')?.parentElement || null);

                let raidButton = null;
                if (lid) {
                  raidButton =
                    document.getElementById(`startRaidBtnTop${lid}`) ||
                    document.querySelector(`button.startRaidButton[data-lid="${lid}"]`);
                }
                if (!raidButton) {
                  raidButton = container.querySelector('button[id^="startRaidBtnTop"], button.startRaidButton[data-lid]');
                }

                const readTimerFrom = (root) => {
                  if (!root) return '';
                  const span = root.querySelector('span[id^="timerTop"]');
                  const spanText = normalize(span?.textContent);
                  if (/\d{1,3}:\d{2}/.test(spanText)) return spanText;
                  const contentText = normalize(root.querySelector('.button-content')?.textContent || root.textContent);
                  const match = contentText.match(/\d{1,3}:\d{2}(?::\d{2})?/);
                  return match ? match[0] : '';
                };

                const timerText = readTimerFrom(raidButton) || readTimerFrom(container);

                let disabled = false;
                if (raidButton) {
                  const cls = (raidButton.className || '').toLowerCase();
                  disabled = !!raidButton.disabled || raidButton.getAttribute('disabled') !== null || cls.includes('disabled');
                }

                const key = name.toLowerCase();
                const existing = seenByName.get(key);
                if (!existing) {
                  seenByName.set(key, { name, activeFarmCount: active, totalFarmCount: total, timerText, disabled, lid: lid || '' });
                  continue;
                }

                seenByName.set(key, {
                  name,
                  activeFarmCount: Math.max(existing.activeFarmCount || 0, active),
                  totalFarmCount: Math.max(existing.totalFarmCount || 0, total),
                  timerText: (existing.timerText && existing.timerText.length > 0) ? existing.timerText : timerText,
                  disabled: existing.disabled || disabled,
                  lid: (existing.lid && existing.lid.length > 0) ? existing.lid : (lid || '')
                });
              }

              for (const value of seenByName.values()) {
                rows.push(value);
              }

              return rows.slice(0, 200);
            }
            """);

        return rawRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .Select(row =>
            {
                var timer = ResolveFarmListRemaining(row.TimerText);
                return new FarmListOverview(
                    Name: row.Name!,
                    ActiveFarmCount: Math.Min(MaxFarmsPerFarmList, Math.Max(0, row.ActiveFarmCount ?? 0)),
                    TotalFarmCount: Math.Min(MaxFarmsPerFarmList, Math.Max(0, row.TotalFarmCount ?? 0)),
                    RemainingSeconds: timer.RemainingSeconds,
                    ListId: string.IsNullOrWhiteSpace(row.Lid) ? null : row.Lid!.Trim(),
                    Capacity: row.Capacity,
                    FarmCoordinates: row.FarmCoordinates ?? [],
                    Finish: timer.RemainingSeconds is > 0 ? TimerSnapshot.FromRemaining(timer.RemainingSeconds.Value) : null,
                    TimerIsEstimated: timer.IsEstimated,
                    VillageName: string.IsNullOrWhiteSpace(row.VillageName) ? null : row.VillageName!.Trim(),
                    VillageIndex: row.VillageIndex is >= 0 ? row.VillageIndex : null);
            })
            .ToList();
    }

    internal static (int? RemainingSeconds, bool IsEstimated) ResolveFarmListRemaining(string? timerText)
    {
        var seconds = TravianParsing.ParseDurationToSeconds(timerText);
        return (seconds is > 0 ? seconds : null, false);
    }

    private async Task WaitForDispatchLimitToClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = await _page.EvaluateAsync<FarmDispatchLimitStateJs>(
            """
            () => {
              const parse = (raw) => {
                const text = (raw || '').trim();
                if (!text) return null;
                const parts = text.split(':').map((p) => Number.parseInt(p.trim(), 10)).filter((n) => Number.isFinite(n));
                if (parts.length === 2) return (parts[0] * 60) + parts[1];
                if (parts.length === 3) return (parts[0] * 3600) + (parts[1] * 60) + parts[2];
                return null;
              };

              const hasLimit = !!document.querySelector('.dispatchLimitError');
              let minTimer = null;
              document.querySelectorAll('span[id^="timerTop"]').forEach((node) => {
                const seconds = parse(node.textContent || '');
                if (seconds === null) return;
                if (minTimer === null || seconds < minTimer) minTimer = seconds;
              });

              return { hasLimit, minTimerSeconds: minTimer };
            }
            """);

        if (state is null || !state.HasLimit)
        {
            return;
        }

        var waitSeconds = state.MinTimerSeconds is > 0
            ? Math.Max(1, state.MinTimerSeconds.Value)
            : 1;
        Notify($"[farm-list] dispatch limit active — deferring farming for {waitSeconds}s");
        throw new InvalidOperationException($"Farm dispatch limit active. queue_wait_seconds={waitSeconds}");
    }

    // Clicks a farm-list button with a real, trusted Playwright click (Playwright moves the pointer and
    // fires the full event sequence, so the click reads as isTrusted). Falls back to the supplied
    // synthetic-dispatch action only when the real click is not actionable, so behavior never regresses.
    private async Task<bool> TryRealClickFarmButtonAsync(
        ILocator button,
        Func<Task<bool>> jsFallback,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await button.CountAsync() > 0)
            {
                await DelayBeforeClickAsync(cancellationToken, reason);
                await button.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                return true;
            }

            Notify($"[farm-list] real click target not found ({reason}); using fallback action.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            Notify($"[farm-list] real click timed out ({reason}); using fallback action: {ex.Message}");
        }
        catch (PlaywrightException ex)
        {
            Notify($"[farm-list] real click failed ({reason}); using fallback action: {ex.Message}");
        }

        return await jsFallback();
    }

    private async Task<IReadOnlyList<FarmListLossRowJs>> ReadFarmListLossRowsFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        var rows = await _page.EvaluateAsync<FarmListLossRowJs[]>(
            """
            () => {
              const clean = (value) => (value || '')
                .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                .replace(/\s+/g, ' ')
                .trim();
              const classText = (node) => {
                if (!node) return '';
                const value = node.getAttribute ? node.getAttribute('class') : '';
                return typeof value === 'string' ? value : '';
              };
              const rows = Array.from(document.querySelectorAll('#rallyPointFarmList tr.slot, tr.slot'));
              return rows.map((row, rowIndex) => {
                const raidClasses = [];
                const lastRaid = row.querySelector('td.lastRaid');
                if (lastRaid) {
                  raidClasses.push(classText(lastRaid));
                  lastRaid.querySelectorAll('[class]').forEach(node => raidClasses.push(classText(node)));
                }

                const input = row.querySelector('input[data-slot-id]');
                const wrapper = row.closest('.farmListWrapper');
                return {
                  rowIndex,
                  slotId: input?.getAttribute('data-slot-id') || '',
                  listId: wrapper?.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') || '',
                  listName: clean(wrapper?.querySelector('.farmListName .name')?.textContent || ''),
                  targetName: clean(row.querySelector('td.target a')?.textContent || row.querySelector('td.target')?.textContent || ''),
                  rowClass: classText(row),
                  raidClass: raidClasses.filter(Boolean).join(' '),
                  disabled: row.classList.contains('disabled')
                };
              });
            }
            """).WaitAsync(cancellationToken);
        return rows ?? [];
    }

    private static bool IsFarmListLossRow(FarmListLossRowJs row)
    {
        return row is { Disabled: false }
            && FarmListLossStateClassifier.Classify(row.RaidClass) == FarmListLossState.Loss;
    }

    private static bool IsRequestedFarmListLossRow(FarmListLossRowJs row, FarmListLossHandlingRequest request)
    {
        if (!MatchesRequestedFarmListLossColor(row, request))
        {
            return false;
        }

        return FarmListLossStateClassifier.IsUnoccupiedOasis(row.TargetName)
            ? request.IncludeUnoccupiedOasis
            : request.IncludeNonOasisLosses;
    }

    private static bool MatchesRequestedFarmListLossColor(FarmListLossRowJs row, FarmListLossHandlingRequest request)
    {
        if (!IsFarmListLossRow(row))
        {
            return false;
        }

        var colors = request.LossColors;
        return (colors.HasFlag(FarmListLossColors.Red) && FarmListLossStateClassifier.IsRedLoss(row.RaidClass))
            || (colors.HasFlag(FarmListLossColors.Yellow) && FarmListLossStateClassifier.IsYellowLoss(row.RaidClass));
    }

    private static string FarmListLossRowKey(FarmListLossRowJs row)
        => string.IsNullOrWhiteSpace(row.SlotId) ? $"row:{row.RowIndex}" : row.SlotId;

    private static string DescribeLossColors(FarmListLossColors colors) => colors switch
    {
        FarmListLossColors.Red => "red",
        FarmListLossColors.Yellow => "yellow",
        _ => "red/yellow",
    };

    private async Task<bool> TryDeactivateFarmListLossRowAsync(FarmListLossRowJs row, CancellationToken cancellationToken)
    {
        var rows = _page.Locator("#rallyPointFarmList tr.slot, tr.slot");
        var farmRow = !string.IsNullOrWhiteSpace(row.SlotId) && row.SlotId.All(char.IsAsciiDigit)
            ? _page.Locator($"#rallyPointFarmList tr.slot:has(input[data-slot-id='{row.SlotId}']), tr.slot:has(input[data-slot-id='{row.SlotId}'])").First
            : rows.Nth(row.RowIndex);
        var menuTrigger = farmRow.Locator("td.openContextMenu a, td.openContextMenu button, td.openContextMenu").First;
        var menuOpened = await TryRealClickFarmButtonAsync(
            menuTrigger,
            () => JsOpenFarmListRowMenuAsync(row),
            $"open loss-row menu for slot '{row.SlotId}'",
            cancellationToken);
        if (!menuOpened)
        {
            Notify($"[farm-list] loss-row deactivation failed at menu trigger target='{row.TargetName}' slot='{row.SlotId}'.");
            return false;
        }

        var deactivateEntry = _page
            .Locator(".entry.deactivate:visible, button.entry.deactivate:visible, [class~='deactivate']:visible")
            .First;
        try
        {
            await deactivateEntry.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = _config.TimeoutMs,
            }).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            Notify($"[farm-list] loss-row deactivate action timed out target='{row.TargetName}' slot='{row.SlotId}': {ex.Message}");
            return false;
        }
        catch (PlaywrightException ex)
        {
            Notify($"[farm-list] loss-row deactivate action did not become visible target='{row.TargetName}' slot='{row.SlotId}': {ex.Message}");
            return false;
        }

        var deactivateClicked = await TryRealClickFarmButtonAsync(
            deactivateEntry,
            JsClickVisibleFarmListDeactivateEntryAsync,
            $"deactivate loss row slot '{row.SlotId}'",
            cancellationToken);
        if (!deactivateClicked)
        {
            Notify($"[farm-list] loss-row deactivation failed at deactivate action target='{row.TargetName}' slot='{row.SlotId}'.");
            return false;
        }

        try
        {
            await _page.WaitForFunctionAsync(
                """
                (candidate) => {
                  const findRow = () => {
                    if (candidate.slotId) {
                      const input = document.querySelector(`#rallyPointFarmList tr.slot input[data-slot-id="${CSS.escape(candidate.slotId)}"], tr.slot input[data-slot-id="${CSS.escape(candidate.slotId)}"]`);
                      if (input) return input.closest('tr.slot');
                    }

                    return Array.from(document.querySelectorAll('#rallyPointFarmList tr.slot, tr.slot'))[candidate.rowIndex] || null;
                  };
                  const row = findRow();
                  return !row || row.classList.contains('disabled');
                }
                """,
                new { slotId = row.SlotId ?? string.Empty, rowIndex = row.RowIndex },
                new PageWaitForFunctionOptions { Timeout = _config.TimeoutMs }).WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            Notify($"[farm-list] loss-row deactivate confirmation timed out target='{row.TargetName}' slot='{row.SlotId}': {ex.Message}");
            return false;
        }
        catch (PlaywrightException ex)
        {
            Notify($"[farm-list] loss-row deactivate click was not confirmed target='{row.TargetName}' slot='{row.SlotId}': {ex.Message}");
            return false;
        }
    }

    private async Task<bool> JsOpenFarmListRowMenuAsync(FarmListLossRowJs row)
    {
        return await _page.EvaluateAsync<bool>(
            """
            (candidate) => {
              const findRow = () => {
                if (candidate.slotId) {
                  const bySlot = document.querySelector(`#rallyPointFarmList tr.slot input[data-slot-id="${CSS.escape(candidate.slotId)}"], tr.slot input[data-slot-id="${CSS.escape(candidate.slotId)}"]`);
                  if (bySlot) return bySlot.closest('tr.slot');
                }

                const rows = Array.from(document.querySelectorAll('#rallyPointFarmList tr.slot, tr.slot'));
                return rows[candidate.rowIndex] || null;
              };
              const row = findRow();
              if (!row) return false;
              row.scrollIntoView({ block: 'center' });
              const trigger = row.querySelector('td.openContextMenu a, td.openContextMenu button, td.openContextMenu');
              if (!trigger) return false;
              trigger.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
              trigger.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
              trigger.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """,
            new { slotId = row.SlotId ?? string.Empty, rowIndex = row.RowIndex });
    }

    private async Task<bool> JsClickVisibleFarmListDeactivateEntryAsync()
    {
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const entries = Array.from(document.querySelectorAll('.entry.deactivate, button.entry.deactivate, [class~="deactivate"]'));
              const entry = entries.find(node => node.getClientRects().length > 0 && clean(node.textContent).includes('deactivate'));
              if (!entry) return false;
              entry.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
              entry.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
              entry.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """);
    }

    private async Task<bool> TryClickFarmListSendNowAsync(string farmListName, CancellationToken cancellationToken)
    {
        // Fast path: resolve the Official wrapper's stable list id, then click its Start button for real
        // (isTrusted). If the list is missing/disabled or the layout is not the Official one, fall back to
        // the name-based synthetic-dispatch resolver, which also handles the legacy raid-button layout.
        var lid = await ResolveOfficialFarmListStartIdAsync(farmListName);
        if (!string.IsNullOrEmpty(lid))
        {
            var button = _page
                .Locator($"#rallyPointFarmList .farmListWrapper:has(.dragAndDrop[data-list='{lid}']) button.startFarmList")
                .First;
            return await TryRealClickFarmButtonAsync(
                button,
                () => JsDispatchFarmListSendNowAsync(farmListName),
                $"start farm list '{farmListName}'",
                cancellationToken);
        }

        return await JsDispatchFarmListSendNowAsync(farmListName);
    }

    // Resolves the stable data-list id of the Official farm-list wrapper whose name matches, but only when
    // its Start button exists and is enabled. Returns null for a missing/disabled list or a non-Official
    // layout, which routes the caller to the synthetic-dispatch fallback.
    private async Task<string?> ResolveOfficialFarmListStartIdAsync(string farmListName)
    {
        return await _page.EvaluateAsync<string?>(
            """
            (targetName) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const normalizeListName = (value) => normalize(value)
                .replace(/\(\d+\s*farms?\)/i, '')
                .replace(/\bdelete\b/ig, '')
                .trim()
                .toLowerCase();
              const target = normalizeListName(targetName);
              if (!target) return null;

              const wrappers = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'));
              for (const wrapper of wrappers) {
                const name = normalizeListName(wrapper.querySelector('.farmListName .name')?.textContent || '');
                if (name !== target) continue;

                const startButton = wrapper.querySelector('button.startFarmList');
                if (!startButton) return null;
                const cls = (startButton.className || '').toLowerCase();
                if (startButton.disabled || cls.includes('disabled')) return null;

                return wrapper.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list')
                    || wrapper.querySelector('[data-farm-list-id]')?.getAttribute('data-farm-list-id')
                    || null;
              }
              return null;
            }
            """,
            farmListName);
    }

    private async Task<bool> JsDispatchFarmListSendNowAsync(string farmListName)
    {
        var clicked = await _page.EvaluateAsync<bool>(
            """
            (targetName) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const normalizeListName = (value) => normalize(value)
                .replace(/\(\d+\s*farms?\)/i, '')
                .replace(/\bdelete\b/ig, '')
                .trim()
                .toLowerCase();
              const target = normalizeListName(targetName);
              if (!target) return false;

              // Official Travian (T4.6): each list is a #rallyPointFarmList .farmListWrapper with the
              // name in .farmListName .name and a single "Start (N)" button.startFarmList. Clicking it
              // sends every selected target, so no mark-all checkbox is needed.
              const officialWrappers = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'));
              for (const wrapper of officialWrappers) {
                const name = normalizeListName(wrapper.querySelector('.farmListName .name')?.textContent || '');
                if (name !== target) continue;

                const startButton = wrapper.querySelector('button.startFarmList');
                if (!startButton) return false;

                const startClass = (startButton.className || '').toLowerCase();
                if (startButton.disabled || startClass.includes('disabled')) return false;

                startButton.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
                startButton.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
                startButton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                return true;
              }

              const tryReadListId = (root) => {
                if (!root) return null;
                const markAll = root.querySelector('input[id^="raidListMarkAll"]');
                if (markAll?.id) {
                  const match = markAll.id.match(/raidListMarkAll(\d+)/i);
                  if (match) return match[1];
                }

                const button = root.querySelector('button[id^="startRaidBtnTop"], button.startRaidButton[data-lid]');
                if (button?.id) {
                  const match = button.id.match(/startRaidBtnTop(\d+)/i);
                  if (match) return match[1];
                }
                if (button?.getAttribute('data-lid')) {
                  return button.getAttribute('data-lid');
                }

                const switchNode = root.querySelector('.openedClosedSwitch[onclick*="toggleList"]');
                const onclick = switchNode?.getAttribute('onclick') || '';
                const switchMatch = onclick.match(/toggleList\((\d+)\)/i);
                if (switchMatch) return switchMatch[1];

                return null;
              };

              let lid = null;
              const titleNodes = Array.from(document.querySelectorAll('.listTitle .listTitleText, .listTitleText'));
              for (const titleNode of titleNodes) {
                const titleName = normalizeListName(titleNode.textContent);
                if (titleName !== target) continue;

                const titleRoot = titleNode.closest('.listTitle') || titleNode.parentElement;
                lid = tryReadListId(titleRoot?.parentElement || titleRoot);
                if (!lid) {
                  lid = tryReadListId(titleRoot);
                }
                if (lid) break;
              }

              if (!lid) {
                const buttons = Array.from(document.querySelectorAll('button.startRaidButton[data-lid], button[id^="startRaidBtnTop"]'));
                for (const button of buttons) {
                  const row = button.closest('tr, li, article, section, .listEntry, .farmList, .farmlist, .slot, .box, .list, .raidList');
                  const rowName = normalizeListName(row?.querySelector('.listTitleText, h1, h2, h3, h4, .title, .name, strong')?.textContent || row?.textContent || '');
                  if (rowName === target) {
                    lid = button.getAttribute('data-lid') || ((button.id || '').match(/startRaidBtnTop(\d+)/i) || [])[1] || null;
                    if (lid) break;
                  }
                }
              }

              if (!lid) return false;

              const markAll = document.getElementById(`raidListMarkAll${lid}`) || document.querySelector(`input.markAll[id="raidListMarkAll${lid}"]`);
              if (markAll && markAll instanceof HTMLInputElement) {
                if (!markAll.checked) {
                  markAll.checked = true;
                }
                markAll.dispatchEvent(new Event('input', { bubbles: true }));
                markAll.dispatchEvent(new Event('change', { bubbles: true }));
              }

              const button = document.getElementById(`startRaidBtnTop${lid}`) || document.querySelector(`button.startRaidButton[data-lid="${lid}"]`);
              if (!button) return false;

              const className = (button.className || '').toLowerCase();
              if (button.disabled || className.includes('disabled')) return false;

              const text = normalize(button.textContent).toLowerCase();
              if (!text.includes('start raid') && !text.includes('send')) {
                return false;
              }

              button.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """,
            farmListName);

        if (!clicked)
        {
            return false;
        }

        return true;
    }

    private async Task<int?> ReadFarmListTimerSecondsByNameAsync(string farmListName, CancellationToken cancellationToken)
    {
        var rawTimer = await _page.EvaluateAsync<string?>(
            """
            (targetName) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const normalizeListName = (value) => normalize(value)
                .replace(/\(\d+\s*farms?\)/i, '')
                .replace(/\bdelete\b/ig, '')
                .trim()
                .toLowerCase();
              const target = normalizeListName(targetName);
              if (!target) return null;

              const titleNodes = Array.from(document.querySelectorAll('.listTitle .listTitleText, .listTitleText'));
              let lid = null;
              for (const titleNode of titleNodes) {
                const titleName = normalizeListName(titleNode.textContent);
                if (titleName !== target) continue;

                const root = titleNode.closest('.listTitle')?.parentElement || titleNode.closest('.listTitle') || titleNode.parentElement;
                const markAll = root?.querySelector('input[id^="raidListMarkAll"]');
                const markAllMatch = (markAll?.id || '').match(/raidListMarkAll(\d+)/i);
                if (markAllMatch) {
                  lid = markAllMatch[1];
                  break;
                }

                const btn = root?.querySelector('button[id^="startRaidBtnTop"], button.startRaidButton[data-lid]');
                const btnIdMatch = (btn?.id || '').match(/startRaidBtnTop(\d+)/i);
                if (btnIdMatch) {
                  lid = btnIdMatch[1];
                  break;
                }
                if (btn?.getAttribute('data-lid')) {
                  lid = btn.getAttribute('data-lid');
                  break;
                }
              }

              if (lid) {
                const byId = document.getElementById(`timerTop${lid}`);
                if (byId) return normalize(byId.textContent || '');
              }

              const rows = Array.from(document.querySelectorAll('tr, li, article, section, .listEntry, .farmList, .farmlist, .slot, .box, .list, .raidList'));
              for (const row of rows) {
                const text = normalizeListName(row.querySelector('.listTitleText, h1, h2, h3, h4, .title, .name, strong')?.textContent || row.textContent || '');
                if (text !== target) continue;

                const timer = row.querySelector('span[id^="timerTop"]');
                if (timer) return normalize(timer.textContent || '');

                const content = row.querySelector('.button-content');
                if (!content) return null;
                const match = normalize(content.textContent || '').match(/\d{1,3}:\d{2}(?::\d{2})?/);
                return match ? match[0] : null;
              }

              return null;
            }
            """,
            farmListName);

        return TravianParsing.ParseDurationToSeconds(rawTimer);
    }

    private async Task<string?> TryResolveFarmListSlotIdByNameAsync(string farmListName, CancellationToken cancellationToken)
    {
        return await _page.EvaluateAsync<string?>(
            """
            (targetName) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const normalizeListName = (value) => normalize(value)
                .replace(/\(\d+\s*farms?\)/i, '')
                .replace(/\bdelete\b/ig, '')
                .trim()
                .toLowerCase();
              const target = normalizeListName(targetName);
              if (!target) return null;

              for (const wrapper of document.querySelectorAll('#rallyPointFarmList .farmListWrapper')) {
                const name = normalizeListName(wrapper.querySelector('.farmListName .name')?.textContent);
                if (name !== target) continue;
                const listId = wrapper.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list');
                if (listId) return listId;
              }

              const tryReadListId = (root) => {
                if (!root) return null;
                const markAll = root.querySelector('input[id^="raidListMarkAll"]');
                if (markAll?.id) {
                  const match = markAll.id.match(/raidListMarkAll(\d+)/i);
                  if (match) return match[1];
                }

                const button = root.querySelector('button[id^="startRaidBtnTop"], button.startRaidButton[data-lid], button[onclick*="showSlot"][onclick*="lid="]');
                if (button?.id) {
                  const match = button.id.match(/startRaidBtnTop(\d+)/i);
                  if (match) return match[1];
                }
                if (button?.getAttribute('data-lid')) {
                  return button.getAttribute('data-lid');
                }
                const onclick = button?.getAttribute('onclick') || '';
                const onclickMatch = onclick.match(/[?&]lid=(\d+)/i) || onclick.match(/lid=(\d+)/i);
                if (onclickMatch) return onclickMatch[1];

                return null;
              };

              const titleNodes = Array.from(document.querySelectorAll('.listTitle .listTitleText, .listTitleText, .listTitle, h1, h2, h3, h4, .title, .name, strong'));
              for (const titleNode of titleNodes) {
                const titleName = normalizeListName(titleNode.textContent);
                if (titleName !== target) continue;

                const titleRoot = titleNode.closest('.listTitle') || titleNode.parentElement;
                const lid = tryReadListId(titleRoot?.parentElement || titleRoot) || tryReadListId(titleRoot);
                if (lid) return lid;
              }

              return null;
            }
            """,
            farmListName);
    }

}
