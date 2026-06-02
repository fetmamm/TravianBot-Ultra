using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Official Travian (T4.6) only: when an upgrade/construction lacks resources, the build page shows
// the missing resource costs as clickable icons (div.inlineIcon.resource.transfer) that open a
// "Transfer resources" dialog (div.resourceTransferDialog). Travian pre-fills how much of each
// resource to pull from the hero's inventory; clicking the green "Transfer selected" button moves
// the resources and reloads the page so the upgrade can then succeed.
public sealed partial class TravianClient
{
    // Attempts to top up the active village from the hero inventory for the upgrade on the current
    // build page. Returns true when the transfer dialog was confirmed (page reloads afterwards), so
    // the caller should `continue` its loop and re-analyze whether the upgrade is now possible.
    // Official-only and opt-in; both gates fail fast so callers can call this unconditionally.
    private async Task<bool> TryHeroResourceTransferForConstructionAsync(
        string label,
        CancellationToken cancellationToken)
    {
        if (_config.IsPrivateServer)
        {
            return false;
        }

        if (!_config.HeroResourceTransferEnabled)
        {
            return false;
        }

        // Only act when the build page actually offers a hero transfer for a missing resource.
        bool transferAvailable;
        try
        {
            transferAvailable = await _page.EvaluateAsync<bool>(
                "() => !!document.querySelector('.inlineIcon.resource.transfer')");
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"[hero-transfer] transient error probing transfer availability at {label}; skipping");
            return false;
        }

        if (!transferAvailable)
        {
            Notify($"[hero-transfer] skip at {label}. No hero transfer offered on this page.");
            return false;
        }

        // Proactive gate: if we already know (from the cached inventory) the hero cannot fully cover
        // the missing resources, skip without opening the dialog — a partial transfer would spend
        // hero resources without unblocking the upgrade. With no cache yet we fall through to the
        // reactive behaviour (open the dialog and let Travian decide).
        var cachedInventory = TryGetCachedHeroInventory();
        if (cachedInventory is not null)
        {
            var shortfall = await ReadUpgradeShortfallOnBuildPageAsync(cancellationToken);
            if (shortfall is not null && !HeroCoversShortfall(cachedInventory, shortfall))
            {
                Notify($"[hero-transfer] skip at {label}. Cached hero inventory cannot cover the shortfall "
                    + $"(need wood={shortfall.Wood} clay={shortfall.Clay} iron={shortfall.Iron} crop={shortfall.Crop}; "
                    + $"hero has wood={cachedInventory.Wood} clay={cachedInventory.Clay} iron={cachedInventory.Iron} crop={cachedInventory.Crop}).");
                return false;
            }
        }

        Notify($"[hero-transfer] opening transfer dialog for {label}");

