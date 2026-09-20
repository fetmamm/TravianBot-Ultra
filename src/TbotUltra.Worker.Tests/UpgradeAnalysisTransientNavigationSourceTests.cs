using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class UpgradeAnalysisTransientNavigationSourceTests
{
    [Fact]
    public void UpgradeAnalysis_preserves_safe_navigation_failures_without_diagnostics_capture()
    {
        var source = ReadSource("TravianClient.Buildings.UpgradeAnalysis.cs");
        var transientCatch = source.IndexOf("catch (TransientNavigationException)", StringComparison.Ordinal);
        var diagnosticCapture = transientCatch < 0
            ? -1
            : source.IndexOf("CaptureFailureArtifactsAsync($\"upgrade-slot-", transientCatch, StringComparison.Ordinal);

        Assert.True(transientCatch >= 0);
        Assert.True(diagnosticCapture > transientCatch);
    }

    private static string ReadSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(
                directory.FullName,
                "src",
                "TbotUltra.Worker",
                "Services",
                "Automation",
                "Buildings",
                fileName);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
