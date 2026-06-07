using Microsoft.Playwright;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private async Task<bool> TrySolveCaptchaAutomaticallyAsync(
        string label,
        string? screenshotPath,
        CancellationToken cancellationToken)
    {
        if (!_config.IsPrivateServer)
        {
            return false; // Captcha auto-solve is SS-Travi only; official has no such captcha.
        }

        if (!_config.CaptchaAutoSolveEnabled)
        {
            return false;
        }

        if (_captchaAutoSolver is null)
        {
            Notify("Captcha auto-solve is enabled, but no solver service is available.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(screenshotPath))
        {
            Notify($"Captcha screenshot was not available for auto-solve at '{label}'.");
            return false;
        }

        var attempts = Math.Max(1, _config.CaptchaSolverMaxAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await CaptchaOrManualStepVisibleAsync())
            {
                return true;
            }

            if (attempt > 1)
            {
                var refreshedScreenshotPath = await CaptureManualVerificationScreenshotAsync(label, cancellationToken, force: true);
                if (!string.IsNullOrWhiteSpace(refreshedScreenshotPath))
                {
                    screenshotPath = refreshedScreenshotPath;
                }
            }

            Notify($"Captcha auto-solve attempt {attempt}/{attempts} using '{screenshotPath}'.");
            var solvedFromPage = await TryResolveCaptchaAnswerFromPageAsync();
            CaptchaSolverResult solverResult;

            if (!string.IsNullOrWhiteSpace(solvedFromPage.Answer))
            {
                solverResult = new CaptchaSolverResult(
                    true,
                    solvedFromPage.Answer,
                    solvedFromPage.Expression,
                    100d,
                    "Resolved from page text.");
                Notify($"Captcha page fallback result: expression='{solverResult.Expression}', answer='{solverResult.Answer}'.");
            }
            else
            {
                var timeoutSeconds = Math.Max(60, _config.CaptchaSolverTimeoutSeconds);
                solverResult = await _captchaAutoSolver.TrySolveAsync(
                    screenshotPath,
                    timeoutSeconds,
                    cancellationToken);

                if (!solverResult.Success || string.IsNullOrWhiteSpace(solverResult.Answer))
                {
                    Notify($"Captcha solver could not solve '{label}': {solverResult.Reason}");
                    continue;
                }

                Notify(
                    $"Captcha solver result: expression='{solverResult.Expression}', answer='{solverResult.Answer}', confidence={solverResult.Confidence:F2}%.");
            }

            var inputFilled = await TryFillCaptchaAnswerAsync(solverResult.Answer, cancellationToken);
            if (!inputFilled)
            {
                Notify("Captcha answer could not be filled because no captcha input field was found.");
                continue;
            }

            var submitted = await TrySubmitCaptchaAsync(cancellationToken);
            if (!submitted)
            {
                Notify("Captcha answer was filled, but no submit action was available.");
                continue;
            }

            var submitOutcome = await WaitForCaptchaSubmitOutcomeAsync(cancellationToken);
            if (submitOutcome == CaptchaSubmitOutcome.Rejected)
            {
                Notify("Captcha answer was rejected by the server.");
                continue;
            }

            if (submitOutcome == CaptchaSubmitOutcome.Cleared)
            {
                return true;
            }

            Notify("Captcha was still visible after auto-submit.");
        }

        Notify($"Captcha auto-solve failed after {attempts} attempt(s). Falling back to manual verification.");
        return false;
    }

    private async Task<CaptchaSubmitOutcome> WaitForCaptchaSubmitOutcomeAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryHandleIncorrectCaptchaDialogAsync(cancellationToken))
            {
                return CaptchaSubmitOutcome.Rejected;
            }

            if (!await CaptchaOrManualStepVisibleAsync())
            {
                await TryClickCaptchaSuccessDialogOkAsync(cancellationToken);
                return CaptchaSubmitOutcome.Cleared;
            }

            await Task.Delay(250, cancellationToken);
        }

        return CaptchaSubmitOutcome.StillVisible;
    }

    private async Task<(string Answer, string Expression)> TryResolveCaptchaAnswerFromPageAsync()
    {
        var locatorBasedResult = await TryResolveCaptchaAnswerFromVisibleInputContextAsync();
        if (!string.IsNullOrWhiteSpace(locatorBasedResult.Answer))
        {
            return locatorBasedResult;
        }

        try
        {
            var expression = await _page.EvaluateAsync<string?>(
                """
                () => {
                  const isVisible = (node) => {
                    if (!node || !(node instanceof Element)) return false;
                    const style = window.getComputedStyle(node);
                    if (!style || style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity || '1') === 0) return false;
                    const rect = node.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0;
                  };

                  const extractExpression = (text) => {
                    if (!text) return null;
                    const normalized = text.replace(/\s+/g, ' ').trim();
                    const match = normalized.match(/(\d+)\s*([+\-])\s*(\d+)\s*=\s*\?/);
                    if (!match) return null;
                    return `${match[1]}${match[2]}${match[3]}`;
                  };

                  const input = document.querySelector('#captcha_answer, input[name="captcha_answer"], input.captcha-input');
                  if (input && input instanceof Element) {
                    const containers = [
                      input.closest('form'),
                      input.closest('.content'),
                      input.closest('.dialog-contents'),
                      input.parentElement,
                      input.parentElement?.parentElement,
                      input.parentElement?.parentElement?.parentElement,
                    ].filter(Boolean);

                    for (const container of containers) {
                      if (!(container instanceof Element) || !isVisible(container)) continue;
                      const expression = extractExpression(container.textContent || '');
                      if (expression) {
                        return expression;
                      }
                    }
                  }

                  const headlineContainers = Array.from(document.querySelectorAll('form, .content, .dialog-contents, .box, .panel, main, section, article, div'));
                  for (const container of headlineContainers) {
                    if (!(container instanceof Element) || !isVisible(container)) continue;

                    const text = (container.textContent || '').toLowerCase();
                    if (!text.includes('security check') && !text.includes('calculate') && !text.includes('verify')) {
                      continue;
                    }

                    const expression = extractExpression(container.textContent || '');
                    if (expression) {
                      return expression;
                    }
                  }

                  return extractExpression(document.body?.innerText || '');
                }
                """);

            if (string.IsNullOrWhiteSpace(expression))
            {
                Notify("Captcha page fallback could not find a readable expression in DOM.");
                return ("", "");
            }

            var match = System.Text.RegularExpressions.Regex.Match(expression, @"^\s*(\d+)\s*([+-])\s*(\d+)\s*$");
            if (!match.Success)
            {
                return ("", expression);
            }

            var left = int.Parse(match.Groups[1].Value);
            var op = match.Groups[2].Value;
            var right = int.Parse(match.Groups[3].Value);
            var answer = op == "+" ? left + right : left - right;
            return (answer.ToString(), expression);
        }
        catch (PlaywrightException)
        {
            return ("", "");
        }
    }

    private async Task<(string Answer, string Expression)> TryResolveCaptchaAnswerFromVisibleInputContextAsync()
    {
        foreach (var selector in Selectors.CaptchaInputField)
        {
            var locator = await FindVisibleEditableLocatorAsync(selector);
            if (locator is null)
            {
                continue;
            }

            try
            {
                var expression = await locator.EvaluateAsync<string?>(
                    """
                    element => {
                      const extractExpression = text => {
                        if (!text) return null;
                        const normalized = text.replace(/\s+/g, ' ').trim();
                        const match = normalized.match(/(\d+)\s*([+\-])\s*(\d+)\s*=\s*\?/);
                        if (!match) return null;
                        return `${match[1]}${match[2]}${match[3]}`;
                      };

                      const containers = [
                        element.closest('form'),
                        element.parentElement,
                        element.parentElement?.parentElement,
                        element.parentElement?.parentElement?.parentElement,
                        element.closest('div'),
                      ].filter(Boolean);

                      for (const container of containers) {
                        if (!(container instanceof Element)) continue;
                        const expression = extractExpression(container.textContent || '');
                        if (expression) {
                          return expression;
                        }
                      }

                      return null;
                    }
                    """);

                if (string.IsNullOrWhiteSpace(expression))
                {
                    continue;
                }

                var match = System.Text.RegularExpressions.Regex.Match(expression, @"^\s*(\d+)\s*([+-])\s*(\d+)\s*$");
                if (!match.Success)
                {
                    return ("", expression);
                }

                var left = int.Parse(match.Groups[1].Value);
                var op = match.Groups[2].Value;
                var right = int.Parse(match.Groups[3].Value);
                var answer = op == "+" ? left + right : left - right;
                Notify($"Captcha input-context fallback result: expression='{expression}', answer='{answer}'.");
                return (answer.ToString(), expression);
            }
            catch (PlaywrightException)
            {
            }
        }

        return ("", "");
    }

    private async Task<bool> TryFillCaptchaAnswerAsync(string answer, CancellationToken cancellationToken)
    {
        foreach (var selector in Selectors.CaptchaInputField)
        {
            var locator = await FindVisibleEditableLocatorAsync(selector);
            if (locator is null)
            {
                continue;
            }

            try
            {
                await RetryAsync($"fill captcha selector {selector}", async () =>
                {
                    await locator.FillAsync(answer, new LocatorFillOptions { Timeout = _config.TimeoutMs });
                }, cancellationToken: cancellationToken);
                return true;
            }
            catch (PlaywrightException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        return false;
    }

    private async Task<bool> TrySubmitCaptchaAsync(CancellationToken cancellationToken)
    {
        if (await TryClickFirstAsync(Selectors.CaptchaSubmitButton, cancellationToken))
        {
            return true;
        }

        foreach (var selector in Selectors.CaptchaInputField)
        {
            var locator = await FindVisibleEditableLocatorAsync(selector);
            if (locator is null)
            {
                continue;
            }

            try
            {
                await RetryAsync($"press enter on captcha selector {selector}", async () =>
                {
                    await locator.PressAsync("Enter", new LocatorPressOptions { Timeout = _config.TimeoutMs });
                }, cancellationToken: cancellationToken);
                return true;
            }
            catch (PlaywrightException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        return false;
    }

    private async Task<bool> TryHandleIncorrectCaptchaDialogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var hasIncorrectDialog = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const nodes = Array.from(document.querySelectorAll('.dialog-contents .content, .dialog-contents, #dialogContent'));
                  return nodes.some(node => (node.textContent || '').toLowerCase().includes('incorrect captcha answer'));
                }
                """);

            if (!hasIncorrectDialog)
            {
                return false;
            }
        }
        catch (PlaywrightException)
        {
            return false;
        }

        Notify("Incorrect CAPTCHA answer dialog detected.");
        await TryClickCaptchaErrorDialogOkAsync(cancellationToken);
        await Task.Delay(300, cancellationToken);
        return true;
    }

    private async Task TryClickCaptchaErrorDialogOkAsync(CancellationToken cancellationToken)
    {
        foreach (var selector in Selectors.CaptchaErrorDialogOkButton)
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await RetryAsync($"click captcha error dialog selector {selector}", async () =>
                {
                    await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                    await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                }, cancellationToken: cancellationToken);
                return;
            }
            catch (PlaywrightException)
            {
            }
            catch (TimeoutException)
            {
            }
        }
    }

    private async Task<bool> TryClickCaptchaSuccessDialogOkAsync(CancellationToken cancellationToken)
    {
        foreach (var selector in Selectors.CaptchaSuccessDialogOkButton)
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                if (!await locator.IsVisibleAsync())
                {
                    continue;
                }

                await RetryAsync($"click captcha success dialog selector {selector}", async () =>
                {
                    await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                    await locator.ClickAsync(new LocatorClickOptions { Timeout = Math.Min(_config.TimeoutMs, 1500) });
                }, cancellationToken: cancellationToken);
                await Task.Delay(250, cancellationToken);
                return true;
            }
            catch (PlaywrightException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        return false;
    }

    private async Task<ILocator?> FindVisibleEditableLocatorAsync(string selector)
    {
        var candidates = _page.Locator(selector);
        var count = await candidates.CountAsync();
        for (var index = 0; index < count; index++)
        {
            var candidate = candidates.Nth(index);

            try
            {
                if (!await candidate.IsVisibleAsync())
                {
                    continue;
                }

                var isEditable = await candidate.EvaluateAsync<bool>(
                    """
                    element => {
                        if (!(element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement)) {
                            return false;
                        }

                        if (element.type && element.type.toLowerCase() === "hidden") {
                            return false;
                        }

                        return !element.disabled && !element.readOnly;
                    }
                    """);

                if (!isEditable)
                {
                    continue;
                }

                return candidate;
            }
            catch (PlaywrightException)
            {
            }
        }

        return null;
    }

    private enum CaptchaSubmitOutcome
    {
        Cleared,
        Rejected,
        StillVisible,
    }
}
