using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationNetworkBackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NextRetryDelay_UsesBoundedExponentialJitter()
    {
        var backoff = new AutomationNetworkBackoff(
            new MutableTimeProvider(Now),
            (minimum, _) => minimum);

        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextRetryDelay());
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextRetryDelay());
        Assert.Equal(TimeSpan.FromSeconds(120), backoff.NextRetryDelay());
        Assert.Equal(TimeSpan.FromSeconds(120), backoff.NextRetryDelay());
        Assert.Equal(3, backoff.ConsecutiveFailures);
    }

    [Fact]
    public void MarkUnavailable_NeverShortensAnAuthoritativeDeadline()
    {
        var time = new MutableTimeProvider(Now);
        var backoff = new AutomationNetworkBackoff(time);

        backoff.MarkUnavailable(TimeSpan.FromMinutes(5));
        backoff.MarkUnavailable(TimeSpan.FromMinutes(1));
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(3), backoff.Remaining);
    }

    [Fact]
    public void MarkHealthy_ClearsFailuresAndDeadline()
    {
        var backoff = new AutomationNetworkBackoff(new MutableTimeProvider(Now));
        backoff.NextRetryDelay();
        backoff.MarkUnavailable(TimeSpan.FromMinutes(1));

        backoff.MarkHealthy();

        Assert.Equal(0, backoff.ConsecutiveFailures);
        Assert.False(backoff.IsUnavailable);
    }

    [Fact]
    public void IsTransientConnectionFailure_ClassifiesNestedNavigationAndUnknownState()
    {
        Assert.True(AutomationNetworkBackoff.IsTransientConnectionFailure(
            new TransientNavigationException("Navigation timed out.")));
        Assert.True(AutomationNetworkBackoff.IsTransientConnectionFailure(
            new InvalidOperationException("Wrapped", new InvalidOperationException("page state is 'unknown'"))));
        Assert.False(AutomationNetworkBackoff.IsTransientConnectionFailure(
            new InvalidOperationException("page state is 'logged_out'")));
    }

    [Theory]
    [InlineData("net::ERR_NAME_NOT_RESOLVED")]
    [InlineData("net::ERR_NETWORK_CHANGED")]
    [InlineData("net::ERR_CONNECTION_TIMED_OUT")]
    [InlineData("Current URL is chrome-error://chromewebdata/")]
    public void IsTransientConnectionFailure_ClassifiesRawNetworkFailures(string message)
    {
        var exception = new InvalidOperationException(
            "Worker operation failed.",
            new InvalidOperationException(message));

        Assert.True(AutomationNetworkBackoff.IsTransientConnectionFailure(exception));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan delay) => _now += delay;
    }
}
