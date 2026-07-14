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


}

