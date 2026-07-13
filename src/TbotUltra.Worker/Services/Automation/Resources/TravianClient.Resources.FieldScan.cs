using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Official resource-field scan with the preserved compatibility fallback.
public sealed partial class TravianClient
{
    private async Task<IReadOnlyList<ResourceField>> ReadResourceFieldsAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resource fields.", cancellationToken);
        await WaitForResourceFieldsHydratedAsync(cancellationToken);

        // Primary scan: read the image map (#rx) area links and the #village_map level
        // overlays directly. Modern Travian (2026) uses hrefs like dorf1.php?<cyrillic-a>=<slot>
        // instead of the legacy build.php?id=<slot>; the legacy selectors miss everything.
        string primaryJson = string.Empty;
        try
        {
            primaryJson = await _page.EvaluateAsync<string>(
                """
                () => {
                  // Slot parameter key varies across Travian skins/servers:
                  //   - Modern obfuscated: dorf1.php?а=N (Cyrillic 'а', U+0430)
                  //   - Latin variant:     dorf1.php?a=N
                  //   - Legacy:            build.php?id=N
                  // Match all three so the scan works regardless of which markup the
                  // server emits today.
                  const slotKeyPattern = /[?&](?:id|a|а)=(\d{1,2})(?:[^0-9]|$)/i;
                  const fieldTypes = { 1: 'wood', 2: 'clay', 3: 'iron', 4: 'crop' };
                  const fieldNames = {
                    wood: 'Woodcutter',
                    clay: 'Clay pit',
                    iron: 'Iron mine',
                    crop: 'Cropland'
                  };

                  // 1) Collect slot anchors from the image map, preserving HTML order
                  //    (Travian emits areas in slot-id 1..18 order).
                  const areas = Array.from(document.querySelectorAll('map#rx area, map[name="rx"] area'));
                  const slots = [];
                  for (const area of areas) {
                    const href = area.getAttribute('href') || '';
                    const m = href.match(slotKeyPattern);
                    if (!m) continue;
                    const slotId = parseInt(m[1], 10);
                    if (slotId < 1 || slotId > 18) continue;
                    const coords = (area.getAttribute('coords') || '').split(',').map(s => parseFloat(s.trim()));
                    slots.push({
                      slotId,
                      cx: coords[0],
                      cy: coords[1],
                      href
                    });
                  }

                  // 2) Collect level overlays from #village_map. Each has gid<N> and level<N>
                  //    classes plus a left/top inline style. Travian emits overlays in the
                  //    same order as areas — slot 1 first, slot 18 last — so we can pair
                  //    them by index without trusting the offset positions (which are
                  //    sometimes 0 before CSS finishes applying).
                  const overlays = Array.from(document.querySelectorAll('#village_map .level'));
                  const overlayInfo = overlays.map(el => {
                    const cls = el.className || '';
                    const gidMatch = cls.match(/\bgid(\d+)\b/i);
                    const levelMatch = cls.match(/\blevel(\d+)\b/i);
                    const labelText = ((el.querySelector('.labelLayer') || {}).textContent || '').trim();
                    const labelLevel = /^\d+$/.test(labelText) ? parseInt(labelText, 10) : null;
                    const style = el.getAttribute('style') || '';
                    const leftMatch = style.match(/left\s*:\s*(-?\d+(?:\.\d+)?)px/i);
                    const topMatch = style.match(/top\s*:\s*(-?\d+(?:\.\d+)?)px/i);
                    return {
                      gid: gidMatch ? parseInt(gidMatch[1], 10) : null,
                      level: labelLevel ?? (levelMatch ? parseInt(levelMatch[1], 10) : null),
                      left: leftMatch ? parseFloat(leftMatch[1]) : NaN,
                      top: topMatch ? parseFloat(topMatch[1]) : NaN
                    };
                  });

                  // 3a) Preferred path: zip by index when both lists have the same length.
                  //     This is the common case (always 18+18) and avoids spatial mismatches
                  //     caused by offsetWidth==0 during initial render.
                  const out = [];
                  if (slots.length === overlays.length && slots.length > 0) {
                    for (let i = 0; i < slots.length; i++) {
                      const slot = slots[i];
                      const overlay = overlayInfo[i];
                      const fieldType = overlay && fieldTypes[overlay.gid] ? fieldTypes[overlay.gid] : 'unknown';
                      out.push({
                        slotId: slot.slotId,
                        fieldType,
                        name: fieldNames[fieldType] || 'Unknown field',
                        level: overlay ? overlay.level : null,
                        href: slot.href
                      });
                    }
                  } else {
                    // 3b) Fallback: spatial matching when counts disagree. Pair each slot
                    //     to its nearest overlay using inline-style left/top, with a tolerance
                    //     generous enough to absorb the icon→label offset (~40-60px).
                    const used = new Set();
                    for (const slot of slots) {
                      let bestIdx = -1;
                      let bestDist = Infinity;
                      for (let i = 0; i < overlayInfo.length; i++) {
                        if (used.has(i)) continue;
                        const ov = overlayInfo[i];
                        if (!isFinite(ov.left) || !isFinite(ov.top)) continue;
                        // Overlays are placed top-left, ~40px right and ~10px above the icon
                        // centre. Compare overlay top-left to area centre directly.
                        const dx = ov.left - slot.cx;
                        const dy = ov.top - slot.cy;
                        const dist = Math.sqrt(dx * dx + dy * dy);
                        if (dist < bestDist) {
                          bestDist = dist;
                          bestIdx = i;
                        }
                      }
                      const overlay = (bestIdx >= 0 && bestDist <= 120) ? overlayInfo[bestIdx] : null;
                      if (overlay) used.add(bestIdx);

                      const fieldType = overlay && fieldTypes[overlay.gid] ? fieldTypes[overlay.gid] : 'unknown';
                      out.push({
                        slotId: slot.slotId,
                        fieldType,
                        name: fieldNames[fieldType] || 'Unknown field',
                        level: overlay ? overlay.level : null,
                        href: slot.href
                      });
                    }
                  }

                  // Diagnostic for empty results so we can keep up with Travian markup drift.
                  if (out.length === 0) {
                    try {
                      window.__resourceFieldScanDiag = {
                        url: location.pathname + location.search,
                        areaCount: areas.length,
                        slotsParsed: slots.length,
                        overlayCount: overlays.length,
                        sampleArea: areas.length > 0 ? (areas[0].getAttribute('href') || '') : '',
                        sampleOverlay: overlays.length > 0 ? (overlays[0].className || '') : ''
                      };
                    } catch (_) {}
                  }

                  return JSON.stringify(out);
                }
                """);
        }
        catch (Exception ex) when (IsTransientExecutionContextException(ex))
        {
            Notify($"Resource field primary scan hit transient navigation context ({ex.Message}). Falling back to legacy scan.");
        }

