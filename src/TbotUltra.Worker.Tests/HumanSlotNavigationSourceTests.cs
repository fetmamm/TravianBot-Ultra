using System.Xml.Linq;
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
    public void SlotEntry_UsesSvgClickAndAlarmsBeforeVerifiedUrlFallback()
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
        Assert.Contains(".buildingSlot[data-aid='{slotId}'] svg path[onclick*='build.php?id={slotId}']", navigation, StringComparison.Ordinal);
        Assert.Contains("#resourceFieldContainer svg path.buildingSlot{slotId}[onclick*='build.php?id={slotId}']", navigation, StringComparison.Ordinal);
        Assert.Contains("ALARM: [slot-nav]", navigation, StringComparison.Ordinal);
        Assert.Contains("await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken)", navigation, StringComparison.Ordinal);
        Assert.Contains("#resourceFieldContainer svg path.buildingSlot{slotId}[onclick*='build.php?id={slotId}']:visible", navigation, StringComparison.Ordinal);
        Assert.Contains(".buildingSlot[data-aid='{slotId}']:visible", navigation, StringComparison.Ordinal);
        Assert.True(navigation.IndexOf("_page.Locator(visibleSlot).CountAsync()", StringComparison.Ordinal)
            < navigation.IndexOf("await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken)", StringComparison.Ordinal));
        Assert.True(navigation.IndexOf("Notify($\"ALARM: [slot-nav]", StringComparison.Ordinal)
            < navigation.IndexOf("await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken)", StringComparison.Ordinal));
        Assert.DoesNotContain("await GotoAsync(Paths.BuildBySlot(", upgrade, StringComparison.Ordinal);
        Assert.DoesNotContain("await GotoAsync(Paths.BuildBySlot(", construct, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(34)]
    public void CapturedEmptyBuildingSlots_ExposeSvgClickInsideExactSlot(int slotId)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "dorf2_empty_slots.xml");
        var document = XDocument.Load(fixture);
        var slot = Assert.Single(document.Descendants("div"),
            node => node.Attribute("data-aid")?.Value == slotId.ToString());

        Assert.Equal("0", slot.Attribute("data-gid")?.Value);
        Assert.Contains(slot.Descendants("a"), node => node.Attribute("href")?.Value == $"/build.php?id={slotId}");
        Assert.Contains(slot.Descendants("path"), node =>
            node.Attribute("onclick")?.Value == $"window.location.href='/build.php?id={slotId}'");
    }

    [Fact]
    public void CapturedResourceField_ExposesSvgClickWhenAnchorCannotBeUsed()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "dorf1_resource_field.xml");
        var document = XDocument.Load(fixture);
        var field = Assert.Single(document.Descendants("a"),
            node => node.Attribute("data-aid")?.Value == "1");

        Assert.Equal("/build.php?id=1", field.Attribute("href")?.Value);
        Assert.Contains(document.Descendants("path"), node =>
            node.Attribute("class")?.Value == "buildingSlot1"
            && node.Attribute("onclick")?.Value == "window.location.href='/build.php?id=1'");
    }

    [Fact]
    public void CapturedWall_HasMultipleLayersForSameSlot()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "dorf2_wall_layers.xml");
        var document = XDocument.Load(fixture);
        Assert.Equal(2, document.Descendants("div")
            .Count(node => node.Attribute("data-aid")?.Value == "40"));
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
