using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BotTaskRunnerLicenseLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-license-runner-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExecuteOnceStopsBeforeBrowserWhenLicenseLeaseIsDenied()
    {
        Directory.CreateDirectory(_root);
        var runner = new BotTaskRunner(new FixedAccountProvider(), new ProjectContext(_root));
        var requests = 0;
        runner.AutomationUseLeaseFactory = _ =>
        {
            requests++;
            throw new InvalidOperationException("License denied");
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteOnceAsync(
            new BotOptions { ServerName = "test", BaseUrl = "https://example.invalid" }, _ => { }));

        Assert.Equal("License denied", error.Message);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task LicenseLeaseIsReleasedWhenBrowserAdmissionFails()
    {
        Directory.CreateDirectory(_root);
        var runner = new BotTaskRunner(new FixedAccountProvider(neverUseOwnIp: true), new ProjectContext(_root));
        var lease = new ObservedLease();
        runner.AutomationUseLeaseFactory = _ => ValueTask.FromResult<IAsyncDisposable>(lease);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteOnceAsync(
            new BotOptions { ServerName = "test", BaseUrl = "https://example.invalid" }, _ => { }));

        Assert.True(lease.Disposed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedAccountProvider(bool neverUseOwnIp = false) : IAccountProvider
    {
        public AccountOptions LoadAccount(string? accountName = null) => new()
        {
            Name = "test",
            Username = "test",
            Password = "test",
            ServerName = "test",
            ServerUrl = "https://example.invalid",
            NeverUseOwnIp = neverUseOwnIp,
        };
    }

    private sealed class ObservedLease : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
