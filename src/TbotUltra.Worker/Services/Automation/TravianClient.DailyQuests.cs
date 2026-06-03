using Microsoft.Playwright;

namespace TbotUltra.Worker.Services;

// Official Travian daily quests render as a React dialog opened from the topbar dailyQuests
// button. This is separate from Questmaster task rewards on /tasks.
public sealed partial class TravianClient
{
    public async Task<bool> HasClaimableDailyQuestsOnCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var html = await _page.ContentAsync();
            return DailyQuestDomParser.HasClaimableDailyQuests(html);
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

    public async Task<string> CollectDailyQuestRewardsAsync(CancellationToken cancellationToken = default)
    {
        Notify("[daily-quests] auto-collect starting");
        await EnsureLoggedInAsync();

        if (!await OpenDailyQuestsDialogAsync(cancellationToken))
        {
            Notify("[daily-quests] no claimable daily quests signal found");
            return "No claimable daily quest rewards found.";
        }

        try
        {
            if (await ClickDailyQuestCollectRewardsAsync(cancellationToken))
            {
                Notify("[daily-quests] opened collectable rewards screen");
            }
            else
            {
                Notify("[daily-quests] collect rewards button was not available");
            }

            var collected = await ClickDailyQuestCollectButtonsAsync(cancellationToken);
            Notify($"[daily-quests] auto-collect done - collected {collected} reward(s)");
            return $"Collected {collected} daily quest reward(s).";
        }
        finally
        {
            await CloseDailyQuestsDialogAsync(cancellationToken);
        }
    }

    private async Task<bool> OpenDailyQuestsDialogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var clicked = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const link = document.querySelector('a.dailyQuests');
                  const indicator = link?.querySelector('.indicator');
                  if (!link || (indicator?.textContent || '').trim() !== '!') {
                    return false;
                  }
                  link.scrollIntoView({ block: 'center' });
                  link.click();
                  return true;
                }
                """);
            if (!clicked)
            {
                return false;
            }

            await WaitForDailyQuestsDialogAsync(cancellationToken);
            return true;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private async Task WaitForDailyQuestsDialogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => !!document.querySelector('.dailyQuestsDialog #dailyQuests, .dailyQuestsDialog, #dailyQuests')
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

        await Task.Delay(150, cancellationToken);
    }

    private async Task<bool> ClickDailyQuestCollectRewardsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var clicked = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const button = document.querySelector('button.textButtonV2.collectRewards, button.collectRewards');
                  if (!button || button.disabled || button.getAttribute('aria-disabled') === 'true') {
                    return false;
                  }
                  button.scrollIntoView({ block: 'center' });
                  button.click();
                  return true;
                }
                """);
            if (clicked)
            {
                await ApplyCollectStepDelayAsync(cancellationToken);
            }

            return clicked;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private async Task<int> ClickDailyQuestCollectButtonsAsync(CancellationToken cancellationToken)
    {
        var collected = 0;
        const int safetyCap = 20;
        for (var i = 0; i < safetyCap; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool clicked;
            try
            {
                clicked = await _page.EvaluateAsync<bool>(
                    """
                    () => {
                      const buttons = Array.from(document.querySelectorAll('button.textButtonV2.collect.collectable, button.collect.collectable'));
                      for (const button of buttons) {
                        const className = button.className || '';
                        const disabled = button.disabled
                          || /(^|\s)disabled(\s|$)/i.test(className)
                          || button.getAttribute('aria-disabled') === 'true';
                        const label = (button.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                        if (disabled || label !== 'collect') {
                          continue;
                        }
                        button.scrollIntoView({ block: 'center' });
                        button.click();
                        return true;
                      }
                      return false;
                    }
                    """);
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                Notify("[daily-quests] transient execution-context error while clicking collect buttons; stopping");
                break;
            }

            if (!clicked)
            {
                break;
            }

            collected += 1;
            await ApplyCollectStepDelayAsync(cancellationToken);
        }

        return collected;
    }

    private async Task CloseDailyQuestsDialogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var clicked = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const close = document.querySelector('.dailyQuestsDialog .dialogCancelButton, .dialogCancelButton.cancel');
                  if (!close) {
                    return false;
                  }
                  close.click();
                  return true;
                }
                """);
            if (clicked)
            {
                await Task.Delay(150, cancellationToken);
            }
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
        }
        catch (PlaywrightException)
        {
        }
    }
}
