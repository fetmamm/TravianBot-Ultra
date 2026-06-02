using Microsoft.Playwright;

namespace TbotUltra.Worker.Services;

// Official Travian (T4.6) Questmaster task rewards. When tasks are achieved a speech bubble
// (div.newQuestSpeechBubble) and a claimable questmaster button (#questmasterButton.claimable)
// appear on normal pages; rewards are claimed on the React /tasks page which has two tabs
// (the active village and "General tasks"), each with green "Collect" buttons.
public sealed partial class TravianClient
{
    // Cheap, no-navigation probe used by the periodic refresh to decide whether to queue a
    // collection. Reads only the current page, so it must be tolerant of any page state.
    public async Task<bool> HasClaimableTasksOnCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => !!document.querySelector('div.newQuestSpeechBubble, #questmasterButton.claimable')
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

    // Navigates to /tasks, collects every available reward on the village tab and the general
    // tab, then returns to dorf1 and re-checks the claimable signal. Repeats up to 2 passes so a
    // reward unlocked by collecting another one is not missed. Official-only (caller gates this).
    public async Task<string> CollectTaskRewardsAsync(CancellationToken cancellationToken = default)
    {
        Notify("[tasks] auto-collect starting");
        await EnsureLoggedInAsync();

        var totalCollected = 0;
        const int maxPasses = 2;
        for (var pass = 1; pass <= maxPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await GotoAsync(Paths.Tasks, cancellationToken);
            await WaitForNavigationSettledAsync(cancellationToken);
            await EnsureLoggedInAsync();
            await PauseForManualStepIfVisibleAsync("Manual verification appeared on the tasks page.", cancellationToken);
            await WaitForTasksPageRenderAsync(cancellationToken);

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

            // Re-read on dorf1 so nothing is missed and the next refresh sees a clean page.
            await GotoAsync(Paths.Resources, cancellationToken);
            await WaitForNavigationSettledAsync(cancellationToken);
            if (!await HasClaimableTasksOnCurrentPageAsync(cancellationToken))
            {
                break;
            }

            Notify($"[tasks] still claimable after pass {pass}; re-checking");
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
                () => !!document.querySelector('a.tabItem.active')
                   || !!document.querySelector('button.textButtonV2.collect')
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

    // Clicks every enabled green "Collect" button on the currently visible tab. Already-claimed
    // tasks render the button disabled / with the "Collected" label, so they are skipped.
    private async Task<int> ClickCollectButtonsOnCurrentTabAsync(CancellationToken cancellationToken)
    {
        try
        {
            var collected = await _page.EvaluateAsync<int>(
                """
                () => {
                  const buttons = Array.from(document.querySelectorAll(
                    'div.task.achieved button.textButtonV2.collect, button.textButtonV2.collect.green'));
                  let count = 0;
                  for (const btn of buttons) {
                    const disabled = btn.disabled
                      || /disabled/i.test(btn.className || '')
                      || btn.getAttribute('aria-disabled') === 'true';
                    if (disabled) {
                      continue;
                    }
                    btn.click();
                    count += 1;
                  }
                  return count;
                }
                """);
            if (collected > 0)
            {
                await ApplyActionDelayAsync(cancellationToken);
            }

            return collected;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify("[tasks] transient execution-context error while clicking collect buttons; skipping tab");
            return 0;
        }
    }

    private async Task<bool> SwitchToGeneralTasksTabAsync(CancellationToken cancellationToken)
    {
        try
        {
            var switched = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const tabs = Array.from(document.querySelectorAll('a.tabItem'));
                  const general = tabs.find(t => /general/i.test(t.textContent || ''));
                  if (!general || general.classList.contains('active')) {
                    return false;
                  }
                  general.click();
                  return true;
                }
                """);
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
}
