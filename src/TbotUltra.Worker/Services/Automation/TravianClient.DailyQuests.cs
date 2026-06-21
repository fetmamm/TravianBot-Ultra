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
            await RefreshDailyQuestSignalIfStillStaleAsync(cancellationToken);
        }
    }

    private async Task<bool> OpenDailyQuestsDialogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var hasClaimableSignal = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const link = document.querySelector('a.dailyQuests');
                  const indicator = link?.querySelector('.indicator');
                  return !!link && (indicator?.textContent || '').trim() === '!';
                }
                """);
            if (!hasClaimableSignal)
            {
                return false;
            }

            var clicked = await TryClickFirstVisibleEnabledAsync(
                "a.dailyQuests",
                cancellationToken,
                reason: "open daily quests");
            if (!clicked)
            {
                await DelayBeforeClickAsync(cancellationToken, "open daily quests fallback");
                clicked = await _page.EvaluateAsync<bool>(
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
            }
            if (!clicked)
            {
                return false;
            }

            await WaitForDailyQuestsDialogAsync(cancellationToken);
            await ApplyCollectStepDelayAsync(cancellationToken);
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
                  const dialog = document.querySelector('.dailyQuestsDialog #dailyQuests, .dailyQuestsDialog, #dailyQuests');
                  if (!isVisible(dialog)) return false;
                  const readyElement = dialog.querySelector('button.collectRewards, button.textButtonV2.collectRewards, button.collect.collectable, button.textButtonV2.collect.collectable');
                  return isVisible(readyElement)
                    || /daily quests/i.test(dialog.textContent || '');
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

        await Task.Delay(300, cancellationToken);
    }

    private async Task<bool> ClickDailyQuestCollectRewardsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WaitForDailyQuestCollectRewardsButtonAsync(cancellationToken);
            var clicked = await TryClickFirstVisibleEnabledAsync(
                "button.textButtonV2.collectRewards, button.collectRewards",
                cancellationToken,
                reason: "daily quest collect rewards");
            if (!clicked)
            {
                await DelayBeforeClickAsync(cancellationToken, "daily quest collect rewards fallback");
                clicked = await _page.EvaluateAsync<bool>(
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
            }
            if (clicked)
            {
                await ApplyCollectStepDelayAsync(cancellationToken);
                await WaitForDailyQuestCollectableRewardsAsync(cancellationToken);
            }

            return clicked;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private async Task WaitForDailyQuestCollectRewardsButtonAsync(CancellationToken cancellationToken)
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
                  const button = document.querySelector('button.textButtonV2.collectRewards, button.collectRewards');
                  return isVisible(button) && !button.disabled && button.getAttribute('aria-disabled') !== 'true';
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 2500 });
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException)
        {
        }

        await Task.Delay(250, cancellationToken);
    }

    private async Task WaitForDailyQuestCollectableRewardsAsync(CancellationToken cancellationToken)
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
                  return Array.from(document.querySelectorAll('button.textButtonV2.collect.collectable, button.collect.collectable'))
                    .some(button => isVisible(button));
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 3000 });
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException)
        {
        }

        await Task.Delay(250, cancellationToken);
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
                clicked = await TryClickFirstVisibleEnabledAsync(
                    "button.textButtonV2.collect.collectable, button.collect.collectable",
                    cancellationToken,
                    requiredText: "Collect",
                    requireExactText: true,
                    reason: "daily quest collect reward");
                if (!clicked)
                {
                    await DelayBeforeClickAsync(cancellationToken, "daily quest collect reward fallback");
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
                      const buttons = Array.from(document.querySelectorAll('button.textButtonV2.collect.collectable, button.collect.collectable'));
                      for (const button of buttons) {
                        const className = button.className || '';
                        const disabled = button.disabled
                          || /(^|\s)disabled(\s|$)/i.test(className)
                          || button.getAttribute('aria-disabled') === 'true';
                        const label = (button.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                        if (!isVisible(button) || disabled || label !== 'collect') {
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
            var clicked = await TryClickFirstVisibleEnabledAsync(
                ".dailyQuestsDialog .dialogCancelButton, .dialogCancelButton.cancel",
                cancellationToken,
                reason: "close daily quests",
                timeoutMs: 2000);
            if (!clicked)
            {
                await DelayBeforeClickAsync(cancellationToken, "close daily quests fallback");
                clicked = await _page.EvaluateAsync<bool>(
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
            }
            if (clicked)
            {
                await Task.Delay(300, cancellationToken);
            }
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
        }
        catch (PlaywrightException)
        {
        }
    }

    private async Task RefreshDailyQuestSignalIfStillStaleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cleared = await WaitForDailyQuestIndicatorClearedAsync();
            if (cleared)
            {
                return;
            }

            Notify("[daily-quests] topbar signal still claimable after collect; reloading current page once");
            await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
        }
        catch (PlaywrightException ex)
        {
            Notify($"[daily-quests] could not refresh stale topbar signal: {ex.Message}");
        }
        catch (TimeoutException)
        {
            Notify("[daily-quests] current-page reload timed out while refreshing stale topbar signal");
        }
    }

    private async Task<bool> WaitForDailyQuestIndicatorClearedAsync()
    {
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const indicator = document.querySelector('a.dailyQuests .indicator');
                  return !indicator || (indicator.textContent || '').trim() !== '!';
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 2000 });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }
}
