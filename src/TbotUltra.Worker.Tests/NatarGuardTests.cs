using TbotUltra.Core.Configuration;
using TbotUltra.Worker;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class NatarGuardTests
{
    [Fact]
    public async Task EnsureNatarFarmCache_OnOfficialServer_NoOpsWithoutLaunchingClient()
    {
        var runner = new BotTaskRunner(
            new ThrowingAccountProvider(),
            new ProjectContext(Path.GetTempPath()),
            new ThrowingCaptchaSolver());

        var logs = new List<string>();
        var options = new BotOptions
        {
            BaseUrl = "https://ts1.travian.com",
            ServerFlavor = ServerFlavor.Official,
        };

        var count = await runner.EnsureNatarFarmCacheAndReturnToFarmListAsync(options, logs.Add);

        Assert.Equal(0, count);
        Assert.Contains(logs, line => line.Contains("only available on the SS-Travi private server", StringComparison.OrdinalIgnoreCase));
    }

    // The account provider / captcha solver would only be touched if the guard failed to
    // short-circuit and the bot actually tried to launch a browser session. Throwing here
    // proves the official-server path never reaches that point.
    private sealed class ThrowingAccountProvider : IAccountProvider
    {
        public AccountOptions LoadAccount(string? accountName = null)
            => throw new InvalidOperationException("Client session must not be launched on official servers for Natar analysis.");
    }

    private sealed class ThrowingCaptchaSolver : ICaptchaAutoSolver
    {
        public Task<bool> WarmupAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Captcha solver must not be used in this test.");

        public Task<CaptchaSolverResult> TrySolveAsync(string imagePath, int timeoutSeconds, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Captcha solver must not be used in this test.");
    }
}
