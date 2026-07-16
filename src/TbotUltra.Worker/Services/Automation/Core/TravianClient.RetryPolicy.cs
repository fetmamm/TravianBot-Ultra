using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private static bool IsTransientExecutionContextError(PlaywrightException ex)
    {
        var value = ex.Message?.ToLowerInvariant() ?? string.Empty;
        return value.Contains("execution context was destroyed")
            || value.Contains("cannot find context with specified id")
            || value.Contains("err_aborted")
            || value.Contains("frame was detached")
            || value.Contains("target page, context or browser has been closed")
            || value.Contains("navigation interrupted");
    }

    private static bool IsBonusVideoNavigationTransition(PlaywrightException ex)
    {
        if (BrowserFailureClassifier.IsTargetCrash(ex))
        {
            return false;
        }

        var value = ex.Message?.ToLowerInvariant() ?? string.Empty;
        return value.Contains("execution context was destroyed")
            || value.Contains("cannot find context with specified id")
            || value.Contains("err_aborted")
            || value.Contains("frame was detached")
            || value.Contains("navigation interrupted");
    }

    private static bool IsTransientExecutionContextException(Exception ex)
    {
        if (ex is PlaywrightException playwrightException && IsTransientExecutionContextError(playwrightException))
        {
            return true;
        }

        return ex.InnerException is not null
            && IsTransientExecutionContextException(ex.InnerException);
    }

    // A navigation/action timeout (e.g. Playwright's "Timeout 20000ms exceeded.") almost always means a slow
    // network or server, so a 400ms pause between attempts recovers nothing — back off longer for these.
    private static bool IsTimeoutError(Exception ex)
    {
        if (ex is TimeoutException)
        {
            return true;
        }

        var message = ex.Message?.ToLowerInvariant() ?? string.Empty;
        if (message.Contains("timeout") && message.Contains("exceeded"))
        {
            return true;
        }

        return ex.InnerException is not null && IsTimeoutError(ex.InnerException);
    }

    private async Task RetryAsync(string label, Func<Task> action, int attempts = 3, CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryDismissContinuePromptAsync();

            try
            {
                await action();
                return;
            }
            catch (ManualVerificationRequiredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (ex is PlaywrightException pwx && IsTransientExecutionContextError(pwx) && attempt < attempts)
                {
                    await TryDismissContinuePromptAsync();
                    Notify($"[retry:verbose] {label} hit transient navigation context on attempt {attempt}/{attempts}. Retrying...");
                    await Task.Delay(250 * attempt, cancellationToken);
                    continue;
                }

                if (attempt >= attempts)
                {
                    break;
                }

                await TryDismissContinuePromptAsync();
                Notify($"[retry:verbose] {label} failed on attempt {attempt}/{attempts}. Retrying...");
                await Task.Delay((IsTimeoutError(ex) ? 5000 : 400) * attempt, cancellationToken);
            }
        }

        throw new InvalidOperationException($"{label} failed after {attempts} attempts: {lastError?.Message}", lastError);
    }

    private async Task<T> RetryAsync<T>(string label, Func<Task<T>> action, int attempts = 3, CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryDismissContinuePromptAsync();

            try
            {
                return await action();
            }
            catch (ManualVerificationRequiredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (IsTransientExecutionContextException(ex) && attempt < attempts)
                {
                    await TryDismissContinuePromptAsync();
                    Notify($"[retry:verbose] {label} hit transient navigation context on attempt {attempt}/{attempts}. Retrying...");
                    await Task.Delay(250 * attempt, cancellationToken);
                    continue;
                }

                if (attempt >= attempts)
                {
                    break;
                }

                await TryDismissContinuePromptAsync();
                Notify($"[retry:verbose] {label} failed on attempt {attempt}/{attempts}. Retrying...");
                await Task.Delay((IsTimeoutError(ex) ? 5000 : 400) * attempt, cancellationToken);
            }
        }

        throw new InvalidOperationException($"{label} failed after {attempts} attempts: {lastError?.Message}", lastError);
    }

}
