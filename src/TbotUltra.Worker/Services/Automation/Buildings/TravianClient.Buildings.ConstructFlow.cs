using Microsoft.Playwright;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Building surface of the TravianClient facade. The interface list is declared
// on this partial to co-locate the contract with the domain it covers.
public sealed partial class TravianClient : IBuildingClient
{

    public async Task<string> ConstructBuildingAsync(int slotId, int gid, string name, CancellationToken cancellationToken = default)
    {
        using var navDiagnostics = BeginConstructionNavigationDiagnostics($"construct_building slot={slotId} gid={gid}");
        Notify($"[construct] starting — slot={slotId}, gid={gid}");
        if (slotId < 19)
        {
            throw new InvalidOperationException($"Building slot {slotId} is outside the building range.");
        }
        if (gid <= 0)
        {
            throw new InvalidOperationException("Building gid must be positive.");
        }

        var buildingName = string.IsNullOrWhiteSpace(name) ? $"gid {gid}" : name.Trim();
        const int safetyCap = 6;
        var constructionNpcTradeAttempted = false;
        var heroTransferAttempted = false;

        for (var attempt = 0; attempt < safetyCap; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buildQueueBefore = await ReadBuildQueueAsync(cancellationToken);

            // Pre-flight queue gate: defer to program queue if no construction slot is free.
            var deferMessage = await CheckQueueOrDeferAsync(ConstructionKind.Building, slotId, attempt, cancellationToken);
            if (deferMessage is not null)
            {
                return deferMessage;
            }

            // Step 1: open the slot's construction page on the right category tab so the building's
            // wrapper actually exists in the DOM. Walls (slot 40) ignore category — only one option.
            var url = Paths.BuildBySlot(slotId);
            var categoryIndex = BuildingCatalogService.CategoryIndexFor(gid);
            if (categoryIndex.HasValue && slotId != 40)
            {
                var separator = url.Contains('?') ? '&' : '?';
                url = $"{url}{separator}category={categoryIndex.Value}";
            }
            await GotoAsync(url, cancellationToken);

            // Confirmed already-built guard: a stale construct task can target a slot that already holds the
            // building — e.g. a special fixed slot (Rally Point slot 39 / Wall slot 40 exist from founding)
            // or a building that appeared since the task was queued. Such a slot's build page shows the
            // building's upgrade UI, not a construct-choice page, so EnsureExpectedConstructChoicePageAsync
            // would burn retries and ALARM. Wait for the build page, then if it confirms an existing building
            // return a remove result so the queue drops the impossible task instead of failing it forever.
            await WaitForBuildSlotContextAsync(slotId, 5000, cancellationToken);
            var existingBuilding = await TryReadExistingBuildingOnSlotBuildPageAsync(slotId);
            if (existingBuilding is { Level: >= 1 } built)
            {
                Notify($"[construct] slot {slotId} already holds '{built.Name}' level {built.Level} — confirmed already built; removing task from queue.");
                return $"Construct skipped: {buildingName} already exists at slot {slotId} (confirmed '{built.Name}' level {built.Level}). Removing from queue.";
            }

            // Server-appended gid guard: on Official, build.php?id=N for an OCCUPIED slot redirects to
            // ...&gid=<existing building>. The bot never puts gid= in the construct url itself, so a
            // matching gid here proves the slot already holds this building — typically level 0 because
            // an earlier click landed but its confirmation was missed, so the level>=1 guard above does
            // not fire. The construct-choice page will never load in that state; defer until the
            // construction completes instead of burning retries into an ALARM.
            if (existingBuilding is null)
            {
                var slotOccupiedByRequestedGid = false;
                try
                {
                    slotOccupiedByRequestedGid = await _page.EvaluateAsync<bool>(
                        """
                        ({ slotId, gid }) => {
                          const url = window.location.href;
                          if (!/build\.php/i.test(url)) return false;
                          const idMatch = url.match(/[?&]id=(\d+)/);
                          if (!idMatch || Number(idMatch[1]) !== slotId) return false;
                          const gidMatch = url.match(/[?&]gid=(\d+)/);
                          if (!gidMatch || Number(gidMatch[1]) !== gid) return false;
                          // A construct-choice page offers contracts; an occupied slot's page does not.
                          return !document.querySelector('[id^="contract_building"], #contract_building');
                        }
                        """,
                        new { slotId, gid });
                }
                catch (Exception ex) when (IsTransientExecutionContextException(ex))
                {
                    Notify($"[construct] slot {slotId} occupied-gid check hit transient navigation: {ex.Message}");
                }

                if (slotOccupiedByRequestedGid)
                {
                    var waitSeconds = await ReadQueuedBuildingWaitSecondsAsync(buildingName, 60, cancellationToken);
                    Notify($"[construct] slot {slotId} already holds gid {gid} ({buildingName}), still under construction — deferring {waitSeconds}s until it completes.");
                    return $"Slot {slotId}: {buildingName} construction already in progress (slot already holds gid {gid}). queue_wait_seconds={waitSeconds}";
                }
            }

            await EnsureExpectedConstructChoicePageAsync(slotId, gid, url, "construct", cancellationToken);

            // Step 2: read build page state, duration and population from one page analysis.
            var pageAnalysis = await ReadConstructionPageAnalysisAsync(
                slotId,
                "construct pre-click",
                cancellationToken,
                constructGid: gid);
            var durationSeconds = pageAnalysis.DurationSeconds;
            // Read the population the new building grants before clicking (page changes after).
            var populationDelta = pageAnalysis.PopulationDelta;

            // Step 3: click the "Construct building" button (scoped to this gid when possible).
            var clicked = await TryUseConstructFasterForBuildAsync(
                slotId,
                gid,
                buildingName,
                0,
                1,
                buildQueueBefore,
                durationSeconds,
                url,
                cancellationToken);
            var usedConstructFasterVideo = clicked;
            if (!clicked)
            {
                clicked = await ClickConstructBuildingButtonAsync(gid, cancellationToken);
            }
            if (!clicked)
            {
                // Classify the construct page before any queue/progress check navigates to dorf2.
                // Otherwise a normal resource block is read from the wrong page and degrades into the
                // misleading "could not find Construct building button" alarm.
                pageAnalysis = await ReadConstructionPageAnalysisAsync(
                    slotId,
                    "construct no-click",
                    cancellationToken,
                    constructGid: gid);
                var blockedByResources = pageAnalysis.LooksBlockedByResources;
                var missingRequirements = pageAnalysis.ConstructRequirementError;
                if (!string.IsNullOrWhiteSpace(missingRequirements))
                {
                    // Requirement errors are more specific than resource hints on Official construct
                    // pages. A soon-available building can still show resource rows, but hero transfer/NPC
                    // cannot make it buildable until the prerequisite exists.
                    var waitSeconds = UpgradeMath.ClampResourceWaitSeconds(null);
                    Notify($"Slot {slotId}: {buildingName} not buildable yet — missing {missingRequirements}.");
                    return $"Slot {slotId}: {buildingName} cannot be built yet. Missing requirements: {missingRequirements}. Upgrades performed: 0. queue_wait_seconds={waitSeconds}";
                }

                if (blockedByResources)
                {
                    if (!heroTransferAttempted)
                    {
                        heroTransferAttempted = true;
                        if (await TryHeroResourceTransferForConstructionAsync($"Building slot {slotId} construct {buildingName}", cancellationToken))
                        {
                            continue;
                        }
                    }

                    // The construct page has the exact resource block and hero-transfer control. Only
                    // navigate to dorf2 after that direct attempt, matching the upgrade flow and avoiding
                    // a false "No hero transfer offered" result from probing the overview page.
                    var queueDefer = await CheckQueueOrDeferAsync(ConstructionKind.Building, slotId, attempt, cancellationToken);
                    if (queueDefer is not null)
                    {
                        return queueDefer;
                    }

                    var snapshot = await ReadUpgradeResourceWaitSnapshotAsync(
                        $"Building slot {slotId} construct {buildingName}",
                        60,
                        cancellationToken);

                    if (!constructionNpcTradeAttempted)
                    {
                        constructionNpcTradeAttempted = true;
                        if (await TryNpcTradeForConstructionAsync($"Building slot {slotId} construct {buildingName}", cancellationToken))
                        {
                            continue;
                        }
                    }

                    return BuildUpgradeResourceBlockedResultMessage(snapshot);
                }

                var waitAfterBusy = await WaitForConstructionSlotIfBusyAsync(ConstructionKind.Building, cancellationToken);
                if (waitAfterBusy > 0)
                {
                    continue;
                }

                var existingProgress = await DetectConstructProgressAsync(slotId, gid, buildingName, buildQueueBefore, 1, cancellationToken);
                if (existingProgress.Started)
                {
                    return $"Queued {buildingName} in slot {slotId}. Evidence: {existingProgress.Evidence}.";
                }

                // Queue/progress verification navigates away from the construct page. Re-open and verify
                // the exact slot/category before one final click attempt, covering transient redirects or
                // a construct page that had not finished rendering on the first scan.
                await EnsureExpectedConstructChoicePageAsync(
                    slotId,
                    gid,
                    url,
                    "construct retry",
                    cancellationToken);
                clicked = await ClickConstructBuildingButtonAsync(gid, cancellationToken);
                if (clicked)
                {
                    Notify($"Slot {slotId}: construct button for gid {gid} found on verified retry.");
                }
            }

            if (!clicked)
            {
                await CaptureFailureArtifactsAsync($"construct-slot-{slotId}-gid-{gid}-no-click", cancellationToken);
                return $"Slot {slotId}: verified construct page but could not find an actionable 'Construct building' button for gid {gid}.";
            }

            if (populationDelta is int popDelta)
            {
                await AddPopulationToActiveVillageCacheAsync(popDelta, cancellationToken);
            }

            var constructFasterResultNote = usedConstructFasterVideo ? " 25% faster (video)." : string.Empty;
            var progress = await WaitForBuildingLevelAdvanceAsync(slotId, 0, buildingName, buildQueueBefore, gid, 1, cancellationToken);
            if (!progress.Advanced && !progress.QueuedOrInProgress)
            {
                // Final dorf2 probe: an instant-build server can finish a level-1 construct
                // before the queue ever shows it; any visible level > 0 means the click landed.
                var dorf2Level = await ProbeSlotLevelOnDorf2Async(slotId, cancellationToken);
                if (dorf2Level is int confirmedLevel && confirmedLevel >= 1)
                {
                    return $"Constructed {buildingName} in slot {slotId} (confirmed level {confirmedLevel} on dorf2).{constructFasterResultNote}";
                }

                var waitMs = ComputePostActionWaitMs(durationSeconds);
                var waitSeconds = Math.Max(1, (int)Math.Ceiling(waitMs / 1000d));
                Notify($"Slot {slotId}: construct click did not confirm immediately ({progress.Evidence}). Deferring {waitSeconds}s before retry.");
                return $"Slot {slotId}: construct click did not confirm immediately ({progress.Evidence}). queue_wait_seconds={waitSeconds}";
            }

            return $"Queued {buildingName} in slot {slotId}. Evidence: {progress.Evidence}.{constructFasterResultNote}";
        }

        return $"Slot {slotId}: hit safety cap while trying to queue {buildingName}.";
    }

