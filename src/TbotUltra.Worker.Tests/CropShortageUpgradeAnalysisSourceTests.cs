using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class CropShortageUpgradeAnalysisSourceTests
{
    [Fact]
    public void UpgradeAnalysis_RecognizesCropVariantAndScopesClickableCandidates()
    {
        var source = ReadSource("TravianClient.Buildings.UpgradeAnalysis.cs");

        Assert.Contains("increase\\s+crop\\s+production", source, StringComparison.Ordinal);
        Assert.Contains("(!inOfficialPrimarySection && !inUpgradeContainer)", source, StringComparison.Ordinal);
        Assert.Contains("Unrecognized upgradeBlocked panel:", source, StringComparison.Ordinal);
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

        throw new FileNotFoundException($"Could not locate {fileName} from {AppContext.BaseDirectory}.");
    }
}
