using Microsoft.Playwright;
using System.Text.Json;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Smithy navigation and upgrade workflow. Kept on the TravianClient partial so session,
// pacing, logging, and browser ownership remain unchanged.
public sealed partial class TravianClient
{
    /// <summary>
    /// Improves only the user-selected troops at the Smithy, each up to its own target level. Reads every
    /// troop row, classifies it (improvable / already at target / maxed / no resources / queue busy / not
    /// researched), clicks Improve for an available targeted troop, and defers on the live queue timer when
    /// the smithy is busy. Rows are identified by unit id (img.unit.uNN) or troop slot (t=tN),
    /// never by button text alone.
    /// </summary>
    public async Task<string> UpgradeSelectedTroopsAtSmithyAsync(
        IReadOnlyList<SmithyTroopTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var targetList = targets ?? [];
        Notify($"UpgradeSelectedTroopsAtSmithyAsync started with {targetList.Count} target(s): "
            + string.Join(", ", targetList.Select(t => $"{t.Key}->{t.TargetLevel}")));
        if (targetList.Count == 0)
        {
            return "Smithy: no troops selected for upgrade.";
        }

        var smithySlotId = await TryResolveSmithySlotIdAsync(cancellationToken);
        if (!smithySlotId.HasValue)
        {
            return "Smithy not found in this village. Build a Smithy first.";
        }
        Notify($"Smithy found at slot {smithySlotId.Value}.");

        // Travian Plus grants a second concurrent Smithy research slot (same idea as the second build queue
        // slot for construction). Read it once so the loop greedily fills BOTH slots before deferring,
        // instead of stopping after one. Unknown Plus is treated as 1 slot (conservative, never over-fills).
        var (_, smithyPlusActive) = await GetCachedTribeAndPlusAsync(cancellationToken);
        var maxConcurrentUpgrades = smithyPlusActive ? 2 : 1;
        Notify($"Smithy: Plus={smithyPlusActive}; max concurrent upgrades={maxConcurrentUpgrades}.");

        // Targets still needing a decision, keyed by their identity (dedupes duplicate selections).
        var pending = new Dictionary<string, SmithyTroopTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targetList)
        {
            if (target is not null && !string.IsNullOrWhiteSpace(target.Key))
            {
                pending[target.Key] = target;
            }
        }

        const int safetyCap = 60;
        // Re-check interval when the page gives no exact ETA (e.g. resource shortage without a countdown).
        const int DefaultRecheckSeconds = 300;
        var improved = 0;
        var skipped = 0;
        var consecutiveEmptyReloads = 0;
        var consecutiveZeroDurationReloads = 0;
        // A slot is free but the page showed no ready Improve button (usually a React re-render race right
        // after starting a research). Bounded reloads to fill the free slot before giving up.
        var consecutiveFreeSlotStallReads = 0;
        // Last Smithy research queue we reported to the dashboard ("[smithy-queue]"), to emit on change only.
        string? lastQueueCsv = null;
        // Troops we already tried to top up from the hero this run — bounds hero transfers to one attempt
        // per troop so a partial/ineffective transfer can never drain the hero in a loop.
        var heroTransferAttempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var iter = 0; iter < safetyCap && pending.Count > 0; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var smithyPath = Paths.BuildBySlot(smithySlotId.Value);
            if (!IsCurrentUrlForPath(smithyPath))
            {
                await GotoAsync(smithyPath, cancellationToken);
            }
            try
            {
                // Official Smithy is React-rendered; wait for the page to be actionable (not just DOM-present)
                // so the very first read sees real buttons/resources and can improve immediately when possible.
                await WaitForPageReadyAsync(cancellationToken);
            }
            catch
            {
                // Continue; the row read below is retry-wrapped.
            }
            await Task.Delay(400, cancellationToken);

            // The smithy itself may need an upgrade before more troop levels can be researched.
            var needsSmithyUpgrade = await RetryAsync(
                "Smithy: detect 'improve the blacksmith'",
                () => _page.EvaluateAsync<bool>(
                    "() => /improve\\s+the\\s+(blacksmith|smithy)/i.test(document.body.innerText || '')"),
                cancellationToken: cancellationToken);
            if (needsSmithyUpgrade)
            {
                Notify($"Smithy capacity exhausted (\"Improve the blacksmith\" detected). Building slot {smithySlotId.Value}.");
                var upgradeResult = await UpgradeBuildingToMaxAsync(smithySlotId.Value, cancellationToken: cancellationToken);
                Notify($"Smithy build result: {upgradeResult}");
                consecutiveEmptyReloads = 0;
                consecutiveZeroDurationReloads = 0;

                // If the construction queue is full/busy, the Smithy can't grow right now. Defer on that
                // timer (no point retrying while the build queue is full) so the task comes back when the
                // building finishes — without blocking the other loop groups.
                if (TryReadQueueWaitSeconds(upgradeResult, out var buildWaitSeconds))
                {
                    Notify($"Smithy: build queue busy, deferring {buildWaitSeconds}s before improving more troops.");
                    return $"Smithy: improved {improved}, skipped {skipped}, {pending.Count} pending. Smithy build queued. queue_wait_seconds={buildWaitSeconds}";
                }
                continue;
            }

