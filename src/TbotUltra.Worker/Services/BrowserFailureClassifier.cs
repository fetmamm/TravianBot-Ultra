namespace TbotUltra.Worker.Services;

public static class BrowserFailureClassifier
{
    public static bool IsTargetCrash(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("Target crashed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
