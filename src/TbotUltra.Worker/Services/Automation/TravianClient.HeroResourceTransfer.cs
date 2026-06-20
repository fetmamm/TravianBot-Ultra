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
    // Set when a transfer is skipped because topping up would exceed the per-resource hero-use limit.
    // Holds the calculated seconds to wait until the village has accumulated enough that the hero share
    // fits the limit; consumed by ReadUpgradeResourceWaitSnapshotAsync so the construction defer uses
    // this targeted wait instead of the (longer) "until fully affordable" estimate. Reset each attempt.
    private int? _heroTransferOverLimitWaitSeconds;

    private async Task<bool> TryHeroResourceTransferForConstructionAsync(
        string label,
        CancellationToken cancellationToken)
    {
        if (!_config.HeroResourceUseConstruction)
        {
            return false;
        }

        return await TryHeroResourceTransferOnCurrentBuildPageAsync(label, cancellationToken);
    }

    // Best-effort hero top-up for the celebration on the current brewery build page. Reuses the generic
    // build-page transfer flow (the brewery page shows the same .inlineIcon.resource.transfer when short
    // on resources). Gated by the per-consumer brewery toggle on top of the master switch.
    private async Task<bool> TryHeroResourceTransferForBreweryAsync(
        string label,
        CancellationToken cancellationToken)
    {
        if (!_config.HeroResourceUseBrewery)
        {
            return false;
        }

        return await TryHeroResourceTransferOnCurrentBuildPageAsync(label, cancellationToken);
    }

    // Best-effort hero top-up for Town Hall celebrations on the current Town Hall build page.
    // Gated separately so the user can opt Town Hall in without changing construction/brewery behavior.
    private async Task<bool> TryHeroResourceTransferForTownHallAsync(
        string label,
        CancellationToken cancellationToken)
    {
        if (!_config.HeroResourceUseTownHall)
        {
            return false;
        }

        return await TryHeroResourceTransferOnCurrentBuildPageAsync(
            label,
            cancellationToken,
            preferTownHallCelebration: true);
    }

    // Generic Official build-page hero top-up: probes for a transfer icon, applies the per-resource use
    // limit / cached-inventory gates, then opens and confirms the transfer dialog. Shared by construction
    // and brewery; the per-consumer gating is done by the callers above.
    private async Task<bool> TryHeroResourceTransferOnCurrentBuildPageAsync(
        string label,
        CancellationToken cancellationToken,
        bool preferTownHallCelebration = false)
    {
        _heroTransferOverLimitWaitSeconds = null;

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
                """
                (preferTownHallCelebration) => {
                  const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
                  const findTownHallCelebrationScope = () => {
                    const root = document.querySelector('.build_details') || document;
                    const rows = Array.from(root.querySelectorAll('.research, tr, li, .row, .information'));
                    return rows.find(row => /small\s+celebration/i.test(normalize(row.textContent || ''))) || root;
                  };
                  const root = preferTownHallCelebration ? findTownHallCelebrationScope() : document;
                  return !!root?.querySelector('.inlineIcon.resource.transfer');
                }
                """,
                preferTownHallCelebration);
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

        // Per-resource missing amount (cost minus current village stock) for this upgrade. Used by both
        // the hero-use limit gate and the cached-inventory cover check below.
        var shortfall = await ReadUpgradeShortfallOnBuildPageAsync(
            cancellationToken,
            preferTownHallCelebration);

        // Hero-use limit gate: if covering any single resource from the hero would pull more than the
        // configured per-resource limit, skip the transfer and defer until the village has produced
        // enough that the hero share fits the limit (then the transfer runs with the limited amount).
        // Keeps the hero's resources from being drained on expensive buildings. Non-blocking: the defer
        // is handled by the caller's resource-wait path, and it's logged once (no spam).
        if (_config.HeroResourceMaxUseEnabled
            && _config.HeroResourceMaxUsePerResource > 0
            && shortfall is not null)
        {
            var overLimitWait = await ComputeHeroUseLimitDeferSecondsAsync(shortfall, cancellationToken);
            if (overLimitWait is int waitSeconds)
            {
                _heroTransferOverLimitWaitSeconds = waitSeconds;
                Notify($"[hero-transfer] skip at {label}. Per-resource hero-use limit "
                    + $"({_config.HeroResourceMaxUsePerResource}) would be exceeded "
                    + $"(need from hero: wood={shortfall.Wood} clay={shortfall.Clay} iron={shortfall.Iron} crop={shortfall.Crop}). "
                    + $"Waiting ~{waitSeconds}s for the village to accumulate enough, then retrying.");
                return false;
            }
        }

        // Proactive gate: if we already know (from the cached inventory) the hero cannot fully cover
        // the missing resources, skip without opening the dialog — a partial transfer would spend
        // hero resources without unblocking the upgrade. With no cache yet we fall through to the
        // reactive behaviour (open the dialog and let Travian decide).
        var cachedInventory = TryGetCachedHeroInventory();
        if (cachedInventory is not null && shortfall is not null)
        {
            if (!HeroCoversShortfall(cachedInventory, shortfall))
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
                (preferTownHallCelebration) => {
                  const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
                  const findTownHallCelebrationScope = () => {
                    const root = document.querySelector('.build_details') || document;
                    const rows = Array.from(root.querySelectorAll('.research, tr, li, .row, .information'));
                    return rows.find(row => /small\s+celebration/i.test(normalize(row.textContent || ''))) || root;
                  };
                  const root = preferTownHallCelebration ? findTownHallCelebrationScope() : document;
                  const icon = preferTownHallCelebration
                    ? root?.querySelector('.inlineIcon.resource.transfer.fillUp, .inlineIcon.resource.transfer[onclick], .inlineIcon.resource.transfer')
                    : document.querySelector('.upgradeBlocked .inlineIcon.resource.transfer.fillUp, .inlineIcon.resource.transfer.fillUp, .inlineIcon.resource.transfer');
                  if (!icon) return false;
                  icon.click();
                  return true;
                }
                """,
                preferTownHallCelebration);
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

        // Travian usually auto-fills the amounts. When it opens the dialog with all inputs left at 0,
        // fill the exact village shortfall manually so the confirm button can become active.

        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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

        var actualInventory = await ReadHeroInventoryFromTransferDialogAsync(cancellationToken);
        if (actualInventory is not null)
        {
            Notify($"[hero-transfer] inventory resynced from dialog: wood={actualInventory.Wood} clay={actualInventory.Clay} iron={actualInventory.Iron} crop={actualInventory.Crop}");
            UpdateHeroInventoryCache(actualInventory);
        }

        // The dialog now reports what the hero actually carries. If that still cannot cover the shortfall
        // (Travian greys out the inputs and the "Transfer selected" button stays disabled), a transfer
        // would do nothing — so close the dialog and wait for the village to accumulate the rest. The
        // wait is computed from cached production vs the shortfall the hero can't cover (no page reads).
        if (actualInventory is not null && shortfall is not null && !HeroCoversShortfall(actualInventory, shortfall))
        {
            var waitSeconds = await ComputeAccumulationWaitSecondsAsync(
                Math.Max(0, shortfall.Wood - actualInventory.Wood),
                Math.Max(0, shortfall.Clay - actualInventory.Clay),
                Math.Max(0, shortfall.Iron - actualInventory.Iron),
                Math.Max(0, shortfall.Crop - actualInventory.Crop),
                cancellationToken);
            _heroTransferOverLimitWaitSeconds = waitSeconds;
            Notify($"[hero-transfer] hero inventory cannot cover {label} "
                + $"(need wood={shortfall.Wood} clay={shortfall.Clay} iron={shortfall.Iron} crop={shortfall.Crop}; "
                + $"hero has wood={actualInventory.Wood} clay={actualInventory.Clay} iron={actualInventory.Iron} crop={actualInventory.Crop}). "
                + $"Waiting ~{waitSeconds}s for the village to accumulate the rest.");
            await TryDismissResourceTransferDialogAsync(cancellationToken);
            return false;
        }

        if (shortfall is not null)
        {
            var manualFill = await TryFillHeroResourceTransferDialogAsync(shortfall, actualInventory, cancellationToken);
            if (manualFill is not null)
            {
                transferred = manualFill;
                Notify($"[hero-transfer] manually filled amounts: wood={manualFill.Wood} clay={manualFill.Clay} iron={manualFill.Iron} crop={manualFill.Crop}");
            }
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
                  return !!button
                    && !button.disabled
                    && button.getAttribute('aria-disabled') !== 'true'
                    && !button.classList.contains('disabled');
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
                  if (button.disabled || button.getAttribute('aria-disabled') === 'true' || button.classList.contains('disabled')) return false;
                  button.click();
                  return true;
                }
                """);
        }
        catch (TimeoutException)
        {
            // Button never became enabled (e.g. hero still can't cover after all) — don't transfer.
            Notify($"[hero-transfer] 'Transfer selected' stayed disabled at {label}; closing dialog and skipping.");
            await TryDismissResourceTransferDialogAsync(cancellationToken);
            return false;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"[hero-transfer] transient error confirming transfer at {label}; skipping");
            return false;
        }

        if (!confirmed)
        {
            Notify($"[hero-transfer] could not find 'Transfer selected' button at {label}; skipping");
            await TryDismissResourceTransferDialogAsync(cancellationToken);
            return false;
        }

        Notify($"[hero-transfer] transfer confirmed at {label}; waiting for page reload");
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load

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
            // The dialog did not close on its own (synthetic click can be ignored by React, or Travian
            // keeps the dialog open after the transfer). Do NOT leave it open: a lingering #dialogOverlay
            // intercepts pointer events and breaks the next upgrade/construct click (same failure class as
            // the "Open shop" overlay). Actively dismiss it, then re-check.
            if (await TryDismissResourceTransferDialogAsync(cancellationToken))
            {
                Notify($"[hero-transfer] transfer dialog dismissed after confirm at {label}.");
            }
            else
            {
                Notify($"[hero-transfer] transfer dialog still visible after confirm at {label}; continuing with current page state.");
            }
        }

        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay

        // Keep the cached inventory in sync by deducting what Travian just moved out of it. We do
        // NOT re-navigate to the inventory page here (that would derail the upgrade loop) — the
        // amounts the dialog reported are exactly what was transferred.
        if (transferred is not null)
        {
            DeductFromHeroInventoryCache(transferred);
        }

        return true;
    }

    private async Task<HeroInventoryResources?> TryFillHeroResourceTransferDialogAsync(
        HeroInventoryResources shortfall,
        HeroInventoryResources? actualInventory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (shortfall.Wood <= 0 && shortfall.Clay <= 0 && shortfall.Iron <= 0 && shortfall.Crop <= 0)
        {
            return null;
        }

        object? inventoryPayload = actualInventory is null
            ? null
            : new { wood = actualInventory.Wood, clay = actualInventory.Clay, iron = actualInventory.Iron, crop = actualInventory.Crop };

        try
        {
            var json = await _page.EvaluateAsync<string>(
                """
                (data) => {
                  const dialog = document.querySelector('div.resourceTransferDialog')
                    || ((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources'
                      ? document.querySelector('#dialogContent')
                      : null);
                  if (!dialog) return '';

                  const desired = data.shortfall || {};
                  const inventory = data.inventory || null;
                  const names = [
                    ['lumber', 'wood'],
                    ['clay', 'clay'],
                    ['iron', 'iron'],
                    ['crop', 'crop']
                  ];

                  const parse = (value) => {
                    const text = String(value || '').replace(/[^0-9]/g, '');
                    return text ? Number(text) || 0 : 0;
                  };
                  const current = {};
                  for (const [inputName, key] of names) {
                    current[key] = parse(dialog.querySelector('input[name="' + inputName + '"]')?.value);
                  }

                  const coversShortfall = names.every(([, key]) => current[key] >= (Number(desired[key]) || 0));
                  if (coversShortfall) {
                    return '';
                  }

                  const setInputValue = (input, value) => {
                    const text = String(Math.max(0, Math.floor(Number(value) || 0)));
                    const valueSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
                    if (valueSetter) {
                      valueSetter.call(input, text);
                    } else {
                      input.value = text;
                    }
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                    input.dispatchEvent(new Event('change', { bubbles: true }));
                  };

                  const filled = {};
                  for (const [inputName, key] of names) {
                    const input = dialog.querySelector('input[name="' + inputName + '"]');
                    if (!input) return '';
                    const need = Number(desired[key]) || 0;
                    const available = inventory ? Math.max(0, Number(inventory[key]) || 0) : need;
                    const amount = Math.min(need, available);
                    setInputValue(input, amount);
                    filled[key] = amount;
                  }

                  return JSON.stringify(filled);
                }
                """,
                new
                {
                    shortfall = new { wood = shortfall.Wood, clay = shortfall.Clay, iron = shortfall.Iron, crop = shortfall.Crop },
                    inventory = inventoryPayload
                });

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<HeroInventoryResources>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"[hero-transfer] transient error while manually filling transfer dialog: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is PlaywrightException or JsonException)
        {
            Notify($"[hero-transfer] manual transfer fill failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Best-effort close of a resource-transfer dialog that stayed open after confirm. Tries the dialog
    /// close button, then Escape, and waits briefly for the dialog (and its #dialogOverlay) to disappear so
    /// it cannot intercept the next page click. Returns true once the dialog is gone.
    /// </summary>
    private async Task<bool> TryDismissResourceTransferDialogAsync(CancellationToken cancellationToken)
    {
        const string dialogGoneScript =
            """
            () => !document.querySelector('div.resourceTransferDialog')
              && !((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources')
            """;

        try
        {
            var closeButton = _page.Locator("#dialogCancelButton, .dialogCancelButton, button[aria-label='Close']").First;
            if (await closeButton.CountAsync() > 0)
            {
                try
                {
                    await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                    await closeButton.ClickAsync(new LocatorClickOptions { Timeout = 2000 });
                }
                catch
                {
                    // Fall through to Escape.
                }
            }

            if (await _page.EvaluateAsync<bool>(dialogGoneScript))
            {
                return true;
            }

            await _page.Keyboard.PressAsync("Escape");

            await _page.WaitForFunctionAsync(
                dialogGoneScript,
                null,
                new PageWaitForFunctionOptions { Timeout = 3000 });
            return true;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<HeroInventoryResources?> ReadHeroInventoryFromTransferDialogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string json;
        try
        {
            json = await _page.EvaluateAsync<string>(
                """
                () => {
                  const dialog = document.querySelector('div.resourceTransferDialog')
                    || ((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources'
                      ? document.querySelector('#dialogContent')
                      : null);
                  if (!dialog) return '';

                  const parseCount = (text) => {
                    const normalized = (text || '').replace(/[^0-9]/g, '');
                    return normalized ? Number(normalized) || 0 : 0;
                  };

                  const findCountForInput = (name) => {
                    const input = dialog.querySelector('input[name="' + name + '"]');
                    if (!input) return null;

                    let node = input;
                    while (node && node !== dialog) {
                      const count = node.querySelector?.('.count');
                      const inputs = Array.from(node.querySelectorAll?.('input[name="lumber"], input[name="clay"], input[name="iron"], input[name="crop"]') || []);
                      if (count && inputs.length <= 1) return parseCount(count.textContent);

                      const previous = node.previousElementSibling;
                      if (previous?.matches?.('.count')) return parseCount(previous.textContent);
                      const next = node.nextElementSibling;
                      if (next?.matches?.('.count')) return parseCount(next.textContent);

                      node = node.parentElement;
                    }

                    return null;
                  };

                  const values = {
                    wood: findCountForInput('lumber'),
                    clay: findCountForInput('clay'),
                    iron: findCountForInput('iron'),
                    crop: findCountForInput('crop')
                  };

                  if (Object.values(values).every(value => value === null)) return '';

                  return JSON.stringify({
                    wood: values.wood ?? 0,
                    clay: values.clay ?? 0,
                    iron: values.iron ?? 0,
                    crop: values.crop ?? 0
                  });
                }
                """);
        }
        catch (PlaywrightException ex)
        {
            Notify($"[hero-transfer] inventory dialog resync skipped: {ex.Message}");
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
        catch (JsonException ex)
        {
            Notify($"[hero-transfer] inventory dialog resync parse failed: {ex.Message}");
            return null;
        }
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
        => await ReadUpgradeShortfallOnBuildPageAsync(cancellationToken, preferTownHallCelebration: false);

    private async Task<HeroInventoryResources?> ReadUpgradeShortfallOnBuildPageAsync(
        CancellationToken cancellationToken,
        bool preferTownHallCelebration)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string json;
        try
        {
            json = await _page.EvaluateAsync<string>(
                """
                (preferTownHallCelebration) => {
                  const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
                  const findTownHallCelebrationScope = () => {
                    const root = document.querySelector('.build_details') || document;
                    const rows = Array.from(root.querySelectorAll('.research, tr, li, .row, .information'));
                    return rows.find(row => /small\s+celebration/i.test(normalize(row.textContent || ''))) || root;
                  };
                  const root = preferTownHallCelebration ? findTownHallCelebrationScope() : document;
                  const icon = root?.querySelector('.inlineIcon.resource.transfer[onclick]');
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
                """,
                preferTownHallCelebration);
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

    // When any resource would need more than the per-resource limit pulled from the hero, returns the
    // seconds to wait until the most-constrained resource has produced enough locally that the hero
    // share drops to the limit (stock reaches cost - limit). Returns null when no resource is over the
    // limit (transfer may proceed). Falls back to a fixed wait when production can't be read, so the
    // build still defers without spamming retries.
    private async Task<int?> ComputeHeroUseLimitDeferSecondsAsync(
        HeroInventoryResources shortfall,
        CancellationToken cancellationToken)
    {
        var limit = _config.HeroResourceMaxUsePerResource;
        var anyOverLimit = shortfall.Wood > limit
            || shortfall.Clay > limit
            || shortfall.Iron > limit
            || shortfall.Crop > limit;
        if (!anyOverLimit)
        {
            return null;
        }

        // The village must still produce whatever exceeds the limit for each over-limit resource.
        return await ComputeAccumulationWaitSecondsAsync(
            Math.Max(0, shortfall.Wood - limit),
            Math.Max(0, shortfall.Clay - limit),
            Math.Max(0, shortfall.Iron - limit),
            Math.Max(0, shortfall.Crop - limit),
            cancellationToken);
    }

    // Seconds until the village will have produced the given per-resource amounts, using the production
    // the program already knows (cached from earlier reads) — never a build-page read, which would
    // trigger slow failing retries. Returns a fixed fallback when production is unknown/non-positive for
    // a needed resource, so the build defers (and retries later) instead of spamming.
    private async Task<int> ComputeAccumulationWaitSecondsAsync(
        long remainingWood,
        long remainingClay,
        long remainingIron,
        long remainingCrop,
        CancellationToken cancellationToken)
    {
        var productionByHour = await ReadCachedProductionByHourForActiveVillageAsync(cancellationToken);
        return UpgradeMath.ComputeResourceAccumulationWaitSeconds(
            remainingWood,
            remainingClay,
            remainingIron,
            remainingCrop,
            productionByHour);
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
