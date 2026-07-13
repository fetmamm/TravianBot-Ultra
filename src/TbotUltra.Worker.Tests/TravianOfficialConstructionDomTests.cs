using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TravianOfficialConstructionDomTests
{
    [Fact]
    public void OfficialResourceUpgradeDom_SelectsPrimaryUpgradeButtonAndReadsCostAndDuration()
    {
        var html = ReadDomFixture("upgrade_resourcefield.txt");

        var button = BuildingDomParser.SelectUpgradeButtonCandidateFromHtmlForTests(html, nextLevel: 1);
        var cost = BuildingDomParser.ReadConstructionCostFromHtmlForTests(html);
        var duration = BuildingDomParser.ReadPrimaryBuildDurationSecondsFromHtmlForTests(html);

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

        var button = BuildingDomParser.SelectUpgradeButtonCandidateFromHtmlForTests(html, nextLevel: 2);
        var cost = BuildingDomParser.ReadConstructionCostFromHtmlForTests(html);
        var duration = BuildingDomParser.ReadPrimaryBuildDurationSecondsFromHtmlForTests(html);

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

        var cranny = BuildingDomParser.SelectConstructButtonCandidateFromHtmlForTests(html, gid: 23);
        var warehouse = BuildingDomParser.SelectConstructButtonCandidateFromHtmlForTests(html, gid: 10);

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
    public void OfficialConstructDom_DoesNotTreatRequirementTextAsConstructButton()
    {
        const string html = """
            <div id="contract_building26" data-gid="26">
              <div class="upgradeButtonsContainer">
                <div class="section1">
                  <span class="buildingCondition error">Main Building Level 5</span>
                </div>
              </div>
            </div>
            """;

        Assert.Null(BuildingDomParser.SelectConstructButtonCandidateFromHtmlForTests(html, gid: 26));
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

    [Fact]
    public void BuildingOverviewParser_EmptyDom_ReturnsNoBuildings()
    {
        Assert.Empty(TravianClient.ParseBuildingOverviewHtmlForTests("<main></main>"));
    }

    [Fact]
    public void BuildingOverviewParser_PartialDom_KeepsKnownSlotEvidence()
    {
        const string html = """
            <div class="buildingSlot aid26 g15" data-name="Main Building" data-level="3">
              <a href="build.php?id=26&amp;gid=15"><div class="labelLayer">3</div></a>
            </div>
            """;

        var building = Assert.Single(TravianClient.ParseBuildingOverviewHtmlForTests(html));

        Assert.Equal(26, building.SlotId);
        Assert.Equal(15, building.Gid);
        Assert.Equal("Main Building", building.Name);
        Assert.Equal(3, building.Level);
    }

    [Fact]
    public void BuildingOverviewParser_MalformedSlot_DoesNotDiscardValidSlots()
    {
        const string html = """
            <div class="buildingSlot aidXX g15"><a href="build.php?id=broken">broken</a></div>
            <div class="buildingSlot aid31 g19" data-level="2">
              <a href="build.php?id=31&amp;gid=19"><div class="labelLayer">2</div></a>
            </div>
            """;

        var building = Assert.Single(TravianClient.ParseBuildingOverviewHtmlForTests(html));

        Assert.Equal(31, building.SlotId);
        Assert.Equal(19, building.Gid);
        Assert.Equal(2, building.Level);
    }

    [Theory]
    [InlineData("Main Building Level 5", "Main Building", 5)]
    [InlineData("Warehouse lvl 12", "Warehouse", 12)]
    public void OfficialBuildPageTitle_ParsesNameAndLevel(string title, string expectedName, int expectedLevel)
    {
        var parsed = BuildingDomParser.ParseBuildPageTitle(title);

        Assert.Equal(expectedName, parsed.Name);
        Assert.Equal(expectedLevel, parsed.Level);
    }

    [Fact]
    public void OfficialUpgradeDom_ExcludesGreenOpenShopPaymentDecoy_AndKeepsRealUpgradeButton()
    {
        // Regression: on a resource-blocked field the only green control can be the payment-wizard
        // "Open shop" button. It previously matched the bare 'green' signal, got clicked, opened a
        // modal whose #dialogOverlay then blocked every later click → upgrade-click timeout loop.
        // The exact decoy markup is taken from the failing session's Playwright call log (slot 10).
        const string html = """
            <div class="upgradeButtonsContainer "><div class="section1">
              <button type="button" value="Upgrade to level 7" class="textButtonV1 green build" onclick="this.disabled=true; window.location.href='/dorf1.php?a=10&amp;c=33bd4d'; return false;">Upgrade to level 7</button>
            </div></div>
            <button type="button" value="Open shop" version="textButtonV1" class="textButtonV1 green " onclick="Travian.React.openPaymentWizard({activeTab: 'advantages'}); return false;">Open shop</button>
            """;

        var candidates = BuildingDomParser.ExtractButtonCandidatesFromHtmlForTests(html);
        var openShop = candidates.Single(candidate => candidate.Text.Contains("Open shop", StringComparison.OrdinalIgnoreCase));
        var selected = BuildingDomParser.SelectUpgradeButtonCandidateFromHtmlForTests(html, nextLevel: 7);

        Assert.True(openShop.IsGold, $"Open shop payment decoy must be excluded. {DescribeCandidates(html)}");
        Assert.True(selected is not null, DescribeCandidates(html));
        Assert.Equal("Upgrade to level 7", selected.Text);
    }

    [Fact]
    public void OfficialQueuedResourceDom_UsesButtonLevelWhenHeaderLevelIsStale()
    {
        const string level4Html = """
            <h1 class="titleInHeader">Cropland <span class="level">Level 3</span></h1>
            <div id="build" class="gid4 level3">
              <div class="inlineIconList">
                <i class="r1Big"></i><span class="value">325</span>
                <i class="r2Big"></i><span class="value">420</span>
                <i class="r3Big"></i><span class="value">325</span>
                <i class="r4Big"></i><span class="value">95</span>
              </div>
              <div class="upgradeButtonsContainer section2Enabled">
                <div class="section1">
                  <button type="button" value="Upgrade to level 4" class="textButtonV1 green build"
                          onclick="window.location.href='/dorf1.php?id=15&amp;gid=4&amp;action=build&amp;checksum=c92d6a'; return false;">
                    Upgrade to level 4
                  </button>
                </div>
              </div>
            </div>
            """;
        const string level5Html = """
            <h1 class="titleInHeader">Cropland <span class="level">Level 3</span></h1>
            <div id="build" class="gid4 level3">
              <div class="inlineIconList">
                <i class="r1Big"></i><span class="value">545</span>
                <i class="r2Big"></i><span class="value">700</span>
                <i class="r3Big"></i><span class="value">545</span>
                <i class="r4Big"></i><span class="value">155</span>
              </div>
              <div class="upgradeButtonsContainer section2Enabled">
                <div class="section1">
                  <button type="button" value="Upgrade to level 5" class="textButtonV1 green build"
                          onclick="window.location.href='/dorf1.php?id=15&amp;gid=4&amp;action=build&amp;checksum=c92d6a'; return false;">
                    Upgrade to level 5
                  </button>
                </div>
              </div>
            </div>
            """;

        var title4 = BuildingDomParser.ParseBuildPageTitle("Cropland Level 3");
        var title5 = BuildingDomParser.ParseBuildPageTitle("Cropland Level 3");
        var button4 = BuildingDomParser.SelectUpgradeButtonCandidateFromHtmlForTests(level4Html, nextLevel: 4);
        var button5 = BuildingDomParser.SelectUpgradeButtonCandidateFromHtmlForTests(level5Html, nextLevel: 5);

        Assert.Equal(3, title4.Level);
        Assert.Equal(3, title5.Level);
        Assert.NotNull(button4);
        Assert.NotNull(button5);
        Assert.Equal("Upgrade to level 4", button4.Text);
        Assert.Equal("Upgrade to level 5", button5.Text);
        Assert.Contains("id=15", button4.OnClick, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=15", button5.OnClick, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(420, BuildingDomParser.ReadConstructionCostFromHtmlForTests(level4Html)["clay"]);
        Assert.Equal(700, BuildingDomParser.ReadConstructionCostFromHtmlForTests(level5Html)["clay"]);
    }

    [Fact]
    public void ResourceHeroTransferOfferKey_DistinguishesSameSlotDifferentQueuedLevels()
    {
        var level4 = TravianClient.BuildConstructionHeroTransferOfferKeyForTests(15, 4, 325, 420, 325, 95);
        var level5 = TravianClient.BuildConstructionHeroTransferOfferKeyForTests(15, 5, 545, 700, 545, 155);
        var unknownLevel4 = TravianClient.BuildConstructionHeroTransferOfferKeyForTests(15, null, 325, 420, 325, 95);
        var unknownLevel5 = TravianClient.BuildConstructionHeroTransferOfferKeyForTests(15, null, 545, 700, 545, 155);

        Assert.NotEqual(level4, level5);
        Assert.NotEqual(unknownLevel4, unknownLevel5);
    }

    [Fact]
    public void OfficialQueuedBuildingDom_UsesButtonLevelWhenHeaderLevelIsStale()
    {
        const string level6Html = """
            <h1 class="titleInHeader">Main Building <span class="level">Level 5</span></h1>
            <div id="build" class="gid15 level5">
              <div class="inlineIconList">
                <i class="r1Big"></i><span class="value">240</span>
                <i class="r2Big"></i><span class="value">135</span>
                <i class="r3Big"></i><span class="value">205</span>
                <i class="r4Big"></i><span class="value">70</span>
              </div>
              <div class="upgradeButtonsContainer section2Enabled">
                <div class="section1">
                  <button type="button" value="Upgrade to level 6" class="textButtonV1 green build"
                          onclick="window.location.href='/dorf2.php?id=26&amp;gid=15&amp;action=build&amp;checksum=6321f2'; return false;">
                    Upgrade to level 6
                  </button>
                </div>
              </div>
            </div>
            """;
        const string level7Html = """
            <h1 class="titleInHeader">Main Building <span class="level">Level 5</span></h1>
            <div id="build" class="gid15 level5">
              <div class="inlineIconList">
                <i class="r1Big"></i><span class="value">310</span>
                <i class="r2Big"></i><span class="value">175</span>
                <i class="r3Big"></i><span class="value">265</span>
                <i class="r4Big"></i><span class="value">90</span>
              </div>
              <div class="upgradeButtonsContainer section2Enabled">
                <div class="section1">
                  <button type="button" value="Upgrade to level 7" class="textButtonV1 green build"
                          onclick="window.location.href='/dorf2.php?id=26&amp;gid=15&amp;action=build&amp;checksum=6321f2'; return false;">
                    Upgrade to level 7
                  </button>
                </div>
              </div>
            </div>
            """;

        var title6 = BuildingDomParser.ParseBuildPageTitle("Main Building Level 5");
        var title7 = BuildingDomParser.ParseBuildPageTitle("Main Building Level 5");
        var button6 = BuildingDomParser.SelectUpgradeButtonCandidateFromHtmlForTests(level6Html, nextLevel: 6);
        var button7 = BuildingDomParser.SelectUpgradeButtonCandidateFromHtmlForTests(level7Html, nextLevel: 7);
        var key6 = TravianClient.BuildConstructionHeroTransferOfferKeyForTests(26, 6, 240, 135, 205, 70);
        var key7 = TravianClient.BuildConstructionHeroTransferOfferKeyForTests(26, 7, 310, 175, 265, 90);

        Assert.Equal(5, title6.Level);
        Assert.Equal(5, title7.Level);
        Assert.NotNull(button6);
        Assert.NotNull(button7);
        Assert.Equal("Upgrade to level 6", button6.Text);
        Assert.Equal("Upgrade to level 7", button7.Text);
        Assert.Contains("id=26", button6.OnClick, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=26", button7.OnClick, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(135, BuildingDomParser.ReadConstructionCostFromHtmlForTests(level6Html)["clay"]);
        Assert.Equal(175, BuildingDomParser.ReadConstructionCostFromHtmlForTests(level7Html)["clay"]);
        Assert.NotEqual(key6, key7);
    }

    [Theory]
    [InlineData("buildingpage_infrastructure.txt")]
    [InlineData("buildingpage_military.txt")]
    [InlineData("buildingpage_resources.txt")]
    [InlineData("construct_new_building_infrastructure.txt")]
    public void OfficialConstructChoiceDom_IsDetectedAsEmptySlot(string fixture)
    {
        // Regression: the construct-choice page wraps every building in its own .upgradeButtonsContainer,
        // so the old heuristic wrongly saw an "upgrade affordance" and fell through to the upgrade scanner,
        // which then matched a "Construct building" button as a false CanUpgrade (slot 21 in the session log).
        var html = ReadDomFixture(fixture);

        Assert.True(BuildingDomParser.IsEmptyConstructionSlotHtmlForTests(html), DescribeCandidates(html));
        // No genuine "Upgrade to level N" button must be selectable on a construct-choice page.
        Assert.Null(BuildingDomParser.SelectUpgradeButtonCandidateFromHtmlForTests(html, nextLevel: 1));
    }

    [Theory]
    [InlineData("upgrade_building.txt")]
    [InlineData("upgrade_resourcefield.txt")]
    public void OfficialUpgradeDom_IsNotDetectedAsEmptySlot(string fixture)
    {
        var html = ReadDomFixture(fixture);

        Assert.False(BuildingDomParser.IsEmptyConstructionSlotHtmlForTests(html), DescribeCandidates(html));
    }

    [Fact]
    public void OfficialConstructDom_ReadsMissingRequirementsForUnbuildableBuilding()
    {
        // Regression (P2b): an unbuildable building (no 'Construct building' button) should yield a clear
        // "Missing requirements" message from span.buildingCondition.error instead of "could not find button".
        var html = ReadDomFixture("buildingpage_resources.txt");

        // Sawmill (gid 5) requires Woodcutter 10 + Main Building 5 → not buildable on this fixture.
        var requirements = BuildingDomParser.ReadConstructRequirementErrorFromHtmlForTests(html, gid: 5);

        Assert.False(string.IsNullOrWhiteSpace(requirements));
        Assert.Contains("Woodcutter", requirements, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Main Building", requirements, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Level 10", requirements, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OfficialConstructDom_ReturnsNullRequirementsForBuildableBuilding()
    {
        // Warehouse (gid 10) is buildable on this fixture (has a 'Construct building' button) → no error text.
        var html = ReadDomFixture("buildingpage_infrastructure.txt");

        Assert.Null(BuildingDomParser.ReadConstructRequirementErrorFromHtmlForTests(html, gid: 10));
    }

    [Fact]
    public void OfficialConstructDom_DoesNotSelectOtherButtonForSoonAvailableBuilding()
    {
        var html = ReadDomFixture("construct_stable_notavailable.txt");

        var requirements = BuildingDomParser.ReadConstructRequirementErrorFromHtmlForTests(html, gid: 20);

        Assert.Contains("Academy Level 5", requirements, StringComparison.OrdinalIgnoreCase);
        Assert.Null(BuildingDomParser.SelectConstructButtonCandidateFromHtmlForTests(html, gid: 20));
        Assert.NotNull(BuildingDomParser.SelectConstructButtonCandidateFromHtmlForTests(html, gid: 37));
    }

    private static string ReadDomFixture(string fileName)
    {
        return TestDomFixtures.Read(fileName);
    }

    private static string DescribeCandidates(string html)
    {
        return string.Join(
            Environment.NewLine,
            BuildingDomParser.ExtractButtonCandidatesFromHtmlForTests(html).Select(candidate =>
                $"{candidate.Text}|class={candidate.Classes}|wrapper={candidate.WrapperGid}|disabled={candidate.Disabled}|gold={candidate.IsGold}|speedup={candidate.IsSpeedup}|primary={candidate.InOfficialPrimarySection}|onclick={candidate.OnClick}"));
    }
}
