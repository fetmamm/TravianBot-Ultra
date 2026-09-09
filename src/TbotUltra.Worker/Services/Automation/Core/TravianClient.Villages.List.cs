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
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task<IReadOnlyList<Village>> ReadCurrentVillageMembershipAsync(
        CancellationToken cancellationToken = default)
    {
        using var trace = _browserTrace.BeginOperation(
            "READ",
            "village-membership-sidebar",
            "scope=account source=live-sidebar");
        await WaitForPageReadyAsync(cancellationToken);
        var villages = await ReadVillagesFromCurrentPageAsync(cancellationToken);
        trace.Complete("success", $"count={villages.Count}");
        return villages;
    }

    private async Task<IReadOnlyList<Village>> ReadVillagesAsync(CancellationToken cancellationToken)
    {
        using var trace = _browserTrace.BeginOperation("READ", "villages", "scope=account source=cache-or-profile");
        Notify("[scan:verbose] ReadVillagesAsync started");
        // Only navigate to spieler.php when the population cache has been explicitly invalidated
        // (i.e. on a real village switch, where SwitchToVillageAsync resets the timestamp to
        // MinValue). Otherwise serve the cache: lightweight sidebar reads and incremental
        // population updates keep it current without an expensive spieler navigation. This means
        // resource ticks / ui-sync no longer trigger a spieler read.
        var populationInvalidated = _cachedVillagesPopulationAt == DateTimeOffset.MinValue;
        var capitalProfileVerificationDue = IsCapitalProfileVerificationDue();
        if (_cachedVillages is { Count: > 0 } cached && !populationInvalidated && !capitalProfileVerificationDue)
        {
            var ageMs = (long)(DateTimeOffset.UtcNow - _cachedVillagesAt).TotalMilliseconds;
            _browserTrace.Event("CACHE", "villages-hit", "hit", $"ageMs={ageMs} count={cached.Count} populationInvalidated=false");
            trace.Complete("success", $"source=cache count={cached.Count} ageMs={ageMs}");
            return cached;
        }

        _browserTrace.Event(
            "CACHE",
            "villages-miss",
            "miss",
            $"reason={(capitalProfileVerificationDue ? "capital-profile-verification" : populationInvalidated ? "population-invalidated" : "missing")}");

        if (capitalProfileVerificationDue)
        {
            Notify("[capital] cached capital state is uncertain; refreshing player profile.");
        }

        var villages = await ReadVillagesFromServerAsync(cancellationToken);
        if (villages.Count > 0)
        {
            UpdateCachedVillages(villages);
            if (villages.Any(v => v.Population.HasValue))
            {
                _cachedVillagesPopulationAt = DateTimeOffset.UtcNow;
            }
        }
        Notify("[scan:verbose] ReadVillagesAsync finished");
        trace.Complete("success", $"source=profile count={villages.Count}");
        return villages;
    }

    // Like ReadVillagesAsync but never navigates to spieler.php just to refresh the list.
    // The current sidebar is checked first because it is the earliest reliable signal for a
    // newly founded village. The cache is only a fallback when the current page has no readable
    // village list (for example during a navigation transition).
    // Used by lightweight refresh paths (e.g. post-upgrade) where the page navigation would
    // appear to the user as an unnecessary refresh.
    private async Task<IReadOnlyList<Village>> ReadVillagesPreferCacheAsync(
        CancellationToken cancellationToken,
        bool allowProfileNavigation = true)
    {
        using var trace = _browserTrace.BeginOperation("READ", "villages-prefer-cache", "scope=account source=cache-or-sidebar");
        _browserTrace.Event("CACHE", "villages-check", "observed", "source=current-page-sidebar reason=discover-new-villages");

        try
        {
            var sidebar = await ReadVillagesFromCurrentPageAsync(cancellationToken);
            if (sidebar.Count > 0)
            {
                if (_cachedVillages is { Count: > 0 } prior)
                {
                    var membershipVerifiedVillages = await TryVerifyMissingSidebarVillagesAsync(
                        sidebar,
                        prior,
                        cancellationToken,
                        allowProfileNavigation);
                    if (membershipVerifiedVillages is not null)
                    {
                        trace.Complete("success", $"source=profile-membership-verification count={membershipVerifiedVillages.Count}");
                        return membershipVerifiedVillages;
                    }

                    // Fresh sidebar coordinates are authoritative. A partial render must still keep
                    // villages from the last verified list instead of shrinking account automation.
                    var merged = VillageIdentityReconciler.MergeFreshListWithCached(sidebar, prior);
                    UpdateCachedVillages(merged);
                    if (allowProfileNavigation && IsCapitalProfileVerificationDue())
                    {
                        Notify("[capital] sidebar capital state is uncertain; refreshing player profile.");
                        var verifiedProfileVillages = await ReadVillagesFromServerAsync(cancellationToken);
                        UpdateCachedVillages(verifiedProfileVillages);
                        return verifiedProfileVillages;
                    }
                    trace.Complete("success", $"source=sidebar-merged count={merged.Count}");
                    return _cachedVillages!;
                }

                UpdateCachedVillages(sidebar);
                if (allowProfileNavigation && IsCapitalProfileVerificationDue())
                {
                    Notify("[capital] sidebar capital state is uncertain; refreshing player profile.");
                    var verifiedProfileVillages = await ReadVillagesFromServerAsync(cancellationToken);
                    UpdateCachedVillages(verifiedProfileVillages);
                    return verifiedProfileVillages;
                }
                trace.Complete("success", $"source=sidebar count={sidebar.Count}");
                return _cachedVillages!;
            }
        }
        catch (Exception ex)
        {
            Notify($"[scan:verbose] ReadVillagesPreferCache sidebar read failed, falling back: {ex.Message}");
        }

        if (_cachedVillages is { Count: > 0 } cached)
        {
            var ageMs = (long)(DateTimeOffset.UtcNow - _cachedVillagesAt).TotalMilliseconds;
            _browserTrace.Event("CACHE", "villages-hit", "hit", $"ageMs={ageMs} count={cached.Count} reason=sidebar-empty-or-failed");
            trace.Complete("success", $"source=cache-fallback count={cached.Count} ageMs={ageMs}");
            return cached;
        }

        if (!allowProfileNavigation)
        {
            trace.Complete("success", "source=empty-no-navigation count=0");
            return [];
        }

        var profile = await ReadVillagesAsync(cancellationToken);
        trace.Complete("success", $"source=profile-fallback count={profile.Count}");
        return profile;
    }

    private async Task<IReadOnlyList<Village>> ReadVillagesForUiSyncAsync(CancellationToken cancellationToken)
    {
        using var trace = _browserTrace.BeginOperation(
            "READ",
            "villages-ui-sync",
            "scope=account source=live-sidebar-or-profile");

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await WaitForPageReadyAsync(cancellationToken);
                var sidebarVillages = await ReadVillagesFromCurrentPageAsync(cancellationToken);
                if (sidebarVillages.Count > 0)
                {
                    if (_cachedVillages is { Count: > 0 } cached)
                    {
                        var membershipVerifiedVillages = await TryVerifyMissingSidebarVillagesAsync(
                            sidebarVillages,
                            cached,
                            cancellationToken,
                            allowProfileNavigation: false);
                        if (membershipVerifiedVillages is not null)
                        {
                            trace.Complete("success", $"source=profile-membership-verification count={membershipVerifiedVillages.Count}");
                            return membershipVerifiedVillages;
                        }
                    }

                    var merged = _cachedVillages is { Count: > 0 } prior
                        ? VillageIdentityReconciler.MergeFreshListWithCached(sidebarVillages, prior)
                        : sidebarVillages.ToList();
                    UpdateCachedVillages(merged);
                    trace.Complete("success", $"source=live-sidebar count={_cachedVillages!.Count} attempt={attempt}");
                    return _cachedVillages;
                }

                _browserTrace.Event(
                    "CACHE",
                    "villages-ui-sync-sidebar-empty",
                    "miss",
                    $"attempt={attempt} url={_page.Url}");
            }
            catch (Exception ex) when (BrowserFailureClassifier.IsTransientNavigation(ex))
            {
                Notify($"[ui-sync] live village sidebar read transiently failed; "
                    + (attempt < 2 ? "retrying" : "using profile fallback")
                    + $": {ex.Message}");
            }

            if (attempt == 1)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        if (_cachedVillages is { Count: > 0 } cachedFallback)
        {
            trace.Complete("success", $"source=cache-fallback count={cachedFallback.Count}");
            return cachedFallback;
        }

        trace.Complete("success", "source=empty-no-navigation count=0");
        return [];
    }

    private async Task<IReadOnlyList<Village>?> TryVerifyMissingSidebarVillagesAsync(
        IReadOnlyList<Village> sidebarVillages,
        IReadOnlyList<Village> cachedVillages,
        CancellationToken cancellationToken,
        bool allowProfileNavigation = true)
    {
        var observedKeys = sidebarVillages
            .Select(VillageIdentityReconciler.BuildMembershipKey)
            .Where(key => key is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _session.VerifiedVillageMembershipSuspicions.RemoveWhere(observedKeys.Contains);

        var missingKeys = VillageIdentityReconciler.FindUnverifiedMissingVillageKeys(
            sidebarVillages,
            cachedVillages,
            _session.VerifiedVillageMembershipSuspicions);
        if (missingKeys.Count == 0 || !allowProfileNavigation)
        {
            return null;
        }

        Notify(
            $"[village-membership] live sidebar contains {sidebarVillages.Count}/{cachedVillages.Count} cached villages; "
            + "verifying ownership once on the player profile.");
        var profileVillages = await ReadVillagesFromServerAsync(cancellationToken);
        if (profileVillages.Count == 0)
        {
            Notify("[village-membership] profile verification returned no villages; preserving the cached list.");
            return null;
        }

        foreach (var missingKey in missingKeys)
        {
            _session.VerifiedVillageMembershipSuspicions.Add(missingKey);
        }

        var removalConfirmed = VillageIdentityReconciler.HasMissingCachedVillage(profileVillages, cachedVillages);
        _villageListRequiresAuthoritativeUiSync |= removalConfirmed;
        UpdateCachedVillages(profileVillages);
        Notify(
            $"[village-membership] profile verification complete: villages={profileVillages.Count} "
            + $"removedConfirmed={removalConfirmed.ToString().ToLowerInvariant()}.");
        return profileVillages;
    }

    private async Task<IReadOnlyList<Village>> ReadVillagesFromCurrentPageAsync(CancellationToken cancellationToken)
    {

        var raw = await _page.EvaluateAsync<SidebarVillageJs[]>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const parseCoordPart = (value) => {
                const text = clean(value).replace(/[()]/g, '');
                if (!text) return null;
                const parsed = Number.parseInt(text, 10);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const parseIntFromText = (value) => {
                const text = clean(value).replace(/[^\d]/g, '');
                if (!text) return null;
                const parsed = Number.parseInt(text, 10);
                return Number.isFinite(parsed) ? parsed : null;
              };
              // Official coordinate cells contain bidi direction marks and a Unicode minus (U+2212);
              // strip/normalize them before parsing a signed integer.
              const parseSignedInt = (value) => {
                const text = clean(value)
                  .replace(/[‪-‮‎‏]/g, '')
                  .replace(/−/g, '-');
                const match = text.match(/-?\d+/);
                if (!match) return null;
                const parsed = Number.parseInt(match[0], 10);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const rows = [];
              const seen = new Set();
              const selectors = [
                '#sidebarBoxVillagelist a[href*="newdid="]',
                '#villageList a[href*="newdid="]',
                '.villageList a[href*="newdid="]',
                'a.village-name[href*="newdid="]'
              ];

              for (const selector of selectors) {
                for (const node of document.querySelectorAll(selector)) {
                  const name = clean(node.textContent || node.getAttribute('title') || '');
                  const url = clean(node.getAttribute('href') || '');
                  if (!name || !url) continue;
                  const key = `${name}|${url}`;
                  if (seen.has(key)) continue;
                  seen.add(key);
                  const container = node.closest('li, .active, .listEntry, .village') || node.parentElement || node;
                  const classText = clean(`${node.className || ''} ${container.className || ''}`).toLowerCase();
                  const x = parseCoordPart(
                    container.querySelector('.coordinateX')?.textContent
                    || node.parentElement?.querySelector('.coordinateX')?.textContent
                    || '');
                  const y = parseCoordPart(
                    container.querySelector('.coordinateY')?.textContent
                    || node.parentElement?.querySelector('.coordinateY')?.textContent
                    || '');
                  // The active village's row carries its current population in
                  // a "div.population > span". Read it scoped to
                  // the active container, falling back to the active sidebar entry on the page.
                  const isActive = classText.includes('active');
                  let population = null;
                  if (isActive) {
                    population = parseIntFromText(
                      container.querySelector('.population span')?.textContent
                      || document.querySelector('#sidebarBoxVillagelist .active .population span')?.textContent
                      || '');
                  }
                  rows.push({
                    name,
                    url,
                    isCapital: classText.includes('capital') ? true : null,
                    x,
                    y,
                    population,
                    isActive
                  });
                }

                if (rows.length > 0) {
                  return rows;
                }
              }

              // Official T4.6: the village list renders each entry as
              // div.listEntry.village[data-did] with a placeholder href="#" (no newdid anchor).
              // Build the switch URL from data-did so renames/new villages are picked up here too.
              for (const entry of document.querySelectorAll(
                  '#sidebarBoxVillageList .listEntry.village[data-did], .villageList .listEntry.village[data-did], .listEntry.village[data-did]')) {
                const did = (entry.getAttribute('data-did') || '').trim();
                if (!did) continue;
                const name = clean(entry.querySelector('.name')?.textContent || '');
                if (!name) continue;
                const key = `did:${did}`;
                if (seen.has(key)) continue;
                seen.add(key);
                const classText = (entry.className || '').toLowerCase();
                const isActive = classText.includes('active');
                const x = parseSignedInt(entry.querySelector('.coordinateX')?.textContent || '');
                const y = parseSignedInt(entry.querySelector('.coordinateY')?.textContent || '');
                let population = null;
                if (isActive) {
                  // Official renders the active village's population in the active-village box
                  // (#sidebarBoxActiveVillage div.population > span), not inside the list entry.
                  population = parseIntFromText(
                    document.querySelector('#sidebarBoxActiveVillage .population span, .villageInfobox .population span, div.population span')?.textContent
                    || entry.querySelector('.population span, .population')?.textContent
                    || '');
                }
                rows.push({
                  name,
                  url: `dorf1.php?newdid=${did}`,
                  isCapital: classText.includes('capital') ? true : null,
                  x,
                  y,
                  population,
                  isActive
                });
              }

              return rows;
            }
            """);

        // Trust the active village's population read from the sidebar.
        return raw
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item =>
            {
                var (cachedX, cachedY) = TryGetCachedVillageCoords(item.Name!);
                int? sidebarPopulation = null;
                if (item.IsActive && item.Population is int pop)
                {
                    sidebarPopulation = pop;
                    if (_session.LogValueChanged($"pop:{item.Name}", pop.ToString()))
                    {
                        Notify($"[population] active village '{item.Name}' from sidebar = {pop}");
                    }
                }
                return new Village(
                    Name: item.Name!,
                    Url: item.Url,
                    IsCapital: item.IsCapital ?? (_session.CapitalProfileVerificationRequired
                        ? null
                        : TryGetCachedCapitalState(item.Name!)),
                    CoordX: item.X ?? cachedX,
                    CoordY: item.Y ?? cachedY,
                    Population: sidebarPopulation);
            })
            .ToList();
    }

    private void UpdateCachedVillages(IReadOnlyList<Village> villages)
    {
        if (villages.Count == 0)
        {
            return;
        }

        var withKnownTribes = ApplyKnownVillageTribes(villages);
        var capitalCount = withKnownTribes.Count(village => village.IsCapital == true);
        if (capitalCount > 1)
        {
            Notify($"[capital] conflicting cached capital flags ({capitalCount}); clearing them and requiring a live profile scan.");
            RequireCapitalProfileVerification($"shared village cache contains {capitalCount} capital candidates", delayRetry: false);
            _cachedVillages = CapitalStateResolver.ClearConflictingCapitalFlags(withKnownTribes).ToList();
            _cachedVillagesAt = DateTimeOffset.MinValue;
            return;
        }

        _cachedVillages = withKnownTribes;
        _cachedVillagesAt = DateTimeOffset.UtcNow;
        if (CapitalStateResolver.RequiresProfileVerification(withKnownTribes)
            && (!_session.CapitalProfileVerificationRequired || IsCapitalProfileVerificationDue()))
        {
            RequireCapitalProfileVerification("shared village cache has no confirmed capital", delayRetry: false);
        }
    }

    private async Task<int?> ReadActiveVillagePopulationFromCurrentPageAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await _page.EvaluateAsync<string?>(
            """
            () => document.querySelector(
              '#sidebarBoxActiveVillage .population span, ' +
              '.villageInfobox .population span, ' +
              '.listEntry.village.active .population span')?.textContent || null
            """);
        var digits = Regex.Replace(raw ?? string.Empty, @"[^\d]", string.Empty);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var population)
            ? population
            : null;
    }

    private List<Village> ApplyKnownVillageTribes(IReadOnlyList<Village> villages)
    {
        return villages.Select(village =>
        {
            if (IsKnownTribe(village.Tribe))
            {
                return village;
            }

            var did = TravianUrls.TryParseNewdid(village.Url);
            if (did.HasValue && _session.VillageTribes.TryGetValue($"did:{did.Value}", out var didTribe))
            {
                return village with { Tribe = didTribe };
            }

            if (village.CoordX.HasValue && village.CoordY.HasValue
                && _session.VillageTribes.TryGetValue($"xy:{village.CoordX.Value}|{village.CoordY.Value}", out var coordTribe))
            {
                return village with { Tribe = coordTribe };
            }

            return village;
        }).ToList();
    }

    private async Task<IReadOnlyList<Village>> ReadVillagesFromServerAsync(
        CancellationToken cancellationToken,
        bool restorePreviousUrl = true)
    {
        var previousUrl = _page.Url;
        string? activeVillageBeforeProfile = null;
        int? activeVillageDidBeforeProfile = null;
        (int? X, int? Y) activeCoordsBeforeProfile = (null, null);
        IReadOnlyList<Village> sidebarOrder = [];
        try
        {
            activeVillageBeforeProfile = await ReadActiveVillageNameAsync(cancellationToken);
            activeVillageDidBeforeProfile = await TryReadActiveVillageDidFromCurrentPageAsync(cancellationToken);
            activeCoordsBeforeProfile = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
            try
            {
                // The profile table may be population-sorted. Capture the user's Travian sidebar
                // order before navigating away; profile data will enrich these rows, not order them.
                sidebarOrder = await ReadVillagesFromCurrentPageAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Notify($"[scan:verbose] could not capture sidebar village order before profile read: {ex.Message}");
            }

            await GotoAsync(Paths.PlayerProfile, cancellationToken);
            await EnsureLoggedInAsync();
            try
            {
                await CaptureAccountTribeFromCurrentProfileAsync();
            }
            catch (Exception ex)
            {
                Notify($"[tribe] account profile detection failed: {ex.Message}");
            }

            // The profile can render the villages table client-side, and the
            // population cell (td.inhabitants) is filled in slightly after the row shell. Reading
            // too early returns the village name/coords/capital but a null population. Best-effort
            // wait for a populated inhabitants cell before parsing; timeout falls through to the parse anyway.
            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    () => {
                      const cells = document.querySelectorAll('table.villages td.inhabitants, td.inhabitants');
                      for (const cell of cells) {
                        if (/\d/.test((cell.textContent || ''))) {
                          return true;
                        }
                      }
                      return false;
                    }
                    """,
                    new PageWaitForFunctionOptions { Timeout = 3000 });
            }
            catch
            {
                Notify("[scan:verbose] villages table population cell not ready within wait; parsing current DOM.");
            }

            var raw = await _page.EvaluateAsync<PlayerProfileVillageRowJs[]>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
                  const parseIntFromText = (value) => {
                    const match = clean(value).match(/(\d[\d\s.]*)/);
                    if (!match) return null;
                    const digits = match[1].replace(/[^\d]/g, '');
                    if (!digits) return null;
                    const parsed = Number.parseInt(digits, 10);
                    return Number.isFinite(parsed) ? parsed : null;
                  };
                  const parseCoords = (textOrHref) => {
                    const source = clean(textOrHref || '')
                      .replace(/[‪-‮⁦-⁩‎‏]/g, '')
                      .replace(/−/g, '-');
                    const xQuery = source.match(/[?&]x=(-?\d+)/i);
                    const yQuery = source.match(/[?&]y=(-?\d+)/i);
                    if (xQuery && yQuery) {
                      return { x: Number.parseInt(xQuery[1], 10), y: Number.parseInt(yQuery[1], 10) };
                    }

                    const pair = source.match(/(-?\d+)\s*[|,]\s*(-?\d+)/);
                    if (!pair) return { x: null, y: null };
                    return {
                      x: Number.parseInt(pair[1], 10),
                      y: Number.parseInt(pair[2], 10)
                    };
                  };
                  const parseVillageId = (value) => {
                    const source = value || '';
                    const match =
                      source.match(/[?&]newdid=(\d+)/i)
                      || source.match(/[?&]vid=(\d+)/i)
                      || source.match(/[?&]z=(\d+)/i)
                      || source.match(/[?&]d=(\d+)/i);
                    if (!match) return null;
                    const parsed = Number.parseInt(match[1], 10);
                    return Number.isFinite(parsed) ? parsed : null;
                  };
                  const resolveVillageHref = (row, preferredHref) => {
                    const preferredId = parseVillageId(preferredHref || '');
                    if (preferredId !== null && !/karte\.php/i.test(preferredHref || '')) {
                      return preferredHref || '';
                    }

                    const candidates = [
                      preferredHref || '',
                      ...Array.from(row.querySelectorAll('a[href]')).map(node => node.getAttribute('href') || '')
                    ];
                    for (const href of candidates) {
                      const villageId = parseVillageId(href);
                      if (villageId !== null) {
                        return `dorf1.php?newdid=${villageId}`;
                      }
                    }

                    return preferredHref || '';
                  };

                  const rows = [];
                  const seen = new Set();
                  for (const row of document.querySelectorAll('table tr')) {
                    const rowText = clean(row.textContent || '');
                    if (!rowText) continue;

                    // Prefer village-switch URLs (newdid / dorf) over coord links (karte.php).
                    // Some servers render the village name as a karte link, which would make the
                    // bot navigate to the map instead of switching villages.
                    const nameAnchor =
                      row.querySelector('td.name a[href*="newdid"], td.village a[href*="newdid"], td:nth-child(1) a[href*="newdid"]')
                      || row.querySelector('td.name a[href*="dorf"], td.village a[href*="dorf"], td:nth-child(1) a[href*="dorf"]')
                      || row.querySelector('a[href*="newdid"]')
                      || row.querySelector('a[href*="dorf1.php"], a[href*="dorf2.php"]')
                      || row.querySelector('td.name a[href]:not([href*="karte"]), td.village a[href]:not([href*="karte"]), td:nth-child(1) a[href]:not([href*="karte"])')
                      || row.querySelector('td.name a[href], td.village a[href], td:nth-child(1) a[href]');
                    const name = clean(nameAnchor?.textContent || row.querySelector('td.name, td.village')?.textContent || '');
                    if (!name) continue;

                    const profileLikeRow = !!row.querySelector('td.coordinates, a[href*="karte.php"], span.additionalInfo');
                    if (!profileLikeRow) continue;

                    const villageHref = resolveVillageHref(row, nameAnchor?.getAttribute('href') || '');
                    // Prefer the actual coordinate link (karte.php?x=..&y=..) over the village-name
                    // link, which on official Travian also points at karte.php but only carries ?d=<did>.
                    const coordAnchor =
                      row.querySelector('td.coordinates a[href*="x="], a[href*="karte.php?x="], a[href*="x="][href*="y="]')
                      || row.querySelector('a[href*="karte.php"]');
                    const coordHref = coordAnchor?.getAttribute('href') || '';
                    const coordXText = clean(row.querySelector('td.coordinates .coordinateX')?.textContent || '');
                    const coordYText = clean(row.querySelector('td.coordinates .coordinateY')?.textContent || '');
                    const coordText = clean(coordAnchor?.textContent || row.querySelector('td.coordinates')?.textContent || '');
                    const coord = parseCoords(
                      coordHref
                      || (coordXText && coordYText ? `${coordXText}|${coordYText}` : '')
                      || coordText
                      || rowText);

                    const popText = clean(
                      row.querySelector('td.inhabitants, td.population, td.pop')?.textContent
                      || row.querySelector('td:nth-child(2)')?.textContent
                      || '');
                    let population = parseIntFromText(popText);
                    if (population === null) {
                      const compactNumbers = rowText.match(/\b\d{2,6}\b/g) || [];
                      if (compactNumbers.length > 0) {
                        population = Number.parseInt(compactNumbers[compactNumbers.length - 1], 10);
                      }
                    }

                    const cropMatch = rowText.match(/\b(\d{1,2})\s*c\b/i) || name.match(/\b(\d{1,2})\s*c\b/i);
                    const cropFields = cropMatch ? Number.parseInt(cropMatch[1], 10) : null;

                    const isCapital = Array.from(row.querySelectorAll('span.additionalInfo')).some(node => /\bcapital\b/i.test(node.textContent || ''));

                    // Special servers that allow one tribe per village render a per-row tribe icon
                    // (<td class="tribe"><i class="tribe8_medium"></i></td>). Normal servers have no
                    // such cell, so tribeId stays null and the caller keeps the village tribe as-is.
                    const tribeClass = row.querySelector('td.tribe [class*="tribe"]')?.getAttribute('class') || '';
                    const tribeIdMatch = tribeClass.match(/\btribe(\d+)/i);
                    const tribeId = tribeIdMatch ? Number.parseInt(tribeIdMatch[1], 10) : null;
                    const key = `${name}|${coord.x ?? ''}|${coord.y ?? ''}`;
                    if (seen.has(key)) continue;
                    seen.add(key);

                    rows.push({
                      name,
                      url: villageHref || '',
                      isCapital,
                      x: Number.isFinite(coord.x) ? coord.x : null,
                      y: Number.isFinite(coord.y) ? coord.y : null,
                      population: Number.isFinite(population) ? population : null,
                      cropFields: Number.isFinite(cropFields) ? cropFields : null,
                      tribeId: Number.isFinite(tribeId) ? tribeId : null
                    });
                  }

                  return rows;
                }
                """);

            var rawList = (raw ?? []).Where(v => !string.IsNullOrWhiteSpace(v.Name)).ToList();
            // If the spieler scan identified at least one capital village, trust the per-row data
            // verbatim — there is exactly one capital, and an OR with stale cache could keep an
            // old village marked as capital after the capital is moved.
            var reportedCapitalCount = rawList.Count(v => v.IsCapital);
            var trustScanCapital = reportedCapitalCount == 1;
            if (trustScanCapital)
            {
                ConfirmCapitalProfileVerification();
            }
            else
            {
                var reason = reportedCapitalCount == 0
                    ? "player profile contained no capital marker"
                    : $"player profile contained {reportedCapitalCount} capital markers";
                RequireCapitalProfileVerification(reason, delayRetry: true);
                ClearCachedCapitalStatesForCurrentAccount();
                Notify($"ALARM: [capital] profile verification failed: {reason}. Capital state remains unknown; retrying player profile in 20 minutes.");
            }

            var profileVillages = rawList
                .Select(v =>
                {
                    var duplicateName = rawList.Count(candidate =>
                        string.Equals(candidate.Name, v.Name, StringComparison.OrdinalIgnoreCase)) > 1;
                    var (cachedX, cachedY) = duplicateName
                        ? (null, null)
                        : TryGetCachedVillageCoords(v.Name!);
                    bool? resolvedCapital = trustScanCapital ? v.IsCapital : null;
                    var resolvedX = v.X ?? cachedX;
                    var resolvedY = v.Y ?? cachedY;
                    // The legacy capital cache is keyed by display name and therefore cannot safely
                    // represent two same-name villages. Do not let one twin overwrite the other.
                    if (!duplicateName && reportedCapitalCount <= 1)
                    {
                        SaveCachedVillageState(v.Name!, resolvedCapital, resolvedX, resolvedY);
                    }
                    // Only present on servers that allow a different tribe per village; elsewhere the
                    // profile has no tribe cell, so this stays "Unknown" and ApplyKnownVillageTribes
                    // fills it from the session cache exactly as before.
                    var rowTribe = (v.TribeId.HasValue
                        ? TroopCatalog.ResolveTribeFromTravianId(v.TribeId.Value)
                        : null) ?? "Unknown";
                    return new Village(
                        Name: v.Name!,
                        Url: ResolveUrl(v.Url ?? string.Empty),
                        IsCapital: resolvedCapital,
                        CoordX: resolvedX,
                        CoordY: resolvedY,
                        Population: v.Population,
                        CropFields: v.CropFields,
                        Tribe: rowTribe);
                })
                .ToList();

            if (profileVillages.Count > 0)
            {
                var ordered = new List<Village>(profileVillages.Count);
                foreach (var sidebarVillage in sidebarOrder)
                {
                    var sidebarId = TravianUrls.TryParseNewdid(sidebarVillage.Url);
                    var match = sidebarId is not null
                        ? profileVillages.FirstOrDefault(v => TravianUrls.TryParseNewdid(v.Url) == sidebarId)
                        : null;
                    match ??= profileVillages.FirstOrDefault(v =>
                        string.Equals(v.Name, sidebarVillage.Name, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !ordered.Contains(match))
                    {
                        ordered.Add(match);
                    }
                }

                ordered.AddRange(profileVillages.Where(v => !ordered.Contains(v)));
                var villages = ordered
                    .OrderByDescending(v => v.IsCapital == true)
                    .ToList();
                Notify($"[villages] order source={(sidebarOrder.Count > 0 ? "sidebar" : "profile")} "
                    + $"villages={string.Join(" > ", villages.Select(v => v.Name))}");
                return villages;
            }
        }
        finally
        {
            if (restorePreviousUrl && !string.IsNullOrWhiteSpace(previousUrl))
            {
                await GotoAsync(previousUrl, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(activeVillageBeforeProfile))
        {
            var activeCoords = VillageIdentityReconciler.HasCoordinates(activeCoordsBeforeProfile)
                ? activeCoordsBeforeProfile
                : await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
            if ((activeVillageDidBeforeProfile.HasValue || VillageIdentityReconciler.HasCoordinates(activeCoords))
                && _cachedVillages is { Count: > 0 } cachedVillages)
            {
                var enriched = VillageIdentityReconciler.EnrichActiveVillageCoordinates(
                    cachedVillages,
                    activeVillageDidBeforeProfile,
                    activeCoords);
                UpdateCachedVillages(enriched);
                if (VillageIdentityReconciler.HasCoordinates(activeCoords))
                {
                    SaveCachedVillageState(activeVillageBeforeProfile, null, activeCoords.X, activeCoords.Y);
                }
                return enriched;
            }
        }

        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const selectors = [
                '#sidebarBoxVillagelist a[href*="newdid"]',
                '#sidebarBoxVillageList a[href*="newdid"]',
                '#villageList a[href*="newdid"]',
                '.villageList a[href*="newdid"]',
                'a[href*="newdid"]'
              ];
              const seen = new Set();
              const villages = [];

              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const name = (element.textContent || '').replace(/\s+/g, ' ').trim();
                  const href = element.getAttribute('href');
                  const key = `${name}|${href}`;
                  if (!name || seen.has(key)) continue;
                  seen.add(key);
                  villages.push([name, href || '']);
                }
                if (villages.length) return JSON.stringify(villages);
              }

              // Official Travian (T4.6) renders the village switcher as
              // div.listEntry.village[data-did] with the name in span.name[data-did];
              // there are no newdid anchors (switching is JS-driven via data-did).
              // Reconstruct the classic switch URL dorf1.php?newdid=<did> so the rest
              // of the pipeline keeps working unchanged.
              for (const node of document.querySelectorAll('.listEntry.village[data-did], #sidebarBoxVillageList .name[data-did], span.name[data-did]')) {
                const did = node.getAttribute('data-did');
                if (!did) continue;
                const nameNode = node.classList.contains('name') ? node : node.querySelector('.name');
                const name = ((nameNode ? nameNode.textContent : node.textContent) || '').replace(/\s+/g, ' ').trim();
                const key = `${name}|${did}`;
                if (!name || seen.has(key)) continue;
                seen.add(key);
                villages.push([name, 'dorf1.php?newdid=' + did]);
              }
              if (villages.length) return JSON.stringify(villages);

              const heading = document.querySelector('h1, .titleInHeader, #content h2');
              const fallbackName = heading ? heading.textContent.replace(/\s+/g, ' ').trim() : '';
              return JSON.stringify(fallbackName ? [[fallbackName, '']] : []);
            }
            """);

        var rawFallback = string.IsNullOrWhiteSpace(rawJson)
            ? new List<List<string>>()
            : JsonSerializer.Deserialize<List<List<string>>>(rawJson) ?? new List<List<string>>();

        rawFallback ??= [];
        return rawFallback
            .Where(v => v.Count > 0 && !string.IsNullOrWhiteSpace(v[0]))
            .Select(v =>
            {
                var name = v[0];
                var (cx, cy) = TryGetCachedVillageCoords(name);
                return new Village(
                    Name: name,
                    Url: ResolveUrl(v.Count > 1 ? v[1] : string.Empty),
                    IsCapital: TryGetCachedCapitalState(name),
                    CoordX: cx,
                    CoordY: cy,
                    Population: null,
                    CropFields: null);
            })
            .OrderByDescending(v => v.IsCapital == true)
            .ToList();
    }

    private async Task<(int? X, int? Y)> TryReadActiveVillageCoordsFromCurrentPageAsync(CancellationToken cancellationToken)
    {

        var coord = await _page.EvaluateAsync<ActiveVillageCoordJs>(
            """
            () => {
              const clean = (value) => (value || '')
                .replace(/[‪-‮⁦-⁩‎‏]/g, '')
                .replace(/−/g, '-')
                .replace(/\s+/g, ' ')
                .trim();
              const parseCoords = (value) => {
                const source = clean(value);
                if (!source) return { x: null, y: null };

                const query = source.match(/[?&]x=(-?\d+).*?[?&]y=(-?\d+)/i) || source.match(/[?&]y=(-?\d+).*?[?&]x=(-?\d+)/i);
                if (query) {
                  const first = Number.parseInt(query[1], 10);
                  const second = Number.parseInt(query[2], 10);
                  if (source.toLowerCase().includes('?y=')) {
                    return { x: second, y: first };
                  }
                  return { x: first, y: second };
                }

                const pair = source.match(/\(\s*(-?\d+)\s*[|,]\s*(-?\d+)\s*\)/)
                  || source.match(/\b(-?\d+)\s*[|,]\s*(-?\d+)\b/);
                if (!pair) return { x: null, y: null };
                return {
                  x: Number.parseInt(pair[1], 10),
                  y: Number.parseInt(pair[2], 10)
                };
              };

              // Exact source first: the sidebar's active-village box carries the coordinates as data
              // attributes (#villageName data-x/data-y on Official T4.6) — no text parsing needed.
              const villageNameBox = document.querySelector('#villageName[data-x][data-y]');
              if (villageNameBox) {
                const x = Number.parseInt(villageNameBox.getAttribute('data-x'), 10);
                const y = Number.parseInt(villageNameBox.getAttribute('data-y'), 10);
                if (Number.isFinite(x) && Number.isFinite(y)) {
                  return { x, y };
                }
              }

              const candidates = [];
              const selectors = [
                '#villageNameField',
                '.villageNameField',
                '#sidebarBoxVillagelist .active',
                '#sidebarBoxVillagelist .active a[href*="newdid"]',
                '.listEntry.village.active',
                '#content h1',
                '.boxTitle'
              ];

              for (const selector of selectors) {
                for (const node of document.querySelectorAll(selector)) {
                  candidates.push(clean(node.textContent || ''));
                  if (node instanceof Element) {
                    candidates.push(clean(node.getAttribute('title') || ''));
                    candidates.push(clean(node.getAttribute('aria-label') || ''));
                    for (const anchor of node.querySelectorAll('a[href*="karte.php"], a[href*="x="][href*="y="]')) {
                      candidates.push(clean(anchor.getAttribute('href') || ''));
                      candidates.push(clean(anchor.textContent || ''));
                      candidates.push(clean(anchor.getAttribute('title') || ''));
                    }

                    const next = node.nextElementSibling;
                    const prev = node.previousElementSibling;
                    if (next) candidates.push(clean(next.textContent || ''));
                    if (prev) candidates.push(clean(prev.textContent || ''));
                  }
                }
              }

              for (const value of candidates) {
                const parsed = parseCoords(value);
                if (Number.isFinite(parsed.x) && Number.isFinite(parsed.y)) {
                  return parsed;
                }
              }

              for (const anchor of document.querySelectorAll('a[href*="karte.php"], a[href*="x="][href*="y="]')) {
                const parsed = parseCoords(anchor.getAttribute('href') || anchor.textContent || '');
                if (Number.isFinite(parsed.x) && Number.isFinite(parsed.y)) {
                  return parsed;
                }
              }

              return { x: null, y: null };
            }
            """);

        AutomationLogContext.UpdateVillageCoordinates(coord?.X, coord?.Y);
        return (coord?.X, coord?.Y);
    }

    private async Task<int?> TryReadActiveVillageDidFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawDid = await _page.EvaluateAsync<string?>(
            """
            () => document.querySelector('#villageName input[data-did]')?.getAttribute('data-did')
              || document.querySelector(
                '#sidebarBoxVillageList .listEntry.village.active[data-did], ' +
                '#sidebarBoxVillagelist .listEntry.village.active[data-did], ' +
                '.listEntry.village.active[data-did]')?.getAttribute('data-did')
              || null
            """);
        return int.TryParse(rawDid, out var did) ? did : null;
    }

    private async Task<string> ReadActiveVillageNameAsync(CancellationToken cancellationToken)
    {
        var value = await _page.EvaluateAsync<string>(
            """
            () => {
              // Strip Unicode bidi/direction marks (Travian wraps coords in them) + normalize the
              // U+2212 minus, then collapse whitespace.
              const clean = (raw) => (raw || '')
                .replace(/[‪-‮⁦-⁩‎‏]/g, '')
                .replace(/−/g, '-')
                .replace(/\s+/g, ' ')
                .trim();

              // Official T4.6: the active village is the highlighted sidebar entry. Read ONLY its name
              // span so the result is the clean village name, not "GREZ(-27|-66)" (name + coordinates).
              const nameSpan = document.querySelector(
                '.listEntry.village.active .name, #sidebarBoxVillagelist .active .name, .villageList .active .name');
              const spanText = clean(nameSpan ? nameSpan.textContent : '');
              if (spanText) return spanText;

              const selectors = [
                '#villageNameField',
                '#villageNameField.boxTitle',
                '.villageList .active',
                '#villageList .active',
                '#sidebarBoxVillagelist .active',
                '.villageNameField',
                'h1',
                '.titleInHeader'
              ];

              for (const selector of selectors) {
                const element = document.querySelector(selector);
                // Drop a trailing coordinate part "(x|y)" that some headers append after the name.
                const text = clean(element ? element.textContent : '').replace(/\s*\([^)]*\)\s*$/, '').trim();
                if (text) return text;
              }

              return 'Unknown village';
            }
            """);
        var activeVillage = string.IsNullOrWhiteSpace(value) ? "Unknown village" : value;
        if (!string.Equals(activeVillage, "Unknown village", StringComparison.OrdinalIgnoreCase))
        {
            AutomationLogContext.UpdateVillageName(activeVillage);
        }

        return activeVillage;
    }

}