            var rowsJson = await RetryAsync(
                "Smithy: read troop rows",
                () => ReadSmithyRowsJsonAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var rows = SmithyPageParser.ParseRows(rowsJson);
            if (rows.Count == 0)
            {
                consecutiveEmptyReloads += 1;
                if (consecutiveEmptyReloads >= 3)
                {
                    await GotoAsync(Paths.Buildings, cancellationToken);
                    return $"Smithy: no troop rows found after 3 reloads. Improved {improved}, skipped {skipped}.";
                }
                Notify($"Smithy: no troop rows visible, reload {consecutiveEmptyReloads}/3.");
                await TryReloadSmithyAsync(cancellationToken);
                continue;
            }
            consecutiveEmptyReloads = 0;

            // Report the live Smithy research queue (under_progress timers) so the dashboard shows the real
            // per-village queue/timers — emitted only when it changes to keep the log clean.
            var dashQueue = await ReadSmithyQueueEntriesAsync(cancellationToken);
            var dashQueueJson = JsonSerializer.Serialize(dashQueue);
            if (!string.Equals(dashQueueJson, lastQueueCsv, StringComparison.Ordinal))
            {
                Notify($"[smithy-queue] entries_json={dashQueueJson}");
                lastQueueCsv = dashQueueJson;
            }

            // Smithy slots currently occupied (live under_progress rows). With Plus this can be 2.
            var activeUpgradeCount = dashQueue.Count;

            // Classify every pending target. Terminal outcomes (maxed / already at target / not researched)
            // are logged and removed. Queue-busy and resource-shortage keep the troop pending and contribute
            // a wait time so the task defers and re-checks (picking up freed queue slots / incoming resources)
            // instead of skipping. An improvable troop is remembered to click.
            SmithyTroopTarget? toClick = null;
            SmithyTroopTarget? firstNoResourceTarget = null;
            var firstNoResourceLabel = string.Empty;
            var anyResearchInProgress = false;
            var anyWaitingForResources = false;
            int? minResourceWaitSeconds = null;
            foreach (var target in pending.Values.ToList())
            {
                var row = SmithyPageParser.FindRowForTarget(rows, target);
                var outcome = SmithyPageParser.Classify(row, target);
                var label = row?.Name is { Length: > 0 } rowName ? rowName : (target.Name ?? target.Key);
                switch (outcome)
                {
                    case SmithyTroopOutcome.NotResearched:
                        Notify($"Smithy: '{label}' is not listed on the Smithy page — likely not researched in the Academy yet. Skipping.");
                        pending.Remove(target.Key);
                        skipped += 1;
                        break;
                    case SmithyTroopOutcome.Maxed:
                        Notify($"Smithy: '{label}' is already at max level ({SmithyPageParser.MaxLevel}). Skipping.");
                        pending.Remove(target.Key);
                        skipped += 1;
                        break;
                    case SmithyTroopOutcome.AlreadyAtTarget:
                        Notify($"Smithy: '{label}' already at level {row!.CurrentLevel} (target {target.TargetLevel}). Skipping.");
                        pending.Remove(target.Key);
                        skipped += 1;
                        break;
                    case SmithyTroopOutcome.SmithyLevelTooLow:
                        // Terminal: the troop is at the smithy's level cap and can't reach the requested
                        // target until the smithy building is upgraded. Skip instead of deferring forever.
                        Notify($"Smithy: '{label}' is at level {row!.CurrentLevel}; the smithy level is too low to reach target {target.TargetLevel}. Upgrade the smithy first. Skipping.");
                        pending.Remove(target.Key);
                        skipped += 1;
                        break;
                    case SmithyTroopOutcome.NoResources:
                        // Keep pending and wait — the bot re-checks and improves the troop as soon as enough
                        // resources have come in (exact ETA parsed from the page when Travian exposes it).
                        anyWaitingForResources = true;
                        if (firstNoResourceTarget is null)
                        {
                            firstNoResourceTarget = target;
                            firstNoResourceLabel = label;
                        }
                        if (row?.ResourceWaitSeconds is int rowWait && rowWait > 0)
                        {
                            minResourceWaitSeconds = minResourceWaitSeconds is int currentWait
                                ? Math.Min(currentWait, rowWait)
                                : rowWait;
                            Notify($"Smithy: '{label}' lacks resources; enough in ~{rowWait}s. Waiting.");
                        }
                        else
                        {
                            Notify($"Smithy: '{label}' lacks resources (no exact ETA on the page). Will re-check.");
                        }
                        break;
                    case SmithyTroopOutcome.InProgress:
                        anyResearchInProgress = true; // smithy busy; keep pending and defer below
                        break;
                    case SmithyTroopOutcome.Improve:
                        toClick ??= target;
                        break;
                }
            }

            if (toClick is not null)
            {
                var clicked = await RetryAsync(
                    $"Smithy: click Improve for {toClick.Key}",
                    () => ClickSmithyImproveButtonForKeyAsync(toClick.Key, cancellationToken),
                    cancellationToken: cancellationToken);
                if (clicked)
                {
                    improved += 1;
                    consecutiveZeroDurationReloads = 0;
                    Notify($"Smithy: clicked Improve for '{toClick.Name ?? toClick.Key}'. Improvements this run: {improved}.");
                    // Stay on the Smithy page and wait for its React queue to reflect the click. The
                    // next iteration reads the changed DOM directly; navigation remains fallback-only.
                    await WaitForSmithyQueueMutationAfterClickAsync(activeUpgradeCount, cancellationToken);
                    continue;
                }

                Notify($"Smithy: could not click Improve for '{toClick.Key}'; will re-check after the queue frees.");
                anyResearchInProgress = true;
            }

            // Resource shortage + the user enabled hero-inventory resources: top the troop up from the hero
            // and re-evaluate (opt-in, best-effort). One attempt per troop per run so an
            // ineffective transfer can't drain the hero in a loop.
            if (toClick is null
                && firstNoResourceTarget is not null
                && _config.HeroResourceTransferEnabled
                && _config.HeroResourceUseSmithy
                && heroTransferAttempted.Add(firstNoResourceTarget.Key))
            {
                var transferred = await TryHeroResourceTransferForSmithyTroopAsync(
                    firstNoResourceTarget.Key, firstNoResourceLabel, cancellationToken);
                if (transferred)
                {
                    Notify($"Smithy: topped up '{firstNoResourceLabel}' from the hero inventory; re-checking.");
                    await Task.Delay(400, cancellationToken);
                    continue;
                }
            }

            if (pending.Count == 0)
            {
                break;
            }

            // With Plus, a research can start while another runs. A free slot means we should keep trying
            // to fill it rather than deferring on the (long) active research timer.
            var hasFreeSlot = activeUpgradeCount < maxConcurrentUpgrades;

            // Free slot but nothing was clickable, and no resource shortage explains it: the Improve buttons
            // most likely hadn't re-rendered yet after the previous click (React). Reload a few times to fill
            // the free slot before deferring, so the second Plus slot isn't left idle until the first finishes.
            if (toClick is null && hasFreeSlot && anyResearchInProgress && !anyWaitingForResources)
            {
                consecutiveFreeSlotStallReads += 1;
                if (consecutiveFreeSlotStallReads < 3)
                {
                    Notify($"Smithy: slot free ({activeUpgradeCount}/{maxConcurrentUpgrades}) but no Improve button was ready; reloading to fill it (attempt {consecutiveFreeSlotStallReads}/3).");
                    await TryReloadSmithyAsync(cancellationToken);
                    continue;
                }
            }
            else
            {
                consecutiveFreeSlotStallReads = 0;
            }

            if (anyResearchInProgress || anyWaitingForResources)
            {
                // Use the soonest concrete signal: the active research timer (queue busy) or the resource
                // ETA. When neither exposes an exact time, re-check on a moderate interval so the task picks
                // up a freed queue slot / incoming resources. Deferring never blocks the other loop groups.
                var waitCandidates = new List<int>();
                // Wait on the research-queue timer when the queue is genuinely FULL, OR when a free slot
                // could not be filled after the stall reloads above (e.g. the only pending troop is the one
                // already researching, so the free Plus slot isn't usable until it finishes). With a fillable
                // free slot we fall through to the resource ETA / moderate re-check instead of the long timer.
                if (anyResearchInProgress && (!hasFreeSlot || consecutiveFreeSlotStallReads >= 3))
                {
                    var timers = await RetryAsync(
                        "Smithy: read queue timers",
                        () => ReadSmithyQueueTimersAsync(cancellationToken),
                        cancellationToken: cancellationToken);
                    // The Smithy research queue is the source of truth: when it's full the next slot only
                    // frees when the SOONEST queued upgrade finishes, so defer on that timer (not DOM order).
                    var soonestTimer = timers.Where(t => t > 0).DefaultIfEmpty(0).Min();
                    if (soonestTimer > 0)
                    {
                        waitCandidates.Add(soonestTimer);
                    }
                }
                if (minResourceWaitSeconds is int resWait && resWait > 0)
                {
                    waitCandidates.Add(resWait);
                }

                // A concrete page timer (queue slot freeing / resources arriving) is authoritative.
                var hasConcreteWait = waitCandidates.Count > 0;
                var dur = hasConcreteWait ? waitCandidates.Min() : 0;

                // Queue busy with no readable timer can mean Travian's auto-reload stalled — reload a few
                // times before falling back to the periodic re-check.
                if (dur <= 0 && anyResearchInProgress && await IsPageMarkedStaleAsync())
                {
                    consecutiveZeroDurationReloads += 1;
                    if (consecutiveZeroDurationReloads < 3)
                    {
                        Notify($"Smithy: queue busy, timer not ready, reloading (attempt {consecutiveZeroDurationReloads}/3).");
                        await TryReloadSmithyAsync(cancellationToken);
                        continue;
                    }
                }
                consecutiveZeroDurationReloads = 0;

                if (dur <= 0)
                {
                    dur = DefaultRecheckSeconds; // no exact ETA available — re-check on a moderate interval
                    hasConcreteWait = false;
                }

                // With a real queue timer, defer on it in full (the queue stays full until then) instead of
                // waking every 10 min for nothing. Only the no-ETA fallback keeps the short periodic cap.
                var waitSec = hasConcreteWait
                    ? Math.Clamp(dur + 1, 2, 12 * 60 * 60)
                    : Math.Clamp(dur + 1, 2, 600);
                var reasonText = anyResearchInProgress && !anyWaitingForResources
                    ? "research in progress"
                    : !anyResearchInProgress && anyWaitingForResources
                        ? "waiting for resources"
                        : "queue busy / waiting for resources";
                Notify($"Smithy: {reasonText}, deferring {waitSec}s for {pending.Count} pending troop(s).");
                return $"Smithy: improved {improved}, skipped {skipped}, {pending.Count} pending. queue_wait_seconds={waitSec}";
            }

            // Nothing clickable and nothing waiting for the remaining targets — avoid an infinite loop.
            Notify($"Smithy: no actionable state for {pending.Count} pending troop(s); stopping. Improved {improved}, skipped {skipped}.");
            break;
        }

