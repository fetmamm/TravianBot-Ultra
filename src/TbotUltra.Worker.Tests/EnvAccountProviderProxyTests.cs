using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class EnvAccountProviderProxyTests : IDisposable
{
    private readonly string _envPath;

    public EnvAccountProviderProxyTests()
    {
        _envPath = Path.Combine(Path.GetTempPath(), $"tbot-env-proxy-{Guid.NewGuid():N}.env");
    }

    [Fact]
    public void LoadAccount_ReadsProxyFields()
    {
        File.WriteAllText(_envPath, string.Join('\n',
            "TBOT_ACTIVE_ACCOUNT=alice",
            "TBOT_ACCOUNTS=alice",
            "TBOT_ALICE_USERNAME=alice",
            "TBOT_ALICE_PASSWORD=secret",
            "TBOT_ALICE_SERVER_URL=https://ts1.travian.eu",
            "TBOT_ALICE_PROXY_ENABLED=true",
            "TBOT_ALICE_PROXY_SERVER=1.2.3.4:8080"));

        var account = new EnvAccountProvider(_envPath).LoadAccount();

        Assert.True(account.ProxyEnabled);
        Assert.Equal("1.2.3.4:8080", account.ProxyServer);
    }

    [Fact]
    public void LoadAccount_DefaultsProxyOffWhenMissingOrUnknown()
    {
        File.WriteAllText(_envPath, string.Join('\n',
            "TBOT_ACTIVE_ACCOUNT=alice",
            "TBOT_ACCOUNTS=alice",
            "TBOT_ALICE_USERNAME=alice",
            "TBOT_ALICE_PASSWORD=secret",
            "TBOT_ALICE_SERVER_URL=https://ts1.travian.eu",
            "TBOT_ALICE_PROXY_ENABLED=")); // empty → off

        var account = new EnvAccountProvider(_envPath).LoadAccount();

        Assert.False(account.ProxyEnabled);
        Assert.Equal(string.Empty, account.ProxyServer);
    }

    public void Dispose()
    {
        if (File.Exists(_envPath))
        {
            File.Delete(_envPath);
        }
    }
}
