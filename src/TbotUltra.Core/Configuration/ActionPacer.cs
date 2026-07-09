namespace TbotUltra.Core.Configuration;

public sealed class ActionPacer
{
    private readonly bool _enabled;
    private readonly Action<string>? _logger;

    public ActionPacer(bool enabled, Action<string>? logger = null)
    {
        _enabled = enabled;
        _logger = logger;
    }

    public static ActionPacer FromOptions(BotOptions options, Action<string>? logger = null)
    {
        return new ActionPacer(options.ActionPacingEnabled, logger);
    }

    public Task DelayAsync(double minSeconds, double maxSeconds, CancellationToken cancellationToken, string? reason = null)
    {
        return DelayMillisecondsAsync(
            (int)Math.Round(Math.Max(0, minSeconds) * 1000),
            (int)Math.Round(Math.Max(0, maxSeconds) * 1000),
            cancellationToken,
            reason);
    }

    public Task DelayMillisecondsAsync(int minMilliseconds, int maxMilliseconds, CancellationToken cancellationToken, string? reason = null)
    {
        if (!_enabled)
        {
            return Task.CompletedTask;
        }

        var min = Math.Max(0, minMilliseconds);
        var max = Math.Max(min, maxMilliseconds);
        if (max <= 0)
        {
            return Task.CompletedTask;
        }

        var delayMs = Random.Shared.Next(min, max + 1);
        if (delayMs <= 0)
        {
            return Task.CompletedTask;
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            _logger?.Invoke($"[pacing] {FormatReasonForLog(reason)}: waiting {delayMs / 1000.0:F1}s");
        }

        return Task.Delay(delayMs, cancellationToken);
    }

    private static string FormatReasonForLog(string reason)
    {
        var trimmed = reason.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        if (HasCategoryPrefix(trimmed))
        {
            return trimmed;
        }

        var lower = trimmed.ToLowerInvariant();
        if (lower.Contains("click", StringComparison.Ordinal))
        {
            return lower == "action pacing \"click\" delay"
                ? "Click"
                : $"Click: {trimmed}";
        }

        if (lower.Contains("page load", StringComparison.Ordinal)
            || lower.Contains("page reload", StringComparison.Ordinal))
        {
            return $"Page load: {trimmed}";
        }

        if (lower.Contains("farm list", StringComparison.Ordinal))
        {
            return $"Farm list: {trimmed}";
        }

        if (lower.Contains("before task", StringComparison.Ordinal)
            || lower.Contains("state-changing task", StringComparison.Ordinal)
            || lower.Contains("between actions", StringComparison.Ordinal))
        {
            return $"Task: {trimmed}";
        }

        return trimmed;
    }

    private static bool HasCategoryPrefix(string value)
    {
        return value.StartsWith("Click:", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Click", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Page load:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Task:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Farm list:", StringComparison.OrdinalIgnoreCase);
    }
}
