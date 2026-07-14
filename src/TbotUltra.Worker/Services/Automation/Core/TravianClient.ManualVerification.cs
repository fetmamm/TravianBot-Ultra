using Microsoft.Playwright;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private async Task<bool> TryDismissContinuePromptAsync(CancellationToken cancellationToken = default)
    {
        if (_page.IsClosed)
        {
            return false;
        }

        var clickTimeoutMs = Math.Min(Math.Max(_config.TimeoutMs / 4, 500), 2500);
        var hadMatch = false;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = await FindContinuePromptLocatorAsync(clickTimeoutMs);
            if (candidate is null)
            {
                return false;
            }

            hadMatch = true;

            try
            {
                if (!await IsLocatorVisibleAsync(candidate, clickTimeoutMs))
                {
                    if (attempt < 2)
                    {
                        await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
                        continue;
                    }

                    break;
                }

                await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                await candidate.ClickAsync(new LocatorClickOptions { Timeout = clickTimeoutMs });
                Notify("Detected update popup. Clicked 'Continue' automatically.");
                await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
                return true;
            }
            catch (PlaywrightException ex)
            {
                if (attempt < 2)
                {
                    Notify($"Found 'Continue' prompt but click failed on attempt {attempt}/2. Retrying...");
                    await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
                    continue;
                }

                Notify($"Found 'Continue' prompt but could not click it: {ex.Message}");
            }
            catch (TimeoutException ex)
            {
                if (attempt < 2)
                {
                    Notify($"Found 'Continue' prompt but click timed out on attempt {attempt}/2. Retrying...");
                    await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
                    continue;
                }

                Notify($"Found 'Continue' prompt but click timed out: {ex.Message}");
            }
        }

        if (hadMatch)
        {
            Notify("Found 'Continue' prompt but it was not clickable. Continuing with normal flow.");
        }

        return false;
    }

    private async Task<ILocator?> FindContinuePromptLocatorAsync(int timeoutMs)
    {
        try
        {
            var directContinueLink = _page.Locator(Selectors.ContinueAfterUpdateLink);
            if (await directContinueLink.CountAsync() > 0 && await IsLocatorVisibleAsync(directContinueLink.First, timeoutMs))
            {
                return directContinueLink.First;
            }

            var textSelectors = new[]
            {
                "button",
                "a",
                "[role='button']",
            };

            foreach (var selector in textSelectors)
            {
                var candidates = _page.Locator(selector);
                var count = Math.Min(await candidates.CountAsync(), 20);
                for (var index = 0; index < count; index++)
                {
                    var candidate = candidates.Nth(index);
                    string? text;
                    try
                    {
                        // Explicit short timeout: without it InnerText falls back to the 15s default
                        // action timeout and hangs when the page is navigating (e.g. logout redirect).
                        text = (await candidate.InnerTextAsync(new LocatorInnerTextOptions { Timeout = timeoutMs }))?.Trim();
                    }
                    catch (PlaywrightException)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    if (text.IndexOf("Continue", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (await IsLocatorVisibleAsync(candidate, timeoutMs))
                    {
                        return candidate;
                    }
                }
            }

            var inputSelectors = new[]
            {
                "input[type='button']",
                "input[type='submit']",
            };

            foreach (var selector in inputSelectors)
            {
                var candidates = _page.Locator(selector);
                var count = Math.Min(await candidates.CountAsync(), 6);
                for (var index = 0; index < count; index++)
                {
                    var candidate = candidates.Nth(index);
                    string? value;
                    try
                    {
                        value = await candidate.GetAttributeAsync("value", new LocatorGetAttributeOptions { Timeout = timeoutMs });
                    }
                    catch (PlaywrightException)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (value.IndexOf("Continue", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (await IsLocatorVisibleAsync(candidate, timeoutMs))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<bool> IsLocatorVisibleAsync(ILocator locator, int timeoutMs)
    {
        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs,
            });
            return true;
        }
        catch (PlaywrightException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

}

