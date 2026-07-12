namespace TbotUltra.Worker.Services;

/// <summary>
/// A safe page navigation/read failed before any state-changing browser action. Queue orchestration
/// may defer and retry this without consuming the task's functional retry budget.
/// </summary>
public sealed class TransientNavigationException : TimeoutException
{
    public TransientNavigationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public static class BrowserFailureClassifier
{
    // Fatal Playwright/Chromium disconnect messages: when any of these appears the shared page/context
    // is dead and cannot be reused, so the caller must discard the session and create a fresh one and
    // defer the queue item. Matched case-insensitively, including on inner exceptions.
    //
    // NOTE: "Execution context was destroyed" is deliberately NOT in this list. That is a transient
    // navigation race (the page reloaded while a read was in flight) which the worker simply retries,
    // and which the desktop UI classifies as a non-alarm. Treating it as a crash here would needlessly
    // tear down a healthy session.
    private static readonly string[] FatalDisconnectMarkers =
    {
        "Target crashed",
        "Target page, context or browser has been closed",
        "Target closed",
        "browser has been closed",
        "page has been closed",
        "page is closed",
        "Cannot navigate to closed page",
    };

    public static bool IsTargetCrash(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (string.IsNullOrEmpty(message))
            {
                continue;
            }

            foreach (var marker in FatalDisconnectMarkers)
            {
                if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
