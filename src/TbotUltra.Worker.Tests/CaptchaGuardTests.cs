using System.Reflection;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class CaptchaGuardTests
{
    [Fact]
    public async Task TrySolveCaptchaAutomatically_OnOfficialServer_NeverInvokesSolver()
    {
        // ServerFlavor is derived from BaseUrl; ts1.travian.com resolves to Official.
        // Even with captcha auto-solve explicitly enabled, the solver must not be touched.
        var options = new BotOptions
        {
            BaseUrl = "https://ts1.travian.com",
            CaptchaAutoSolveEnabled = true,
        };
        var account = new AccountOptions { Name = "test", Username = "u", Password = "p" };

        // The constructor only stores the page; the IsPrivateServer guard returns before any
        // page use, so a null page is safe here. ThrowingCaptchaSolver proves the solver is
        // never reached on official servers.
        var client = new TravianClient(
            page: null!,
            options,
            account,
            captchaAutoSolver: new ThrowingCaptchaSolver());

        var method = typeof(TravianClient).GetMethod(
            "TrySolveCaptchaAutomaticallyAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var task = (Task<bool>)method.Invoke(
            client,
            new object?[] { "login-page", "fake-screenshot.png", CancellationToken.None })!;

        Assert.False(await task);
    }

    private sealed class ThrowingCaptchaSolver : ICaptchaAutoSolver
    {
        public Task<bool> WarmupAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Captcha solver must not be used on official servers.");

        public Task<CaptchaSolverResult> TrySolveAsync(string imagePath, int timeoutSeconds, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Captcha solver must not be used on official servers.");
    }
}
