using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ConstructBuildingNavigationSourceTests
{
    [Fact]
    public void ConstructFlow_reuses_a_fresh_current_dorf2_overview()
    {
        var constructSource = ReadSource("TravianClient.Buildings.ConstructFlow.cs");
        var overviewSource = ReadSource("TravianClient.Buildings.Overview.cs");

        Assert.Contains(
            "ReadBuildingsAsync(cancellationToken, reuseFreshCurrentOverview: true)",
            constructSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!reuseFreshCurrentOverview || !IsCurrentUrlForPath(Paths.Buildings) || await IsPageMarkedStaleAsync())",
            overviewSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructFlow_recovers_the_exact_slot_after_hero_transfer_reload_race()
    {
        var source = ReadSource("TravianClient.Buildings.ConstructFlow.cs");

        Assert.Contains("retryExactPageOnceWhenChoiceMissing: true", source, StringComparison.Ordinal);
        Assert.Contains("reopening exact slot once", source, StringComparison.Ordinal);
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
