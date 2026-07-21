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
using TbotUltra.Worker.Services.Automation.Combat;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private Task EnsureRallyPointAndOpenSendTroopsPageAsync(CancellationToken cancellationToken, bool allowReuseCurrentPage)
        => CreateSendTroopsNavigator().OpenSendTroopsAsync(allowReuseCurrentPage, cancellationToken);

    // True when the current page is the Rally Point build view showing "Level 0" — i.e. the Rally Point
    // is not built yet, so farm lists are unavailable. Scoped to the rally point construct view
    // (#content.buildRallyPoint), so an open farm list (built rally point) never matches.
    private Task<bool> IsRallyPointLevelZeroAsync(CancellationToken cancellationToken)
        => CreateSendTroopsNavigator().IsRallyPointLevelZeroAsync(cancellationToken);

    private IRallyPointNavigator CreateSendTroopsNavigator() => new SendTroopsNavigator(
        _page,
        GotoAsync,
        token => EnsureLoggedInAsync(cancellationToken: token),
        Notify,
        Paths.RallyPointSendTroops,
        Paths.FarmListFastUp);

    private Task<bool> IsSendTroopsPageAsync(CancellationToken cancellationToken)
        => SendTroopsNavigator.IsSendTroopsPageAsync(_page, cancellationToken);

    private async Task<long?> ReadAvailableTroopCountAsync(string fieldToken, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _page.EvaluateAsync<long?>(
                """
                (fieldToken) => {
                  const token = (fieldToken || '').toLowerCase();
                  const parseAmount = (value) => {
                    const parsed = Number.parseInt((value || '').replace(/[^\d]/g, ''), 10);
                    return Number.isFinite(parsed) ? parsed : null;
                  };
                  const referencesField = (value) => {
                    const lower = (value || '').toLowerCase();
                    return lower.includes(`.${token}.value`)
                      || lower.includes(`[${token}]`)
                      || lower.includes(`'${token}'`)
                      || lower.includes(`"${token}"`);
                  };

                  const anchors = Array.from(document.querySelectorAll('a[onclick], button[onclick], div[onclick]'));
                  for (const anchor of anchors) {
                    const onclick = anchor.getAttribute('onclick') || '';
                    if (!referencesField(onclick)) continue;

                    const match = onclick.match(/(?:\.value\s*=\s*|\.val\(\s*)([\d\s.,]+)/i);
                    const parsed = match ? parseAmount(match[1]) : null;
                    if (parsed !== null) return parsed;
                  }

                  const input = document.querySelector(`input[name="${fieldToken}"], input[name="troop[${fieldToken}]"], input[name="troops[0][${fieldToken}]"], input[id$="${fieldToken}"], input[name$="[${fieldToken}]"]`);
                  if (!input) return null;

                  const scopeText = input.closest('td, tr, div')?.textContent || '';
                  const scopeMatch = scopeText.match(/\(([\d\s.,]+)\)/);
                  if (scopeMatch) {
                    const parsed = parseAmount(scopeMatch[1]);
                    if (parsed !== null) return parsed;
                  }

                  // Official Travian (T4.6): the available count is shown after the input as
                  // "<input> / <a onclick=\"...val(123)\">123</a>" or "<span>0</span>"
                  // inside the same cell (span.none means zero).
                  for (let sib = input.nextElementSibling; sib; sib = sib.nextElementSibling) {
                    if (sib.tagName === 'SPAN' || sib.tagName === 'A') {
                      const onclick = sib.getAttribute('onclick') || '';
                      const onclickMatch = referencesField(onclick)
                        ? onclick.match(/(?:\.value\s*=\s*|\.val\(\s*)([\d\s.,]+)/i)
                        : null;
                      const n = parseAmount(onclickMatch?.[1] || sib.textContent || '');
                      if (n !== null) return n;
                    }
                  }

                  if (input.disabled || input.getAttribute('disabled') !== null) return 0;

                  const maxValue = input.getAttribute('max') || '';
                  const maxParsed = Number.parseInt((maxValue || '').replace(/[^\d]/g, ''), 10);
                  if (Number.isFinite(maxParsed) && maxParsed > 0) return maxParsed;

                  return null;
                }
                """,
                fieldToken);

            return result;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify("Page navigated while reading available troops. Continuing without exact availability.");
            return null;
        }
    }

    private async Task<bool> TryFillTroopInputAsync(string fieldToken, string troopType, long troopCountToSend, CancellationToken cancellationToken)
    {
        foreach (var selector in BuildTroopInputSelectors(fieldToken))
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            var isDisabled = await locator.EvaluateAsync<bool>("node => !!node.disabled || !!node.readOnly || node.getAttribute('disabled') !== null");
            if (isDisabled)
            {
                continue;
            }

            await RetryAsync($"fill troop input {selector}", async () =>
            {
                await TypeHumanlyAsync(locator, troopCountToSend.ToString(CultureInfo.InvariantCulture), cancellationToken);
            }, cancellationToken: cancellationToken);
            return true;
        }

        var fallbackFilled = await _page.EvaluateAsync<bool>(
            """
            (args) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const inputs = Array.from(document.querySelectorAll('input[type="text"], input[type="number"], input:not([type])'));
              const candidate = inputs.find(node => {
                if (node.disabled || node.readOnly || node.getAttribute('disabled') !== null) return false;
                const text = normalize(`${node.closest('tr, td, div, label, li, .troop_details, .details')?.textContent || ''} ${node.getAttribute('title') || ''} ${node.getAttribute('aria-label') || ''}`);
                return text.includes(normalize(args.troopType));
              });
              if (!candidate) return false;
              candidate.focus();
              candidate.value = String(args.count);
              candidate.dispatchEvent(new Event('input', { bubbles: true }));
              candidate.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            }
            """,
            new { troopType, count = troopCountToSend });

        return fallbackFilled;
    }

    private async Task<bool> TrySelectAttackModeAsync(bool raidAttack, CancellationToken cancellationToken)
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                (raidAttack) => {
                  // Attack (3) and raid (4) values are stable values.
                  const radioButtons = Array.from(document.querySelectorAll('input[type="radio"][name="eventType"]'));
                  const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const radio = radioButtons.find(node => {
                    const value = (node.getAttribute('value') || '').trim();
                    const label = normalize(node.parentElement?.textContent || node.closest('label')?.textContent || '');
                    if (raidAttack) return value === '4' || label.includes('raid');
                    return value === '3' || label.includes('normal attack');
                  });
                  if (!radio) return false;
                  radio.checked = true;
                  radio.dispatchEvent(new Event('input', { bubbles: true }));
                  radio.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                }
                """,
                raidAttack);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify("Page navigated while selecting attack mode.");
            return false;
        }
    }

    private async Task<bool> WaitForSendTroopsConfirmationPageAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded)
                    .WaitAsync(cancellationToken);
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                // Continue polling during navigation.
            }

            var confirmReady = await HasAnySelectorAsync(
            [
                "button#confirmSendTroops",
                "button[name='confirmSendTroops']",
                "button.rallyPointConfirm",
                ".button-container:has(.button-content:text-is('Confirm'))",
                ".button-content:text-is('Confirm')",
                "button:has-text('Confirm')",
                "a:has-text('Confirm')",
            ]);

            if (confirmReady)
            {
                return true;
            }

            if (attempt < 12)
            {
                await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
            }
        }

        return false;
    }

    private async Task WaitForSendTroopsCompletionAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                {
                    Timeout = Math.Min(_config.TimeoutMs, 1200),
                });
            }
            catch (TimeoutException)
            {
                // The next loop iteration will recover by reopening Send Troops if needed.
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                // Continue polling during navigation.
            }

            if (await IsSendTroopsPageAsync(cancellationToken))
            {
                return;
            }

            var confirmStillVisible = await HasAnySelectorAsync(
            [
                "button#confirmSendTroops",
                "button[name='confirmSendTroops']",
                "button.rallyPointConfirm",
                ".button-container:has(.button-content:text-is('Confirm'))",
                ".button-content:text-is('Confirm')",
                "button:has-text('Confirm')",
                "a:has-text('Confirm')",
            ]);

            if (!confirmStillVisible)
            {
                return;
            }

            if (attempt < 4)
            {
                await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
            }
        }
    }

    private async Task<bool> TryClickConfirmButtonAsync(CancellationToken cancellationToken)
    {
        var selectors = new[]
        {
            "button#ok",
            "button[name='ok'][value='ok']",
            "button[type='submit'][name='ok']",
            "input[type='submit'][name='ok']",
            "button#confirmSendTroops",
            "button[name='confirmSendTroops']",
            "button.rallyPointConfirm",
            ".button-container:has(.button-content:text-is('Confirm'))",
            ".button-content:text-is('Confirm')",
            "button:has-text('Confirm')",
            "input[type='submit'][value*='Confirm' i]",
            "input[type='button'][value*='Confirm' i]",
            "a:has-text('Confirm')",
        };

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                foreach (var selector in selectors)
                {
                    var locator = _page.Locator(selector).First;
                    if (await locator.CountAsync() == 0)
                    {
                        continue;
                    }

                    await RetryAsync($"click confirm selector {selector}", async () =>
                    {
                        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                        await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                    }, cancellationToken: cancellationToken);

                    return true;
                }
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                Notify($"Confirm page navigated during attempt {attempt}/4. Retrying...");
            }
            catch (TimeoutException) when (attempt < 4)
            {
                Notify($"Confirm button timed out on attempt {attempt}/4. Retrying...");
            }

            if (attempt < 4)
            {
                await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
            }
        }

        return false;
    }

    private static string FormatLargeCount(long value)
    {
        return Math.Max(0, value).ToString("#,0", CultureInfo.InvariantCulture);
    }

    private static int? ClampLongToInt32(long? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Value switch
        {
            < int.MinValue => int.MinValue,
            > int.MaxValue => int.MaxValue,
            _ => (int)value.Value,
        };
    }

}