        // If the primary map-based scan succeeded, return immediately.
        if (!string.IsNullOrWhiteSpace(primaryJson))
        {
            var primary = ResourceFieldScanParser.ParseOfficialMap(primaryJson);
            if (primary.Count > 0)
            {
                return BuildResourceFieldsFromJs(primary);
            }
        }

        // Legacy scan kept as a safety net for older skins / pre-map Travian variants.
        string rawFieldsJson = string.Empty;
        await RetryAsync("read resource fields snapshot", async () =>
        {
            rawFieldsJson = await _page.EvaluateAsync<string>(
                """
            () => {
              const fieldTypes = {
                1: 'wood',
                2: 'clay',
                3: 'iron',
                4: 'crop'
              };
              const fieldNames = {
                wood: 'Woodcutter',
                clay: 'Clay pit',
                iron: 'Iron mine',
                crop: 'Cropland',
                unknown: 'Unknown field'
              };

              const parseSlotIdFromText = (value) => {
                if (!value) return null;
                const idMatch = String(value).match(/[?&]id=(\d+)/i);
                if (idMatch) return Number(idMatch[1]);
                const aidMatch = String(value).match(/(?:^|[^a-z])aid[_:=\s-]?(\d{2})/i);
                if (aidMatch) return Number(aidMatch[1]);
                const slotMatch = String(value).match(/(?:^|[^a-z])slot[_:=\s-]?(\d{2})/i);
                if (slotMatch) return Number(slotMatch[1]);
                return null;
              };

              const parseSlotId = (element, href) => {
                const fromHref = parseSlotIdFromText(href);
                if (fromHref !== null) return fromHref;

                if (!element || typeof element.getAttribute !== 'function') return null;
                const attrs = [
                  element.getAttribute('data-aid'),
                  element.getAttribute('aid'),
                  element.getAttribute('data-id'),
                  element.getAttribute('data-slot'),
                  element.getAttribute('data-targetid'),
                  element.getAttribute('data-target-id'),
                  element.getAttribute('href'),
                  element.getAttribute('data-href'),
                  element.getAttribute('onclick'),
                  element.id || '',
                  element.className || ''
                ];

                for (const attr of attrs) {
                  const fromAttr = parseSlotIdFromText(attr);
                  if (fromAttr !== null) return fromAttr;
                }

                return null;
              };

              const directText = (element) => {
                const parts = [
                  element.getAttribute('title') || '',
                  element.getAttribute('alt') || '',
                  element.getAttribute('aria-label') || '',
                  element.getAttribute('data-name') || '',
                  element.getAttribute('data-level') || '',
                  element.getAttribute('data-gid') || '',
                  element.getAttribute('data-aid') || '',
                  element.id || '',
                  element.className || '',
                  element.textContent || ''
                ];
                return parts.join(' ').replace(/\s+/g, ' ').trim();
              };

              const localText = (element) => {
                const parts = [directText(element)];
                for (const child of element.querySelectorAll('img, span, div, area')) {
                  parts.push(directText(child));
                }
                return parts.join(' ').replace(/\s+/g, ' ').trim();
              };

              const resourceLevelOverlays = Array.from(document.querySelectorAll('#village_map .level'))
                .filter((element) => /(?:^|\s)gid\d+(?:\s|$)/i.test(element.className || ''))
                .slice(0, 18);

              const overlayText = (slotId) => {
                const overlay = resourceLevelOverlays[slotId - 1];
                return overlay ? directText(overlay) : '';
              };

              const parseLevel = (element, slotId) => {
                const text = `${localText(element)} ${overlayText(slotId)}`;
                const match = text.match(/(?:^|\s|_|-)level[_-]?(\d{1,2})(?:\s|$|_|-)/i)
                  || text.match(/(?:^|\s|_|-)lvl(?:e|_)?(\d{1,2})(?:\s|$|_|-)/i)
                  || text.match(/(?:level|niveau|lvl|niv\.?|stufe)[^0-9]*(\d{1,2})/i);
                if (match) return Number(match[1]);
                return null;
              };

              const parseType = (element, slotId) => {
                const text = `${localText(element)} ${overlayText(slotId)}`.toLowerCase();
                const gidMatch = text.match(/(?:^|\s|_|-)gid[_-]?(\d+)(?:\s|$|_|-)/);
                if (gidMatch && fieldTypes[Number(gidMatch[1])]) return fieldTypes[Number(gidMatch[1])];

                // Travian often puts gid class on a parent container (e.g. <div class="field gid4">).
                const gidEl = element.closest('[class*="gid"]');
                if (gidEl) {
                  const ancestorGidMatch = (gidEl.className || '').toLowerCase().match(/(?:^|\s)gid[_-]?(\d+)(?:\s|$)/);
                  if (ancestorGidMatch && fieldTypes[Number(ancestorGidMatch[1])]) {
                    return fieldTypes[Number(ancestorGidMatch[1])];
                  }
                }

                if (text.includes('wood') || text.includes('lumber') || text.includes('trä')) return 'wood';
                if (text.includes('clay') || text.includes('lera')) return 'clay';
                if (text.includes('iron') || text.includes('järn')) return 'iron';
                if (text.includes('crop') || text.includes('wheat') || text.includes('gröda')) return 'crop';
                return 'unknown';
              };

              const parseName = (fieldType, element) => {
                const text = localText(element);
                const isUsefulName = (value) => {
                  if (!value || /^\d+$/.test(value) || value.length > 40) return false;
                  if (/^(gid|aid|level|lvl)/i.test(value)) return false;
                  if (/(good|resourceField|labelLayer|colorLayer|contractLink|underConstruction)/i.test(value)) return false;
                  if (/^(a|g)\d+$/i.test(value)) return false;
                  return true;
                };
                const titleLike = text
                  .replace(/(?:^|\s|_|-)gid[_-]?\d+(?:\s|$|_|-)/gi, ' ')
                  .replace(/(?:^|\s|_|-)aid[_-]?\d+(?:\s|$|_|-)/gi, ' ')
                  .replace(/level\s*\d+/gi, '')
                  .replace(/level\d+/gi, '')
                  .replace(/lvl\s*\d+/gi, '')
                  .replace(/lvl(?:e|_)?\d+/gi, '')
                  .replace(/niveau\s*\d+/gi, '')
                  .replace(/stufe\s*\d+/gi, '')
                  .replace(/\s+/g, ' ')
                  .trim();
                if (isUsefulName(titleLike)) return titleLike;
                return fieldNames[fieldType] || fieldNames.unknown;
              };

              const selectors = [
                '#resourceFieldContainer area[href*="build.php?id="]',
                '#rx area[href*="build.php?id="]',
                'area[href*="build.php?id="]',
                '#resourceFieldContainer a[href*="build.php?id="]',
                '#rx a[href*="build.php?id="]',
                '.resourceField a[href*="build.php?id="]',
                'a[href*="build.php?id="]',
                // Modern Travian skins drop the <area>/<a> map and bind clicks via JS,
                // leaving only the resource-level overlays + onclick handlers. Pick them
                // up via the `aid<N>` class on overlay/container divs.
                '#village_map [class*="aid"]',
                '#resourceFieldContainer [class*="aid"]',
                '#rx [class*="aid"]',
                '#village_map [class*="gid"]',
                '#resourceFieldContainer [class*="gid"]',
                '#rx [class*="gid"]',
                '[onclick*="build.php?id="]',
                '[data-href*="build.php?id="]'
              ];

              const seen = new Set();
              const fields = [];
              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const href = element.getAttribute('href')
                    || element.getAttribute('data-href')
                    || element.getAttribute('onclick')
                    || '';
                  const slotId = parseSlotId(element, href);
                  if (slotId === null || slotId < 1 || slotId > 18) continue;
                  const key = String(slotId);
                  if (seen.has(key)) continue;
                  seen.add(key);
                  const fieldType = parseType(element, slotId);
                  fields.push({
                    slotId,
                    fieldType,
                    name: parseName(fieldType, element),
                    level: parseLevel(element, slotId),
                    href: href || `build.php?id=${slotId}`
                  });
                }
                // Stop once we have all 18 fields to keep selector order priority.
                if (fields.length >= 18) break;
              }

              // Always log diagnostic info to the page console so playwright can pipe it
              // back when scans come up empty. Helps catch Travian markup changes early.
              if (fields.length === 0) {
                try {
                  const diag = {
                    url: location.pathname + location.search,
                    areaWithBuild: document.querySelectorAll('area[href*="build.php"]').length,
                    anchorWithBuild: document.querySelectorAll('a[href*="build.php"]').length,
                    villageMapAids: document.querySelectorAll('#village_map [class*="aid"]').length,
                    villageMapGids: document.querySelectorAll('#village_map [class*="gid"]').length,
                    resourceFieldContainer: !!document.querySelector('#resourceFieldContainer'),
                    rx: !!document.querySelector('#rx'),
                    villageMap: !!document.querySelector('#village_map'),
                    levelOverlays: document.querySelectorAll('#village_map .level').length,
                    onclickWithBuild: document.querySelectorAll('[onclick*="build.php"]').length
                  };
                  // Stash the diagnostic on window so the C# follow-up read can grab it.
                  window.__resourceFieldScanDiag = diag;
                } catch (_) {}
              }

              return JSON.stringify(fields);
            }
            """);
        }, cancellationToken: cancellationToken);

