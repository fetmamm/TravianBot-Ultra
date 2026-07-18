using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Hero revive probes and state-changing revive flow.
public sealed partial class TravianClient
{
    public async Task<bool> CheckAndReviveDeadHeroOnCurrentPageAsync(bool autoRevive, CancellationToken cancellationToken = default)
    {
        // Lightweight check from whatever page we are currently on. The dead hero is shown either by the
        // sidebar speech bubble (<div class="bigSpeechBubble dead">) or the top-bar hero status icon
        // (<div class="heroStatus">...<i class="heroDead">), depending on the page — accept either.
        var isDead = await _page.EvaluateAsync<bool>(
            "() => !!document.querySelector('.bigSpeechBubble.dead, .heroStatus i.heroDead, i.heroDead, [class*=\"heroDead\"]')");
        if (!isDead)
        {
            return false;
        }

        Notify("[hero] dead — bigSpeechBubble.dead detected on current page");
        if (!autoRevive)
        {
            Notify("Auto revive is disabled. Skipping revive.");
            return false;
        }

        var revived = await ReviveHeroOnInventoryAsync(cancellationToken);
        Notify(revived
            ? "Auto revive: clicked Revive on hero inventory."
            : "Auto revive: hero is dead but Revive button could not be located.");
        return revived;
    }

    // Lightweight current-page probe (no navigation): the top-bar hero status shows an
    // <i class="heroReviving"> icon on every page while the hero regenerates. The periodic refresh uses
    // this to release a hero_manage that was deferred for the full revive time when the user revives the
    // hero early (e.g. with a bucket), so adventures resume without waiting out the original countdown.
    public async Task<bool> IsHeroRevivingOnCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            "() => !!document.querySelector('.heroStatus i.heroReviving, i.heroReviving, [class*=\"heroReviving\"]')");
    }

    // Positive-only current-page probe. Unknown/missing widgets return false, so a deferred away
    // task is released only when the global hero widget explicitly says the hero is home.
    public async Task<bool> IsHeroHomeOnCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const running = !!document.querySelector(
                '.heroStatus i.heroRunning, .heroStatus [class*="heroRunning"], .heroStatus [class*="statusRunning"], .heroStatus .timerReact');
              const unavailable = !!document.querySelector(
                '.heroStatus i.heroDead, .heroStatus i.heroReviving, .heroStatus [class*="heroDead"], .heroStatus [class*="heroReviving"]');
              const home = !!document.querySelector('.heroStatus i.heroHome, .heroStatus [class*="heroHome"]');
              return home && !running && !unavailable;
            }
            """);
    }

    private async Task<bool> ReviveHeroOnInventoryAsync(CancellationToken cancellationToken)
    {
        Notify("[hero] revive flow starting (attributes page)");
        await GotoAsync(Paths.HeroAttributes, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load

        // Read the revive duration shown above the button (example: 00:00:03) before clicking revive.
        var reviveDurationRaw = await _page.EvaluateAsync<string?>(
            """
            () => {
              const wrapper = document.querySelector('.lineWrapper');
              if (!wrapper) return null;
              const value = wrapper.querySelector('.inlineIcon.duration .value, .duration .value');
              return value ? (value.textContent || '').trim() : null;
            }
            """);
        // Reuse shared parser so we support HH:MM:SS and other duration formats used elsewhere.
        var reviveDurationSeconds = TravianParsing.ParseDurationToSeconds(reviveDurationRaw);

        // Click the revive button using direct selector first, then a text-based fallback.
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        var clickedRevive = await _page.EvaluateAsync<bool>(
            """
            () => {
              const isDisabled = (node) =>
                !node || (node.hasAttribute && node.hasAttribute('disabled'))
                || (node.className || '').toString().toLowerCase().includes('disabled');

              const direct = document.querySelector('button#save.green, button#save.startTraining, button[name="save"][value="Revive" i]');
              if (direct && !isDisabled(direct)) { direct.click(); return true; }

              const candidate = Array.from(document.querySelectorAll('button, input[type="submit"]'))
                .find(node => {
                  if (isDisabled(node)) return false;
                  const text = ((node.value || '') + ' ' + (node.textContent || '')).toLowerCase();
                  return text.includes('revive') || text.includes('resurrect');
                });
              if (!candidate) return false;
              candidate.click();
              return true;
            }
            """);

        if (clickedRevive)
        {
            // Wait revive duration + 1 second to avoid continuing before the server-side revive is completed.
            var reviveWaitSeconds = Math.Max(1, (reviveDurationSeconds ?? 0) + 1);
            Notify($"Revive duration detected: {TravianParsing.FormatDuration(reviveWaitSeconds)}. Starting countdown.");
            for (var remaining = reviveWaitSeconds; remaining > 0; remaining--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Notify($"Revive countdown: {remaining}s remaining.");
                await Task.Delay(1000, cancellationToken);
            }

            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            Notify("Revive button clicked.");
        }
        else
        {
            Notify("Revive button could not be found on hero inventory.");
        }

        return clickedRevive;
    }

    private async Task<bool> TryReviveHeroAsync(CancellationToken cancellationToken)
    {
        // Revive UI is on the inventory/attributes page on this Travian version. /hero.php opens Appearance.
        await GotoAsync(Paths.HeroInventory, cancellationToken);
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const buttons = Array.from(document.querySelectorAll('button, input[type="submit"], a'));
              const candidate = buttons.find(node => {
                const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '')).toLowerCase();
                const cls = (node.className || '').toLowerCase();
                const isRevive = text.includes('revive') || text.includes('resurrect') || text.includes('återuppliva');
                const isGold = text.includes('gold') || text.includes('instant') || cls.includes('gold');
                const disabled = node.hasAttribute('disabled') || cls.includes('disabled');
                return isRevive && !isGold && !disabled;
              });
              if (!candidate) return false;
              candidate.click();
              return true;
            }
            """);
    }

}
