using Microsoft.Playwright;
using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Hero inventory ointment reads and use flow.
public sealed partial class TravianClient
{
    private void UpdateHeroOintmentAutoUseState(bool enabled)
    {
        if (_lastHeroAutoUseOintmentsEnabled != enabled)
        {
            _lastHeroOintmentMissKey = null;
            _lastHeroAutoUseOintmentsEnabled = enabled;
        }

        if (!enabled)
        {
            _lastHeroOintmentMissKey = null;
        }
    }

    private void ClearHeroOintmentMiss()
    {
        _lastHeroOintmentMissKey = null;
    }

    private void ClearPersistedHeroOintmentCooldown()
    {
        try
        {
            _heroOintmentAvailabilityStore.Clear(AccountName, ServerUrl);
        }
        catch (Exception ex)
        {
            Notify($"[hero-ointment] could not clear availability cooldown: {ex.Message}");
        }
    }

    private DateTimeOffset PersistEmptyHeroOintmentCooldown(DateTimeOffset observedAtUtc)
    {
        var retryNotBeforeUtc = observedAtUtc + HeroOintmentRetryPolicy.EmptyInventoryCooldown;
        try
        {
            _heroOintmentAvailabilityStore.SaveUnavailable(AccountName, ServerUrl, retryNotBeforeUtc);
        }
        catch (Exception ex)
        {
            Notify($"[hero-ointment] could not persist empty-inventory cooldown: {ex.Message}");
        }

        return retryNotBeforeUtc;
    }

    internal async Task<HeroOintmentUseResult> TryUseHeroOintmentsForAdventureAsync(
        int currentHpPercent,
        int minHpForAdventure,
        int targetHpPercent,
        int adventureCount,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var availabilityState = _heroOintmentAvailabilityStore.TryLoad(AccountName, ServerUrl);
        if (!HeroOintmentRetryPolicy.ShouldLookup(availabilityState, now))
        {
            return HeroOintmentUseResult.Suppressed;
        }

        var normalizedTarget = HeroCalc.NormalizeOintmentTargetHp(targetHpPercent);
        var retryKey = new HeroOintmentRetryKey(adventureCount, currentHpPercent, minHpForAdventure, normalizedTarget);
        if (_lastHeroOintmentMissKey == retryKey)
        {
            Notify($"Hero ointment lookup skipped for unchanged state: hp={currentHpPercent}%, adventures={adventureCount}.");
            return HeroOintmentUseResult.Suppressed;
        }

        await GotoAsync(Paths.HeroInventory, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load

        var info = await ReadHeroOintmentInventoryInfoAsync(cancellationToken);
        if (!info.Found || info.Count <= 0)
        {
            _lastHeroOintmentMissKey = retryKey;
            var retryNotBeforeUtc = PersistEmptyHeroOintmentCooldown(now);
            Notify($"Hero ointments not found in inventory. Next automatic check after {retryNotBeforeUtc:O}.");
            return HeroOintmentUseResult.AttemptedWithoutUse;
        }

        ClearPersistedHeroOintmentCooldown();

        var useCount = HeroCalc.CalculateOintmentsToUse(currentHpPercent, normalizedTarget, info.Count);
        if (useCount <= 0)
        {
            ClearHeroOintmentMiss();
            return HeroOintmentUseResult.AttemptedWithoutUse;
        }

        Notify($"Hero ointments found: {info.Count}. Trying to use {useCount} to reach {normalizedTarget}% HP.");
        var clicked = await ClickHeroOintmentItemAsync(info, cancellationToken);
        if (!clicked)
        {
            _lastHeroOintmentMissKey = retryKey;
            Notify("Hero ointment item was detected but could not be clicked. Suppressing repeat checks for this state.");
            return HeroOintmentUseResult.AttemptedWithoutUse;
        }

        try
        {
            await _page.Locator(".heroConsumablesPopup").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000,
            });
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            _lastHeroOintmentMissKey = retryKey;
            Notify($"Hero ointment click did not open the use dialog: {ex.Message}");
            return HeroOintmentUseResult.AttemptedWithoutUse;
        }
        var submitted = await ConfirmHeroOintmentUseAsync(useCount, cancellationToken);
        if (!submitted)
        {
            _lastHeroOintmentMissKey = retryKey;
            Notify("Hero ointment dialog opened, but the Use action could not be submitted.");
            return HeroOintmentUseResult.AttemptedWithoutUse;
        }

        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        int? refreshedHp = null;
        for (var attempt = 0; attempt < 3 && (!refreshedHp.HasValue || refreshedHp.Value <= currentHpPercent); attempt++)
        {
            await Task.Delay(500, cancellationToken);
            refreshedHp = await ReadHeroHpFromCurrentPageAsync(cancellationToken);
        }

