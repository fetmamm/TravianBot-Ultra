using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class FarmListRallyPointFallbackSourceTests
{
    [Fact]
    public void MissingCurrentRallyPoint_TriesOtherOwnedVillagesBeforeFailing()
    {
        var source = ReadSource();
        var start = source.IndexOf("if (await IsRallyPointLevelZeroAsync(cancellationToken))", StringComparison.Ordinal);
        var end = source.IndexOf("await GotoAsync(Paths.FarmListFastUp", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var missingRallyPointPath = source[start..end];
        Assert.Contains("await TryOpenFarmListsFromAnotherVillageAsync(cancellationToken)", missingRallyPointPath, StringComparison.Ordinal);
        Assert.DoesNotContain("ConstructBuildingAsync", missingRallyPointPath, StringComparison.Ordinal);
    }

    private static string ReadSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "TbotUltra.Worker", "Services", "Automation", "Farming", "TravianClient.FarmLists.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new DirectoryNotFoundException("Could not locate TravianClient.FarmLists.cs.");
    }
}