        try
        {
            var opened = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const icon = document.querySelector('.upgradeBlocked .inlineIcon.resource.transfer.fillUp, .inlineIcon.resource.transfer.fillUp, .inlineIcon.resource.transfer');
                  if (!icon) return false;
                  icon.click();
                  return true;
                }
                """);
            if (!opened)
            {
                Notify($"[hero-transfer] could not click transfer icon at {label}.");
                return false;
            }
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"[hero-transfer] transient error clicking transfer icon at {label}; skipping");
            return false;
        }

        // Wait for the React-rendered dialog. A timeout means the hero has nothing to transfer
        // (or the dialog failed to open) — fall back to the caller's other handling.
        try
        {
            await _page.WaitForSelectorAsync(
                "div.resourceTransferDialog, #dialogContent",
                new PageWaitForSelectorOptions { Timeout = 8000 });
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const dialog = document.querySelector('div.resourceTransferDialog')
                    || ((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources'
                      ? document.querySelector('#dialogContent')
                      : null);
                  return !!dialog?.querySelector('input[name="lumber"], input[name="clay"], input[name="iron"], input[name="crop"]');
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 });
        }
        catch (TimeoutException)
        {
            Notify($"[hero-transfer] transfer dialog did not appear at {label}; skipping");
            return false;
        }
        catch (PlaywrightException)
        {
            Notify($"[hero-transfer] transfer dialog wait failed at {label}; skipping");
            return false;
        }

        // Travian auto-fills the amounts. We read them (for the log AND to keep the cached
        // inventory in sync afterwards) but do NOT modify the inputs.
        HeroInventoryResources? transferred = null;
        try
        {
            var amountsJson = await _page.EvaluateAsync<string>(
                """
                () => {
                  const dialog = document.querySelector('div.resourceTransferDialog')
                    || ((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources'
                      ? document.querySelector('#dialogContent')
                      : null);
                  const read = (name) => {
                    const input = dialog?.querySelector('input[name="' + name + '"]');
                    const text = (input?.value || '').replace(/[^0-9]/g, '');
                    return text ? Number(text) || 0 : 0;
                  };
                  return JSON.stringify({ wood: read('lumber'), clay: read('clay'), iron: read('iron'), crop: read('crop') });
                }
                """);
            if (!string.IsNullOrWhiteSpace(amountsJson))
            {
                transferred = JsonSerializer.Deserialize<HeroInventoryResources>(
                    amountsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            if (transferred is not null)
            {
                Notify($"[hero-transfer] auto-filled amounts: wood={transferred.Wood} clay={transferred.Clay} iron={transferred.Iron} crop={transferred.Crop}");
            }
        }
        catch (Exception)
        {
            // Logging / cache sync only — ignore failures reading the pre-filled amounts.
        }

        bool confirmed;
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const dialog = document.querySelector('div.resourceTransferDialog')
                    || ((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources'
                      ? document.querySelector('#dialogContent')
                      : null);
                  if (!dialog) return false;
                  let button = dialog.querySelector('.actionButton.preSelected button');
                  if (!button) {
                    const buttons = Array.from(dialog.querySelectorAll('button'));
                    button = buttons.find(b => /transfer selected/i.test(b.textContent || ''));
                  }
                  return !!button && !button.disabled && button.getAttribute('aria-disabled') !== 'true';
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 });
            confirmed = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const dialog = document.querySelector('div.resourceTransferDialog')
                    || ((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources'
                      ? document.querySelector('#dialogContent')
                      : null);
                  if (!dialog) return false;
                  let button = dialog.querySelector('.actionButton.preSelected button');
                  if (!button) {
                    const buttons = Array.from(dialog.querySelectorAll('button'));
                    button = buttons.find(b => /transfer selected/i.test(b.textContent || ''));
                  }
                  if (!button) return false;
                  button.click();
                  return true;
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"[hero-transfer] transient error confirming transfer at {label}; skipping");
            return false;
        }

        if (!confirmed)
        {
            Notify($"[hero-transfer] could not find 'Transfer selected' button at {label}; skipping");
            return false;
        }

        Notify($"[hero-transfer] transfer confirmed at {label}; waiting for page reload");
        await WaitForPostUpgradeClickPageLoadAsync(cancellationToken);
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => !document.querySelector('div.resourceTransferDialog')
                  && !((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources')
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 8000 });
        }
        catch (TimeoutException)
        {
            Notify($"[hero-transfer] transfer dialog still visible after confirm at {label}; continuing with current page state.");
        }

        await Task.Delay(350, cancellationToken);
        await ApplyActionDelayAsync(cancellationToken);

        // Keep the cached inventory in sync by deducting what Travian just moved out of it. We do
        // NOT re-navigate to the inventory page here (that would derail the upgrade loop) — the
        // amounts the dialog reported are exactly what was transferred.
        if (transferred is not null)
        {
            DeductFromHeroInventoryCache(transferred);
        }

        return true;
    }

    // --- In-memory hero inventory cache (per account+server) -------------------------------------
    // Lets the bot know how much the hero is carrying without re-reading the page, and lets the
    // desktop reflect changes live via HeroInventoryUpdated. Single-process, so a static store
    // keyed by account+server mirrors the existing hero-attribute snapshot cache.

    private static readonly object HeroInventoryCacheSync = new();
    private static readonly Dictionary<string, HeroInventoryResources> CachedHeroInventoryByKey =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised whenever the cached hero inventory changes (read or transfer). The string is
    /// the account name so the UI can ignore updates for a non-active account.</summary>
    public static event Action<string, HeroInventoryResources>? HeroInventoryUpdated;

    // Reads the per-resource shortfall (cost minus current stock, floored at 0) for the upgrade on
    // the current build page. Cost comes from the transfer icon's `targetResourceAmount` onclick
    // payload; stock from the top-bar values (#l1..#l4). Returns null when the data can't be read,
    // so the caller treats "unknown" as "proceed" rather than wrongly skipping.
    private async Task<HeroInventoryResources?> ReadUpgradeShortfallOnBuildPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string json;
        try
        {
            json = await _page.EvaluateAsync<string>(
                """
                () => {
                  const icon = document.querySelector('.inlineIcon.resource.transfer[onclick]');
                  if (!icon) return '';
                  const onclick = icon.getAttribute('onclick') || '';
                  const grab = (name) => {
                    const m = onclick.match(new RegExp(name + '\\s*:\\s*(\\d+)'));
                    return m ? (Number(m[1]) || 0) : 0;
                  };
                  const cost = { wood: grab('lumber'), clay: grab('clay'), iron: grab('iron'), crop: grab('crop') };
                  const stockNum = (id) => {
                    const el = document.querySelector(id);
                    const t = (el ? (el.textContent || '') : '').replace(/[^0-9]/g, '');
                    return t ? (Number(t) || 0) : 0;
                  };
                  const stock = { wood: stockNum('#l1'), clay: stockNum('#l2'), iron: stockNum('#l3'), crop: stockNum('#l4') };
                  const short = (k) => Math.max(0, cost[k] - stock[k]);
                  return JSON.stringify({ wood: short('wood'), clay: short('clay'), iron: short('iron'), crop: short('crop') });
                }
                """);
        }
        catch (PlaywrightException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HeroInventoryResources>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // True when the hero's cached amounts cover every resource that is short. Resources that are
    // not short have shortfall 0, so they are trivially covered.
    private static bool HeroCoversShortfall(HeroInventoryResources hero, HeroInventoryResources shortfall)
    {
        return hero.Wood >= shortfall.Wood
            && hero.Clay >= shortfall.Clay
            && hero.Iron >= shortfall.Iron
            && hero.Crop >= shortfall.Crop;
    }

    private string BuildHeroInventoryCacheKey() => $"{AccountName}|{ServerUrl}";

    /// <summary>Returns the last known hero inventory for this account, or null if never read.</summary>
    public HeroInventoryResources? TryGetCachedHeroInventory()
    {
        var key = BuildHeroInventoryCacheKey();
        lock (HeroInventoryCacheSync)
        {
            return CachedHeroInventoryByKey.TryGetValue(key, out var cached) ? cached : null;
        }
    }

    // Stores the latest full read and notifies listeners.
    private void UpdateHeroInventoryCache(HeroInventoryResources resources)
    {
        var key = BuildHeroInventoryCacheKey();
        lock (HeroInventoryCacheSync)
        {
            CachedHeroInventoryByKey[key] = resources;
        }

        HeroInventoryUpdated?.Invoke(AccountName, resources);
    }

    // Subtracts the just-transferred amounts from the cache (floored at 0) and notifies listeners.
    // No-op when nothing has been read yet — we only adjust a value we actually know.
    private void DeductFromHeroInventoryCache(HeroInventoryResources transferred)
    {
        var current = TryGetCachedHeroInventory();
        if (current is null)
        {
            return;
        }

        var updated = new HeroInventoryResources(
            Math.Max(0, current.Wood - transferred.Wood),
            Math.Max(0, current.Clay - transferred.Clay),
            Math.Max(0, current.Iron - transferred.Iron),
            Math.Max(0, current.Crop - transferred.Crop));

        Notify($"[hero-transfer] cached inventory updated: wood={updated.Wood} clay={updated.Clay} iron={updated.Iron} crop={updated.Crop}");
        UpdateHeroInventoryCache(updated);
    }
}