        if (refreshedHp > currentHpPercent)
        {
            ClearHeroOintmentMiss();
            ClearPersistedHeroOintmentCooldown();
            var observedUsed = refreshedHp > currentHpPercent
                ? Math.Min(useCount, refreshedHp.Value - currentHpPercent)
                : useCount;
            Notify($"Hero ointment use completed. HP before={currentHpPercent}%, after={refreshedHp?.ToString() ?? "?"}%.");
            return new HeroOintmentUseResult(observedUsed, LookupAttempted: true, SkippedBySuppression: false);
        }

        _lastHeroOintmentMissKey = retryKey;
        Notify("Hero ointment use could not be confirmed. Suppressing repeat checks for this state.");
        return HeroOintmentUseResult.AttemptedWithoutUse;
    }

    private async Task<HeroOintmentInventoryInfo> ReadHeroOintmentInventoryInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const isVisible = (node) => {
                if (!node) return false;
                const style = window.getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden') return false;
                const rect = node.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
              };
              const readText = (node) => {
                const parts = [
                  node.textContent,
                  node.getAttribute('title'),
                  node.getAttribute('aria-label'),
                  node.getAttribute('data-title'),
                  node.getAttribute('data-tooltip'),
                  node.getAttribute('data-tip'),
                  node.getAttribute('alt'),
                  node.className
                ];
                for (const child of Array.from(node.querySelectorAll('img, [title], [aria-label], [data-title], [data-tooltip], [data-tip], [alt]'))) {
                  parts.push(child.getAttribute('title'));
                  parts.push(child.getAttribute('aria-label'));
                  parts.push(child.getAttribute('data-title'));
                  parts.push(child.getAttribute('data-tooltip'));
                  parts.push(child.getAttribute('data-tip'));
                  parts.push(child.getAttribute('alt'));
                  parts.push(child.className);
                }
                return clean(parts.filter(Boolean).join(' '));
              };
              const matchesOintment = (text) => /(^|[^a-z])(ointment|ointments|salve|salves|salva|salvor|salbe|salben)([^a-z]|$)/i.test(text);
              const parseCount = (node) => {
                const countNode = node.querySelector('.amount, .itemAmount, .count, .number, [class*="amount" i], [class*="count" i]');
                const countText = clean(countNode?.textContent || '');
                let match = countText.match(/(\d+)/);
                if (match) return Number(match[1]) || 0;
                match = clean(node.textContent || '').match(/[x\u00d7]\s*(\d+)|(\d+)\s*[x\u00d7]/i);
                if (match) return Number(match[1] || match[2]) || 1;
                return 1;
              };
              const candidates = Array.from(document.querySelectorAll('.heroItem'))
                .filter(isVisible)
                .map((node) => ({ node, text: readText(node) }))
                .filter(item => !!item.node.querySelector('.item106')
                  || matchesOintment(item.text));
              if (candidates.length === 0) {
                return JSON.stringify({ found: false, count: 0, itemId: '', itemRefKey: '' });
              }
              const picked = candidates[0];
              return JSON.stringify({
                found: true,
                count: parseCount(picked.node),
                itemId: picked.node.getAttribute('data-id') || '',
                itemRefKey: picked.node.getAttribute('data-refkey') || ''
              });
            }
            """);

        return JsonSerializer.Deserialize<HeroOintmentInventoryInfo>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new HeroOintmentInventoryInfo();
    }

    private async Task<bool> ClickHeroOintmentItemAsync(
        HeroOintmentInventoryInfo ointment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        return await _page.EvaluateAsync<bool>(
            """
            (identity) => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const isVisible = (node) => {
                if (!node) return false;
                const style = window.getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden') return false;
                const rect = node.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
              };
              const readText = (node) => {
                const parts = [
                  node.textContent,
                  node.getAttribute('title'),
                  node.getAttribute('aria-label'),
                  node.getAttribute('data-title'),
                  node.getAttribute('data-tooltip'),
                  node.getAttribute('data-tip'),
                  node.getAttribute('alt'),
                  node.className
                ];
                for (const child of Array.from(node.querySelectorAll('img, [title], [aria-label], [data-title], [data-tooltip], [data-tip], [alt]'))) {
                  parts.push(child.getAttribute('title'));
                  parts.push(child.getAttribute('aria-label'));
                  parts.push(child.getAttribute('data-title'));
                  parts.push(child.getAttribute('data-tooltip'));
                  parts.push(child.getAttribute('data-tip'));
                  parts.push(child.getAttribute('alt'));
                  parts.push(child.className);
                }
                return clean(parts.filter(Boolean).join(' '));
              };
              const matchesOintment = (text) => /(^|[^a-z])(ointment|ointments|salve|salves|salva|salvor|salbe|salben)([^a-z]|$)/i.test(text);
              const candidates = Array.from(document.querySelectorAll('.heroItem'))
                .filter(isVisible)
                .filter(node => !!node.querySelector('.item106')
                  || matchesOintment(readText(node)));
              const item = candidates.find(node => identity.itemId
                ? node.getAttribute('data-id') === identity.itemId
                : identity.itemRefKey && node.getAttribute('data-refkey') === identity.itemRefKey);
              if (!item) return false;
              const target = item.querySelector('button:not([disabled]), a, [role="button"], img') || item;
              target.click();
              return true;
            }
            """,
            new Dictionary<string, string?>
            {
                ["itemId"] = ointment.ItemId,
                ["itemRefKey"] = ointment.ItemRefKey,
            });
    }

    private async Task<bool> ConfirmHeroOintmentUseAsync(int useCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        return await _page.EvaluateAsync<bool>(
            """
            (useCount) => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const isVisible = (node) => {
                if (!node) return false;
                const style = window.getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden') return false;
                const rect = node.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
              };
              const dialog = Array.from(document.querySelectorAll('.heroConsumablesPopup, .dialog, .modal, .popup, .overlay, [role="dialog"]'))
                .filter(isVisible)
                .reverse()
                .find(node => node.matches('.heroConsumablesPopup')
                  || /ointment|ointments|salve|salves|salva|salvor|salbe|salben/i.test(clean(node.textContent || '')));
              if (!dialog) return false;
              const input = Array.from(dialog.querySelectorAll('#consumableHeroItem input, input[type="number"], input[type="text"]'))
                .find(node => isVisible(node) && !node.disabled && !node.readOnly);
              if (input) {
                const valueSetter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
                if (valueSetter) valueSetter.call(input, String(Math.max(1, useCount)));
                else input.value = String(Math.max(1, useCount));
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
              }
              const buttons = Array.from(dialog.querySelectorAll('button, input[type="submit"], input[type="button"], a, div.addHoverClick'))
                .filter(node => isVisible(node) && !node.disabled && !(node.className || '').toString().toLowerCase().includes('disabled'));
              const button = buttons.find(node => {
                const text = clean((node.value || '') + ' ' + (node.textContent || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                return /\b(use|apply|confirm|ok|yes|anwenden|benutzen)\b/.test(text);
              });
              if (!button) return false;
              button.click();
              return true;
            }
            """,
            useCount);
    }

}
