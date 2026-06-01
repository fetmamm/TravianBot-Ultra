using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TravianOfficialConstructionDomTests
{
    [Fact]
    public void OfficialResourceUpgradeDom_SelectsPrimaryUpgradeButtonAndReadsCostAndDuration()
    {
        var html = ReadDomFixture("upgrade_resourcefield.txt");

        var button = TravianClient.SelectUpgradeButtonCandidateFromHtmlForTests(html, nextLevel: 1);
        var cost = TravianClient.ReadConstructionCostFromHtmlForTests(html);
        var duration = TravianClient.ReadPrimaryBuildDurationSecondsFromHtmlForTests(html);

        Assert.True(button is not null, DescribeCandidates(html));
        Assert.Equal("Upgrade to level 1", button.Text);
        Assert.Contains("green", button.Classes);
        Assert.True(button.InOfficialPrimarySection);
        Assert.False(button.IsSpeedup);
        Assert.Equal(40, cost["wood"]);
        Assert.Equal(100, cost["clay"]);
        Assert.Equal(50, cost["iron"]);
        Assert.Equal(60, cost["crop"]);
        Assert.Equal(50, duration);
    }

    [Fact]
    public void OfficialBuildingUpgradeDom_SelectsPrimaryUpgradeButtonAndSkipsVideoButton()
    {
        var html = ReadDomFixture("upgrade_building.txt");

        var button = TravianClient.SelectUpgradeButtonCandidateFromHtmlForTests(html, nextLevel: 2);
        var cost = TravianClient.ReadConstructionCostFromHtmlForTests(html);
        var duration = TravianClient.ReadPrimaryBuildDurationSecondsFromHtmlForTests(html);

        Assert.True(button is not null, DescribeCandidates(html));
        Assert.Equal("Upgrade to level 2", button.Text);
        Assert.Contains("green", button.Classes);
        Assert.DoesNotContain("purple", button.Classes, StringComparison.OrdinalIgnoreCase);
        Assert.False(button.IsSpeedup);
        Assert.Equal(90, cost["wood"]);
        Assert.Equal(50, cost["clay"]);
        Assert.Equal(75, cost["iron"]);
        Assert.Equal(25, cost["crop"]);
        Assert.Equal(520, duration);
    }

    [Fact]
    public void OfficialConstructDom_SelectsConstructButtonScopedToRequestedGid()
    {
        var html = ReadDomFixture("construct_new_building_infrastructure.txt");

        var cranny = TravianClient.SelectConstructButtonCandidateFromHtmlForTests(html, gid: 23);
        var warehouse = TravianClient.SelectConstructButtonCandidateFromHtmlForTests(html, gid: 10);

        Assert.True(cranny is not null, DescribeCandidates(html));
        Assert.Equal("23", cranny.WrapperGid);
        Assert.Equal("Construct building", cranny.Text);
        Assert.True(cranny.InOfficialPrimarySection);
        Assert.False(cranny.IsSpeedup);

        Assert.NotNull(warehouse);
        Assert.Equal("10", warehouse.WrapperGid);
        Assert.Equal("Construct building", warehouse.Text);
        Assert.True(warehouse.InOfficialPrimarySection);
        Assert.False(warehouse.IsSpeedup);
    }

    [Fact]
    public void OfficialDorf2Dom_ParsesOfficialDataAttributes()
    {
        var html = ReadDomFixture("TS50_Village - Buildings.txt");

        var buildings = TravianClient.ParseBuildingOverviewHtmlForTests(html);

        Assert.Contains(buildings, item =>
            item.SlotId == 26
            && item.Gid == 15
            && item.Name == "Main Building"
            && item.Level == 3);
        Assert.Contains(buildings, item =>
            item.SlotId == 31
            && item.Gid == 19
            && item.Name == "Barracks"
            && item.Level == 2);
    }

    private static string ReadDomFixture(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "temp_build_out", "DOM", fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find DOM fixture '{fileName}'.");
    }

    private static string DescribeCandidates(string html)
    {
        return string.Join(
            Environment.NewLine,
            TravianClient.ExtractButtonCandidatesFromHtmlForTests(html).Select(candidate =>
                $"{candidate.Text}|class={candidate.Classes}|wrapper={candidate.WrapperGid}|disabled={candidate.Disabled}|gold={candidate.IsGold}|speedup={candidate.IsSpeedup}|primary={candidate.InOfficialPrimarySection}|onclick={candidate.OnClick}"));
    }
}
