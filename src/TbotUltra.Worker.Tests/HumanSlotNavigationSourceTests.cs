using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HumanSlotNavigationSourceTests
{
    [Fact]
    public void ConstructionPreflight_UsesVisibleStockBeforeOpeningDorf1()
    {
        var source = ReadSource("Construction", "TravianClient.ConstructionAffordability.cs");
        var method = Slice(source, "private async Task<ConstructionAffordabilityDecision> EvaluateLiveConstructionAffordabilityAsync", "private async Task<ConstructionAffordabilityDecision> EvaluateConstructionAffordabilityAsync");

        Assert.True(method.IndexOf("ReadResourceSnapshotAsync", StringComparison.Ordinal)
            < method.IndexOf("EnsureResourceFieldsPageAsync", StringComparison.Ordinal));
        Assert.Contains("ConstructionAffordabilityOperation.TryParseStock", method, StringComparison.Ordinal);
    }

    [Fact]
    public void SlotEntry_UsesOverviewClickWithoutDirectBuildPageFallback()
    {
        var navigation = ReadSource("Core", "TravianClient.Navigation.cs");
        var upgrade = ReadSource("Buildings", "TravianClient.Buildings.UpgradeFlow.cs");
        var analysis = ReadSource("Buildings", "TravianClient.Buildings.UpgradeAnalysis.cs");
        var construct = ReadSource("Buildings", "TravianClient.Buildings.ConstructFlow.cs");

        Assert.Contains("OpenSlotFromOverviewAsync", navigation, StringComparison.Ordinal);
        Assert.Contains("await OpenSlotFromOverviewAsync(slotId, cancellationToken)", upgrade, StringComparison.Ordinal);
        Assert.Contains("await OpenSlotFromOverviewAsync(slotId, cancellationToken)", analysis, StringComparison.Ordinal);
        Assert.Contains("await OpenSlotFromOverviewAsync(slotId, cancellationToken)", construct, StringComparison.Ordinal);
        Assert.Contains(".buildingSlot[data-aid='{slotId}'] a[href*='build.php?id={slotId}']", navigation, StringComparison.Ordinal);
        Assert.Contains(".buildingSlot[data-aid='40'] svg path[onclick*='build.php?id=40']", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("await GotoAsync(Paths.BuildBySlot(", upgrade, StringComparison.Ordinal);
        Assert.DoesNotContain("await GotoAsync(Paths.BuildBySlot(", construct, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string ReadSource(string area, string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "TbotUltra.Worker", "Services", "Automation", area, fileName);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new DirectoryNotFoundException("Could not locate Worker source.");
    }
}
