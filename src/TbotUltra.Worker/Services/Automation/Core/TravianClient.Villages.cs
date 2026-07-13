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
    public async Task<IReadOnlyList<VillageStatus>> ReadAllVillageStatusesAsync(CancellationToken cancellationToken = default)
    {
        Notify("[scan] all-village status scan starting");
        var returnVillageName = await TryReadActiveVillageNameSafeAsync(cancellationToken);
        await GotoAsync(Paths.Resources, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the village overview.", cancellationToken);
        await EnsureLoggedInAsync();

        var villages = await ReadVillagesAsync(cancellationToken);
        if (villages.Count == 0)
        {
            return [await ReadCurrentVillageStatusAsync(cancellationToken)];
        }

        var statuses = new List<VillageStatus>();
        try
        {
            var scanIndex = 0;
            foreach (var village in villages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanIndex++;
                Notify($"[scan:verbose] reading village '{village.Name}' ({scanIndex}/{villages.Count})");

                if (!string.IsNullOrWhiteSpace(village.Url))
                {
                    await GotoAsync(village.Url, cancellationToken);
                }
                else
                {
                    await GotoAsync(Paths.Resources, cancellationToken);
                }

                await PauseForManualStepIfVisibleAsync(
                    $"Manual verification appeared while switching to village '{village.Name}'.",
                    cancellationToken);
                await EnsureLoggedInAsync();
                await ApplyActionDelayAsync(cancellationToken);
                statuses.Add(await ReadCurrentVillageStatusAsync(cancellationToken));
            }
        }
        finally
        {
            Notify($"[scan] all-village status scan finished — read {statuses.Count}/{villages.Count} village(s)");
            if (!string.IsNullOrWhiteSpace(returnVillageName))
            {
                try
                {
                    await SwitchToVillageAsync(returnVillageName, cancellationToken: cancellationToken, skipFeatureRefresh: true);
                    await GotoAsync(Paths.Resources, cancellationToken);
                    Notify($"[scan:verbose] returned to original village '{returnVillageName}' on dorf1");
                }
                catch (Exception ex)
                {
                    Notify($"[scan] could not return to original village '{returnVillageName}': {ex.Message}");
                }
            }
        }

        return statuses;
    }

    public async Task SwitchToVillageAsync(string villageName = "", string? villageUrl = null, CancellationToken cancellationToken = default, bool skipFeatureRefresh = false)
    {
        var activeVillageBeforeSwitch = await TryReadActiveVillageNameSafeAsync(cancellationToken);
        var requestedLabel = !string.IsNullOrWhiteSpace(villageName)
            ? $"'{villageName}'"
            : (!string.IsNullOrWhiteSpace(villageUrl) ? $"url={villageUrl}" : "(unspecified)");
        Notify($"[village-switch] requested {requestedLabel} — current='{activeVillageBeforeSwitch ?? "(unknown)"}'");
        var requestedVillage = !string.IsNullOrWhiteSpace(villageName)
            ? await TryResolveVillageForSwitchAsync(villageName, cancellationToken)
            : null;
        var resolvedVillageName = string.IsNullOrWhiteSpace(requestedVillage?.Name) ? villageName : requestedVillage!.Name;
        var requestedCoords = ResolveVillageCoords(villageName, requestedVillage);

        // If we are already on the requested village, no navigation is needed.
        if (!string.IsNullOrWhiteSpace(villageName)
            && !string.IsNullOrWhiteSpace(activeVillageBeforeSwitch)
            && IsAcceptedVillageSwitchName(activeVillageBeforeSwitch, villageName, resolvedVillageName))
        {
            if (!IsSameVillageName(activeVillageBeforeSwitch, villageName))
            {
                Notify($"[village-switch] already on renamed village '{activeVillageBeforeSwitch}' for requested '{villageName}' — no navigation needed");
                RememberRenamedVillage(villageName, activeVillageBeforeSwitch, requestedVillage, requestedCoords);
            }
            else
            {
                Notify($"[village-switch] already on '{villageName}' — no navigation needed");
            }

            return;
        }

        // karte.php links open the map, they do not switch active village. If a stale picker
        // value passed us a coord URL, ignore it and fall back to a name-based lookup.
        var url = villageUrl;
        if (!string.IsNullOrWhiteSpace(url) && url.Contains("karte.php", StringComparison.OrdinalIgnoreCase))
        {
            url = null;
        }

        // Most reliable path: read the village's switch URL from the in-page sidebar
        // (`<a class="village-name" href="dorf1.php?newdid=X">`). It's present on every page and always
        // carries the correct newdid. Prefer it over a passed-in/cached URL whenever the village name is
        // known — cached payload URLs can be stale or wrong (wrong newdid silently fails to switch and
        // desyncs the working village). Fall back to the passed-in URL only if the sidebar has no match.
        if (!string.IsNullOrWhiteSpace(villageName))
        {
            var sidebarUrl = await TryGetVillageHrefFromSidebarAsync(villageName, cancellationToken);
            if (string.IsNullOrWhiteSpace(sidebarUrl) && HasVillageCoords(requestedCoords))
            {
                sidebarUrl = await TryGetVillageHrefFromSidebarByCoordsAsync(requestedCoords, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(sidebarUrl))
            {
                url = sidebarUrl;
            }
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            await GotoAsync(TravianUrls.CanonicalizeVillageSwitchUrl(url), cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(villageName))
        {
            var villages = await ReadVillagesAsync(cancellationToken);
            var match = FindVillageByNameOrCoords(villages, villageName, requestedCoords);
            if (match is null)
            {
                throw new InvalidOperationException($"Could not find village '{villageName}' in the village list.");
            }

            await GotoAsync(TravianUrls.CanonicalizeVillageSwitchUrl(match.Url!), cancellationToken);
        }
        else
        {
            return;
        }

        // A village switch must land on a logged-in game page. A contaminated/stale switch URL can hit
        // the site root, which the server serves as the login page (looks like a logout). Detect that
        // here and recover via the normal login flow instead of silently reading the wrong village.
        if ((await LoginStateAsync()) != "logged_in")
        {
            Notify($"[village-switch] landed on a non-game page after switching to {requestedLabel} — recovering via login.");
            await EnsureLoggedInAsync(force: true, cancellationToken);
        }

        await PauseForManualStepIfVisibleAsync($"Manual verification appeared while switching to village '{villageName}'.", cancellationToken);
        await EnsureLoggedInAsync();
        var activeVillageAfterSwitch = await TryReadActiveVillageNameSafeAsync(cancellationToken);
        var activeMatchesRequested = string.IsNullOrWhiteSpace(villageName)
            || IsAcceptedVillageSwitchName(activeVillageAfterSwitch, villageName, resolvedVillageName);
        if (activeMatchesRequested
            && !string.IsNullOrWhiteSpace(villageName)
            && !IsSameVillageName(activeVillageAfterSwitch, villageName)
            && IsSameVillageName(activeVillageAfterSwitch, resolvedVillageName))
        {
            Notify($"[village-switch] accepted resolved renamed village: requested '{villageName}' but active village is '{activeVillageAfterSwitch ?? "(unknown)"}'.");
            RememberRenamedVillage(villageName, activeVillageAfterSwitch, requestedVillage, requestedCoords);
        }

        if (!activeMatchesRequested && HasVillageCoords(requestedCoords))
        {
            var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
            if (SameVillageCoords(activeCoords, requestedCoords))
            {
                activeMatchesRequested = true;
                Notify($"[village-switch] accepted renamed village: requested '{villageName}' but active village is '{activeVillageAfterSwitch ?? "(unknown)"}' at ({requestedCoords.X}|{requestedCoords.Y}).");
                RememberRenamedVillage(villageName, activeVillageAfterSwitch, requestedVillage, requestedCoords);
            }
        }

        // Verify we actually landed on the REQUESTED village — not merely that "something changed". A stale
        // or wrong newdid in the payload URL navigates but leaves the active village unchanged, which would
        // desync the program's working village from the browser (it would then run tasks on the wrong
        // village). When a name was requested and we are not on it, retry once via the in-page sidebar href,
        // which always carries the correct newdid, then re-verify.
        if (!string.IsNullOrWhiteSpace(villageName)
            && !activeMatchesRequested)
        {
            Notify($"[village-switch] expected '{villageName}' but active village reads '{activeVillageAfterSwitch ?? "(unknown)"}' — retrying via sidebar.");
            var sidebarUrl = await TryGetVillageHrefFromSidebarAsync(villageName, cancellationToken);
            if (string.IsNullOrWhiteSpace(sidebarUrl) && HasVillageCoords(requestedCoords))
            {
                sidebarUrl = await TryGetVillageHrefFromSidebarByCoordsAsync(requestedCoords, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(sidebarUrl))
            {
                var canonicalSidebar = TravianUrls.CanonicalizeVillageSwitchUrl(sidebarUrl);
                var canonicalUsed = string.IsNullOrWhiteSpace(url) ? string.Empty : TravianUrls.CanonicalizeVillageSwitchUrl(url);
                if (!string.Equals(canonicalSidebar, canonicalUsed, StringComparison.OrdinalIgnoreCase))
                {
                    await GotoAsync(canonicalSidebar, cancellationToken);
                    await EnsureLoggedInAsync();
                    activeVillageAfterSwitch = await TryReadActiveVillageNameSafeAsync(cancellationToken);
                    activeMatchesRequested = IsAcceptedVillageSwitchName(activeVillageAfterSwitch, villageName, resolvedVillageName);
                    if (activeMatchesRequested
                        && !IsSameVillageName(activeVillageAfterSwitch, villageName)
                        && IsSameVillageName(activeVillageAfterSwitch, resolvedVillageName))
                    {
                        Notify($"[village-switch] accepted resolved renamed village after retry: requested '{villageName}' but active village is '{activeVillageAfterSwitch ?? "(unknown)"}'.");
                        RememberRenamedVillage(villageName, activeVillageAfterSwitch, requestedVillage, requestedCoords);
                    }

                    if (!activeMatchesRequested && HasVillageCoords(requestedCoords))
                    {
                        var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
                        if (SameVillageCoords(activeCoords, requestedCoords))
                        {
                            activeMatchesRequested = true;
                            Notify($"[village-switch] accepted renamed village after retry: requested '{villageName}' but active village is '{activeVillageAfterSwitch ?? "(unknown)"}' at ({requestedCoords.X}|{requestedCoords.Y}).");
                            RememberRenamedVillage(villageName, activeVillageAfterSwitch, requestedVillage, requestedCoords);
                        }
                    }
                }
            }
        }

        // Still not on the requested village after the sidebar retry: abort rather than run the task on the
        // wrong village. The caller (runtime loop) will retry on the next cycle.
        if (!string.IsNullOrWhiteSpace(villageName)
            && !activeMatchesRequested)
        {
            Notify(
                $"[village-switch] village_missing_suspected requested='{villageName}' " +
                $"active='{activeVillageAfterSwitch ?? "(unknown)"}'; aborting before task execution.");
            throw new InvalidOperationException(
                $"Village switch failed: requested '{villageName}' but the browser is on '{activeVillageAfterSwitch ?? "(unknown)"}'. "
                + "Aborting so the task does not run on the wrong village.");
        }

        if (!IsSameVillageName(activeVillageBeforeSwitch, activeVillageAfterSwitch))
        {
            Notify($"[village-switch] now on '{activeVillageAfterSwitch ?? "(unknown)"}' (was '{activeVillageBeforeSwitch ?? "(unknown)"}')");
            // Allow population to refresh from the sidebar on the next read.
            _cachedVillagesPopulationAt = DateTimeOffset.MinValue;
            _populationBaselineRead = false;
            // NOTE: no capital re-check here. RefreshCapitalStateForActiveVillageAsync navigates to
            // spieler.php (profile), which added a slow extra navigation on every switch and caused the
            // resource read to occasionally land mid-navigation (resources/storage not filling). Capital
            // is resolved from cache + fast-detection (resource field > level 10) on the resource read,
            // and at login analysis — so the switch only needs dorf1/dorf2 of the target village.
        }
        else if (!string.IsNullOrWhiteSpace(villageName))
        {
            // Same name before and after, but it IS the requested village (we were already there or the
            // sidebar retry confirmed it) — nothing to log as a change.
        }
        else
        {
            Notify($"[village-switch] navigation completed but active village still reads '{activeVillageAfterSwitch ?? "(unknown)"}'");
        }

        if (!skipFeatureRefresh)
        {
            // Re-emit account signals so UI refreshes after a village switch (Plus/Gold can be unchanged but UI may not have them yet).
            await RefreshAccountFeatureSignalsAsync(cancellationToken);
        }
    }

    internal static bool IsAcceptedVillageSwitchNameForTests(string? activeVillageName, string? requestedVillageName, string? resolvedVillageName)
        => IsAcceptedVillageSwitchName(activeVillageName, requestedVillageName, resolvedVillageName);

    private static bool IsAcceptedVillageSwitchName(string? activeVillageName, string? requestedVillageName, string? resolvedVillageName)
    {
        return IsSameVillageName(activeVillageName, requestedVillageName)
            || (!string.IsNullOrWhiteSpace(resolvedVillageName)
                && IsSameVillageName(activeVillageName, resolvedVillageName));
    }

    private static bool IsSameVillageName(string? left, string? right)
    {
        var normalizedLeft = NormalizeVillageNameForComparison(left);
        var normalizedRight = NormalizeVillageNameForComparison(right);
        return normalizedLeft.Length > 0
            && normalizedRight.Length > 0
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Village?> TryResolveVillageForSwitchAsync(string villageName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(villageName))
        {
            return null;
        }

        var cachedByName = _cachedVillages?.FirstOrDefault(v => IsSameVillageName(v.Name, villageName));
        var requestedCoords = ResolveVillageCoords(villageName, cachedByName);
        if (cachedByName is not null && HasVillageCoords(requestedCoords))
        {
            return cachedByName;
        }

        try
        {
            var sidebarVillages = await ReadVillagesFromCurrentPageAsync(cancellationToken);
            var match = FindVillageByNameOrCoords(sidebarVillages, villageName, requestedCoords);
            if (match is not null)
            {
                if (!IsSameVillageName(match.Name, villageName) && HasVillageCoords(requestedCoords))
                {
                    Notify($"[village-switch] resolved renamed village '{villageName}' -> '{match.Name}' by coordinates ({requestedCoords.X}|{requestedCoords.Y}).");
                }

                return match;
            }
        }
        catch (Exception ex)
        {
            Notify($"[village-switch:verbose] could not resolve '{villageName}' from sidebar coordinates: {ex.Message}");
        }

        return cachedByName;
    }

    private (int? X, int? Y) ResolveVillageCoords(string? villageName, Village? village)
    {
        if (village?.CoordX is int vx && village.CoordY is int vy)
        {
            return (vx, vy);
        }

        if (!string.IsNullOrWhiteSpace(villageName))
        {
            return TryGetCachedVillageCoords(villageName);
        }

        return (null, null);
    }

    private static Village? FindVillageByNameOrCoords(
        IReadOnlyList<Village> villages,
        string villageName,
        (int? X, int? Y) coords)
    {
        var byName = villages.FirstOrDefault(v =>
            IsSameVillageName(v.Name, villageName) &&
            !string.IsNullOrWhiteSpace(v.Url));
        if (byName is not null)
        {
            return byName;
        }

        if (!HasVillageCoords(coords))
        {
            return null;
        }

        return villages.FirstOrDefault(v =>
            SameVillageCoords((v.CoordX, v.CoordY), coords) &&
            !string.IsNullOrWhiteSpace(v.Url));
    }

    private static bool HasVillageCoords((int? X, int? Y) coords)
        => coords.X.HasValue && coords.Y.HasValue;

    private static bool SameVillageCoords((int? X, int? Y) left, (int? X, int? Y) right)
        => left.X.HasValue
            && left.Y.HasValue
            && right.X.HasValue
            && right.Y.HasValue
            && left.X.Value == right.X.Value
            && left.Y.Value == right.Y.Value;

    private void RememberRenamedVillage(
        string oldName,
        string? newName,
        Village? requestedVillage,
        (int? X, int? Y) coords)
    {
        if (string.IsNullOrWhiteSpace(oldName)
            || string.IsNullOrWhiteSpace(newName)
            || IsSameVillageName(oldName, newName)
            || !HasVillageCoords(coords))
        {
            return;
        }

        var capital = requestedVillage?.IsCapital ?? TryGetCachedCapitalState(oldName);
        SaveCachedVillageState(newName, capital, coords.X, coords.Y);

        if (_cachedVillages is not { Count: > 0 } cached)
        {
            return;
        }

        var changed = false;
        var requestedNewdid = requestedVillage is null ? null : TravianUrls.TryParseNewdid(requestedVillage.Url);
        var updated = cached
            .Select(v =>
            {
                var sameByCoords = SameVillageCoords((v.CoordX, v.CoordY), coords);
                var sameByName = IsSameVillageName(v.Name, oldName);
                var sameById = requestedNewdid is not null && TravianUrls.TryParseNewdid(v.Url) == requestedNewdid;
                if (!sameByCoords && !sameByName && !sameById)
                {
                    return v;
                }

                changed = true;
                return v with
                {
                    Name = newName.Trim(),
                    CoordX = coords.X,
                    CoordY = coords.Y,
                    IsCapital = v.IsCapital ?? capital
                };
            })
            .ToList();

        if (changed)
        {
            UpdateCachedVillages(updated);
        }
    }

    // Reconciles a renamed ACTIVE village into the cached list within one ui-sync tick, instead of
    // waiting for the next village switch or the cache-TTL sidebar refresh. The active village name is
    // read fresh every tick but the villages list is served from cache, so after an in-game rename the
    // ui-sync payload is internally inconsistent (ActiveVillage='1440' while the list still says
    // 'New village') — which makes the dashboard village name flicker back and forth.
    private async Task ReconcileActiveVillageNameInCacheAsync(string? activeVillageName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activeVillageName)
            || _cachedVillages is not { Count: > 0 } cached
            || cached.Any(v => IsSameVillageName(v.Name, activeVillageName)))
        {
            // Fast path: the active name already matches a cached village — nothing to reconcile. This
            // avoids a coordinate DOM read on every tick; the read below only runs right after a rename.
            return;
        }

        var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        var updated = ReconcileRenamedActiveVillageByCoords(cached, activeVillageName!, activeCoords);
        if (updated is not null)
        {
            Notify($"[village-rename] active village at ({activeCoords.X}|{activeCoords.Y}) renamed to '{activeVillageName}'; refreshed cached village name.");
            UpdateCachedVillages(updated);
        }
    }

    // Pure: refreshes the renamed active village's name in the cached list, matched by COORDINATES
    // (stable and unique per village — a village never moves and keeps its coords across renames, and
    // two villages can share a name but never coordinates). Returns null when nothing needs changing
    // (name already present, no coords, or no coordinate match).
    internal static IReadOnlyList<Village>? ReconcileRenamedActiveVillageByCoords(
        IReadOnlyList<Village> cached,
        string activeVillageName,
        (int? X, int? Y) activeCoords)
    {
        if (string.IsNullOrWhiteSpace(activeVillageName)
            || cached is not { Count: > 0 }
            || !HasVillageCoords(activeCoords)
            || cached.Any(v => IsSameVillageName(v.Name, activeVillageName)))
        {
            return null;
        }

        var changed = false;
        var updated = cached
            .Select(v =>
            {
                if (!SameVillageCoords((v.CoordX, v.CoordY), activeCoords)
                    || IsSameVillageName(v.Name, activeVillageName))
                {
                    return v;
                }

                changed = true;
                return v with { Name = activeVillageName.Trim() };
            })
            .ToList();

        return changed ? updated : null;
    }

    private static string NormalizeVillageNameForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value
            .Replace("\u202A", string.Empty)
            .Replace("\u202B", string.Empty)
            .Replace("\u202C", string.Empty)
            .Replace("\u202D", string.Empty)
            .Replace("\u202E", string.Empty)
            .Replace("\u200E", string.Empty)
            .Replace("\u200F", string.Empty)
            .Replace('−', '-');
        cleaned = Regex.Replace(cleaned, @"\s*\(\s*-?\d+\s*\|\s*-?\d+\s*\)\s*$", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }

    private async Task<string?> TryGetVillageHrefFromSidebarAsync(string villageName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Notify($"[village-switch:verbose] sidebar lookup for '{villageName}'");
        try
        {
            var href = await _page.EvaluateAsync<string?>(
                """
                (name) => {
                  const norm = (t) => (t || '')
                    .replace(/[‪-‮‎‏]/g, '')
                    .replace(/\s+/g, ' ')
                    .trim()
                    .toLowerCase();
                  const wanted = norm(name);
                  if (!wanted) return null;

                  // Official T4.6: the village switcher is React-rendered with no plain newdid anchors —
                  // each entry instead carries the real switch id in data-did and the clean name in a child
                  // ".name". Match on the exact name (short names like "BI"/"PI" must not substring-match)
                  // and build the canonical switch URL from data-did.
                  for (const entry of document.querySelectorAll('.listEntry.village[data-did]')) {
                    const did = entry.getAttribute('data-did');
                    if (!did) continue;
                    const nameEl = entry.querySelector('.name');
                    const text = norm(nameEl ? nameEl.textContent : entry.textContent);
                    if (text === wanted) {
                      return '/dorf1.php?newdid=' + did;
                    }
                  }

                  // Fallback layouts: static anchors carrying newdid in the href.
                  const candidates = [
                    ...document.querySelectorAll('a.village-name'),
                    ...document.querySelectorAll('#sidebarBoxVillagelist a[href*="newdid"]'),
                    ...document.querySelectorAll('#villageList a[href*="newdid"]'),
                    ...document.querySelectorAll('.villageList a[href*="newdid"]'),
                    ...document.querySelectorAll('a[href*="dorf1.php?newdid="]'),
                    ...document.querySelectorAll('a[href*="dorf2.php?newdid="]')
                  ];

                  const seen = new Set();
                  for (const link of candidates) {
                    const text = norm(link.textContent);
                    const href = link.getAttribute('href') || '';
                    if (!text || !href || seen.has(link)) continue;
                    seen.add(link);
                    if (text === wanted || text.includes(wanted)) {
                      return href;
                    }
                  }
                  return null;
                }
                """,
                villageName);

            if (string.IsNullOrWhiteSpace(href))
            {
                Notify($"[village-switch:verbose] sidebar had no matching link for '{villageName}' — falling back to spieler.php village list");
                return null;
            }
            var resolved = ResolveUrl(href);
            Notify($"[village-switch:verbose] sidebar matched '{villageName}' → {resolved}");
            return resolved;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"[village-switch:verbose] sidebar lookup transient navigation for '{villageName}': {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryGetVillageHrefFromSidebarByCoordsAsync((int? X, int? Y) coords, CancellationToken cancellationToken)
    {
        if (!HasVillageCoords(coords))
        {
            return null;
        }

        try
        {
            var villages = await ReadVillagesFromCurrentPageAsync(cancellationToken);
            var match = villages.FirstOrDefault(v => SameVillageCoords((v.CoordX, v.CoordY), coords));
            if (match is null || string.IsNullOrWhiteSpace(match.Url))
            {
                Notify($"[village-switch:verbose] sidebar had no village at coordinates ({coords.X}|{coords.Y}).");
                return null;
            }

            var resolved = ResolveUrl(match.Url);
            Notify($"[village-switch:verbose] sidebar matched coordinates ({coords.X}|{coords.Y}) -> '{match.Name}' → {resolved}");
            return resolved;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"[village-switch:verbose] sidebar coordinate lookup transient navigation for ({coords.X}|{coords.Y}): {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Notify($"[village-switch:verbose] sidebar coordinate lookup failed for ({coords.X}|{coords.Y}): {ex.Message}");
            return null;
        }
    }

    private async Task<VillageStatus> ReadCurrentVillageStatusAsync(
        CancellationToken cancellationToken,
        IReadOnlyList<Village>? knownVillages = null,
        IReadOnlyList<Building>? knownBuildings = null)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before reading village status.", cancellationToken);
        // Read the village list from the sidebar/cache instead of navigating to the profile (spieler.php).
        // On a village switch we only need dorf1/dorf2 of the target village for status; the profile was
        // only used to enumerate villages and re-check the capital — capital comes from cache here. This
        // avoids the extra (slow) profile navigation on every switch/status read.
        var villages = knownVillages is { Count: > 0 }
            ? knownVillages
            : await ReadVillagesPreferCacheAsync(cancellationToken);
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var buildQueue = await ReadBuildQueueAsync(cancellationToken);
        var remaining = TravianParsing.ResolveShortestQueueDurationSeconds(buildQueue);
        var currency = await ReadCurrencyAsync(cancellationToken);
        var unreadInbox = await ReadUnreadInboxCountsAsync(cancellationToken);
        await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
        var snapshot = await ReadResourceSnapshotAsync(cancellationToken);
        var resources = snapshot.Resources;
        var cachedSnapshot = TryGetCachedVillageResourceSnapshot(activeVillage);
        var capacities = (
            Warehouse: snapshot.Capacities.Warehouse ?? cachedSnapshot?.WarehouseCapacity,
            Granary: snapshot.Capacities.Granary ?? cachedSnapshot?.GranaryCapacity);
        var productionByHour = ResourceSnapshotCalculator.MergeProductionByHour(snapshot.ProductionByHour, cachedSnapshot?.ProductionByHour);
        var forecasts = ResourceSnapshotCalculator.BuildStorageForecasts(resources, capacities.Warehouse, capacities.Granary, productionByHour);
        var usingCachedProduction = !HasAnyProduction(snapshot.ProductionByHour) && HasAnyProduction(cachedSnapshot?.ProductionByHour);
        Notify($"Resource read: storage wh={FormatResourceLogNumber(capacities.Warehouse)} gr={FormatResourceLogNumber(capacities.Granary)} | stock {BuildResourceValueLog(resources)} | prod {BuildProductionValueLog(productionByHour)}{(usingCachedProduction ? " (cached production)" : string.Empty)}");

        var resourceFields = await ReadResourceFieldsAsync(cancellationToken);

        // Persist the per-village resource snapshot here too. This full status read runs on dorf1 right
        // after a village switch and is often the only place production is read for a freshly-switched
        // village. Without saving it, later current-page reads (on dorf2/build pages, where production is
        // not present) found an empty cache and showed "@-/h"/"not filling". SaveCached keeps existing
        // values when the new read is empty, so it never overwrites good data with blanks.
        SaveCachedVillageResourceSnapshot(activeVillage, resourceFields, capacities, productionByHour);

        var buildings = knownBuildings is { Count: > 0 }
            ? knownBuildings
            : await ReadBuildingsAsync(cancellationToken);
        // Read Travian's own in-progress construction list so the UI can show upgrades that were
        // started outside the program (e.g. manually before login) with the target level in
        // parentheses. We are on dorf1/dorf2 after the reads above, both of which carry the list,
        // so no extra navigation is needed.
        var activeConstructions = await ReadActiveConstructionsAsync(cancellationToken, allowNavigationToBuildings: false);
        var heroStatus = await ReadHeroStatusAsync(cancellationToken);
        var activeBuildCount = ConstructionSlots.ActiveBuildCount(buildQueue, activeConstructions);
        if (buildQueue.Count != activeConstructions.Count)
        {
            Notify(
                $"[construction-status:verbose] active count sources differ " +
                $"village='{activeVillage}' buildQueue={buildQueue.Count} " +
                $"activeConstructions={activeConstructions.Count} selected={activeBuildCount}");
        }

        return new VillageStatus(
            ActiveVillage: activeVillage,
            Villages: villages,
            Resources: resources,
            ResourceFields: resourceFields,
            Buildings: buildings,
            BuildQueue: buildQueue,
            Tribe: await ReadTribeAsync(cancellationToken),
            VillageCount: villages.Count,
            Gold: currency.Gold,
            Silver: currency.Silver,
            IsBuildingInProgress: activeBuildCount > 0,
            ActiveBuildCount: activeBuildCount,
            BuildQueueRemainingSeconds: remaining,
            BuildQueueRemainingText: remaining is int left ? TravianParsing.FormatDuration(left) : string.Empty,
            IsCapital: TryGetCachedCapitalState(activeVillage),
            ServerTimeUtc: _serverTimeUtc,
            UnreadMessages: unreadInbox.UnreadMessages,
            UnreadReports: unreadInbox.UnreadReports,
            WarehouseCapacity: capacities.Warehouse,
            GranaryCapacity: capacities.Granary,
            ResourceStorageForecasts: forecasts,
            ActiveConstructions: activeConstructions,
            BuildQueueFinish: remaining is > 0 ? TimerSnapshot.FromRemaining(remaining.Value) : null,
            HeroStatus: heroStatus,
            ActiveConstructionsFromOverview: _lastActiveConstructionsFromOverview);
    }

    private async Task<IReadOnlyList<Village>> ReadVillagesAsync(CancellationToken cancellationToken)
    {
        Notify("[scan:verbose] ReadVillagesAsync started");
        // Only navigate to spieler.php when the population cache has been explicitly invalidated
        // (i.e. on a real village switch, where SwitchToVillageAsync resets the timestamp to
        // MinValue). Otherwise serve the cache: lightweight sidebar reads and incremental
        // population updates keep it current without an expensive spieler navigation. This means
        // resource ticks / ui-sync no longer trigger a spieler read.
        var populationInvalidated = _cachedVillagesPopulationAt == DateTimeOffset.MinValue;
        if (_cachedVillages is { Count: > 0 } cached && !populationInvalidated)
        {
            return cached;
        }

        var villages = await ReadVillagesFromServerAsync(cancellationToken);
        if (villages.Count > 0)
        {
            _cachedVillages = villages.ToList();
            _cachedVillagesAt = DateTimeOffset.UtcNow;
            if (villages.Any(v => v.Population.HasValue))
            {
                _cachedVillagesPopulationAt = DateTimeOffset.UtcNow;
            }
        }
        Notify("[scan:verbose] ReadVillagesAsync finished");
        return villages;
    }

    // Like ReadVillagesAsync but never navigates to spieler.php just to refresh the list.
    // Order: fresh cache -> stale cache -> sidebar of current page -> server (last resort).
    // Used by lightweight refresh paths (e.g. post-upgrade) where the page navigation would
    // appear to the user as an unnecessary refresh.
    private async Task<IReadOnlyList<Village>> ReadVillagesPreferCacheAsync(CancellationToken cancellationToken)
    {
        if (_cachedVillages is { Count: > 0 } cached
            && DateTimeOffset.UtcNow - _cachedVillagesAt < VillagesCacheTtl)
        {
            return cached;
        }

        try
        {
            var sidebar = await ReadVillagesFromCurrentPageAsync(cancellationToken);
            if (sidebar.Count > 0)
            {
                if (_cachedVillages is { Count: > 0 } prior)
                {
                    var merged = sidebar
                        .Select(v =>
                        {
                            // Match the prior cache by the stable village id (newdid) first so a
                            // renamed village still merges with its cached coords/population instead
                            // of being treated as a new village. Fall back to the name only when no
                            // id is available on either side.
                            var villageId = TravianUrls.TryParseNewdid(v.Url);
                            var match = villageId is not null
                                ? prior.FirstOrDefault(p => TravianUrls.TryParseNewdid(p.Url) == villageId)
                                : null;
                            match ??= prior.FirstOrDefault(p => string.Equals(p.Name, v.Name, StringComparison.Ordinal));
                            if (match is null)
                            {
                                return v;
                            }
                            return v with
                            {
                                IsCapital = v.IsCapital ?? match.IsCapital,
                                CoordX = match.CoordX,
                                CoordY = match.CoordY,
                                // Sidebar-derived population (active village, official) wins; fall
                                // back to the cached value so non-active villages keep profile data.
                                Population = v.Population ?? match.Population,
                                CropFields = match.CropFields,
                            };
                        })
                        .ToList();
                    _cachedVillages = merged;
                    _cachedVillagesAt = DateTimeOffset.UtcNow;
                    return merged;
                }

                _cachedVillages = sidebar.ToList();
                _cachedVillagesAt = DateTimeOffset.UtcNow;
                return sidebar;
            }
        }
        catch (Exception ex)
        {
            Notify($"[scan:verbose] ReadVillagesPreferCache sidebar read failed, falling back: {ex.Message}");
        }

        if (_cachedVillages is { Count: > 0 } stale)
        {
            return stale;
        }

        return await ReadVillagesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Village>> ReadVillagesFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading villages from current page.", cancellationToken);

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
                    IsCapital: item.IsCapital ?? TryGetCachedCapitalState(item.Name!),
                    CoordX: item.X ?? cachedX,
                    CoordY: item.Y ?? cachedY,
                    Population: sidebarPopulation);
            })
            .ToList();
    }

    private void InvalidateVillagesCache() => _cachedVillagesAt = DateTimeOffset.MinValue;

    private void UpdateCachedVillages(IReadOnlyList<Village> villages)
    {
        if (villages.Count == 0)
        {
            return;
        }

        _cachedVillages = villages.ToList();
        _cachedVillagesAt = DateTimeOffset.UtcNow;
    }

    private async Task<IReadOnlyList<Village>> ReadVillagesFromServerAsync(
        CancellationToken cancellationToken,
        bool restorePreviousUrl = true)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading villages.", cancellationToken);
        var previousUrl = _page.Url;
        string? activeVillageBeforeProfile = null;
        IReadOnlyList<Village> sidebarOrder = [];
        try
        {
            activeVillageBeforeProfile = await ReadActiveVillageNameAsync(cancellationToken);
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
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading villages on spieler.php.", cancellationToken);
            await EnsureLoggedInAsync();

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
                    const source = textOrHref || '';
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
                      cropFields: Number.isFinite(cropFields) ? cropFields : null
                    });
                  }

                  return rows;
                }
                """);

            var rawList = (raw ?? []).Where(v => !string.IsNullOrWhiteSpace(v.Name)).ToList();
            // If the spieler scan identified at least one capital village, trust the per-row data
            // verbatim — there is exactly one capital, and an OR with stale cache could keep an
            // old village marked as capital after the capital is moved.
            var trustScanCapital = rawList.Any(v => v.IsCapital);

            var profileVillages = rawList
                .Select(v =>
                {
                    var cachedCapital = TryGetCachedCapitalState(v.Name!);
                    var (cachedX, cachedY) = TryGetCachedVillageCoords(v.Name!);
                    var resolvedCapital = trustScanCapital ? v.IsCapital : (v.IsCapital || cachedCapital == true);
                    var resolvedX = v.X ?? cachedX;
                    var resolvedY = v.Y ?? cachedY;
                    SaveCachedVillageState(v.Name!, resolvedCapital, resolvedX, resolvedY);
                    return new Village(
                        Name: v.Name!,
                        Url: ResolveUrl(v.Url ?? string.Empty),
                        IsCapital: resolvedCapital,
                        CoordX: resolvedX,
                        CoordY: resolvedY,
                        Population: v.Population,
                        CropFields: v.CropFields);
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
            var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
            if (activeCoords.X.HasValue && activeCoords.Y.HasValue && _cachedVillages is { Count: > 0 } cachedVillages)
            {
                var enriched = cachedVillages
                    .Select(v => string.Equals(v.Name, activeVillageBeforeProfile, StringComparison.Ordinal)
                        ? v with { CoordX = v.CoordX ?? activeCoords.X, CoordY = v.CoordY ?? activeCoords.Y }
                        : v)
                    .ToList();
                UpdateCachedVillages(enriched);
                SaveCachedVillageState(activeVillageBeforeProfile, null, activeCoords.X, activeCoords.Y);
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
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading active village coordinates.", cancellationToken);

        var coord = await _page.EvaluateAsync<ActiveVillageCoordJs>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
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

              const candidates = [];
              const selectors = [
                '#villageNameField',
                '.villageNameField',
                '#sidebarBoxVillagelist .active',
                '#sidebarBoxVillagelist .active a[href*="newdid"]',
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

        return (coord?.X, coord?.Y);
    }

    private async Task<(long? Warehouse, long? Granary)> ReadStorageCapacitiesAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading storage capacity.", cancellationToken);
        var raw = await _page.EvaluateAsync<Dictionary<string, string>>(
            """
            () => {
              const readFirst = (selectors) => {
                for (const selector of selectors) {
                  for (const node of document.querySelectorAll(selector)) {
                    const value =
                      node.getAttribute('data-value')
                      || node.getAttribute('data-max')
                      || node.getAttribute('data-capacity')
                      || node.getAttribute('title')
                      || node.getAttribute('aria-label')
                      || node.textContent
                      || '';
                    const text = String(value).trim();
                    if (text) return text;
                  }
                }
                return null;
              };

              return {
                warehouse: readFirst([
                  '#warehouse .value',
                  '#warehouse',
                  '[id*="warehouse" i][data-max]',
                  '[class*="warehouse" i]'
                ]),
                granary: readFirst([
                  '#granary .value',
                  '#granary',
                  '#silo .value',
                  '#silo',
                  '[id*="granary" i][data-max]',
                  '[id*="silo" i][data-max]',
                  '[class*="granary" i]',
                  '[class*="silo" i]'
                ])
              };
            }
            """);

        if (raw is null)
        {
            return (null, null);
        }

        raw.TryGetValue("warehouse", out var warehouseRaw);
        raw.TryGetValue("granary", out var granaryRaw);
        return (TravianParsing.TryParseResourceValue(warehouseRaw), TravianParsing.TryParseResourceValue(granaryRaw));
    }

    private async Task<string> ReadActiveVillageNameAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading the active village.", cancellationToken);
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
        return string.IsNullOrWhiteSpace(value) ? "Unknown village" : value;
    }

}