        var rawFields = ResourceFieldScanParser.ParseCompatibilityFallback(rawFieldsJson);

        rawFields ??= [];

        // If the dorf1 scan picked up zero field links, try a broader probe before
        // falling back to placeholders. Travian sometimes leaves the image-map <area>
        // elements out of the initial DOM when navigating between dorf1/dorf2, and on
        // some skins they're replaced entirely by onclick/data-attribute bindings on
        // overlay divs (aid<N>/gid<N> classes).
        if (rawFields.Count == 0 && IsCurrentUrlForPath(Paths.Resources))
        {
            // Read the diagnostic stashed by the main scan so we can see exactly which
            // markup variant is present (or absent) on this page.
            try
            {
                var diag = await _page.EvaluateAsync<string>(
                    "() => JSON.stringify(window.__resourceFieldScanDiag || null)");
                if (!string.IsNullOrWhiteSpace(diag) && !string.Equals(diag, "null", StringComparison.Ordinal))
                {
                    Notify($"Resource field scan diagnostic: {diag}");
                }
            }
            catch
            {
                // Diagnostic read is best-effort.
            }

            Notify("Resource field scan returned 0 link elements. Reloading dorf1 once and retrying.");
            try
            {
                await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded })
                    .WaitAsync(cancellationToken);
                await WaitForResourceFieldsHydratedAsync(cancellationToken);

