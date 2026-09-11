using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BonusVideoConsentRecoverySourceTests
{
    [Fact]
    public void ConsentOverlayInterception_RequiresCmpPointerInterception()
    {
        Assert.True(TravianClient.IsConsentOverlayInterception(
            new Exception("<div id=\"cmpwrapper\"> intercepts pointer events")));
        Assert.False(TravianClient.IsConsentOverlayInterception(
            new TimeoutException("Timeout 20000ms exceeded while waiting for a video button.")));
    }

    [Fact]
    public void ConstructFasterRecoversWhenConsentOverlayInterceptsWatchClick()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Buildings",
            "TravianClient.ConstructFaster.cs"));

        Assert.Contains("observeLateOverlay: true", source, StringComparison.Ordinal);
        Assert.Contains("IsConsentOverlayInterception(ex)", source, StringComparison.Ordinal);
        Assert.Contains("consent overlay blocked the video feature button", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TbotUltra.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