        await GotoAsync(Paths.Buildings, cancellationToken);

        // All selected troops resolved to a terminal state (at target / maxed / smithy level too low /
        // not researched) and nothing was improved this run: report "All done" so the task runner
        // permanently blocks the Troops group (ThrowIfTroopsGroupBlocked -> troops_blocked=all_done). The
        // desktop then switches the dashboard "Upgrade Troops" card OFF instead of re-running every loop.
        var nothingToDo = pending.Count == 0 && improved == 0;
        return nothingToDo
            ? $"Smithy: All done — nothing left to upgrade (improved {improved}, skipped {skipped})."
            : $"Smithy: improved {improved} troop(s), skipped {skipped}.";
    }

    // Emits one raw object per Smithy troop row for the browser-free SmithyPageParser to classify.
    private async Task<string> ReadSmithyRowsJsonAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            return await _page.EvaluateAsync<string>(
                """
                () => {
                  const clean = (v) => String(v || '').replace(/\s+/g, ' ').trim();
                  const rows = Array.from(document.querySelectorAll('.build_details.researches .research'));
                  const out = [];
                  for (const row of rows) {
                    const img = row.querySelector('img.unit');
                    const unitClass = img ? String(img.className || '') : '';

                    let name = '';
                    for (const a of Array.from(row.querySelectorAll('.title a'))) {
                      const t = clean(a.textContent);
                      if (t) { name = t; break; }
                    }
                    if (!name && img) { name = clean(img.getAttribute('alt')); }

                    const levelText = clean(row.querySelector('.title .level')?.textContent
                      || row.querySelector('.level')?.textContent || '');
                    const errorText = clean(row.querySelector('.errorMessage')?.textContent || '');
                    const fullyDeveloped = /fully\s+(developed|researched)/i.test(clean(row.textContent));

                    // Hidden countdown inside the resource-shortage message = seconds until enough resources.
                    let errorWaitSeconds = null;
                    const errTimer = row.querySelector('.errorMessage .timer');
                    const errTimerVal = errTimer ? String(errTimer.getAttribute('value') || '') : '';
                    if (/^\d+$/.test(errTimerVal)) { errorWaitSeconds = parseInt(errTimerVal, 10); }

                    const candidates = Array.from(row.querySelectorAll('button, input[type="submit"], input[type="button"], a'));
                    let researchOnClick = '';
                    let researchValue = '';
                    let hasResearchButton = false;
                    let primaryValue = '';
                    for (const b of candidates) {
                      const oc = String(b.getAttribute('onclick') || '') + ' ' + String(b.getAttribute('href') || '');
                      const val = clean(String(b.getAttribute('value') || '') + ' ' + String(b.textContent || '')).toLowerCase();
                      const cls = String(b.className || '').toLowerCase();
                      if (!val && !oc.trim()) continue;
                      if (/\d+\s*%|faster|video/i.test(val)) continue;           // speed-up button
                      if (!primaryValue && val) primaryValue = val;             // first real button (may be "exchange resources")
                      if (cls.includes('gold') || /exchange|npc|instant|open shop/i.test(val)) continue;
                      const isResearch = /action=research/i.test(oc) || /^(improve|upgrade)\b/.test(val);
                      if (!isResearch) continue;
                      const disabled = b.disabled === true || cls.includes('disabled') || b.getAttribute('aria-disabled') === 'true';
                      researchOnClick = oc;
                      researchValue = clean(b.getAttribute('value') || b.textContent || '');
                      hasResearchButton = !disabled;
                      if (!disabled) break;
                    }

                    out.push({
                      name,
                      unitClass,
                      buttonOnClick: researchOnClick,
                      levelText,
                      buttonValue: researchValue || primaryValue,
                      errorText,
                      errorWaitSeconds,
                      hasResearchButton,
                      fullyDeveloped
                    });
                  }
                  return JSON.stringify(out);
                }
                """);
        }
        catch
        {
            return "[]";
        }
    }

    // Clicks the Improve/Upgrade button for the row identified by key ("u21" unit id, or "t1" troop slot).
    private async Task<bool> ClickSmithyImproveButtonForKeyAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            return await _page.EvaluateAsync<bool>(
                """
                (key) => {
                  const rows = Array.from(document.querySelectorAll('.build_details.researches .research'));
                  const wantUnit = key && key[0] === 'u' ? parseInt(key.slice(1), 10) : null;
                  const wantT = key && key[0] === 't' ? key : null;
                  for (const row of rows) {
                    const img = row.querySelector('img.unit');
                    const um = /\bu(\d+)\b/.exec(img ? String(img.className || '') : '');
                    const unit = um ? parseInt(um[1], 10) : null;

                    let btn = null;
                    for (const b of Array.from(row.querySelectorAll('button, input[type="submit"], input[type="button"], a'))) {
                      const oc = String(b.getAttribute('onclick') || '') + ' ' + String(b.getAttribute('href') || '');
                      const val = (String(b.getAttribute('value') || '') + ' ' + String(b.textContent || '')).replace(/\s+/g, ' ').trim().toLowerCase();
                      const cls = String(b.className || '').toLowerCase();
                      if (/\d+\s*%|faster|video/i.test(val)) continue;
                      if (cls.includes('gold') || /exchange|npc|instant|open shop/i.test(val)) continue;
                      const disabled = b.disabled === true || cls.includes('disabled') || b.getAttribute('aria-disabled') === 'true';
                      if (disabled) continue;
                      const isResearch = /action=research/i.test(oc) || /^(improve|upgrade)\b/.test(val);
                      if (!isResearch) continue;
                      btn = b;
                      break;
                    }
                    if (!btn) continue;

                    const oc = String(btn.getAttribute('onclick') || '') + ' ' + String(btn.getAttribute('href') || '');
                    const tm = /[?&]t=(t\d+)\b/.exec(oc);
                    const tkey = tm ? tm[1] : null;
                    const match = (wantUnit !== null && unit === wantUnit) || (wantT !== null && tkey === wantT);
                    if (!match) continue;
                    btn.click();
                    return true;
                  }
                  return false;
                }
                """,
                key);
        }
        catch
        {
            return false;
        }
    }

    // Reads active Smithy research strictly from the queue table, never from troop-row duration labels.
    private async Task<IReadOnlyList<SmithyQueueEntry>> ReadSmithyQueueEntriesAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            var rawJson = await _page.EvaluateAsync<string>(
                """
                () => {
                  const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();
                  const roots = Array.from(document.querySelectorAll('table.under_progress, .under_progress'));
                  const rows = [];
                  const seenTimers = new Set();
                  for (const root of roots) {
                    const timers = Array.from(root.querySelectorAll('.timer, [id^="timer"]'));
                    for (const timer of timers) {
                      if (seenTimers.has(timer)) continue;
                      seenTimers.add(timer);
                      const row = timer.closest('tr, li, .queueRow, .research') || timer.parentElement;
                      if (!row) continue;
                      const image = row.querySelector('img.unit, img[class*="u"]');
                      const nameNode = row.querySelector('.name, .unitName, .researchName, .title');
                      rows.push({
                        timerValue: clean(timer.getAttribute('value')),
                        timerText: clean(timer.textContent),
                        name: clean(nameNode && nameNode.textContent),
                        imageAlt: clean(image && (image.getAttribute('alt') || image.getAttribute('title'))),
                        rowText: clean(row.innerText || row.textContent)
                      });
                    }
                  }
                  return JSON.stringify(rows);
                }
                """);
            return SmithyPageParser.ParseQueueEntries(rawJson);
        }
        catch (Exception ex)
        {
            Notify($"[smithy-queue] read failed: {ex.Message}");
            return [];
        }
    }

    private async Task<IReadOnlyList<int>> ReadSmithyQueueTimersAsync(CancellationToken cancellationToken)
    {
        return (await ReadSmithyQueueEntriesAsync(cancellationToken))
            .Select(entry => entry.RemainingSeconds)
            .ToList();
    }

    // Extracts a "queue_wait_seconds=N" hint from a result string (e.g. the Smithy building upgrade result
    // when the construction queue is full), so the troop-upgrade task can defer on that exact timer.
    private static bool TryReadQueueWaitSeconds(string? text, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        const string token = "queue_wait_seconds=";
        var index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var start = index + token.Length;
        var end = start;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        return end > start
            && int.TryParse(text.AsSpan(start, end - start), out seconds)
            && seconds > 0;
    }

    // Tops the targeted troop up from the hero inventory by opening that troop row's resource-transfer
    // dialog and confirming it — but only when Travian enables "Transfer selected" (i.e. the hero can
    // actually cover the cost), so an ineffective partial transfer never spends the hero's resources.
    // Official-only and opt-in (gated by the caller). Best-effort: any failure returns false and the
    // caller falls back to waiting for the village to accumulate resources.
    private async Task<bool> TryHeroResourceTransferForSmithyTroopAsync(string key, string label, CancellationToken cancellationToken)
    {
        bool opened;
        try
        {
            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            opened = await _page.EvaluateAsync<bool>(
                """
                (key) => {
                  const rows = Array.from(document.querySelectorAll('.build_details.researches .research'));
                  const wantUnit = key && key[0] === 'u' ? parseInt(key.slice(1), 10) : null;
                  const wantT = key && key[0] === 't' ? key : null;
                  for (const row of rows) {
                    const img = row.querySelector('img.unit');
                    const um = /\bu(\d+)\b/.exec(img ? String(img.className || '') : '');
                    const unit = um ? parseInt(um[1], 10) : null;
                    let tkey = null;
                    const onclicks = Array.from(row.querySelectorAll('[onclick]'))
                      .map(e => String(e.getAttribute('onclick') || '')).join(' ');
                    const tm = /[?&]t=(t\d+)\b/.exec(onclicks);
                    if (tm) tkey = tm[1];
                    const match = (wantUnit !== null && unit === wantUnit) || (wantT !== null && tkey === wantT);
                    if (!match) continue;
                    const icon = row.querySelector('.inlineIcon.resource.transfer');
                    if (!icon) return false;
                    icon.click();
                    return true;
                  }
                  return false;
                }
                """,
                key);
        }
        catch
        {
            return false;
        }

        if (!opened)
        {
            Notify($"[hero-transfer] smithy: no hero transfer offered for '{label}'.");
            return false;
        }

        try
        {
            await _page.WaitForSelectorAsync(
                "div.resourceTransferDialog, #dialogContent",
                new PageWaitForSelectorOptions { Timeout = 8000 });
        }
        catch
        {
            Notify($"[hero-transfer] smithy: transfer dialog did not appear for '{label}'.");
            await TryDismissResourceTransferDialogAsync(cancellationToken);
            return false;
        }

        bool confirmed;
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const dialog = document.querySelector('div.resourceTransferDialog')
                    || ((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources'
                      ? document.querySelector('#dialogContent') : null);
                  if (!dialog) return false;
                  let button = dialog.querySelector('.actionButton.preSelected button');
                  if (!button) {
                    button = Array.from(dialog.querySelectorAll('button')).find(b => /transfer selected/i.test(b.textContent || ''));
                  }
                  return !!button && !button.disabled && button.getAttribute('aria-disabled') !== 'true';
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 });
            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            confirmed = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const dialog = document.querySelector('div.resourceTransferDialog')
                    || ((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources'
                      ? document.querySelector('#dialogContent') : null);
                  if (!dialog) return false;
                  let button = dialog.querySelector('.actionButton.preSelected button');
                  if (!button) {
                    button = Array.from(dialog.querySelectorAll('button')).find(b => /transfer selected/i.test(b.textContent || ''));
                  }
                  if (!button) return false;
                  button.click();
                  return true;
                }
                """);
        }
        catch (TimeoutException)
        {
            Notify($"[hero-transfer] smithy: 'Transfer selected' stayed disabled for '{label}' (hero can't cover). Closing.");
            await TryDismissResourceTransferDialogAsync(cancellationToken);
            return false;
        }
        catch
        {
            await TryDismissResourceTransferDialogAsync(cancellationToken);
            return false;
        }

        if (!confirmed)
        {
            await TryDismissResourceTransferDialogAsync(cancellationToken);
            return false;
        }

        Notify($"[hero-transfer] smithy: transferred hero resources for '{label}'.");
        await WaitForPageReadyAsync(cancellationToken);
        await TryDismissResourceTransferDialogAsync(cancellationToken);
        return true;
    }

    private async Task TryReloadSmithyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReloadPageTracedAsync(
                _page,
                "refresh Smithy page",
                new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded },
                cancellationToken);
        }
        catch (Exception ex) when (IsTransientExecutionContextException(ex))
        {
            // Transient navigation race during reload. The next iteration's IsCurrentUrlForPath
            // check + GotoAsync will recover by re-navigating to the smithy page.
            Notify($"Smithy: reload hit transient navigation context ({ex.Message}). Continuing.");
            await Task.Delay(300, cancellationToken);
        }
    }

    private async Task WaitForSmithyQueueMutationAfterClickAsync(
        int previousActiveUpgradeCount,
        CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                    """
                    previousCount => {
                      const roots = Array.from(document.querySelectorAll('table.under_progress, .under_progress'));
                      const rows = roots.flatMap(root => Array.from(root.querySelectorAll('tr')))
                        .filter(row => row.querySelector('.timer, span.timer'));
                      return rows.length > previousCount;
                    }
                    """,
                    previousActiveUpgradeCount,
                    new PageWaitForFunctionOptions { Timeout = 3500 })
                .WaitAsync(cancellationToken);
            Notify("Smithy: research queue updated in place after Improve click.");
        }
        catch (TimeoutException)
        {
            Notify("Smithy: queue DOM did not update after click; next read will validate before reload fallback.");
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"Smithy: queue DOM changed navigation context after click ({ex.Message}); next read will recover.");
        }
    }

    public async Task<SmithyUpgradeStatus> ReadSmithyUpgradeStatusAsync(
        IReadOnlyList<Building>? knownBuildings = null,
        CancellationToken cancellationToken = default)
    {
        Notify("ReadSmithyUpgradeStatusAsync started");

        var smithySlotId = ResolveKnownSmithySlotId(knownBuildings);
        if (!smithySlotId.HasValue && knownBuildings is { Count: > 0 })
        {
            Notify("Smithy: complete buildings snapshot confirms that no Smithy is constructed; skipping overview retries.");
        }
        else if (!smithySlotId.HasValue)
        {
            smithySlotId = await TryResolveSmithySlotIdAsync(cancellationToken);
        }

        if (!smithySlotId.HasValue)
        {
            return new SmithyUpgradeStatus(
                SmithyExists: false,
                SmithySlotId: null,
                ActiveUpgradeCount: 0,
                RemainingSeconds: null,
                ActiveUpgradeRemainingSeconds: [],
                RemainingText: "N/A",
                StatusText: "Smithy not found.");
        }

        await GotoAsync(Paths.BuildBySlot(smithySlotId.Value), cancellationToken);
        await EnsureLoggedInAsync();

        var activeQueue = (await ReadSmithyQueueEntriesAsync(cancellationToken))
            .OrderBy(entry => entry.RemainingSeconds)
            .ToList();
        var activeTimers = activeQueue.Select(entry => entry.RemainingSeconds).ToList();
        var remainingSeconds = activeTimers.Count > 0 ? activeTimers[0] : (int?)null;
        var activeUpgrades = activeQueue
            .Select(entry => new ActiveSmithyUpgrade(
                entry.Name,
                entry.TargetLevel,
                entry.RemainingSeconds,
                TimerSnapshot.FromRemaining(entry.RemainingSeconds)))
            .ToList();

        return new SmithyUpgradeStatus(
            SmithyExists: true,
            SmithySlotId: smithySlotId.Value,
            ActiveUpgradeCount: activeTimers.Count,
            RemainingSeconds: remainingSeconds,
            ActiveUpgradeRemainingSeconds: activeTimers,
            RemainingText: remainingSeconds is > 0 ? TravianParsing.FormatDuration(remainingSeconds.Value) : "Ready",
            StatusText: activeTimers.Count > 0
                ? $"Smithy upgrade{(activeTimers.Count == 1 ? string.Empty : "s")} active."
                : "Ready.",
            ActiveUpgradeFinishes: activeUpgrades.Select(entry => entry.Finish!).ToList(),
            ActiveUpgrades: activeUpgrades);
    }

    public async Task<string> ReadSmithyQueueFromCurrentPageTestAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();

        if (!await IsCurrentPageSmithyAsync(cancellationToken))
        {
            return "Smithy queue test: current page does not look like the Smithy page.";
        }

        var activeQueue = (await ReadSmithyQueueEntriesAsync(cancellationToken))
            .OrderBy(entry => entry.RemainingSeconds)
            .ToList();
        if (activeQueue.Count <= 0)
        {
            return "Smithy queue test: ready. No active Smithy upgrade found on the current page.";
        }

        var entriesText = string.Join(
            ", ",
            activeQueue.Select(entry =>
                $"{entry.Name} -> {(entry.TargetLevel.HasValue ? $"level {entry.TargetLevel.Value}" : "next level")} ({TravianParsing.FormatDuration(entry.RemainingSeconds)})"));
        return $"Smithy queue test: active={activeQueue.Count}, entries=[{entriesText}]";
    }

    private static int? ResolveKnownSmithySlotId(IReadOnlyList<Building>? knownBuildings)
    {
        return knownBuildings?
            .FirstOrDefault(item =>
                item.SlotId is > 0
                && (item.Gid == 13 || string.Equals(item.Name, "Smithy", StringComparison.OrdinalIgnoreCase)))
            ?.SlotId;
    }

    private async Task<bool> IsCurrentPageSmithyAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const headingNodes = Array.from(document.querySelectorAll('h1, h2, h3, .titleInHeader, .build_details .title, .researches .title'));
                  for (const node of headingNodes) {
                    const text = clean(node.textContent);
                    if (text.includes('smithy') || text.includes('blacksmith')) {
                      return true;
                    }
                  }

                  if (document.querySelector('.build_details.researches .research')) {
                    return true;
                  }

                  const bodyText = clean(document.body && document.body.innerText);
                  return bodyText.includes('improve the blacksmith')
                    || bodyText.includes('improve the smithy')
                    || bodyText.includes('fully researched')
                    || bodyText.includes('fully developed');
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    private async Task<int?> TryResolveSmithySlotIdAsync(CancellationToken cancellationToken)
    {
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt == 1 && IsCurrentUrlForPath(Paths.Buildings))
            {
                Notify("Smithy: using current buildings overview without reload.");
            }
            else
            {
                await ReloadOrGotoAsync(Paths.Buildings, cancellationToken);
            }

            await EnsureLoggedInAsync();

            var slots = await ReadBuildingInfosAsync(cancellationToken);
            // Smithy is gid 13 (see ENGINEERING_NOTES §5: no separate Armoury on gid 12). Accept 12 as a
            // defensive fallback so a mislabeled overview entry still resolves the slot.
            var smithyEntry = slots.FirstOrDefault(kvp =>
                ParseGidFromBuildingCode(kvp.Value.BuildingCode) is 13 or 12 && kvp.Value.Level > 0);
            if (smithyEntry.Value is not null)
            {
                Notify($"Smithy found at slot {smithyEntry.Key} on overview attempt {attempt}/{attempts}.");
                return smithyEntry.Key;
            }

            if (attempt < attempts)
            {
                Notify($"Smithy not detected on overview attempt {attempt}/{attempts}. Reloading and retrying.");
                await Task.Delay(350, cancellationToken);
            }
        }

        return null;
    }

}
