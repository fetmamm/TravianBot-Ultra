using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class QuickReloginNewVillageAnalysisSourceTests
{
    [Fact]
    public void QuickRelogin_AnalyzesNewlyDiscoveredVillagesWhenEnabled()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "MainWindow.Session.cs"));

        Assert.Contains("quickResourceStatus = await RefreshResourceSnapshotForUiAsync(", source, StringComparison.Ordinal);
        Assert.Contains(
            "if (options.PostLoginAnalyzeNewVillages && quickResourceStatus is not null)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnalyzeNewVillagesAfterLoginAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "quickResourceStatus.Villages,",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TbotUltra.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
