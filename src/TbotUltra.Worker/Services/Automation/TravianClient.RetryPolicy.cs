using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private static bool IsTransientExecutionContextError(PlaywrightException ex)
    {
        var value = ex.Message?.ToLowerInvariant() ?? string.Empty;
        return value.Contains("execution context was destroyed")
            || value.Contains("cannot find context with specified id");
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

    private async Task RetryAsync(string label, Func<Task> action, int attempts = 3)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
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
                    Notify($"{label} hit transient navigation context error on attempt {attempt}/{attempts}. Retrying...");
                    await Task.Delay(250 * attempt);
                    continue;
                }

                if (attempt >= attempts)
                {
                    break;
                }

                await TryDismissContinuePromptAsync();
                Notify($"{label} failed on attempt {attempt}/{attempts}. Retrying...");
                await Task.Delay(400 * attempt);
            }
        }

        throw new InvalidOperationException($"{label} failed after {attempts} attempts: {lastError?.Message}", lastError);
    }

    private async Task<bool> RetryTruthyAsync(string label, Func<Task<bool>> action, int attempts = 3)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var result = await action();
            if (result)
            {
                return true;
            }

            if (attempt < attempts)
            {
                Notify($"{label} was not available on attempt {attempt}/{attempts}. Retrying...");
                await Task.Delay(400 * attempt);
            }
        }

        return false;
    }
}