                await RetryAsync("read resource fields snapshot (retry)", async () =>
                {
                    rawFieldsJson = await _page.EvaluateAsync<string>(
                        """
                        () => {
                          const parseSlotIdFromText = (value) => {
                            if (!value) return null;
                            const idMatch = String(value).match(/[?&]id=(\d+)/i);
                            if (idMatch) return Number(idMatch[1]);
                            const aidMatch = String(value).match(/(?:^|[^a-z])aid[_:=\s-]?(\d{1,2})/i);
                            if (aidMatch) return Number(aidMatch[1]);
                            const slotMatch = String(value).match(/(?:^|[^a-z])slot[_:=\s-]?(\d{1,2})/i);
                            if (slotMatch) return Number(slotMatch[1]);
                            return null;
                          };
                          const collectSlot = (element) => {
                            if (!element) return null;
                            const candidates = [
                              element.getAttribute && element.getAttribute('href'),
                              element.getAttribute && element.getAttribute('data-href'),
                              element.getAttribute && element.getAttribute('onclick'),
                              element.getAttribute && element.getAttribute('data-aid'),
                              element.getAttribute && element.getAttribute('data-slot'),
                              element.className || '',
                              element.id || ''
                            ];
                            for (const c of candidates) {
                              const s = parseSlotIdFromText(c);
                              if (s !== null && s >= 1 && s <= 18) return s;
                            }
                            let parent = element.parentElement;
                            for (let i = 0; parent && i < 3; i++, parent = parent.parentElement) {
                              const s = parseSlotIdFromText((parent.className || '') + ' ' + (parent.getAttribute && parent.getAttribute('href') || ''));
                              if (s !== null && s >= 1 && s <= 18) return s;
                            }
                            return null;
                          };

                          const selectors = [
                            'area[href*="build.php?id="]',
                            'a[href*="build.php?id="]',
                            '#village_map [class*="aid"]',
                            '#village_map [class*="gid"]',
                            '#resourceFieldContainer [class*="aid"]',
                            '#resourceFieldContainer [class*="gid"]',
                            '#rx [class*="aid"]',
                            '#rx [class*="gid"]',
                            '[onclick*="build.php?id="]',
                            '[data-href*="build.php?id="]'
                          ];

                          const out = [];
                          const seen = new Set();
                          for (const sel of selectors) {
                            for (const el of document.querySelectorAll(sel)) {
                              const slotId = collectSlot(el);
                              if (slotId === null || seen.has(slotId)) continue;
                              seen.add(slotId);
                              const href = el.getAttribute('href')
                                || el.getAttribute('data-href')
                                || `build.php?id=${slotId}`;
                              out.push({ slotId, fieldType: 'unknown', name: '', level: null, href });
                            }
                            if (out.length >= 18) break;
                          }
                          return JSON.stringify(out);
                        }
                        """);
                }, cancellationToken: cancellationToken);

