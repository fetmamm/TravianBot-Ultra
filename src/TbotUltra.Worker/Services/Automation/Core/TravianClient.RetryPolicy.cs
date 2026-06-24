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

    private static bool IsTransientExecutionContextException(Exception ex)
    {
        if (ex is PlaywrightException playwrightException && IsTransientExecutionContextError(playwrightException))
        {
            return true;
        }

        return ex.InnerException is not null
            && IsTransientExecutionContextException(ex.InnerException);
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
                    Notify($"{label} hit transient navigation context on attempt {attempt}/{attempts}. Retrying...");
                    await Task.Delay(250 * attempt, cancellationToken);
                    continue;
                }

                if (attempt >= attempts)
                {
                    break;
                }

                await TryDismissContinuePromptAsync();
                Notify($"{label} failed on attempt {attempt}/{attempts}. Retrying...");
                await Task.Delay(400 * attempt, cancellationToken);
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
                    Notify($"{label} hit transient navigation context on attempt {attempt}/{attempts}. Retrying...");
                    await Task.Delay(250 * attempt, cancellationToken);
                    continue;
                }

                if (attempt >= attempts)
                {
                    break;
                }

                await TryDismissContinuePromptAsync();
                Notify($"{label} failed on attempt {attempt}/{attempts}. Retrying...");
                await Task.Delay(400 * attempt, cancellationToken);
            }
        }

        throw new InvalidOperationException($"{label} failed after {attempts} attempts: {lastError?.Message}", lastError);
    }

}
