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
        Assert.Contains("LicenseStopTimeout = TimeSpan.FromSeconds(5)", source, StringComparison.Ordinal);
        Assert.Contains("StopAsync(runId, stopCancellation.Token)", source, StringComparison.Ordinal);

        var releaseStart = source.IndexOf("private async Task ReleaseLicenseRunAsync", StringComparison.Ordinal);
        var stopHelperStart = source.IndexOf("private async Task StopLicenseRunOnServerAsync", StringComparison.Ordinal);
        Assert.True(releaseStart >= 0 && stopHelperStart > releaseStart);
        var releaseBody = source[releaseStart..stopHelperStart];
        Assert.DoesNotContain("StopAsync(", releaseBody, StringComparison.Ordinal);

        var stopBody = source[stopHelperStart..];
        Assert.Equal(1, CountOccurrences(stopBody, "StopAsync("));
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
