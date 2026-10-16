using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class LicenseLeaseReuseSourceTests
{
    [Fact]
    public void ShortBrowserOperations_ReuseOneLicenseRunWithoutBlockingOnStopNetwork()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot, "src", "TbotUltra.Desktop", "MainWindow.Licensing.cs"));

        Assert.Contains("LicenseReleaseGracePeriod", source, StringComparison.Ordinal);
        Assert.Contains("CancelPendingLicenseRelease();", source, StringComparison.Ordinal);
        Assert.Contains("ScheduleLicenseRelease", source, StringComparison.Ordinal);
        Assert.Contains("StopLicenseRunOnServerAsync", source, StringComparison.Ordinal);

        var releaseStart = source.IndexOf("private async Task ReleaseLicenseRunAsync", StringComparison.Ordinal);
        var stopHelperStart = source.IndexOf("private async Task StopLicenseRunOnServerAsync", StringComparison.Ordinal);
        Assert.True(releaseStart >= 0 && stopHelperStart > releaseStart);
        var releaseBody = source[releaseStart..stopHelperStart];
        Assert.DoesNotContain("StopAsync(", releaseBody, StringComparison.Ordinal);
    }
}