                rawFields = ResourceFieldScanParser.ParseCompatibilityFallback(rawFieldsJson);
                rawFields ??= [];
                Notify($"Resource field retry scan picked up {rawFields.Count} link element(s).");
            }
            catch (Exception ex) when (IsTransientExecutionContextException(ex))
            {
                Notify($"Resource field reload hit transient navigation context ({ex.Message}). Continuing with placeholders.");
            }
        }

        return BuildResourceFieldsFromJs(rawFields);
    }

    /// <summary>
    /// Common projection used by both the modern (map+overlay) scan and the legacy fallback
    /// scan. Maps raw JS field rows into <see cref="ResourceField"/>s and tops up the result
    /// with placeholder rows so SlotId 1..18 are always present.
    /// </summary>
    private List<ResourceField> BuildResourceFieldsFromJs(List<ResourceFieldJs> rawFields)
    {
        var fieldTypeNames = new Dictionary<string, string>
        {
            ["wood"] = "Woodcutter",
            ["clay"] = "Clay pit",
            ["iron"] = "Iron mine",
            ["crop"] = "Cropland",
        };

        var fields = rawFields.Select(item =>
        {
            var fieldType = string.IsNullOrWhiteSpace(item.FieldType) ? "unknown" : item.FieldType!;
            var name = !string.IsNullOrWhiteSpace(item.Name)
                ? item.Name!
                : fieldTypeNames.GetValueOrDefault(fieldType, "Unknown field");
            return new ResourceField(item.SlotId, fieldType, name, item.Level, ResolveUrl(item.Href));
        }).ToList();

        var seenSlots = fields.Where(f => f.SlotId is not null).Select(f => f.SlotId!.Value).ToHashSet();
        for (var slotId = 1; slotId <= 18; slotId++)
        {
            if (seenSlots.Contains(slotId))
            {
                continue;
            }

            fields.Add(new ResourceField(
                SlotId: slotId,
                FieldType: "unknown",
                Name: "Unknown field",
                Level: null,
                Url: ResolveUrl($"build.php?id={slotId}")));
        }

        return fields.OrderBy(f => f.SlotId ?? 999).ToList();
    }

    /// <summary>
    /// Waits up to ~3s for dorf1's resource field link elements to appear in the DOM.
    /// The buildings overview scan added a similar hydration wait to fix partial reads
    /// on V3 layouts; dorf1 has the same lazy-mount behaviour when the page is reused
    /// across status refreshes (Travian sometimes drops the <area> map briefly during
    /// background ajax refreshes). Without this wait the very next refresh after a
    /// fresh navigation returns 0 fields even though the page is on dorf1.
    /// </summary>
    private async Task WaitForResourceFieldsHydratedAsync(CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  // Accept all three slot-link variants Travian uses: build.php?id=N (legacy),
                  // dorf1.php?a=N (latin), dorf1.php?а=N (Cyrillic 'а').
                  const links = document.querySelectorAll('map#rx area, map[name="rx"] area, area[href*="build.php?id="], a[href*="build.php?id="], area[href*="dorf1.php"], a[href*="dorf1.php"]');
                  let count = 0;
                  const seen = new Set();
                  for (const link of links) {
                    const href = link.getAttribute('href') || '';
                    const m = href.match(/[?&](?:id|a|а)=(\d{1,2})(?:[^0-9]|$)/i);
                    if (!m) continue;
                    const slotId = parseInt(m[1], 10);
                    if (slotId < 1 || slotId > 18 || seen.has(slotId)) continue;
                    seen.add(slotId);
                    count++;
                  }
                  return count >= 18;
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 3000 });
        }
        catch (TimeoutException)
        {
            // Continue; the retry-with-reload path inside ReadResourceFieldsAsync still
            // recovers if the JS scan comes back empty.
        }
        catch (Exception ex) when (!IsTransientExecutionContextException(ex))
        {
            Notify($"Resource field hydration wait skipped: {ex.Message}");
        }
    }

}
