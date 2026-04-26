using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

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
    [InlineData("offense", "off")]
    [InlineData("attack", "off")]
    [InlineData("OFFENSE,resource", "off,resource")]
    [InlineData("regen, regeneration, off", "regen,off")]
    [InlineData("", "offense,resource,regeneration")]
    [InlineData("   ", "offense,resource,regeneration")]
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
