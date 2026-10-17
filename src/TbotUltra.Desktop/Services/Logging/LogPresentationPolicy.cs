namespace TbotUltra.Desktop.Services.Logging;

internal static class LogPresentationPolicy
{
    public static bool ShouldQueueForUi(string message, bool logsVisible)
    {
        if (logsVisible || string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        return !IsHighVolumeDiagnostic(message);
    }

    public static bool IsHighVolumeDiagnostic(string message)
        => message.Contains("[browser-trace:verbose]", StringComparison.OrdinalIgnoreCase)
           || message.Contains("[pacing:verbose]", StringComparison.OrdinalIgnoreCase)
           || message.Contains("[scan:verbose]", StringComparison.OrdinalIgnoreCase);
}
