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
        await EnsureLoggedInAsync();

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
            await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            await WaitForFarmListsRenderedAsync(cancellationToken);
            await EnsureOfficialFarmListsExpandedAsync(cancellationToken);
            rows = await ReadFarmListsFromCurrentPageAsync(cancellationToken);
            if (rows.Count > 0 || attempt == maxAttempts)
            {
                break;
            }

            Notify($"[farm-list] no farm lists read on attempt {attempt}/{maxAttempts}; reopening the farm page and retrying.");
            await Task.Delay(Random.Shared.Next(600, 800), cancellationToken); // Random wait
        }

        Notify($"[farm-list] read {rows.Count} farm list(s) from rally point");
        return rows;
    }

    // Waits for the farm list wrappers to render before reading, so a slow React mount does
    // not make us read an empty page. A genuinely empty account simply times out and reads zero.
    private async Task WaitForFarmListsRenderedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                "() => document.querySelectorAll('#rallyPointFarmList .farmListWrapper').length > 0",
                null,
                new PageWaitForFunctionOptions { Timeout = 8000 }).WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            Notify("[farm-list] no farm list wrappers rendered within 8 seconds; the account may have no farm lists.");
        }
        catch (PlaywrightException ex)
        {
            Notify($"[farm-list] waiting for farm list wrappers failed: {ex.Message}");
        }
    }

    public async Task<int?> SendFarmListNowAsync(string farmListName, CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (string.IsNullOrWhiteSpace(farmListName))
        {
            throw new InvalidOperationException("Farm list name is required.");
        }

        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
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
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await WaitForFarmListsRenderedAsync(cancellationToken);
        await WaitForDispatchLimitToClearAsync(cancellationToken);

        var clickState = await TryClickStartAllFarmListsAsync(cancellationToken);
        if (clickState.ListCount <= 0)
        {
            throw new InvalidOperationException("No farm lists were found for start-all farming.");
        }

        if (!clickState.Clicked)
        {
            throw new InvalidOperationException($"Could not click Travian Start all farmlists button: {clickState.Reason ?? "unknown reason"}.");
        }

        Notify($"[farm-list] send-all started for {clickState.ListCount} list(s).");
        await WaitForFarmListStartButtonsDisabledAsync(clickState.ListIds ?? [], cancellationToken);
        await WaitForFarmListStartButtonsEnabledAsync(clickState.ListIds ?? [], cancellationToken);
        Notify($"[farm-list] send-all completed for {clickState.ListCount} list(s).");
        return clickState.ListCount;
    }

    public async Task<FarmListLossDeactivationResult> DeactivateFarmListLossTargetsAsync(
        bool includeUnoccupiedOasis,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await WaitForFarmListsRenderedAsync(cancellationToken);
        await EnsureOfficialFarmListsExpandedAsync(cancellationToken);

        var initialRows = await ReadFarmListLossRowsFromCurrentPageAsync(cancellationToken);
        var lossRows = initialRows.Where(IsFarmListLossRow).ToList();
        var skippedOasisRows = lossRows.Count(row =>
            !includeUnoccupiedOasis && FarmListLossStateClassifier.IsUnoccupiedOasis(row.TargetName));
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

        Notify($"[farm-list] red/yellow loss scan found {lossRows.Count} active row(s); skipped oasis={skippedOasisRows}; includeOasis={includeUnoccupiedOasis}.");

        var deactivated = 0;
        for (var attempt = 1; attempt <= MaxFarmsPerFarmList * 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = attempt == 1
                ? initialRows
                : await ReadFarmListLossRowsFromCurrentPageAsync(cancellationToken);
            var candidate = rows.FirstOrDefault(row => IsFarmListLossDeactivationCandidate(row, includeUnoccupiedOasis));
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

        Notify($"[farm-list] red/yellow loss deactivation done: found={lossRows.Count}, deactivated={deactivated}, skippedOasis={skippedOasisRows}.");
        return new FarmListLossDeactivationResult(lossRows.Count, deactivated, skippedOasisRows);
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

    private async Task EnsureRallyPointAndOpenFarmListPageAsync(CancellationToken cancellationToken)
    {
        if (await CanReuseCurrentFarmListPageAsync(cancellationToken))
        {
            Notify("[farm-list:verbose] reusing the current hydrated farm list page.");
            return;
        }

        await GotoAsync(Paths.RallyPointFarmLists, cancellationToken);
        await EnsureLoggedInAsync();
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
        await EnsureLoggedInAsync();

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
        await EnsureLoggedInAsync();
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
              if (officialCandidates.length > 0) {
                return officialCandidates.slice(0, 200).map((candidate) => {
                  const name =
                    normalize(candidate.querySelector('.farmListName .name')?.textContent) ||
                    'Farm list';
                  const lid =
                    candidate.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') ||
                    candidate.querySelector('[data-farm-list-id]')?.getAttribute('data-farm-list-id') ||
                    '';

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
                    lid
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
                var timer = ResolveFarmListRemaining(row.TimerText, row.Disabled);
                return new FarmListOverview(
                    Name: row.Name!,
                    ActiveFarmCount: Math.Min(MaxFarmsPerFarmList, Math.Max(0, row.ActiveFarmCount ?? 0)),
                    TotalFarmCount: Math.Min(MaxFarmsPerFarmList, Math.Max(0, row.TotalFarmCount ?? 0)),
                    RemainingSeconds: timer.RemainingSeconds,
                    ListId: string.IsNullOrWhiteSpace(row.Lid) ? null : row.Lid!.Trim(),
                    Capacity: row.Capacity,
                    FarmCoordinates: row.FarmCoordinates ?? [],
                    Finish: timer.RemainingSeconds is > 0 ? TimerSnapshot.FromRemaining(timer.RemainingSeconds.Value) : null,
                    TimerIsEstimated: timer.IsEstimated);
            })
            .ToList();
    }

    internal static (int? RemainingSeconds, bool IsEstimated) ResolveFarmListRemaining(string? timerText, bool disabled)
    {
        var seconds = TravianParsing.ParseDurationToSeconds(timerText);
        if (seconds is > 0)
        {
            return (seconds, false);
        }

        // The Start Raid button is disabled while a raid timer is running. If we could not parse
        // the exact countdown text, use a conservative one-minute retry instead of the old 1-second
        // sentinel. The sentinel caused a tight read/defer loop while the button remained disabled.
        return disabled ? (60, true) : (seconds, false);
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

    private async Task<FarmListSendAllClickStateJs> TryClickStartAllFarmListsAsync(CancellationToken cancellationToken)
    {
        // Resolve the start-all button state WITHOUT clicking, so the click itself can be a real, trusted
        // Playwright click below (pointer movement + full event sequence, isTrusted=true). An empty reason
        // with listCount>0 means the button is present and enabled; a non-empty reason means not clickable.
        var state = await _page.EvaluateAsync<FarmListSendAllClickStateJs>(
            """
            () => {
              const readListId = (wrapper) =>
                wrapper?.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') ||
                wrapper?.querySelector('[data-farm-list-id]')?.getAttribute('data-farm-list-id') ||
                '';
              const isDisabled = (node) => {
                const cls = (node?.getAttribute('class') || '').toLowerCase();
                return !node || node.disabled || node.getAttribute('disabled') !== null || cls.includes('disabled');
              };

              // Only track lists whose Start button is ENABLED — those are the ones "send all" actually
              // sends. Lists that are already disabled (empty/no valid targets, or on cooldown) are not
              // part of this send and must be excluded, otherwise the completion wait below would block
              // for its full timeout waiting for a button that never re-enables.
              const wrappers = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'));
              const listIds = wrappers
                .map(wrapper => ({ wrapper, button: wrapper.querySelector('button.startFarmList') }))
                .filter(entry => entry.button && !isDisabled(entry.button))
                .map(entry => readListId(entry.wrapper))
                .filter(id => id && id.length > 0);
              const allButton = document.querySelector('#rallyPointFarmList button.startAllFarmLists, button.startAllFarmLists');
              if (!allButton) {
                return { clicked: false, reason: 'start-all button not found', listIds, listCount: listIds.length };
              }

              if (isDisabled(allButton)) {
                return { clicked: false, reason: 'start-all button disabled', listIds, listCount: listIds.length };
              }

              return { clicked: false, reason: '', listIds, listCount: listIds.length };
            }
            """).WaitAsync(cancellationToken);

        state ??= new FarmListSendAllClickStateJs { Clicked = false, Reason = "start-all state could not be read" };
        if (state.ListCount <= 0 || !string.IsNullOrEmpty(state.Reason))
        {
            return state;
        }

        var clicked = await TryRealClickFarmButtonAsync(
            _page.Locator("#rallyPointFarmList button.startAllFarmLists, button.startAllFarmLists").First,
            JsDispatchStartAllFarmListsAsync,
            "start all farm lists",
            cancellationToken);

        return new FarmListSendAllClickStateJs
        {
            Clicked = clicked,
            Reason = clicked ? string.Empty : "start-all button click did not register",
            ListIds = state.ListIds,
            ListCount = state.ListCount,
        };
    }

    // Fallback that dispatches synthetic mouse events on the start-all button. Used only when the real
    // Playwright click above is not actionable (covered/detached), so farm-send behavior never regresses.
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

            Notify($"[farm-list] real click target not found ({reason}); using JS dispatch fallback.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlaywrightException ex)
        {
            Notify($"[farm-list] real click failed ({reason}); using JS dispatch fallback: {ex.Message}");
        }

        return await jsFallback();
    }

    private async Task WaitForFarmListStartButtonsDisabledAsync(IReadOnlyList<string> listIds, CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                FarmListStartButtonsDisabledScript,
                listIds.ToArray(),
                new PageWaitForFunctionOptions { Timeout = 15000 }).WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            Notify("[farm-list] send-all buttons did not visibly disable within 15 seconds; continuing to completion wait.");
        }
    }

    private async Task WaitForFarmListStartButtonsEnabledAsync(IReadOnlyList<string> listIds, CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                FarmListStartButtonsEnabledScript,
                listIds.ToArray(),
                new PageWaitForFunctionOptions { Timeout = 60000 }).WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            // The raids were already dispatched by the click above; a Start button that has not re-enabled
            // within the timeout (e.g. a list now on cooldown) is not a send failure. Complete the operation
            // with a warning instead of blocking/alarming.
            Notify("[farm-list] send-all: some Start buttons had not re-enabled within 60 seconds; treating the send as complete.");
        }
    }

    private const string FarmListStartButtonsDisabledScript =
        """
        (ids) => {
          const wanted = new Set((ids || []).map(String).filter(Boolean));
          const readListId = (button) => {
            const wrapper = button.closest('.farmListWrapper');
            return wrapper?.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') ||
              wrapper?.querySelector('[data-farm-list-id]')?.getAttribute('data-farm-list-id') ||
              '';
          };
          const isDisabled = (node) => {
            const cls = (node?.getAttribute('class') || '').toLowerCase();
            return !!node && (node.disabled || node.getAttribute('disabled') !== null || cls.includes('disabled'));
          };
          const buttons = Array.from(document.querySelectorAll('#rallyPointFarmList button.startFarmList, button.startFarmList'))
            .filter(button => wanted.size === 0 || wanted.has(readListId(button)));
          return buttons.length === 0 || buttons.some(isDisabled);
        }
        """;

    private const string FarmListStartButtonsEnabledScript =
        """
        (ids) => {
          const wanted = new Set((ids || []).map(String).filter(Boolean));
          const readListId = (button) => {
            const wrapper = button.closest('.farmListWrapper');
            return wrapper?.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') ||
              wrapper?.querySelector('[data-farm-list-id]')?.getAttribute('data-farm-list-id') ||
              '';
          };
          const isDisabled = (node) => {
            const cls = (node?.getAttribute('class') || '').toLowerCase();
            return !!node && (node.disabled || node.getAttribute('disabled') !== null || cls.includes('disabled'));
          };
          const buttons = Array.from(document.querySelectorAll('#rallyPointFarmList button.startFarmList, button.startFarmList'))
            .filter(button => wanted.size === 0 || wanted.has(readListId(button)));
          return buttons.length === 0 || buttons.every(button => !isDisabled(button));
        }
        """;

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

    private static bool IsFarmListLossDeactivationCandidate(FarmListLossRowJs row, bool includeUnoccupiedOasis)
    {
        return IsFarmListLossRow(row)
            && (includeUnoccupiedOasis || !FarmListLossStateClassifier.IsUnoccupiedOasis(row.TargetName));
    }

    private async Task<bool> TryDeactivateFarmListLossRowAsync(FarmListLossRowJs row, CancellationToken cancellationToken)
    {
        var menuOpened = await _page.EvaluateAsync<bool>(
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
            new { slotId = row.SlotId ?? string.Empty, rowIndex = row.RowIndex }).WaitAsync(cancellationToken);
        if (!menuOpened)
        {
            return false;
        }

        await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const entries = Array.from(document.querySelectorAll('.entry.deactivate, button.entry.deactivate, [class~="deactivate"]'));
              const entry = entries.find(node => clean(node.textContent).includes('deactivate'));
              if (!entry) return false;
              entry.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
              entry.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
              entry.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """).WaitAsync(cancellationToken);
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
