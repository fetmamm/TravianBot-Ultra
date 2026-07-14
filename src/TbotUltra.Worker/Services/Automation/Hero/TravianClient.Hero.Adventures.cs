using Microsoft.Playwright;
using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Adventure selection and dispatch workflow. Browser/session ownership remains on TravianClient.
public sealed partial class TravianClient
{
    private async Task<(bool Sent, int DurationSeconds, int ReturnSeconds)> TrySendHeroToAdventureAsync(string pickOrder, CancellationToken cancellationToken)
    {
        await OpenHeroAdventuresPageAsync(cancellationToken);

        // The adventures list on official Travian (T4.6) is React-rendered and is often not in the
        // DOM yet right after navigation. Wait for the adventure rows (Explore buttons) — or the
        // hero-away state — to render before picking; otherwise the pick finds nothing and the
        // dispatch falsely reports "adventure_not_clickable".
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => !!document.querySelector('table.adventureList tbody tr, #adventureListForm tbody tr')
                   || /on its way to an adventure/i.test(document.body?.innerText || '')
                   || !!document.querySelector('.heroState, [class*="statusRunning"], [class*="heroRunning"]')
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 6000 });
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
        }

        // Step 1: pick a row (top or shortest), open the adventure detail page, and report its duration.
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        var pickedJson = await _page.EvaluateAsync<string>(
            $$"""
            () => {
              const order = '{{(pickOrder?.ToLowerInvariant() == "top" ? "top" : "shortest")}}';
              const parseDuration = (text) => {
                const m = (text || '').match(/(\d{1,3}):(\d{2}):(\d{2})/);
                if (!m) return Number.MAX_SAFE_INTEGER;
                return Number(m[1]) * 3600 + Number(m[2]) * 60 + Number(m[3]);
              };

              const isDisabled = (node) =>
                !node || (node.hasAttribute && node.hasAttribute('disabled'))
                || (node.className || '').toString().toLowerCase().includes('disabled');

              const candidates = Array.from(document.querySelectorAll('a, button, input[type="submit"]'))
                .filter(node => {
                  if (isDisabled(node)) return false;
                  const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                  const href = (node.getAttribute('href') || '').toLowerCase();
                  // Href match must require an adventure id: the bare '/hero/adventures' also matches
                  // the sidebar navigation link, which was picked (and clicked) whenever the real
                  // Explore buttons were disabled or not rendered yet.
                  return text.includes('to the adventure')
                    || text.includes('to adventure')
                    || text.includes('start adventure')
                    || text.includes('explore')
                    || /\/hero\/adventures\/\d+/.test(href);
                });
              if (candidates.length === 0) return JSON.stringify({ ok: false });

              const entries = candidates.map(node => {
                const row = node.closest('tr');
                const moveCell = row?.querySelector('td.moveTime, td.duration');
                const duration = parseDuration(moveCell?.textContent || row?.textContent || '');
                return { node, duration };
              });

              if (order === 'shortest') entries.sort((a, b) => a.duration - b.duration);
              const chosen = entries[0];
              chosen.node.click();
              // Unknown duration is MAX_SAFE_INTEGER (parseDuration's sort sentinel) — send null
              // instead: it does not fit the C# int? and the value only feeds logging/ETA fallback.
              const duration = chosen.duration === Number.MAX_SAFE_INTEGER ? null : chosen.duration;
              return JSON.stringify({ ok: true, durationSeconds: duration, returnSeconds: 0 });
            }
            """);

        AdventurePickJs? picked = null;
        if (!string.IsNullOrWhiteSpace(pickedJson))
        {
            try
            {
                picked = JsonSerializer.Deserialize<AdventurePickJs>(pickedJson);
            }
            catch (JsonException ex)
            {
                // A malformed payload must not fail the whole hero_manage task (the pick JS runs
                // against a live, server-variant DOM). Log and treat as "not sent" — the queue retries.
                Notify($"[adventure] could not parse adventure pick payload '{pickedJson}': {ex.Message}");
            }
        }

        if (picked is null || !picked.Ok)
        {
            return (false, 0, 0);
        }

        var duration = picked.DurationSeconds ?? 0;
        var fallbackReturnSeconds = duration > 0 ? duration * 2 : 0;

        // Step 2: confirm the adventure.
        // Official Travian opens a React confirmation modal with a "Continue" button
        // (class "...continue...") and does NOT navigate — the modal needs a moment to
        // render, so poll for the confirm button before giving up.
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        var fallbackReturnFromDetail = await ReadAdventureReturnSecondsAsync(cancellationToken) ?? fallbackReturnSeconds;
        Notify($"[adventure] picked {pickOrder} adventure, duration={duration}s, hero return ETA={fallbackReturnFromDetail}s");

        var confirmed = false;
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        for (var attempt = 0; attempt < 10 && !confirmed; attempt++)
        {
            confirmed = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const isDisabled = (n) => !n
                    || (n.hasAttribute && n.hasAttribute('disabled'))
                    || /(^|\s)disabled(\s|$)/i.test((n.className || '').toString());
                  const cont = Array.from(document.querySelectorAll('button.continue, button'))
                    .find(n => !isDisabled(n) && /\bcontinue\b/i.test((n.value || '') + ' ' + (n.textContent || '')));
                  if (cont) { cont.click(); return true; }
                  // Other localized submit labels.
                  const submit = Array.from(document.querySelectorAll('button, input[type="submit"]'))
                    .find(n => !isDisabled(n) && /to\s+adventure|start\s+adventure/i.test((n.value || '') + ' ' + (n.textContent || '')));
                  if (submit) { submit.click(); return true; }
                  return false;
                }
                """);
            if (!confirmed)
            {
                await Task.Delay(300, cancellationToken);
            }
        }

        if (!confirmed)
        {
            return (false, duration, fallbackReturnFromDetail);
        }

        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        var dispatched = await IsHeroAdventureActivePageAsync(cancellationToken);
        var returnSeconds = await ReadAdventureReturnSecondsAsync(cancellationToken) ?? fallbackReturnFromDetail;
        Notify($"[adventure] dispatch confirmed={dispatched}, hero return ETA={returnSeconds}s");

        // Navigate back to dorf1 after the "To adventure" submit so we don't leave the page on the
        // adventure result view (keeps the page fresh for the next hero status read).
        await EnsureFreshDorf1ForHeroAsync(forceReload: false, cancellationToken);

        return (dispatched, duration, returnSeconds);
    }

    private async Task<string> IncreaseAdventuresToHardForSelectedAdventureAsync(string pickOrder, CancellationToken cancellationToken)
    {
        var selected = await ReadSelectedAdventureBeforeBonusAsync(pickOrder, cancellationToken);
        if (selected?.Found == true)
        {
            var durationText = selected.DurationSeconds is > 0
                ? $", duration={selected.DurationSeconds.Value}s"
                : string.Empty;
            Notify($"[hero] selected adventure before hard bonus: order={pickOrder}, difficulty={selected.Difficulty ?? "unknown"}{durationText}.");
            if (string.Equals(selected.Difficulty, "hard", StringComparison.OrdinalIgnoreCase))
            {
                return "Selected adventure is already hard; skipped increase-danger bonus video.";
            }
        }
        else
        {
            Notify($"[hero] could not read selected adventure difficulty before hard bonus (order={pickOrder}); using existing bonus-video flow.");
        }

        return await IncreaseAdventuresToHardAsync(cancellationToken);
    }

    private async Task<AdventureSelectionPreviewJs?> ReadSelectedAdventureBeforeBonusAsync(string pickOrder, CancellationToken cancellationToken)
    {
        await OpenHeroAdventuresPageAsync(cancellationToken);

        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => !!document.querySelector('table.adventureList tbody tr, #adventureListForm tbody tr')
                   || /on its way to an adventure/i.test(document.body?.innerText || '')
                   || !!document.querySelector('.heroState, [class*="statusRunning"], [class*="heroRunning"]')
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 6000 });
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
        }

        var raw = await _page.EvaluateAsync<string>(
            $$"""
            () => {
              const order = '{{(pickOrder?.ToLowerInvariant() == "top" ? "top" : "shortest")}}';
              const parseDuration = (text) => {
                const m = (text || '').match(/(\d{1,3}):(\d{2}):(\d{2})/);
                if (!m) return Number.MAX_SAFE_INTEGER;
                return Number(m[1]) * 3600 + Number(m[2]) * 60 + Number(m[3]);
              };

              const isDisabled = (node) =>
                !node || (node.hasAttribute && node.hasAttribute('disabled'))
                || (node.className || '').toString().toLowerCase().includes('disabled');

              const collectContext = (root) => {
                if (!root) return '';
                const parts = [root.textContent || '', String(root.className || '')];
                for (const node of root.querySelectorAll?.('*') || []) {
                  parts.push(
                    node.textContent || '',
                    String(node.className || ''),
                    node.getAttribute?.('title') || '',
                    node.getAttribute?.('alt') || '',
                    node.getAttribute?.('aria-label') || '',
                    node.getAttribute?.('src') || '',
                    node.getAttribute?.('data-tooltip') || '',
                    node.getAttribute?.('data-title') || '',
                    node.getAttribute?.('data-difficulty') || '');
                }
                return parts.join(' ').replace(/\s+/g, ' ').toLowerCase();
              };

              const readDifficulty = (node) => {
                const scope = node.closest('tr')
                  || node.closest('[class*="adventure" i]')
                  || node.parentElement
                  || node;
                const difficultyCell = scope.querySelector?.('td.difficulty, [class*="difficulty" i]');
                const difficultyText = collectContext(difficultyCell || scope);
                if (/difficulty[_-]?hard|(^|[^a-z])hard([^a-z]|$)/.test(difficultyText)) return 'hard';
                if (/difficulty[_-]?(normal|easy)|(^|[^a-z])normal([^a-z]|$)/.test(difficultyText)) return 'normal';
                const text = collectContext(scope);
                if (/(^|[^a-z])hard([^a-z]|$)|difficulty[^a-z0-9]*hard|adventure[^a-z0-9]*hard/.test(text)) return 'hard';
                if (/(^|[^a-z])normal([^a-z]|$)|difficulty[^a-z0-9]*normal|adventure[^a-z0-9]*normal/.test(text)) return 'normal';
                return 'unknown';
              };

              const rows = Array.from(document.querySelectorAll('table.adventureList tbody tr, #adventureListForm tbody tr'));
              const rowEntries = rows.map(row => {
                const durationCell = row.querySelector('td.moveTime, td.duration');
                const duration = parseDuration(durationCell?.textContent || row.textContent || '');
                return { node: row, duration };
              });

              const candidates = rowEntries.length > 0
                ? []
                : Array.from(document.querySelectorAll('a, button, input[type="submit"]'))
                    .filter(node => {
                      if (isDisabled(node)) return false;
                      const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                      const href = (node.getAttribute('href') || '').toLowerCase();
                      return text.includes('to the adventure')
                        || text.includes('to adventure')
                        || text.includes('start adventure')
                        || text.includes('explore')
                        || href.includes('/hero/adventures');
                    });

              const actionEntries = candidates.map(node => {
                const row = node.closest('tr');
                const moveCell = row?.querySelector('td.moveTime, td.duration');
                const duration = parseDuration(moveCell?.textContent || row?.textContent || '');
                return { node, duration };
              });

              const entries = rowEntries.length > 0 ? rowEntries : actionEntries;
              if (entries.length === 0) return JSON.stringify({ found: false });

              if (order === 'shortest') entries.sort((a, b) => a.duration - b.duration);
              const chosen = entries[0];
              const duration = chosen.duration === Number.MAX_SAFE_INTEGER ? null : chosen.duration;
              return JSON.stringify({
                found: true,
                difficulty: readDifficulty(chosen.node),
                durationSeconds: duration
              });
            }
            """);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AdventureSelectionPreviewJs>(raw);
        }
        catch (JsonException ex)
        {
            // Degrade to "could not read selection" instead of failing hero_manage on a DOM/payload quirk.
            Notify($"[hero] could not parse adventure preview payload '{raw}': {ex.Message}");
            return null;
        }
    }

    private async Task<int?> ReadAdventureReturnSecondsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await _page.EvaluateAsync<string?>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const text = clean(document.body?.innerText || '');
              if (!text) return null;

              const patterns = [
                /back\s+in\s*:\s*(\d{1,3}:\d{2}:\d{2})/i,
                /back\s+in\s+(\d{1,3}:\d{2}:\d{2})/i,
                /return\s+in\s*:\s*(\d{1,3}:\d{2}:\d{2})/i,
              ];

              for (const pattern of patterns) {
                const match = text.match(pattern);
                if (match && match[1]) return match[1];
              }

              const labels = Array.from(document.querySelectorAll('td, div, span, p'));
              for (const label of labels) {
                const labelText = clean(label.textContent || '');
                if (!/back\s+in|return\s+in/i.test(labelText)) continue;
                const timer = label.querySelector?.('.timer')?.textContent || '';
                const timerText = clean(timer);
                if (/^\d{1,3}:\d{2}:\d{2}$/.test(timerText)) return timerText;
                const inline = labelText.match(/(\d{1,3}:\d{2}:\d{2})/);
                if (inline && inline[1]) return inline[1];
              }

              return null;
            }
            """);

        return TravianParsing.ParseDurationToSeconds(raw);
    }

    private async Task<bool> IsHeroAdventureActivePageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const statusText = clean(document.querySelector('.heroStatusMessage')?.textContent || '');
                  if (statusText.includes('hero is on adventure') || statusText.includes('arrival in')) {
                    return true;
                  }

                  const bodyText = clean(document.body?.innerText || '');
                  return bodyText.includes('hero is on adventure')
                    || bodyText.includes('on its way to an adventure') // official Travian (T4.6)
                    || bodyText.includes('arrival in')
                    || bodyText.includes('back in');
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private sealed class AdventurePickJs
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("durationSeconds")]
        public int? DurationSeconds { get; init; }

        [JsonPropertyName("returnSeconds")]
        public int? ReturnSeconds { get; init; }
    }

    private sealed class AdventureSelectionPreviewJs
    {
        [JsonPropertyName("found")]
        public bool Found { get; init; }

        [JsonPropertyName("difficulty")]
        public string? Difficulty { get; init; }

        [JsonPropertyName("durationSeconds")]
        public int? DurationSeconds { get; init; }
    }

}
