using Microsoft.Playwright;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using System.Globalization;
using System.Text.Json.Serialization;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private static readonly string[] CatapultConfirmButtonSelectors =
    [
        ".button-container:has(.button-content:text-is('Confirm'))",
        "button:has(.button-content:text-is('Confirm'))",
        "button:has-text('Confirm')",
        "input[type='submit'][value*='Confirm' i]",
        "input[type='button'][value*='Confirm' i]",
        "a:has-text('Confirm')",
        ".button-content:text-is('Confirm')",
    ];

    public async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(CancellationToken cancellationToken = default)
    {
        var setupInfo = await ReadCatapultWaveSetupInfoAsync(forceRefresh: false, cancellationToken);
        return setupInfo.AvailableTroops;
    }

    public async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        var setupInfo = await ReadCatapultWaveSetupInfoAsync(forceRefresh, cancellationToken);
        return setupInfo.AvailableTroops;
    }

    public async Task<CatapultWaveSetupInfo> ReadCatapultWaveSetupInfoAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        if (forceRefresh)
        {
            await RefreshCatapultSendTroopsPageAsync(cancellationToken);
        }
        else
        {
            await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: true);
        }

        var tribe = await ReadTribeAsync(cancellationToken);
        var availableTroops = await ReadAvailableTroopsOnCurrentSendTroopsPageAsync(tribe, cancellationToken);
        var rallyPointLevel = await TryReadRallyPointLevelAsync(cancellationToken);
        return new CatapultWaveSetupInfo(availableTroops, rallyPointLevel);
    }

    public async Task<CatapultWaveRunResult> StartCatapultWavesAsync(
        CatapultWaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var plan = CatapultWavePlanner.BuildPlan(request);
        Notify($"[catapult] starting — target ({request.X}|{request.Y}), {plan.Attacks.Count} attack(s): 1 first + {request.WaveCount} wave(s)");

        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: true);
        var tribe = await ReadTribeAsync(cancellationToken);
        var availableTroops = await ReadAvailableTroopsOnCurrentSendTroopsPageAsync(tribe, cancellationToken);
        CatapultWavePlanner.ValidateAvailability(plan, request.WaveCount, availableTroops);

        var prepared = new List<PreparedCatapultAttack>();
        try
        {
            for (var i = 0; i < plan.Attacks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attack = plan.Attacks[i];
                var page = i == 0 ? _page : await _page.Context.NewPageAsync();
                page.SetDefaultTimeout(_config.TimeoutMs);

                Notify($"[catapult:verbose] preparing {attack.Label.ToLowerInvariant()} to ({request.X}|{request.Y})");
                var preparedAttack = await PrepareCatapultAttackPageAsync(
                    page,
                    attack,
                    request,
                    allowReuseCurrentPage: i == 0,
                    cancellationToken);
                prepared.Add(preparedAttack);
            }

            VerifyCatapultArrivalOrder(prepared);
            foreach (var attack in prepared)
            {
                await attack.Page.BringToFrontAsync();
                await EnsureCatapultConfirmReadyAsync(attack, cancellationToken);
            }

            var sent = 0;
            var failed = 0;
            foreach (var attack in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await attack.Page.BringToFrontAsync();
                await EnsureCatapultConfirmReadyAsync(attack, cancellationToken);
                var clicked = await TryClickConfirmButtonAsync(attack.Page, cancellationToken);
                if (clicked)
                {
                    if (await WaitForCatapultSendResultAsync(attack, cancellationToken))
                    {
                        sent++;
                        Notify($"[catapult] sent {attack.Label.ToLowerInvariant()} to ({request.X}|{request.Y})");
                    }
                    else
                    {
                        failed++;
                        Notify($"[catapult] FAILED to confirm {attack.Label.ToLowerInvariant()} to ({request.X}|{request.Y})");
                    }
                }
                else
                {
                    failed++;
                    Notify($"[catapult] FAILED to confirm {attack.Label.ToLowerInvariant()} to ({request.X}|{request.Y}) (confirm button not clickable)");
                }
            }

            await Task.Delay(250, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared after sending catapult waves.", cancellationToken);
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);

            // Only mention failures when there are any: the word "failed" trips the alarm panel,
            // so a clean run must not contain it, while a partial failure legitimately should.
            Notify(failed > 0
                ? $"[catapult] done — sent {sent}/{prepared.Count} prepared, {failed} failed, target ({request.X}|{request.Y})"
                : $"[catapult] done — sent {sent}/{prepared.Count} prepared, target ({request.X}|{request.Y})");
            return new CatapultWaveRunResult(
                plan.Attacks.Count,
                prepared.Count,
                sent,
                failed,
                request.X,
                request.Y);
        }
        finally
        {
            foreach (var attack in prepared.Skip(1))
            {
                try
                {
                    if (!attack.Page.IsClosed)
                    {
                        await attack.Page.CloseAsync();
                    }
                }
                catch
                {
                    // Best effort cleanup for temporary tabs.
                }
            }
        }
    }

    private async Task RefreshCatapultSendTroopsPageAsync(CancellationToken cancellationToken)
    {
        await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: true);
        await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while refreshing send troops.", cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        if (!await IsSendTroopsPageAsync(cancellationToken))
        {
            await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: false);
        }
    }

    private async Task<PreparedCatapultAttack> PrepareCatapultAttackPageAsync(
        IPage page,
        CatapultWaveAttackPlan attack,
        CatapultWaveRequest request,
        bool allowReuseCurrentPage,
        CancellationToken cancellationToken)
    {
        if (!allowReuseCurrentPage || !await IsSendTroopsPageAsync(page, cancellationToken))
        {
            await GotoAsync(page, RallyPointSendTroopsPath, cancellationToken);
        }

        if (!await IsSendTroopsPageAsync(page, cancellationToken))
        {
            throw new InvalidOperationException("Could not open Send Troops page for catapult waves.");
        }

        await ClearTroopInputsAsync(page, cancellationToken);
        await FillFirstAvailableAsync(page, ["input[name='x']", "input[name='xCoord']", "input[id*='xCoord' i]"], request.X.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await FillFirstAvailableAsync(page, ["input[name='y']", "input[name='yCoord']", "input[id*='yCoord' i]"], request.Y.ToString(CultureInfo.InvariantCulture), cancellationToken);

        foreach (var troop in attack.Troops)
        {
            var troopIndex = TroopCatalog.ResolveTroopIndex(troop.Key);
            if (troopIndex is null)
            {
                throw new InvalidOperationException($"Could not resolve troop slot for '{troop.Key}'.");
            }

            var filled = await TryFillTroopInputAsync(page, $"t{troopIndex.Value}", troop.Key, troop.Value, cancellationToken);
            if (!filled)
            {
                throw new InvalidOperationException($"Could not fill troop field '{troop.Key}' for {attack.Label.ToLowerInvariant()}.");
            }

            if (string.Equals(attack.Label, CatapultWavePlanner.FirstAttackLabel, StringComparison.OrdinalIgnoreCase))
            {
                Notify($"[catapult:verbose] filled first-attack troop {troop.Key}={troop.Value}");
            }
        }

        if (!await TrySelectAttackModeAsync(page, request.RaidAttack, cancellationToken))
        {
            var attackMode = request.RaidAttack ? "raid" : "normal attack";
            throw new InvalidOperationException($"Could not select {attackMode} for {attack.Label.ToLowerInvariant()}.");
        }

        if (!await TryClickConfirmButtonAsync(page, cancellationToken))
        {
            throw new InvalidOperationException($"Could not open confirmation page for {attack.Label.ToLowerInvariant()}.");
        }

        var attackError = await TryReadAttackErrorAsync(page, cancellationToken);
        if (!string.IsNullOrWhiteSpace(attackError))
        {
            throw new InvalidOperationException(FormatCatapultAttackError(attack.Label, attackError));
        }

        if (!await WaitForManualAttackConfirmationPageAsync(page, cancellationToken))
        {
            attackError = await TryReadAttackErrorAsync(page, cancellationToken);
            if (!string.IsNullOrWhiteSpace(attackError))
            {
                throw new InvalidOperationException(FormatCatapultAttackError(attack.Label, attackError));
            }

            throw new InvalidOperationException($"Confirmation page did not load for {attack.Label.ToLowerInvariant()}.");
        }

        if (HasCatapultTroops(attack.Troops))
        {
            var targetResult = await TrySelectCatapultTargetsAsync(page, null, null, cancellationToken);
            if (!targetResult.Success)
            {
                throw new InvalidOperationException(targetResult.Message);
            }
        }

        var durationSeconds = await TryReadAttackDurationSecondsAsync(page, cancellationToken);
        return new PreparedCatapultAttack(page, attack.Label, durationSeconds);
    }

    private async Task EnsureCatapultConfirmReadyAsync(PreparedCatapultAttack attack, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await attack.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                {
                    Timeout = Math.Min(_config.TimeoutMs, 1000),
                });
            }
            catch (TimeoutException)
            {
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
            }

            var error = await TryReadAttackErrorAsync(attack.Page, cancellationToken);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(FormatCatapultAttackError(attack.Label, error));
            }

            if (await HasVisibleSelectorAsync(attack.Page, CatapultConfirmButtonSelectors))
            {
                return;
            }

            await Task.Delay(150, cancellationToken);
        }

        throw new InvalidOperationException($"Confirmation page is not ready for {attack.Label.ToLowerInvariant()}.");
    }

    private async Task<bool> WaitForCatapultSendResultAsync(PreparedCatapultAttack attack, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await attack.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                {
                    Timeout = Math.Min(_config.TimeoutMs, 800),
                });
            }
            catch (TimeoutException)
            {
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
            }

            var error = await TryReadAttackErrorAsync(attack.Page, cancellationToken);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(FormatCatapultAttackError(attack.Label, error));
            }

            if (!await HasVisibleSelectorAsync(attack.Page, CatapultConfirmButtonSelectors))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
    }

    private async Task ClearTroopInputsAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.EvaluateAsync(
            """
            () => {
              const isTroopInput = (input) => {
                const name = (input.getAttribute('name') || '').toLowerCase();
                const id = (input.id || '').toLowerCase();
                return /(^|\[)t\d+(\]|$)/.test(name) || /^t\d+$/.test(id);
              };

              for (const input of Array.from(document.querySelectorAll('input[type="text"], input[type="number"], input:not([type])'))) {
                if (!isTroopInput(input) || input.disabled || input.readOnly || input.getAttribute('disabled') !== null) continue;
                input.value = '';
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
              }
            }
            """);
    }

    private async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsOnCurrentSendTroopsPageAsync(string tribe, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var troopType in TroopCatalog.ResolveTroopTypesForTribe(tribe))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var troopIndex = TroopCatalog.ResolveTroopIndex(troopType);
            if (troopIndex is null)
            {
                result[troopType] = 0;
                continue;
            }

            result[troopType] = await ReadAvailableTroopCountAsync($"t{troopIndex.Value}", cancellationToken) ?? 0;
        }

        return result;
    }

    private async Task<int?> TryReadRallyPointLevelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<int?>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
                  const candidates = [
                    ...Array.from(document.querySelectorAll('h1, h2, .titleInHeader, .buildingTitle, .content .title, .content')),
                  ].map(element => clean(element.textContent));

                  for (const text of candidates) {
                    if (!/rally\s*point/i.test(text)) continue;
                    const match = text.match(/(?:level|lvl)\s*(\d{1,2})/i)
                      || text.match(/(\d{1,2})\s*(?:level|lvl)/i);
                    if (match) return Number(match[1]);
                  }

                  const body = clean(document.body?.innerText || '');
                  const bodyMatch = body.match(/rally\s*point.{0,80}(?:level|lvl)\s*(\d{1,2})/i)
                    || body.match(/(?:level|lvl)\s*(\d{1,2}).{0,80}rally\s*point/i);
                  return bodyMatch ? Number(bodyMatch[1]) : null;
                }
                """);
        }
        catch (Exception ex)
        {
            Notify($"[catapult:verbose] could not read Rally Point level: {ex.Message}");
            return null;
        }
    }

    private async Task GotoAsync(IPage page, string pathOrUrl, CancellationToken cancellationToken)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl
            : $"{_config.BaseUrl.TrimEnd('/')}/{pathOrUrl.TrimStart('/')}";

        var response = await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = _config.TimeoutMs,
        });
        if (response is not null && response.Headers.TryGetValue("date", out var dateHeader))
        {
            RecordServerTime(dateHeader);
        }
    }

    private async Task FillFirstAvailableAsync(IPage page, IEnumerable<string> selectors, string value, CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            await locator.FillAsync(value, new LocatorFillOptions { Timeout = _config.TimeoutMs });
            return;
        }

        throw new InvalidOperationException($"Could not find input field for selectors: {string.Join(", ", selectors)}.");
    }

    private async Task<bool> HasVisibleSelectorAsync(IPage page, IEnumerable<string> selectors)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            if (await locator.IsVisibleAsync())
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> IsSendTroopsPageAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await page.EvaluateAsync<bool>(
            """
            () => {
              const hasCoords = !!document.querySelector('input[name="x"], input[name="y"], input[name*="xCoord" i], input[name*="yCoord" i], input[id*="xCoord" i], input[id*="yCoord" i]');
              // SS uses radio name="c"; official Travian (T4.6) uses name="eventType".
              const hasAttackMode = !!document.querySelector('input[type="radio"][name="c"], input[type="radio"][name="eventType"]');
              const body = (document.body?.innerText || '').toLowerCase();
              return hasCoords && hasAttackMode && body.includes('send troops');
            }
            """);
    }

    private async Task<bool> TryFillTroopInputAsync(IPage page, string fieldToken, string troopType, int troopCountToSend, CancellationToken cancellationToken)
    {
        var selectors = new[]
        {
            $"input[name='troops[0][{fieldToken}]']",
            $"input[name$='[{fieldToken}]']",
            $"input[name='{fieldToken}']",
            $"input[id$='{fieldToken}']",
        };

        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            if (!await locator.IsVisibleAsync())
            {
                continue;
            }

            var isDisabled = await locator.EvaluateAsync<bool>("node => !!node.disabled || !!node.readOnly || node.getAttribute('disabled') !== null");
            if (isDisabled)
            {
                continue;
            }

            await locator.FillAsync(troopCountToSend.ToString(CultureInfo.InvariantCulture), new LocatorFillOptions { Timeout = _config.TimeoutMs });
            var filledValue = await locator.InputValueAsync(new LocatorInputValueOptions { Timeout = _config.TimeoutMs });
            return string.Equals(filledValue.Trim(), troopCountToSend.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        return await page.EvaluateAsync<bool>(
            """
            (args) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const inputs = Array.from(document.querySelectorAll('input[type="text"], input[type="number"], input:not([type])'));
              const candidate = inputs.find(node => {
                if (node.disabled || node.readOnly || node.getAttribute('disabled') !== null) return false;
                const style = window.getComputedStyle(node);
                const rect = node.getBoundingClientRect();
                if (style.display === 'none' || style.visibility === 'hidden' || rect.width <= 0 || rect.height <= 0) return false;
                const text = normalize(`${node.closest('tr, td, div, label, li, .troop_details, .details')?.textContent || ''} ${node.getAttribute('title') || ''} ${node.getAttribute('aria-label') || ''}`);
                return text.includes(normalize(args.troopType));
              });
              if (!candidate) return false;
              candidate.focus();
              candidate.value = String(args.count);
              candidate.dispatchEvent(new Event('input', { bubbles: true }));
              candidate.dispatchEvent(new Event('change', { bubbles: true }));
              return candidate.value === String(args.count);
            }
            """,
            new { troopType, count = troopCountToSend });
    }

    private async Task<bool> TrySelectAttackModeAsync(IPage page, bool raidAttack, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await page.EvaluateAsync<bool>(
            """
            (raidAttack) => {
              // SS uses radio name="c"; official Travian (T4.6) uses name="eventType".
              // Attack (3) and raid (4) values are identical on both.
              const radioButtons = Array.from(document.querySelectorAll('input[type="radio"][name="c"], input[type="radio"][name="eventType"]'));
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

    private async Task<bool> WaitForManualAttackConfirmationPageAsync(IPage page, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
            }

            if (await HasVisibleSelectorAsync(page, CatapultConfirmButtonSelectors))
            {
                return true;
            }

            if (attempt < 12)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        return false;
    }

    private async Task<bool> TryClickConfirmButtonAsync(IPage page, CancellationToken cancellationToken)
    {
        foreach (var selector in CatapultConfirmButtonSelectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locator = page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            if (!await locator.IsVisibleAsync())
            {
                continue;
            }
            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs, Force = true });
            return true;
        }

        return false;
    }

    private async Task<string?> TryReadAttackErrorAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await page.EvaluateAsync<string?>(
                """
                () => {
                  const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
                  const error = Array.from(document.querySelectorAll('p.error, .error'))
                    .map(node => normalize(node.textContent))
                    .find(text => text.length > 0);
                  return error || null;
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
    }

    private static string FormatCatapultAttackError(string attackLabel, string error)
    {
        return $"ALARM: Could not send catapult {attackLabel.ToLowerInvariant()}: {error}";
    }

    private async Task<CatapultTargetSelectResult> TrySelectCatapultTargetsAsync(IPage page, string? target1, string? target2, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await page.EvaluateAsync<CatapultTargetSelectResult>(
            """
            (args) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const wanted = [args.target1, args.target2].map(value => normalize(value) || 'random');
              const explicitSecondTarget = normalize(args.target2).length > 0;
              const selects = Array.from(document.querySelectorAll('select')).filter(select => {
                const name = normalize(`${select.getAttribute('name') || ''} ${select.id || ''}`);
                const context = normalize(select.closest('tr, div, td, label, form')?.textContent || '');
                const options = normalize(Array.from(select.options || []).map(option => option.textContent || '').join(' '));
                return name.includes('ctar') || name.includes('target') || name.includes('kata') || name.includes('building') ||
                  context.includes('target') || context.includes('building') || options.includes('random');
              });

              if (selects.length === 0) {
                return { success: false, message: 'Could not find catapult target fields on the confirmation page.' };
              }

              const chooseOption = (select, desired) => {
                const options = Array.from(select.options || []);
                let match = options.find(option => normalize(option.textContent) === desired || normalize(option.value) === desired);
                if (!match && desired !== 'random') {
                  match = options.find(option => normalize(option.textContent).includes(desired));
                }
                if (!match && desired === 'random') {
                  match = options.find(option => normalize(option.textContent).includes('random') || ['0', '-1', '99'].includes((option.value || '').trim()));
                }
                if (!match && desired === 'random') {
                  match = options[0];
                }
                if (!match) {
                  return false;
                }

                select.value = match.value;
                select.dispatchEvent(new Event('input', { bubbles: true }));
                select.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
              };

              if (!chooseOption(selects[0], wanted[0])) {
                return { success: false, message: `Could not select catapult target '${args.target1 || 'Random'}'.` };
              }

              if (selects.length > 1) {
                if (!chooseOption(selects[1], wanted[1])) {
                  return { success: false, message: `Could not select second catapult target '${args.target2 || 'Random'}'.` };
                }
              } else if (explicitSecondTarget) {
                return { success: false, message: 'Second catapult target was requested, but only one target field is available.' };
              }

              return { success: true, message: '' };
            }
            """,
            new { target1, target2 });
    }

    private async Task<int?> TryReadAttackDurationSecondsAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await page.EvaluateAsync<int?>(
            """
            () => {
              const text = (document.body?.innerText || '').replace(/\s+/g, ' ').trim();
              const patterns = [
                /(?:Duration|Dauer|Durée|Durata|Varaktighet|Tijd)[^\d]*(\d{1,2}):(\d{2}):(\d{2})/i,
                /(?:Duration|Dauer|Durée|Durata|Varaktighet|Tijd)[^\d]*(\d{1,2}):(\d{2})/i
              ];
              for (const pattern of patterns) {
                const match = text.match(pattern);
                if (!match) continue;
                if (match.length === 4) {
                  return Number(match[1]) * 3600 + Number(match[2]) * 60 + Number(match[3]);
                }
                return Number(match[1]) * 60 + Number(match[2]);
              }
              return null;
            }
            """);
    }

    private sealed record PreparedCatapultAttack(IPage Page, string Label, int? DurationSeconds);

    private sealed class CatapultTargetSelectResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;
    }

    private static bool HasCatapultTroops(IReadOnlyDictionary<string, int> troops)
    {
        return troops.Any(pair => pair.Value > 0 && TroopCatalog.ResolveTroopIndex(pair.Key) == 8);
    }

    private static void VerifyCatapultArrivalOrder(IReadOnlyList<PreparedCatapultAttack> prepared)
    {
        if (prepared.Count <= 1)
        {
            return;
        }

        var firstDuration = prepared[0].DurationSeconds;
        if (firstDuration is null)
        {
            return;
        }

        foreach (var wave in prepared.Skip(1))
        {
            if (wave.DurationSeconds is not int waveDuration)
            {
                continue;
            }

            if (firstDuration.Value > waveDuration)
            {
                throw new InvalidOperationException(
                    $"First attack travel time is longer than {wave.Label.ToLowerInvariant()}. Add slower troops to waves or use a faster first attack.");
            }
        }
    }
}
