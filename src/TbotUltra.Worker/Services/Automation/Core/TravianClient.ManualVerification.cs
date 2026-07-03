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

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private sealed class CaptchaClipRegion
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
    }

    private async Task<bool> CaptchaOrManualStepVisibleAsync()
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
            """
            (selectors) => {
              const isVisible = (node) => {
                if (!node || !(node instanceof Element)) return false;
                const style = window.getComputedStyle(node);
                if (!style || style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity || '1') === 0) return false;
                const rect = node.getBoundingClientRect();
                if (rect.width <= 0 || rect.height <= 0) return false;
                return true;
              };

              for (const selector of selectors) {
                const nodes = document.querySelectorAll(selector);
                for (const node of nodes) {
                  if (isVisible(node)) return true;
                }
              }

              return false;
            }
            """,
            CaptchaDetectionSelectors);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            // Page is mid-navigation; nothing visible to evaluate. Caller will retry on next tick.
            return false;
        }
    }

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

    private async Task PauseForManualStepIfVisibleAsync(string message, CancellationToken cancellationToken)
    {
        if (!await CaptchaOrManualStepVisibleAsync())
        {
            return;
        }

        await CaptureManualVerificationScreenshotAsync("manual-verification", cancellationToken);

        Notify($"{message} Solve it in the browser window. The bot is paused.");
        if (!_browserVisible)
        {
            throw new ManualVerificationRequiredException(
                "Captcha/manual verification appeared while running headless.");
        }

        await WaitForManualVerificationToClearAsync(cancellationToken);
    }

    // Upper bound for interactive "wait for the user" pauses (captcha/manual login). These waits run
    // while the worker session gate is held, so an unattended captcha must not block the queue, UI
    // reads and account switching forever. On expiry the task fails with an alarm and the queue
    // retries it later — the pause simply restarts when someone is around to solve it.
    private static readonly TimeSpan ManualInteractiveWaitMaxDuration = TimeSpan.FromMinutes(30);

    private async Task WaitForManualVerificationToClearAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(ManualInteractiveWaitMaxDuration);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await CaptchaOrManualStepVisibleAsync())
            {
                Notify("Manual verification cleared. Continuing.");
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Notify($"Manual verification was not solved within {ManualInteractiveWaitMaxDuration.TotalMinutes:F0} minutes — giving up so the bot is not blocked forever.");
                throw new ManualVerificationRequiredException(
                    $"Manual verification was not solved within {ManualInteractiveWaitMaxDuration.TotalMinutes:F0} minutes.");
            }

            await Task.Delay(Random.Shared.Next(500, 600), cancellationToken); // Random wait
        }
    }

    private async Task<string?> CaptureManualVerificationScreenshotAsync(string label, CancellationToken cancellationToken, bool force = false)
    {
        if (_page.IsClosed)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force && (now - _lastManualVerificationScreenshotAt) < TimeSpan.FromSeconds(10))
        {
            return null;
        }

        _lastManualVerificationScreenshotAt = now;
        var safeLabel = TravianUrls.SafePathSegment(label);
        var stamp = now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var captchaRoot = Path.Combine(
            _projectRoot,
            "logs",
            "captchas");
        Directory.CreateDirectory(captchaRoot);

        var screenshotPath = Path.Combine(captchaRoot, $"{stamp}-{safeLabel}.png");

        try
        {
            var clipRegion = await WaitForCaptchaVisualAsync(cancellationToken);
            if (clipRegion is not null)
            {
                await _page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = screenshotPath,
                    Clip = new Clip
                    {
                        X = (float)clipRegion.X,
                        Y = (float)clipRegion.Y,
                        Width = (float)clipRegion.Width,
                        Height = (float)clipRegion.Height,
                    },
                });
            }
            else
            {
                await _page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = screenshotPath,
                    FullPage = true,
                });
            }

            Notify($"Captured captcha screenshot: '{screenshotPath}'.");
            return screenshotPath;
        }
        catch (Exception ex)
        {
            Notify($"Could not capture captcha screenshot for '{label}': {ex.Message}");
        }

        return null;
    }

    private async Task<CaptchaClipRegion?> WaitForCaptchaVisualAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        CaptchaClipRegion? previous = null;
        var stableMatches = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await TryResolveCaptchaClipRegionAsync(cancellationToken);
            if (current is not null)
            {
                if (previous is not null && AreCaptchaRegionsSimilar(previous, current))
                {
                    stableMatches++;
                    if (stableMatches >= 2)
                    {
                        await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
                        return current;
                    }
                }
                else
                {
                    stableMatches = 0;
                }

                previous = current;
            }

            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
        }

        return previous;
    }

    private async Task<CaptchaClipRegion?> TryResolveCaptchaClipRegionAsync(CancellationToken cancellationToken)
    {
        var locator = await TryFindVisibleCaptchaLocatorAsync();
        if (locator is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await locator.ScrollIntoViewIfNeededAsync();

        try
        {
            return await locator.EvaluateAsync<CaptchaClipRegion?>(
                """
                node => {
                  const isVisible = element => {
                    if (!element || !(element instanceof Element)) return false;
                    const style = window.getComputedStyle(element);
                    if (!style || style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity || '1') === 0) return false;
                    const rect = element.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0;
                  };

                  const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 0;
                  const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0;
                  const viewportArea = Math.max(1, viewportWidth * viewportHeight);
                  const nodeRect = node.getBoundingClientRect();
                  if (!isVisible(nodeRect) && !isVisible(node)) return null;

                  let bestRect = nodeRect;
                  let bestArea = Math.max(1, nodeRect.width * nodeRect.height);
                  let current = node.parentElement;
                  let depth = 0;
                  const extractExpression = text => {
                    if (!text) return null;
                    const normalized = text.replace(/\s+/g, ' ').trim();
                    const match = normalized.match(/(\d+)\s*([+\-])\s*(\d+)\s*=\s*\?/);
                    return match ? `${match[1]}${match[2]}${match[3]}` : null;
                  };

                  while (current && depth < 6) {
                    if (isVisible(current)) {
                      const rect = current.getBoundingClientRect();
                      const area = rect.width * rect.height;
                      const notTooLarge = area <= viewportArea * 0.75;
                      const largerThanNode = rect.width >= nodeRect.width && rect.height >= nodeRect.height;
                      const containsExpression = !!extractExpression(current.textContent || '');
                      const containsCaptchaUi =
                        containsExpression
                        || (current.textContent || '').toLowerCase().includes('security check')
                        || (current.textContent || '').toLowerCase().includes('calculate')
                        || (current.textContent || '').toLowerCase().includes('verify');
                      if (notTooLarge && largerThanNode && (containsCaptchaUi || area >= bestArea * 1.1)) {
                        bestRect = rect;
                        bestArea = area;
                      }
                    }

                    current = current.parentElement;
                    depth += 1;
                  }

                  if (bestRect.width < 140 || bestRect.height < 40) {
                    return null;
                  }

                  const padX = Math.max(24, Math.min(80, bestRect.width * 0.12));
                  const padY = Math.max(24, Math.min(80, bestRect.height * 0.18));
                  const x = Math.max(0, bestRect.left - padX);
                  const y = Math.max(0, bestRect.top - padY);
                  const right = Math.min(viewportWidth, bestRect.right + padX);
                  const bottom = Math.min(viewportHeight, bestRect.bottom + padY);
                  const width = Math.max(1, right - x);
                  const height = Math.max(1, bottom - y);

                  return { x, y, width, height };
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
    }

    private async Task<ILocator?> TryFindVisibleCaptchaLocatorAsync()
    {
        foreach (var selector in CaptchaDetectionSelectors)
        {
            var candidates = _page.Locator(selector);
            int count;
            try
            {
                count = await candidates.CountAsync();
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                return null;
            }
            catch (PlaywrightException)
            {
                continue;
            }

            var limit = Math.Min(count, 8);
            for (var index = 0; index < limit; index++)
            {
                var candidate = candidates.Nth(index);
                try
                {
                    if (await candidate.IsVisibleAsync())
                    {
                        return candidate;
                    }
                }
                catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
                {
                    return null;
                }
                catch (PlaywrightException)
                {
                    // Try next candidate.
                }
            }
        }

        return null;
    }

    private static bool AreCaptchaRegionsSimilar(CaptchaClipRegion previous, CaptchaClipRegion current)
    {
        return Math.Abs(previous.X - current.X) <= 6
            && Math.Abs(previous.Y - current.Y) <= 6
            && Math.Abs(previous.Width - current.Width) <= 18
            && Math.Abs(previous.Height - current.Height) <= 18;
    }

}
