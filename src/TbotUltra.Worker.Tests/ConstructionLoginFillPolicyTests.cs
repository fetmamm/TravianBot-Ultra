using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ConstructionLoginFillPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000);

    [Theory]
    [InlineData(true, 1001, true)]
    [InlineData(true, 1000, false)]
    [InlineData(true, 999, false)]
    [InlineData(false, 1001, false)]
    public void IsActive_RequiresEnabledUnexpiredSession(bool enabled, long expiry, bool expected)
    {
        Assert.Equal(expected, ConstructionLoginFillPolicy.IsActive(enabled, expiry, Now));
    }

    [Fact]
    public void IsActive_RejectsLegacyFlagWithoutExpiry()
    {
        Assert.False(ConstructionLoginFillPolicy.IsActive(true, null, Now));
    }
}
