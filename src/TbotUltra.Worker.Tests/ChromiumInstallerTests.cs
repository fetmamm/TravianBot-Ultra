using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

/// <summary>
/// The progress line drives the download dialog's progress bar, so a parsing miss shows the user a frozen
/// bar during a multi-minute download. Samples are taken from real driver output.
/// </summary>
public sealed class ChromiumInstallerTests
{
    [Theory]
    [InlineData("|■■■■■■■■                    |  10% of 183.6 MiB", 10)]
    [InlineData("|                            |   0% of 183.6 MiB", 0)]
    [InlineData("|■■■■■■■■■■■■■■■■■■■■■■■■■■■■| 100% of 113.6 MiB", 100)]
    public void ParsesTheDriverDownloadProgressLine(string line, int expectedPercent)
    {
        Assert.True(ChromiumInstaller.TryParseProgressLine(line, out var progress));
        Assert.Equal(expectedPercent, progress.PercentComplete);
        Assert.Contains($"{expectedPercent}%", progress.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Downloading Chrome for Testing 149.0.7827.55 (playwright chromium v1228) from https://cdn")]
    [InlineData("Chrome for Testing 149.0.7827.55 downloaded to C:\\repo\\ms-playwright\\chromium-1228")]
    [InlineData("Removing unused browser at C:\\repo\\ms-playwright\\chromium-1161")]
    [InlineData("Failed to download Chromium, caused by network error")]
    [InlineData("")]
    public void LeavesNonProgressLinesForTheLog(string line)
    {
        Assert.False(ChromiumInstaller.TryParseProgressLine(line, out _));
    }

    [Fact]
    public void DoesNotTreatAnUnrelatedPercentageAsDownloadProgress()
    {
        // Guards the progress bar against a value it cannot display.
        Assert.False(ChromiumInstaller.TryParseProgressLine("cache hit 250% of expected", out _));
    }

    [Fact]
    public void ReportsTheShippedDriverAsAvailable()
    {
        // The install path depends on the driver sitting next to the app; the release workflow copies it in
        // deliberately, and Test-ReleaseBundle.ps1 asserts the same file.
        Assert.True(ChromiumInstaller.DriverAvailable());
    }
}
