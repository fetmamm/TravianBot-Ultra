using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class VillageGroupExpansionSourceTests
{
    [Fact]
    public void SidebarVillageReads_ExpandCollapsedGroupsBeforeInspectingVillageRows()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var listSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Core",
            "TravianClient.Villages.List.cs"));
        var switchSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Core",
            "TravianClient.Villages.Switch.cs"));

        var listRead = SliceMethod(
            listSource,
            "private async Task<IReadOnlyList<Village>> ReadVillagesFromCurrentPageAsync",
            "private async Task<");
        var nameLookup = SliceMethod(
            switchSource,
            "private async Task<string?> TryGetVillageHrefFromSidebarAsync",
            "private async Task<string?> TryGetVillageHrefFromSidebarByCoordsAsync");

        Assert.Contains("await EnsureVillageGroupsExpandedAsync(cancellationToken);", listRead, StringComparison.Ordinal);
        Assert.Contains("await EnsureVillageGroupsExpandedAsync(cancellationToken);", nameLookup, StringComparison.Ordinal);
        Assert.True(
            listRead.IndexOf("EnsureVillageGroupsExpandedAsync", StringComparison.Ordinal)
            < listRead.IndexOf("EvaluateAsync<SidebarVillageJs[]>", StringComparison.Ordinal));
        Assert.True(
            nameLookup.IndexOf("EnsureVillageGroupsExpandedAsync", StringComparison.Ordinal)
            < nameLookup.IndexOf("EvaluateAsync<string?>", StringComparison.Ordinal));
    }

    [Fact]
    public void VillageGroupExpansion_UsesScopedTrustedClicksAndVerifiesReactState()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Core",
            "TravianClient.Villages.List.cs"));

        Assert.Contains("private async Task EnsureVillageGroupsExpandedAsync", source, StringComparison.Ordinal);
        Assert.Contains("#sidebarBoxVillageList .listEntry.group.collapsed", source, StringComparison.Ordinal);
        Assert.Contains("button.secondaryIconButton.expand", source, StringComparison.Ordinal);
        Assert.Contains("ClickLocatorAsync", source, StringComparison.Ordinal);
        Assert.Contains("WaitForFunctionAsync", source, StringComparison.Ordinal);
        Assert.Contains("[village-list] expanded", source, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string nextMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not locate '{startMarker}'.");
        var end = source.IndexOf(nextMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not locate the end of '{startMarker}'.");
        return source[start..end];
    }
}