    private async Task<(bool Started, string Evidence)> DetectConstructProgressAsync(
        int slotId,
        int gid,
        string buildingName,
        IReadOnlyList<BuildQueueItem> buildQueueBefore,
        int targetLevel,
        CancellationToken cancellationToken)
    {
        try
        {
            var queueItems = await ReadBuildQueueAsync(cancellationToken);
            var queueFingerprintBefore = BuildQueueFingerprints.Identity(buildQueueBefore);
            var queueFingerprintAfter = BuildQueueFingerprints.Identity(queueItems);
            var targetQueueItem = BuildQueueFingerprints.FindNewTargetBuilding(buildQueueBefore, queueItems, buildingName, slotId, gid, targetLevel)
                ?? BuildQueueFingerprints.FindTargetBuilding(queueItems, buildingName, slotId, gid, targetLevel);
            if (targetQueueItem is not null)
            {
                return (true, $"build queue contains slot {targetQueueItem.SlotId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} {buildingName}");
            }

            var newQueueItem = BuildQueueFingerprints.FindNewBuildingByName(buildQueueBefore, queueItems, buildingName);
            if (newQueueItem is not null)
            {
                return (true, $"build queue added {buildingName}");
            }

            if (!string.Equals(queueFingerprintBefore, queueFingerprintAfter, StringComparison.Ordinal))
            {
                Notify($"Construct progress check for slot {slotId}: queue changed but no {buildingName} entry was found.");
            }

            var activeConstructions = await ReadActiveConstructionsAsync(cancellationToken);
            var matchingActiveConstruction = activeConstructions.FirstOrDefault(item =>
                item.Kind != ConstructionKind.Resource
                && ActiveConstructionMatchesTarget(item, slotId, gid, targetLevel));
            if (matchingActiveConstruction is not null)
            {
                return (true, $"active construction detected for slot {matchingActiveConstruction.SlotId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} {matchingActiveConstruction.Name}");
            }

            await GotoAsync(Paths.Buildings, cancellationToken);
            var slots = await ReadBuildingInfosAsync(cancellationToken);
            if (slots.TryGetValue(slotId, out var slotInfo))
            {
                var slotGid = ParseGidFromBuildingCode(slotInfo.BuildingCode);
                var sameBuilding = slotGid == gid || BuildingNames.Same(slotInfo.BuildingName, buildingName);
                if (sameBuilding && slotInfo.Level >= 0)
                {
                    var slotLabel = string.IsNullOrWhiteSpace(slotInfo.BuildingName) ? buildingName : slotInfo.BuildingName;
                    return (true, $"slot {slotId} now shows {slotLabel} level {slotInfo.Level}");
                }
            }
        }
        catch (Exception ex)
        {
            Notify($"Construct progress verification for slot {slotId} skipped: {ex.Message}");
        }

        return (false, "no queue or construction evidence");
    }

