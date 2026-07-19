using Microsoft.Playwright;

namespace TbotUltra.Worker.Services;

// Official Travian (T4.6) Questmaster task rewards. When tasks are achieved a speech bubble
// (div.newQuestSpeechBubble) and a claimable questmaster button (#questmasterButton.claimable)
// appear on normal pages; rewards are claimed on the React /tasks page which has two tabs
// (the active village and "General tasks"), each with green "Collect" buttons.
public sealed partial class TravianClient
{
    // The two markers that mean "task rewards are waiting". Kept in one place so the queue probe and
    // the pre-collect guard below can never drift apart.
    private const string ClaimableTaskMarkersSelector = "div.newQuestSpeechBubble, #questmasterButton.claimable";

    // Cheap, no-navigation probe used by the periodic refresh to decide whether to queue a
    // collection. Reads only the current page, so it must be tolerant of any page state.
    public async Task<bool> HasClaimableTasksOnCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                $$"""
                () => !!document.querySelector('{{ClaimableTaskMarkersSelector}}')
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    // Returns true only for a definite "nothing to collect": the questmaster sidebar box is on the
    // page and carries neither claimable marker. Any other state (box missing because we are on a
    // page without the sidebar, script error) returns false so the caller still does the full pass.
    private async Task<bool> IsQuestmasterConfirmedEmptyOnCurrentPageAsync()
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                $$"""
                () => {
                  if (!document.querySelector('#sidebarBoxQuestmaster')) return false;
                  return !document.querySelector('{{ClaimableTaskMarkersSelector}}');
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    // Navigates to /tasks and collects every available reward on the village and general tabs.
    // Re-checks the village tab in-place once because a general reward can unlock another village
    // reward. Leaves the browser on /tasks so the scheduler can navigate directly to its next job.
    public async Task<string> CollectTaskRewardsAsync(CancellationToken cancellationToken = default)
    {
        Notify("[tasks] auto-collect starting");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        // A queued collect_tasks outlives the rewards that triggered it (the queue survives restarts),
        // so confirm on the current page that the questmaster still has something claimable before
        // spending four navigations on /tasks. Only skips on a definite "box present, no marker".
        if (await IsQuestmasterConfirmedEmptyOnCurrentPageAsync())
        {
            Notify("[tasks] questmaster shows no claimable rewards — skipping the /tasks pass.");
            return "Collected 0 task reward(s).";
        }

        var totalCollected = 0;
        const int maxPasses = 2;
        for (var pass = 1; pass <= maxPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (pass == 1)
            {
                await GotoAsync(Paths.Tasks, cancellationToken);
                await WaitForPageReadyAsync(cancellationToken);
                await EnsureLoggedInAsync(cancellationToken: cancellationToken);
                await WaitForTasksPageRenderAsync(cancellationToken);
            }

            // Village tab (active by default on arrival).
            var villageCollected = await ClickCollectButtonsOnCurrentTabAsync(cancellationToken);
            Notify($"[tasks] pass {pass}: collected {villageCollected} reward(s) on the village tab");

            // General tab — React-switched (no href), so click by matching tab text.
            var collectedOnGeneral = 0;
            if (await SwitchToGeneralTasksTabAsync(cancellationToken))
            {
                await WaitForTasksPageRenderAsync(cancellationToken);
                collectedOnGeneral = await ClickCollectButtonsOnCurrentTabAsync(cancellationToken);
                Notify($"[tasks] pass {pass}: collected {collectedOnGeneral} reward(s) on the general tab");
            }

            totalCollected += villageCollected + collectedOnGeneral;

            if (pass >= maxPasses || villageCollected + collectedOnGeneral == 0)
            {
                break;
            }

            // General rewards can unlock village rewards. Switch back inside the React page and
            // continue only when a new enabled Collect button is actually visible; no dorf1 bounce.
            if (!await SwitchToVillageTasksTabAsync(cancellationToken))
            {
                break;
            }
            await WaitForTasksPageRenderAsync(cancellationToken);
            if (!await HasVisibleCollectButtonOnCurrentTabAsync())
            {
                break;
            }

            Notify($"[tasks] newly unlocked village reward detected after pass {pass}; re-checking in place");
        }

        Notify($"[tasks] auto-collect done — collected {totalCollected} reward(s)");
        return $"Collected {totalCollected} task reward(s).";
    }

    // The /tasks page is React-rendered; wait for the tab strip (and any collect buttons) to
    // appear before reading. Tolerant: a timeout just falls through to a best-effort read.
    private async Task WaitForTasksPageRenderAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const isVisible = element => {
                    if (!element) return false;
                    const style = window.getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.visibility !== 'hidden'
                      && style.display !== 'none'
                      && rect.width > 0
                      && rect.height > 0;
                  };
                  return isVisible(document.querySelector('a.tabItem.active'))
                    || Array.from(document.querySelectorAll('button.textButtonV2.collect')).some(button => isVisible(button));
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 });
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException)
        {
        }
    }

    // Clicks the green "Collect" buttons on the currently visible tab, ONE AT A TIME with a small
    // delay between clicks — clicking them all in one synchronous burst makes the React page glitch
    // and silently drop clicks. Re-queries the DOM each pass so a claimed button is skipped next pass.
    // A claimed button keeps the DOM text "Collect" (Official renders the "Collected" label from the
    // data-text-collected attribute via CSS) and is instead marked by the `collected` class plus the
    // `disabled` attribute — so those two, not the text, are what excludes it.
    private async Task<int> ClickCollectButtonsOnCurrentTabAsync(CancellationToken cancellationToken)
    {
        // Collect buttons render client-side slightly after the tab; wait briefly for at least one.
        // None is valid (e.g. the general tab may have nothing to claim) — just return 0 then.
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const isVisible = element => {
                    if (!element) return false;
                    const style = window.getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.visibility !== 'hidden'
                      && style.display !== 'none'
                      && rect.width > 0
                      && rect.height > 0;
                  };
                  const isEnabledCollectButton = button => {
                    if (!isVisible(button)) return false;
                    const className = button.className || '';
                    const disabled = button.disabled
                      || /(^|\s)disabled(\s|$)/i.test(className)
                      || button.getAttribute('aria-disabled') === 'true';
                    if (disabled) return false;
                    const label = (button.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                    return label === 'collect';
                  };
                  return Array.from(document.querySelectorAll('button.textButtonV2.collect:not(.collected)'))
                    .some(button => isEnabledCollectButton(button));
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 2500 });
        }
        catch (TimeoutException)
        {
            return 0;
        }
        catch (PlaywrightException)
        {
            return 0;
        }

        var collected = 0;
        const int safetyCap = 40; // bounds the loop if a click never flips the button state
        for (var i = 0; i < safetyCap; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Paced by the dedicated "Collect tasks delay" setting, before every click, so the
            // React page registers the previous claim and the burst does not look robotic. The click
            // below therefore skips the generic click pacing — running both stacked two waits per
            // reward and made collecting far slower than the configured delay implies.
            await ApplyCollectStepDelayAsync("task collect reward", cancellationToken);

            bool clicked;
            try
            {
                clicked = await TryClickFirstVisibleEnabledAsync(
                    // :not(.collected) is the primary claimed-state guard; the disabled check inside the
                    // helper is the backstop. The text is NOT a guard — it stays "Collect" when claimed.
                    "button.textButtonV2.collect:not(.collected)",
                    cancellationToken,
                    requiredText: "Collect",
                    requireExactText: true,
                    reason: "task collect reward",
                    // Short timeout: a collect button that is present but not actionable should fail fast,
                    // not waste the full 20s page timeout.
                    timeoutMs: 3000,
                    // The reward dialog animates, so let a forced (still trusted) click land before we
                    // give up on the real click and fall through to synthetic dispatch.
                    allowForcedRetry: true,
                    useClickPacing: false);
                if (!clicked)
                {
                    // Last resort only: the attempt above is already paced, so no extra delay here.
                    clicked = await _page.EvaluateAsync<bool>(
                    """
                    () => {
                      const isVisible = element => {
                        if (!element) return false;
                        const style = window.getComputedStyle(element);
                        const rect = element.getBoundingClientRect();
                        return style.visibility !== 'hidden'
                          && style.display !== 'none'
                          && rect.width > 0
                          && rect.height > 0;
                      };
                      const buttons = Array.from(document.querySelectorAll('button.textButtonV2.collect:not(.collected)'));
                      for (const btn of buttons) {
                        const disabled = btn.disabled
                          || /(^|\s)disabled(\s|$)/i.test(btn.className || '')
                          || btn.getAttribute('aria-disabled') === 'true';
                        if (!isVisible(btn) || disabled) {
                          continue;
                        }
                        // Claimed buttons keep the text "Collect" (the "Collected" label comes from
                        // data-text-collected via CSS), so :not(.collected) + disabled above are the
                        // real guards. This only drops unrelated buttons that share the class.
                        const label = (btn.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                        if (label !== 'collect') {
                          continue;
                        }
                        btn.scrollIntoView({ block: 'center' });
                        btn.click();
                        return true;
                      }
                      return false;
                    }
                    """);
                }
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                Notify("[tasks] transient execution-context error while clicking collect buttons; stopping tab");
                break;
            }

            if (!clicked)
            {
                break;
            }

            collected += 1;
        }

        return collected;
    }

    // Randomized delay between the reward clicks in the auto-collect tasks flow only (configured by
    // CollectStepDelayMin/MaxSeconds). Set both to 0 to disable. Daily quests deliberately do not use
    // this — a single reward button there is covered by the normal click pacing.
    // Routed through the shared pacing helper so it behaves exactly like the click/page-load delays:
    // it obeys the master "Action pacing" toggle, emits the browser-trace WAIT span, and logs the
    // "[pacing] ...: waiting Xs" line the Pacing log view filters on.
    private Task ApplyCollectStepDelayAsync(string reason, CancellationToken cancellationToken)
    {
        return ApplyPacingDelayAsync(
            _config.CollectStepDelayMinSeconds,
            _config.CollectStepDelayMaxSeconds,
            "collect-pacing",
            $"Collect: {reason}",
            cancellationToken);
    }

    private async Task<bool> SwitchToGeneralTasksTabAsync(CancellationToken cancellationToken)
        => await SwitchTasksTabAsync(general: true, cancellationToken);

    private async Task<bool> SwitchToVillageTasksTabAsync(CancellationToken cancellationToken)
        => await SwitchTasksTabAsync(general: false, cancellationToken);

    private async Task<bool> SwitchTasksTabAsync(bool general, CancellationToken cancellationToken)
    {
        try
        {
            var switched = await _page.EvaluateAsync<bool>(
                """
                (general) => {
                  const isVisible = element => {
                    if (!element) return false;
                    const style = window.getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.visibility !== 'hidden'
                      && style.display !== 'none'
                      && rect.width > 0
                      && rect.height > 0;
                  };
                  const tabs = Array.from(document.querySelectorAll('a.tabItem'));
                  const target = general
                    ? tabs.find(t => /general/i.test(t.textContent || ''))
                    : tabs.find(t => !/general/i.test(t.textContent || ''));
                  if (!isVisible(target) || target.classList.contains('active')) {
                    return false;
                  }
                  target.click();
                  return true;
                }
                """,
                general);
            if (switched)
            {
                await ApplyActionDelayAsync(cancellationToken);
            }

            return switched;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private async Task<bool> HasVisibleCollectButtonOnCurrentTabAsync()
    {
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => Array.from(document.querySelectorAll('button.textButtonV2.collect')).some(button => {
                  const style = window.getComputedStyle(button);
                  const rect = button.getBoundingClientRect();
                  const visible = style.visibility !== 'hidden'
                    && style.display !== 'none'
                    && rect.width > 0
                    && rect.height > 0;
                  const disabled = button.disabled
                    || /(^|\s)disabled(\s|$)/i.test(button.className || '')
                    || button.getAttribute('aria-disabled') === 'true';
                  const label = (button.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  return visible && !disabled && label === 'collect';
                })
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 2500 });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }
}
