using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;
using System.Reflection;

namespace TbotUltra.Worker.Tests;

public sealed class TravianClientHelperTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("1234", 1234)]
    [InlineData("1 234", 1234)]
    [InlineData("12,345", 12345)]
    [InlineData("12.345", 12345)]
    [InlineData("/ 25 000", 25000)]
    [InlineData("abc", null)]
    public void TryParseResourceValue_ParsesDigitsOnly(string? raw, int? expected)
    {
        Assert.Equal(expected, TravianClient.TryParseResourceValue(raw));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("no digits", null)]
    [InlineData("Level 12", 12)]
    [InlineData("1,234", 1234)]
    [InlineData("1 234 567", 1234567)]
    [InlineData("  42  ", 42)]
    public void ParseNumericTextToInt_ExtractsLeadingNumber(string? value, int? expected)
    {
        Assert.Equal(expected, TravianClient.ParseNumericTextToInt(value));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("00:00:30", 30)]
    [InlineData("01:02:03", 3723)]
    [InlineData("00:30", 30)]
    [InlineData("12:34", 12 * 60 + 34)]
    [InlineData("5m 10s", 5 * 60 + 10)]
    [InlineData("90s", 90)]
    [InlineData("2 minutes", 120)]
    [InlineData("garbage", null)]
    public void ParseDurationToSeconds_HandlesCommonFormats(string? raw, int? expected)
    {
        Assert.Equal(expected, TravianClient.ParseDurationToSeconds(raw));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(-5, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(60, "01:00")]
    [InlineData(125, "02:05")]
    [InlineData(3600, "01:00:00")]
    [InlineData(3725, "01:02:05")]
    public void FormatDuration_FormatsAsExpected(int seconds, string expected)
    {
        Assert.Equal(expected, TravianClient.FormatDuration(seconds));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(30, 31)]
    [InlineData(60 * 60 * 12 + 100, 12 * 60 * 60)]
    public void ComputeUpgradeWaitSeconds_ClampsBetweenOneAndTwelveHours(int? input, int expected)
    {
        Assert.Equal(expected, TravianClient.ComputeUpgradeWaitSeconds(input));
    }

    [Theory]
    [InlineData(-10, 1)]
    [InlineData(1, 6)]
    [InlineData(20, 25)]
    public void ComputeBuildingUpgradeSafetyCap_AddsBuffer(int targetLevel, int expected)
    {
        Assert.Equal(expected, TravianClient.ComputeBuildingUpgradeSafetyCap(targetLevel));
    }

    [Theory]
    [InlineData(-10, 10)]
    [InlineData(1, 10)]
    [InlineData(20, 28)]
    public void ComputeResourceUpgradeSafetyCap_AddsBufferWithMinimum(int targetLevel, int expected)
    {
        Assert.Equal(expected, TravianClient.ComputeResourceUpgradeSafetyCap(targetLevel));
    }

    [Fact]
    public void ResolveShortestQueueDurationSeconds_ReturnsMinOrNull()
    {
        IReadOnlyList<BuildQueueItem> empty = [];
        Assert.Null(TravianClient.ResolveShortestQueueDurationSeconds(empty));

        IReadOnlyList<BuildQueueItem> queue =
        [
            new BuildQueueItem("A", "00:05:00"),
            new BuildQueueItem("B", "00:01:30"),
            new BuildQueueItem("C", "garbage"),
        ];
        Assert.Equal(90, TravianClient.ResolveShortestQueueDurationSeconds(queue));

        IReadOnlyList<BuildQueueItem> unparseable =
        [
            new BuildQueueItem("A", null),
            new BuildQueueItem("B", "garbage"),
        ];
        Assert.Null(TravianClient.ResolveShortestQueueDurationSeconds(unparseable));
    }

    [Theory]
    [InlineData(true, 40)]
    [InlineData(false, 10)]
    [InlineData(null, 10)]
    public void ResolveResourceMaxLevelFallback_DependsOnCapital(bool? isCapital, int expected)
    {
        Assert.Equal(expected, TravianClient.ResolveResourceMaxLevelFallback(isCapital));
    }

    [Theory]
    [InlineData(null, 5 * 60)]
    [InlineData(0, 5 * 60)]
    [InlineData(-100, 5 * 60)]
    [InlineData(5, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 31)]
    [InlineData(120, 121)]
    [InlineData(12 * 60 * 60, 12 * 60 * 60 + 1)]
    [InlineData(13 * 60 * 60, 12 * 60 * 60)]
    [InlineData(int.MaxValue, 12 * 60 * 60)]
    public void ClampResourceWaitSeconds_ClampsToBoundsWithFallback(int? input, int expected)
    {
        Assert.Equal(expected, TravianClient.ClampResourceWaitSeconds(input));
    }

    [Fact]
    public void ComputeConstructionSlotStatus_EmptyAllowsBoth()
    {
        var status = TravianClient.ComputeConstructionSlotStatus([], "Romans", travianPlusActive: false);
        Assert.True(status.CanStartResource);
        Assert.True(status.CanStartBuilding);
        Assert.Equal(0, status.ResourceSlotsUsed);
        Assert.Equal(0, status.BuildingSlotsUsed);
        Assert.Null(status.ShortestWaitSeconds);
    }

    [Theory]
    [InlineData("Romans", false, 1, 1)]
    [InlineData("Romans", true, 1, 2)]
    [InlineData("Gauls", false, 1, 1)]
    [InlineData("Teutons", true, 1, 2)]
    public void ComputeConstructionSlotStatus_MaxSlotsByTribeAndPlus(string tribe, bool plus, int resMax, int bldMax)
    {
        var status = TravianClient.ComputeConstructionSlotStatus([], tribe, plus);
        Assert.Equal(resMax, status.ResourceSlotsMax);
        Assert.Equal(bldMax, status.BuildingSlotsMax);
    }

    [Fact]
    public void ComputeConstructionSlotStatus_RomanCanRunResourceAndBuildingInParallel()
    {
        IReadOnlyList<ActiveConstruction> active =
        [
            new ActiveConstruction(ConstructionKind.Resource, "Woodcutter", 5, 120, "0:02:00"),
        ];
        var status = TravianClient.ComputeConstructionSlotStatus(active, "Romans", travianPlusActive: false);
        Assert.False(status.CanStartResource);
        Assert.True(status.CanStartBuilding);
        Assert.Equal(120, status.ShortestWaitSeconds);
    }

    [Fact]
    public void ComputeConstructionSlotStatus_NonRomanShareSingleSlot()
    {
        IReadOnlyList<ActiveConstruction> active =
        [
            new ActiveConstruction(ConstructionKind.Resource, "Clay Pit", 3, 60, "0:01:00"),
        ];
        var status = TravianClient.ComputeConstructionSlotStatus(active, "Gauls", travianPlusActive: false);
        Assert.False(status.CanStartResource);
        Assert.False(status.CanStartBuilding);
    }

    [Fact]
    public void ComputeConstructionSlotStatus_PlusGivesSecondBuildingSlot()
    {
        IReadOnlyList<ActiveConstruction> active =
        [
            new ActiveConstruction(ConstructionKind.Building, "Main Building", 2, 540, "0:09:00"),
        ];
        var status = TravianClient.ComputeConstructionSlotStatus(active, "Teutons", travianPlusActive: true);
        Assert.True(status.CanStartBuilding);
        Assert.Equal(2, status.BuildingSlotsMax);
    }

    [Fact]
    public void ComputeConstructionSlotStatus_ShortestWaitPicksMinimum()
    {
        IReadOnlyList<ActiveConstruction> active =
        [
            new ActiveConstruction(ConstructionKind.Resource, "Iron Mine", 4, 300, null),
            new ActiveConstruction(ConstructionKind.Building, "Warehouse", 1, 75, null),
        ];
        var status = TravianClient.ComputeConstructionSlotStatus(active, "Romans", travianPlusActive: true);
        Assert.Equal(75, status.ShortestWaitSeconds);
    }

    [Fact]
    public void EnsureBuildingCanBeConstructed_BlocksGreatWarehouseWithoutBuildingPlans()
    {
        var status = new VillageStatus(
            ActiveVillage: "Capital",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: [],
            BuildQueue: []);

        var ex = Assert.Throws<TargetInvocationException>(() => InvokeEnsureBuildingCanBeConstructed(status, 38, "Great Warehouse"));
        Assert.Equal("Great Warehouse requires building plans and is not supported yet.", ex.InnerException?.Message);
    }

    private static void InvokeEnsureBuildingCanBeConstructed(VillageStatus status, int gid, string name)
    {
        var method = typeof(TravianClient).GetMethod("EnsureBuildingCanBeConstructed", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [status, gid, name]);
    }

    [Theory]
    [InlineData("CanUpgrade", "CanUpgrade")]
    [InlineData("BlockedByResources", "BlockedByResources")]
    [InlineData("BlockedByQueue", "BlockedByQueue")]
    [InlineData("BlockedByMaxLevel", "BlockedByMaxLevel")]
    [InlineData("  CanUpgrade  ", "CanUpgrade")]
    [InlineData("Unknown", "BlockedUnknown")]
    [InlineData(null, "BlockedUnknown")]
    [InlineData("", "BlockedUnknown")]
    public void ParseUpgradeOutcome_MapsKnownValues(string? value, string expectedName)
    {
        Assert.Equal(expectedName, TravianClient.ParseUpgradeOutcome(value).ToString());
    }

    [Theory]
    [InlineData("Main Building", "main building")]
    [InlineData("  Main   Building  ", "main building")]
    [InlineData("Granary / Silo", "granary")]
    [InlineData("Silo", "granary")]
    [InlineData("City Wall", "wall")]
    [InlineData("Earth Wall", "wall")]
    [InlineData("Palisade", "wall")]
    [InlineData("Stone Wall", "wall")]
    [InlineData("Makeshift Wall", "wall")]
    [InlineData("Warehouse", "warehouse")]
    public void NormalizeBuildingName_NormalizesAliases(string input, string expected)
    {
        Assert.Equal(expected, TravianClient.NormalizeBuildingName(input));
    }

    [Theory]
    [InlineData("Main Building", "main building", true)]
    [InlineData("Granary / Silo", "Silo", true)]
    [InlineData("City Wall", "Earth Wall", true)]
    [InlineData("Warehouse", "Granary", false)]
    [InlineData("Cropland", "Crop Land", false)]
    public void SameBuildingName_UsesNormalizedComparison(string left, string right, bool expected)
    {
        Assert.Equal(expected, TravianClient.SameBuildingName(left, right));
    }

    [Fact]
    public void BuildingLevelByName_ReturnsHighestMatchingLevel()
    {
        var status = MakeStatusWithBuildings(
            new Building(SlotId: 1, Name: "Warehouse", Level: 5, Url: null),
            new Building(SlotId: 2, Name: "Warehouse", Level: 12, Url: null),
            new Building(SlotId: 3, Name: "Granary / Silo", Level: 7, Url: null));

        Assert.Equal(12, TravianClient.BuildingLevelByName(status, "Warehouse"));
        Assert.Equal(7, TravianClient.BuildingLevelByName(status, "Silo")); // alias
        Assert.Equal(0, TravianClient.BuildingLevelByName(status, "Embassy"));
    }

    [Fact]
    public void BuildQueueFingerprint_StableForSameInputs()
    {
        IReadOnlyList<BuildQueueItem> empty = [];
        Assert.Equal("empty", TravianClient.BuildQueueFingerprint(empty));

        IReadOnlyList<BuildQueueItem> queue =
        [
            new BuildQueueItem("Warehouse Level 5", "00:05:00"),
            new BuildQueueItem("Granary Level 3", null),
        ];
        var first = TravianClient.BuildQueueFingerprint(queue);
        var second = TravianClient.BuildQueueFingerprint(queue);
        Assert.Equal(first, second);
        Assert.Contains("Warehouse Level 5", first);
        Assert.Contains("00:05:00", first);
        Assert.Contains("Granary Level 3", first);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("https://example.com/dorf1.php", null)]
    [InlineData("/build.php?id=15", 15)]
    [InlineData("/build.php?id=39&t=99", 39)]
    [InlineData("https://t.example.com/build.php?id=8&fastUP=0", 8)]
    [InlineData("/build.php?id=abc", null)]
    public void ExtractSlotIdFromUrl_ParsesIdQuery(string? url, int? expected)
    {
        Assert.Equal(expected, TravianClient.ExtractSlotIdFromUrl(url));
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "artifact")]
    [InlineData("   ", "artifact")]
    [InlineData("with/slash", "with_slash")]
    [InlineData("with:colon*and?stars", "with_colon_and_stars")]
    public void SafePathSegment_SanitizesInvalidChars(string input, string expected)
    {
        Assert.Equal(expected, TravianClient.SafePathSegment(input));
    }

    [Fact]
    public void MaxLevelForBuilding_UsesCatalogWhenGidPresent()
    {
        var withGid = new Building(SlotId: 1, Name: "Warehouse", Level: 1, Url: null, Gid: 10);
        var withoutGid = new Building(SlotId: 2, Name: "Unknown", Level: 1, Url: null);

        var fromCatalog = BuildingCatalogService.MaxLevelFor(10);
        Assert.Equal(fromCatalog, TravianClient.MaxLevelForBuilding(withGid));
        Assert.Equal(40, TravianClient.MaxLevelForBuilding(withoutGid));
    }

    [Theory]
    [InlineData("offense", "offence_bonus,fighting_strength,defence_bonus,resources")]
    [InlineData("attack", "offence_bonus,fighting_strength,defence_bonus,resources")]
    [InlineData("OFFENSE,resource", "offence_bonus,resources,fighting_strength,defence_bonus")]
    [InlineData("fight, strength, off", "fighting_strength,offence_bonus,defence_bonus,resources")]
    [InlineData("", "fighting_strength,offence_bonus,defence_bonus,resources")]
    [InlineData("   ", "fighting_strength,offence_bonus,defence_bonus,resources")]
    public void ParseHeroStatPriority_NormalizesAndDedupes(string input, string expected)
    {
        var result = TravianClient.ParseHeroStatPriority(input);
        Assert.Equal(expected, string.Join(",", result));
    }

    [Theory]
    [InlineData("Main", "main")]
    [InlineData("  Main  ", "main")]
    [InlineData("HTTPS://Example.com/", "https://example.com/")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeCacheKeyPart_LowercasesAndTrims(string? input, string expected)
    {
        Assert.Equal(expected, TravianClient.NormalizeCacheKeyPart(input!));
    }

    [Fact]
    public void BuildCapitalCacheKey_StableAcrossCasingAndWhitespace()
    {
        var a = TravianClient.BuildCapitalCacheKey("Main", "https://example.com/", "Capital");
        var b = TravianClient.BuildCapitalCacheKey("  main ", "HTTPS://example.com/", "CAPITAL");
        Assert.Equal(a, b);
        Assert.Contains("|", a);
    }

    private static VillageStatus MakeStatusWithBuildings(params Building[] buildings) =>
        new(
            ActiveVillage: "Test",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: buildings,
            BuildQueue: []);
}