    private async Task<bool> ClickConstructBuildingButtonAsync(int gid, CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded)
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Notify($"Construct page load wait failed before button scan: {ex.Message}");
            // Continue regardless; we'll still look for the button below.
        }

        try
        {
            await _page.WaitForFunctionAsync(
                "() => /\\bconstruct(?:\\s+building)?\\b|\\bbuild(?:\\s+building)?\\b|\\bbauen\\b|\\bbygg\\b|\\bcostruisci\\b/i.test(document.body.innerText || '')",
                null,
                new PageWaitForFunctionOptions { Timeout = 15000 })
                .WaitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Notify($"Construct page readiness wait failed before button scan: {ex.Message}");
            // Continue anyway.
        }

        var rawJson = await _page.EvaluateAsync<string>(
            """
            ({ gid }) => {
              const gidText = String(gid);
              const candidates = Array.from(document.querySelectorAll(
                'button, input[type="submit"], input[type="button"], a, div.addHoverClick, div.button-container'
              ));
              const seen = [];
              const matches = [];
              // Match `?a=N`, `&a=N`, `?gid=N`, `&gid=N`, plus Cyrillic 'а' (U+0430) as a tolerant fallback.
              const otherGidRe = /[?&](?:[aа]|gid)=(\d+)/gi;
              const constructActionRe = /\bconstruct(?:\s+building)?\b|\bbuild(?:\s+building)?\b|\bbauen\b|\bbygg\b|\bcostruisci\b/i;
              for (const el of candidates) {
                const rawText = (el.textContent || '').replace(/\s+/g, ' ').trim();
                const value = (el.getAttribute('value') || '').replace(/\s+/g, ' ').trim();
                const actionText = `${rawText} ${value}`.replace(/\s+/g, ' ').trim();
                const text = actionText.toLowerCase();
                const classes = (el.className || '').toString().toLowerCase();
                const seenText = rawText || value;
                if (seenText) seen.push({ text: seenText.slice(0, 60), classes: classes.slice(0, 60) });
                if (!constructActionRe.test(actionText)) continue;
                const disabled = el.disabled || classes.includes('disabled') || el.getAttribute('aria-disabled') === 'true';
                if (disabled) continue;
                const inOfficialPrimarySection = !!el.closest('.upgradeButtonsContainer .section1');
                const inOfficialSpeedupSection = !!el.closest('.upgradeButtonsContainer .section2');
                if (text.includes('npc') || text.includes('instant') || text.includes('faster') || classes.includes('gold') || classes.includes('purple') || classes.includes('videofeaturebutton') || inOfficialSpeedupSection) continue;
                const isUpgrade = /upgrade\s+to\s+level/i.test(text);
                if (isUpgrade) continue;
                // Travian wraps each constructable building in `#contract_building{gid}`; search broadly for fallbacks.
                const wrapper = el.closest(
                  `#contract_building${gidText}, #building${gidText}, [id$="_building${gidText}"], [data-gid="${gidText}"], .gid${gidText}`
                );
                const wrapperMatchesGid = wrapper !== null;
                const onclick = (el.getAttribute('onclick') || '');
                const href = (el.getAttribute('href') || '');
                const combined = `${onclick} ${href} ${value}`.toLowerCase();
                const onclickMentionsGid =
                  combined.includes(`gid=${gidText}`)
                  || combined.includes(`gid%3d${gidText}`)
                  || combined.includes(`a=${gidText}`)
                  || combined.includes(`а=${gidText}`)         // Cyrillic 'а' literal
                  || combined.includes(`%d0%b0=${gidText}`);        // Cyrillic 'а' URL-encoded
                // If the click URL or wrapper references a DIFFERENT gid, skip — never click a foreign building.
                otherGidRe.lastIndex = 0;
                let mentionsForeignGid = false;
                let m;
                while ((m = otherGidRe.exec(combined)) !== null) {
                  if (m[1] && m[1] !== gidText) { mentionsForeignGid = true; break; }
                }
                if (mentionsForeignGid && !onclickMentionsGid && !wrapperMatchesGid) continue;
                // Skip if button lives inside a wrapper that explicitly belongs to a different building.
                const otherWrapper = el.closest('[id^="contract_building"], [id^="building"]');
                if (otherWrapper && wrapper && otherWrapper !== wrapper) continue;
                if (otherWrapper && !wrapper) {
                  const otherId = (otherWrapper.id || '').toLowerCase();
                  if (otherId !== `contract_building${gidText}` && otherId !== `building${gidText}`) continue;
                }
                if (!wrapperMatchesGid && !onclickMentionsGid) continue;
                const isConstruct = constructActionRe.test(actionText);
                const isDirectAction = el.matches('button, input[type="submit"], input[type="button"], a, div.addHoverClick');
                const rank = (wrapperMatchesGid ? 10 : 0) + (inOfficialPrimarySection ? 8 : 0) + (onclickMentionsGid ? 5 : 0) + (isDirectAction ? 4 : 0) + (isConstruct ? 3 : 0) + (classes.includes('green') ? 2 : 0) + 1;
                matches.push({ index: candidates.indexOf(el), rank, text: actionText.slice(0, 60), gidContext: { wrapper: wrapperMatchesGid, onclick: onclickMentionsGid } });
              }
              matches.sort((a, b) => b.rank - a.rank);
              const best = matches.length > 0 ? matches[0] : null;
              const bestEl = best ? candidates[best.index] : null;
              return JSON.stringify({ clicked: false, clickIndex: best ? best.index : null, clickId: bestEl && bestEl.id ? bestEl.id : '', matches: matches.slice(0, 5), seen: seen.slice(0, 20) });
            }
            """,
            new { gid });

        Notify($"Construct candidate scan: {rawJson}");
        try
        {
            using var doc = JsonDocument.Parse(rawJson ?? "{}");
            var root = doc.RootElement;

            var clickId = root.TryGetProperty("clickId", out var clickIdProp)
                && clickIdProp.ValueKind == JsonValueKind.String
                    ? clickIdProp.GetString()
                    : null;
            int? clickIndex = root.TryGetProperty("clickIndex", out var clickIndexProp)
                && clickIndexProp.ValueKind == JsonValueKind.Number
                && clickIndexProp.TryGetInt32(out var parsedIndex)
                    ? parsedIndex
                    : null;

            if (string.IsNullOrWhiteSpace(clickId) && clickIndex is null)
            {
                return false;
            }

            // Prefer the matched element's stable id. The scan computes its position via
            // document.querySelectorAll, but Playwright's CSS engine pierces open shadow DOM (cookie
            // consent / React widgets) and runs on a separate snapshot, so a positional Nth(index) can
            // resolve to a different — often hidden — element and hang ClickAsync for the full timeout.
            // Pinning the exact button by id avoids that misalignment; Nth stays as a last-resort fallback
            // for the rare candidate that has no id.
            var clickTarget = !string.IsNullOrWhiteSpace(clickId)
                ? _page.Locator($"[id=\"{clickId}\"]").First
                : _page.Locator("button, input[type='submit'], input[type='button'], a, div.addHoverClick, div.button-container").Nth(clickIndex!.Value);

            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            try
            {
                await clickTarget.ClickAsync(new LocatorClickOptions
                {
                    Timeout = _config.TimeoutMs,
                });
                return true;
            }
            catch (Exception clickEx) when (!string.IsNullOrWhiteSpace(clickId))
            {
                // The target button is known and gid-scoped. If a normal click cannot reach it (e.g. an
                // overlay intercepts pointer events), dispatch the element's own click handler. Its onclick
                // navigates via window.location.href, so this performs the same construction action.
                Notify($"Construct click on '{clickId}' fell back to scripted click: {clickEx.Message}");
                return await _page.EvaluateAsync<bool>(
                    """
                    (id) => {
                      const el = document.getElementById(id);
                      if (!el) return false;
                      el.click();
                      return true;
                    }
                    """,
                    clickId);
            }
        }
        catch (Exception ex)
        {
            Notify($"Could not click construct candidate for gid {gid}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// On the construct-choice page Official renders unmet prerequisites for a building as
    /// <c>span.buildingCondition.error</c> elements (e.g. "Main Building Level 3") inside that gid's
    /// <c>#contract_building{gid}</c> wrapper, with no 'Construct building' button. Returns the joined
    /// requirement text (e.g. "Main Building Level 3, Academy Level 1") or null when none is present.
    /// </summary>
    private async Task<string?> ReadConstructRequirementErrorAsync(int gid, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await _page.EvaluateAsync<string>(
                """
                ({ gid }) => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
                  const wrapper = document.querySelector(`#contract_building${gid}`);
                  if (!wrapper) return '';
                  // A real construct button means it IS buildable — no requirement error to report.
                  if (wrapper.querySelector('button[value="Construct building"], button.green.new')) return '';
                  const conditions = Array.from(wrapper.querySelectorAll('.buildingCondition.error'))
                    .map((node) => clean(node.textContent))
                    .filter((text) => text.length > 0);
                  return conditions.join(', ');
                }
                """,
                new { gid });
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureBuildingCanBeConstructed(VillageStatus status, int gid, string name)
    {
        if (gid is 38 or 39)
        {
            throw new InvalidOperationException($"{name} requires building plans and is not supported yet.");
        }

        var existing = status.Buildings
            .Where(building => building.Gid == gid || BuildingNames.Same(building.Name, name))
            .ToList();
        var duplicateAllowed = gid is 23 or 38 or 39;
        var wallGid = gid is 31 or 32 or 33 or 42 or 43;
        if ((gid is 29 or 30) && status.IsCapital == true)
        {
            throw new InvalidOperationException($"{name} cannot be built in the capital.");
        }

        var conflictingResidenceFamilyGid = BuildingCatalogService.ResidenceFamilyConflictGidsFor(gid)
            .FirstOrDefault(conflictGid => status.Buildings.Any(building =>
                (building.Gid ?? BuildingCatalogService.GidForName(building.Name)) == conflictGid));
        if (conflictingResidenceFamilyGid > 0)
        {
            throw new InvalidOperationException($"{name} conflicts with {BuildingCatalogService.NameForGid(conflictingResidenceFamilyGid)} already in this village.");
        }

        if (BuildingCatalogService.DuplicateRequiredExistingLevelFor(gid) is int duplicateRequiredLevel)
        {
            if (existing.Count > 0)
            {
                var highest = existing
                    .Where(building => building.Level is not null)
                    .Select(building => building.Level!.Value)
                    .DefaultIfEmpty(0)
                    .Max();
                if (highest < duplicateRequiredLevel)
                {
                    throw new InvalidOperationException($"{name} can only be duplicated after an existing one reaches level {duplicateRequiredLevel}.");
                }
            }
        }
        else if (existing.Count > 0 && !duplicateAllowed && !wallGid)
        {
            throw new InvalidOperationException($"{name} already exists in this village.");
        }

        var missing = MissingBuildingRequirements(status, gid);
        if (missing.Count == 0)
        {
            return;
        }

        var requirements = string.Join(", ", missing.Select(item => $"{item.name} level {item.level}"));
        throw new InvalidOperationException($"{name} cannot be built yet. Missing requirements: {requirements}.");
    }

    private static List<(string name, int level)> MissingBuildingRequirements(VillageStatus status, int gid)
    {
        var missing = new List<(string name, int level)>();
        foreach (var requirement in BuildingCatalogService.RequirementsFor(gid))
        {
            var current = BuildingNames.LevelByName(status, requirement.Name);
            if (current < requirement.Level)
            {
                missing.Add((requirement.Name, requirement.Level));
            }
        }

        return missing;
    }

    private async Task EnsureExpectedConstructChoicePageAsync(
        int slotId,
        int gid,
        string constructUrl,
        string operationLabel,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = false;
            try
            {
                ready = await _page.EvaluateAsync<bool>(
                    """
                    ({ slotId, gid }) => {
                      const match = window.location.href.match(/[?&]id=(\d+)/);
                      const currentSlot = match ? Number(match[1]) : null;
                      if (currentSlot !== slotId) return false;
                      return !!document.querySelector(
                        `#contract_building${gid}, #building${gid}, [data-gid="${gid}"], #contract_building, [id^="contract_building"]`
                      );
                    }
                    """,
                    new { slotId, gid });
            }
            catch (Exception ex) when (IsTransientExecutionContextException(ex))
            {
                Notify($"{operationLabel} construct-page verification hit transient navigation: {ex.Message}");
            }

            if (ready)
            {
                return;
            }

            Notify($"{operationLabel} expected construct choices for slot {slotId}/gid {gid}, but current url is '{_page.Url}'. Reopening attempt {attempt}/2.");
            await GotoAsync(constructUrl, cancellationToken);
            await EnsureLoggedInAsync();
        }

        throw new InvalidOperationException(
            $"{operationLabel} could not load the construct-choice page for slot {slotId}/gid {gid}; current url is '{_page.Url}'.");
    }


}

