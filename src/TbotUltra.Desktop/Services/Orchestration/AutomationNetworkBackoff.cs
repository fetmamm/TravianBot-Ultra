using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services.Orchestration;

internal sealed class AutomationNetworkBackoff(
    TimeProvider? timeProvider = null,
    Func<int, int, int>? nextRandom = null)
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<int, int, int> _nextRandom = nextRandom ?? Random.Shared.Next;
    private DateTimeOffset _unavailableUntilUtc = DateTimeOffset.MinValue;
    private int _consecutiveFailures;

    internal int ConsecutiveFailures
    {
        get
        {
            lock (_sync)
            {
                return _consecutiveFailures;
            }
        }
    }

    internal bool IsUnavailable => Remaining > TimeSpan.Zero;

    internal TimeSpan Remaining
    {
        get
        {
            lock (_sync)
            {
                var remaining = _unavailableUntilUtc - _timeProvider.GetUtcNow();
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    internal void MarkUnavailable(TimeSpan delay)
    {
        lock (_sync)
        {
            var unavailableUntil = _timeProvider.GetUtcNow() + delay;
            if (unavailableUntil > _unavailableUntilUtc)
            {
                _unavailableUntilUtc = unavailableUntil;
            }
        }
    }

    internal TimeSpan NextRetryDelay()
    {
        lock (_sync)
        {
            _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 3);
            var minimumSeconds = 30 * (1 << (_consecutiveFailures - 1));
            return TimeSpan.FromSeconds(_nextRandom(minimumSeconds, minimumSeconds * 2 + 1));
        }
    }

    internal void MarkHealthy()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _unavailableUntilUtc = DateTimeOffset.MinValue;
        }
    }

    internal static bool IsTransientConnectionFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (current is TransientNavigationException
                || message.Contains("page state is 'unknown'", StringComparison.OrdinalIgnoreCase)
                || message.Contains("ERR_NAME_NOT_RESOLVED", StringComparison.OrdinalIgnoreCase)
                || message.Contains("ERR_NETWORK_CHANGED", StringComparison.OrdinalIgnoreCase)
                || message.Contains("ERR_CONNECTION_", StringComparison.OrdinalIgnoreCase)
                || message.Contains("chrome-error://", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
