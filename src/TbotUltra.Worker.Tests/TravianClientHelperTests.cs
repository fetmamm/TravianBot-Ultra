using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using TbotUltra.Core.Travian;
using Xunit;
using System.Reflection;

namespace TbotUltra.Worker.Tests;

public sealed class TravianClientHelperTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("1234", 1234L)]
    [InlineData("1 234", 1234L)]
    [InlineData("12,345", 12345L)]
    [InlineData("12.345", 12345L)]
    [InlineData("/ 25 000", 25000L)]
    [InlineData("abc", null)]
    [InlineData("73,600,000,000", 73600000000L)]
    public void TryParseResourceValue_ParsesDigitsOnly(string? raw, long? expected)
    {
        Assert.Equal(expected, TravianParsing.TryParseResourceValue(raw));
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
        Assert.Equal(expected, TravianParsing.ParseNumericTextToInt(value));
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
        Assert.Equal(expected, TravianParsing.ParseDurationToSeconds(raw));
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
        Assert.Equal(expected, TravianParsing.FormatDuration(seconds));
    }

    [Theory]
    [InlineData(null, 30, 10, 0)]
    [InlineData(30, 30, 10, 0)]
    [InlineData(31, 30, 10, 0)]
    [InlineData(20, 30, 10, 10)]
    [InlineData(20, 30, 5, 5)]
    [InlineData(0, 1, 10, 1)]
    [InlineData(99, 150, 10, 1)]
    [InlineData(20, 30, 0, 0)]
    public void CalculateOintmentsToUse_UsesOnlyNeededAmount(int? hp, int minHp, int available, int expected)
    {
        Assert.Equal(expected, HeroCalc.CalculateOintmentsToUse(hp, minHp, available));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(30, 31)]
    [InlineData(60 * 60 * 12 + 100, 12 * 60 * 60)]
    public void ComputeUpgradeWaitSeconds_ClampsBetweenOneAndTwelveHours(int? input, int expected)
    {
        Assert.Equal(expected, UpgradeMath.ComputeUpgradeWaitSeconds(input));
    }

    [Theory]
    [InlineData(-10, 1)]
    [InlineData(1, 6)]
    [InlineData(20, 25)]
    public void ComputeBuildingUpgradeSafetyCap_AddsBuffer(int targetLevel, int expected)
    {
        Assert.Equal(expected, UpgradeMath.ComputeBuildingUpgradeSafetyCap(targetLevel));
    }

    [Theory]
    [InlineData(-10, 10)]
    [InlineData(1, 10)]
    [InlineData(20, 28)]
    public void ComputeResourceUpgradeSafetyCap_AddsBufferWithMinimum(int targetLevel, int expected)
    {
        Assert.Equal(expected, UpgradeMath.ComputeResourceUpgradeSafetyCap(targetLevel));
    }

    [Fact]
    public void ResolveShortestQueueDurationSeconds_ReturnsMinOrNull()
    {
        IReadOnlyList<BuildQueueItem> empty = [];
        Assert.Null(TravianParsing.ResolveShortestQueueDurationSeconds(empty));

        IReadOnlyList<BuildQueueItem> queue =
        [
            new BuildQueueItem("A", "00:05:00"),
            new BuildQueueItem("B", "00:01:30"),
            new BuildQueueItem("C", "garbage"),
        ];
        Assert.Equal(90, TravianParsing.ResolveShortestQueueDurationSeconds(queue));

        IReadOnlyList<BuildQueueItem> unparseable =
        [
            new BuildQueueItem("A", null),
            new BuildQueueItem("B", "garbage"),
        ];
        Assert.Null(TravianParsing.ResolveShortestQueueDurationSeconds(unparseable));
    }

    [Theory]
    // Contaminated/stale switch URLs (extra params from the page they were read on) must reduce to the
    // canonical dorf1.php?newdid=X so the switch never hits the site root (served as the login page).
    [InlineData("?newdid=25471&id=10", "dorf1.php?newdid=25471")]
    [InlineData("dorf1.php?newdid=25471", "dorf1.php?newdid=25471")]
    [InlineData("dorf2.php?newdid=33150", "dorf1.php?newdid=33150")]
    [InlineData("https://ts100.x10.america.travian.com/dorf1.php?newdid=999&extra=1", "dorf1.php?newdid=999")]
    // No newdid: leave the URL untouched (caller falls back to other resolution).
    [InlineData("spieler.php?id=5", "spieler.php?id=5")]
    public void CanonicalizeVillageSwitchUrl_ReducesToNewdidOverview(string input, string expected)
    {
        Assert.Equal(expected, TravianUrls.CanonicalizeVillageSwitchUrl(input));
    }

    [Fact]
    public void BuildQueueIdentityFingerprint_IsStableWhileCountdownTicks()
    {
        // The queue row text embeds a live countdown that changes on every read. The identity
        // fingerprint must ignore it so a click that did nothing (queue full) is not misread as
        // "queue changed" -> false queued=True, which previously spun the upgrade loop.
        IReadOnlyList<BuildQueueItem> before =
        [
            new BuildQueueItem("Cropland Level 10 0:08:12", "0:08:12"),
            new BuildQueueItem("Cropland Level 10 0:11:45", "0:11:45"),
        ];
        IReadOnlyList<BuildQueueItem> after =
        [
            new BuildQueueItem("Cropland Level 10 0:08:07", "0:08:07"),
            new BuildQueueItem("Cropland Level 10 0:11:40", "0:11:40"),
        ];

        Assert.Equal(
            BuildQueueFingerprints.Identity(before),
            BuildQueueFingerprints.Identity(after));
    }

    [Fact]
    public void BuildQueueIdentityFingerprint_ChangesWhenItemsChange()
    {
        IReadOnlyList<BuildQueueItem> twoItems =
        [
            new BuildQueueItem("Cropland Level 10 0:08:12", "0:08:12"),
            new BuildQueueItem("Woodcutter Level 5 0:11:45", "0:11:45"),
        ];
        IReadOnlyList<BuildQueueItem> oneItem =
        [
            new BuildQueueItem("Cropland Level 10 0:08:12", "0:08:12"),
        ];

        Assert.NotEqual(
            BuildQueueFingerprints.Identity(twoItems),
            BuildQueueFingerprints.Identity(oneItem));
    }

    [Fact]
    public void BuildQueueContainsBuilding_IgnoresLevelAndTimer()
    {
        IReadOnlyList<BuildQueueItem> queue =
        [
            new BuildQueueItem("Palace Level 1 0:02:30", "0:02:30"),
        ];

        Assert.True(BuildQueueFingerprints.ContainsBuilding(queue, "Palace"));
    }

    [Fact]
    public void BuildQueueContainsBuilding_DoesNotMatchOtherBuilding()
    {
        IReadOnlyList<BuildQueueItem> queue =
        [
            new BuildQueueItem("Main Building Level 5 0:02:30", "0:02:30"),
        ];

        Assert.False(BuildQueueFingerprints.ContainsBuilding(queue, "Palace"));
    }

    [Fact]
    public void BuildQueueContainsBuilding_DoesNotMatchSuffixBuildingName()
    {
        IReadOnlyList<BuildQueueItem> queue =
        [
            new BuildQueueItem("Great Warehouse Level 5 0:02:30", "0:02:30"),
        ];

        Assert.False(BuildQueueFingerprints.ContainsBuilding(queue, "Warehouse"));
    }

    [Fact]
    public void ResolveTroopTrainingQueueRemainingSeconds_ReturnsLongestOrZero()
    {
        IReadOnlyList<BuildQueueItem> queue =
        [
            new BuildQueueItem("A", "00:05:00"),
            new BuildQueueItem("B", "01:15:00"),
            new BuildQueueItem("C", null),
        ];

        Assert.Equal(4800, TroopTrainingCalculator.ResolveTroopTrainingQueueRemainingSeconds(queue));
        Assert.Equal(0, TroopTrainingCalculator.ResolveTroopTrainingQueueRemainingSeconds([]));
    }

    [Theory]
    [InlineData("no_limit", null)]
    [InlineData("50", 180000)]
    [InlineData("1", 3600)]
    [InlineData("", null)]
    public void TryParseTroopTrainingQueueLimitSeconds_ParsesExpectedValues(string value, int? expected)
    {
        Assert.Equal(expected, TroopTrainingCalculator.TryParseTroopTrainingQueueLimitSeconds(value));
    }

    [Theory]
    [InlineData("maximum", 0, 10, 10)]
    [InlineData("keep_resources", 10, 10, 9)]
    [InlineData("keep_resources", 50, 10, 5)]
    [InlineData("keep_resources", 95, 10, 0)]
    public void CalculateTroopTrainingAmount_RespectsModeAndReserve(string amountMode, int keepPercent, int unitCost, int expected)
    {
        IReadOnlyDictionary<string, long> resources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 100,
            ["clay"] = 100,
            ["iron"] = 100,
            ["crop"] = 100,
        };

        var amount = TroopTrainingCalculator.CalculateTroopTrainingAmount(resources, unitCost, unitCost, unitCost, unitCost, amountMode, keepPercent);
        Assert.Equal(expected, amount);
    }

    [Fact]
    public void CalculateTroopTrainingAmount_HandlesLargeResourceValuesWithoutIntClamp()
    {
        IReadOnlyDictionary<string, long> resources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 41812000000L,
            ["clay"] = 41812000000L,
            ["iron"] = 41812000000L,
            ["crop"] = 76853541651L,
        };

        var amount = TroopTrainingCalculator.CalculateTroopTrainingAmount(resources, 1000, 1000, 1000, 1000, "maximum", 0);
        Assert.Equal(41812000, amount);
    }

    [Theory]
    [InlineData("maximum", "maximum")]
    [InlineData("keep_resources", "keep_resources")]
    [InlineData("KEEP_RESOURCES", "keep_resources")]
    [InlineData("", "maximum")]
    public void NormalizeTroopTrainingAmountMode_NormalizesExpectedValues(string value, string expected)
    {
        Assert.Equal(expected, TroopTrainingCalculator.NormalizeTroopTrainingAmountMode(value));
    }

    [Fact]
    public void CalculateTroopTrainingRequiredResources_RespectsKeepResourcesReserve()
    {
        var required = TroopTrainingCalculator.CalculateTroopTrainingRequiredResources(
            100,
            100,
            100,
            100,
            "keep_resources",
            50,
            1);

        Assert.Equal(199, required["wood"]);
        Assert.Equal(199, required["clay"]);
        Assert.Equal(199, required["iron"]);
        Assert.Equal(199, required["crop"]);
    }

    [Fact]
    public void EstimateTroopTrainingWaitSeconds_UsesLongestFiniteMissingResource()
    {
        IReadOnlyDictionary<string, long> currentResources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 100,
            ["clay"] = 100,
            ["iron"] = 100,
            ["crop"] = 100,
        };
        IReadOnlyDictionary<string, long> requiredResources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 200,
            ["clay"] = 160,
            ["iron"] = 100,
            ["crop"] = 100,
        };
        IReadOnlyDictionary<string, double?> productionByHour = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 100,
            ["clay"] = 120,
            ["iron"] = 0,
            ["crop"] = 0,
        };

        var waitSeconds = TroopTrainingCalculator.EstimateTroopTrainingWaitSeconds(currentResources, requiredResources, productionByHour);
        Assert.Equal(3600, waitSeconds);
    }

    [Fact]
    public void EstimateTroopTrainingWaitSeconds_FallsBackToShortRecheckWhenProductionUnknown()
    {
        IReadOnlyDictionary<string, long> currentResources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 100,
            ["clay"] = 100,
            ["iron"] = 100,
            ["crop"] = 100,
        };
        IReadOnlyDictionary<string, long> requiredResources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 200,
            ["clay"] = 100,
            ["iron"] = 100,
            ["crop"] = 100,
        };
        IReadOnlyDictionary<string, double?> productionByHour = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = null,
            ["clay"] = null,
            ["iron"] = null,
            ["crop"] = null,
        };

        var waitSeconds = TroopTrainingCalculator.EstimateTroopTrainingWaitSeconds(currentResources, requiredResources, productionByHour);
        Assert.Equal(60, waitSeconds);
    }

    [Fact]
    public void MergeTroopTrainingCapacities_UsesStatusThenCacheWhenLiveIsMissing()
    {
        var mergedFromStatus = TroopTrainingCalculator.MergeTroopTrainingCapacities(
            new ResourceCapacitySnapshot(null, null),
            new ResourceCapacitySnapshot(1000, 2000),
            3000,
            4000);

        Assert.Equal(1000, mergedFromStatus.WarehouseCapacity);
        Assert.Equal(2000, mergedFromStatus.GranaryCapacity);

        var mergedFromCache = TroopTrainingCalculator.MergeTroopTrainingCapacities(
            new ResourceCapacitySnapshot(null, null),
            new ResourceCapacitySnapshot(null, null),
            3000,
            4000);

        Assert.Equal(3000, mergedFromCache.WarehouseCapacity);
        Assert.Equal(4000, mergedFromCache.GranaryCapacity);
    }

    [Fact]
    public void MergeTroopTrainingProductionByHour_UsesStatusThenCacheWhenLiveIsMissing()
    {
        IReadOnlyDictionary<string, double?> live = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = null,
            ["clay"] = 10,
            ["iron"] = null,
            ["crop"] = null,
        };
        IReadOnlyDictionary<string, double?> status = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 20,
            ["clay"] = null,
            ["iron"] = null,
            ["crop"] = null,
        };
        IReadOnlyDictionary<string, double?> cached = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 30,
            ["clay"] = 40,
            ["iron"] = 50,
            ["crop"] = 60,
        };

        var merged = TravianClient.MergeTroopTrainingProductionByHour(live, status, cached);

        Assert.Equal(20, merged["wood"]);
        Assert.Equal(10, merged["clay"]);
        Assert.Equal(50, merged["iron"]);
        Assert.Equal(60, merged["crop"]);
    }

    [Theory]
    [InlineData(true, 40)]
    [InlineData(false, 10)]
    [InlineData(null, 10)]
    public void ResolveResourceMaxLevelFallback_DependsOnCapital(bool? isCapital, int expected)
    {
        Assert.Equal(expected, UpgradeMath.ResolveResourceMaxLevelFallback(isCapital));
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
        Assert.Equal(expected, UpgradeMath.ClampResourceWaitSeconds(input));
    }

    [Fact]
    public void ComputeResourceAccumulationWaitSeconds_UsesSlowestMissingResource()
    {
        var production = new Dictionary<string, double?>
        {
            ["wood"] = 600,
            ["clay"] = 300,
            ["iron"] = 1200,
            ["crop"] = 100,
        };

        var waitSeconds = UpgradeMath.ComputeResourceAccumulationWaitSeconds(
            remainingWood: 600,
            remainingClay: 600,
            remainingIron: 0,
            remainingCrop: 50,
            production);

        Assert.Equal(7200, waitSeconds);
    }

    [Fact]
    public void ComputeResourceAccumulationWaitSeconds_FallsBackWhenNeededProductionMissing()
    {
        var production = new Dictionary<string, double?>
        {
            ["wood"] = null,
            ["clay"] = 300,
        };

        var waitSeconds = UpgradeMath.ComputeResourceAccumulationWaitSeconds(
            remainingWood: 10,
            remainingClay: 0,
            remainingIron: 0,
            remainingCrop: 0,
            production,
            fallbackSeconds: 900);

        Assert.Equal(900, waitSeconds);
    }

    [Fact]
    public void ComputeConstructionSlotStatus_EmptyAllowsBoth()
    {
        var status = ConstructionSlots.Compute([], "Romans", travianPlusActive: false);
        Assert.True(status.CanStartResource);
        Assert.True(status.CanStartBuilding);
        Assert.Equal(0, status.ResourceSlotsUsed);
        Assert.Equal(0, status.BuildingSlotsUsed);
        Assert.Null(status.ShortestWaitSeconds);
    }

    [Fact]
    public void ResolveActiveBuildCount_PrefersAuthoritativeActiveConstructions()
    {
        IReadOnlyList<BuildQueueItem> broadQueue =
        [
            new BuildQueueItem("Warehouse level 19", "00:42:41"),
            new BuildQueueItem("Duplicate nested queue markup", "00:42:41"),
        ];
        IReadOnlyList<ActiveConstruction> active =
        [
            new ActiveConstruction(ConstructionKind.Building, "Warehouse", 19, 2561, "done at 23:03"),
        ];

        Assert.Equal(1, ConstructionSlots.ActiveBuildCount(broadQueue, active));
    }

    [Fact]
    public void ResolveActiveBuildCount_FallsBackToBroadQueueWhenActiveListIsEmpty()
    {
        IReadOnlyList<BuildQueueItem> broadQueue =
        [
            new BuildQueueItem("Warehouse level 19", "00:42:41"),
        ];

        Assert.Equal(1, ConstructionSlots.ActiveBuildCount(broadQueue, []));
    }

    [Theory]
    [InlineData("Romans", false, 1, 1)]
    [InlineData("Romans", true, 1, 2)]
    [InlineData("Gauls", false, 1, 1)]
    [InlineData("Teutons", true, 1, 2)]
    public void ComputeConstructionSlotStatus_MaxSlotsByTribeAndPlus(string tribe, bool plus, int resMax, int bldMax)
    {
        var status = ConstructionSlots.Compute([], tribe, plus);
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
        var status = ConstructionSlots.Compute(active, "Romans", travianPlusActive: false);
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
        var status = ConstructionSlots.Compute(active, "Gauls", travianPlusActive: false);
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
        var status = ConstructionSlots.Compute(active, "Teutons", travianPlusActive: true);
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
        var status = ConstructionSlots.Compute(active, "Romans", travianPlusActive: true);
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
        Assert.Equal(expectedName, TravianParsing.ParseUpgradeOutcome(value).ToString());
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
        Assert.Equal(expected, BuildingNames.Normalize(input));
    }

    [Theory]
    [InlineData("Main Building", "main building", true)]
    [InlineData("Granary / Silo", "Silo", true)]
    [InlineData("City Wall", "Earth Wall", true)]
    [InlineData("Warehouse", "Granary", false)]
    [InlineData("Cropland", "Crop Land", false)]
    public void SameBuildingName_UsesNormalizedComparison(string left, string right, bool expected)
    {
        Assert.Equal(expected, BuildingNames.Same(left, right));
    }

    [Fact]
    public void BuildingLevelByName_ReturnsHighestMatchingLevel()
    {
        var status = MakeStatusWithBuildings(
            new Building(SlotId: 1, Name: "Warehouse", Level: 5, Url: null),
            new Building(SlotId: 2, Name: "Warehouse", Level: 12, Url: null),
            new Building(SlotId: 3, Name: "Granary / Silo", Level: 7, Url: null));

        Assert.Equal(12, BuildingNames.LevelByName(status, "Warehouse"));
        Assert.Equal(7, BuildingNames.LevelByName(status, "Silo")); // alias
        Assert.Equal(0, BuildingNames.LevelByName(status, "Embassy"));
    }

    [Fact]
    public void BuildQueueFingerprint_StableForSameInputs()
    {
        IReadOnlyList<BuildQueueItem> empty = [];
        Assert.Equal("empty", BuildQueueFingerprints.Full(empty));

        IReadOnlyList<BuildQueueItem> queue =
        [
            new BuildQueueItem("Warehouse Level 5", "00:05:00"),
            new BuildQueueItem("Granary Level 3", null),
        ];
        var first = BuildQueueFingerprints.Full(queue);
        var second = BuildQueueFingerprints.Full(queue);
        Assert.Equal(first, second);
        Assert.Contains("Warehouse Level 5", first);
        Assert.Contains("00:05:00", first);
        Assert.Contains("Granary Level 3", first);
    }

    [Fact]
    public void BuildQueueIdentityFingerprint_IgnoresTimerChanges()
    {
        IReadOnlyList<BuildQueueItem> firstQueue =
        [
            new BuildQueueItem("Warehouse Level 5", "00:05:00"),
        ];
        IReadOnlyList<BuildQueueItem> secondQueue =
        [
            new BuildQueueItem("Warehouse Level 5", "00:04:59"),
        ];

        Assert.Equal(
            BuildQueueFingerprints.Identity(firstQueue),
            BuildQueueFingerprints.Identity(secondQueue));
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
        Assert.Equal(expected, TravianUrls.ExtractSlotIdFromUrl(url));
    }

    [Fact]
    public void BuildTroopInputSelectors_IncludesOfficialAndLegacyRallyPointNames()
    {
        var selectors = TravianClient.BuildTroopInputSelectors("t2");

        Assert.Contains("input[name='troop[t2]']", selectors);
        Assert.Contains("input[name='troops[0][t2]']", selectors);
        Assert.Contains("input[name$='[t2]']", selectors);
        Assert.Contains("input[name='t2']", selectors);
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "artifact")]
    [InlineData("   ", "artifact")]
    [InlineData("with/slash", "with_slash")]
    [InlineData("with:colon*and?stars", "with_colon_and_stars")]
    public void SafePathSegment_SanitizesInvalidChars(string input, string expected)
    {
        Assert.Equal(expected, TravianUrls.SafePathSegment(input));
    }

    [Fact]
    public void MaxLevelForBuilding_UsesCatalogWhenGidPresent()
    {
        var withGid = new Building(SlotId: 1, Name: "Warehouse", Level: 1, Url: null, Gid: 10);
        var withoutGid = new Building(SlotId: 2, Name: "Unknown", Level: 1, Url: null);

        var fromCatalog = BuildingCatalogService.MaxLevelFor(10);
        Assert.Equal(fromCatalog, BuildingNames.MaxLevelFor(withGid));
        Assert.Equal(40, BuildingNames.MaxLevelFor(withoutGid));
    }

    [Theory]
    [InlineData("offense", "offence_bonus,resources,fighting_strength,defence_bonus")]
    [InlineData("attack", "offence_bonus,resources,fighting_strength,defence_bonus")]
    [InlineData("OFFENSE,resource", "offence_bonus,resources,fighting_strength,defence_bonus")]
    [InlineData("fight, strength, off", "fighting_strength,offence_bonus,resources,defence_bonus")]
    [InlineData("", "resources,fighting_strength,offence_bonus,defence_bonus")]
    [InlineData("   ", "resources,fighting_strength,offence_bonus,defence_bonus")]
    public void ParseHeroStatPriority_NormalizesAndDedupes(string input, string expected)
    {
        var result = HeroCalc.ParseHeroStatPriority(input);
        Assert.Equal(expected, string.Join(",", result));
    }

    [Theory]
    [InlineData("resources,fighting_strength,offence_bonus,defence_bonus", "productionPoints,power,offBonus,defBonus")]
    [InlineData("fighting_strength,resources", "power,productionPoints,offBonus,defBonus")]
    [InlineData("", "productionPoints,power,offBonus,defBonus")]
    public void MapHeroStatPriorityToOfficialFields_PreservesPriority(string input, string expected)
    {
        var result = HeroCalc.MapHeroStatPriorityToOfficialFields(input);
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
        Assert.Equal(expected, CapitalCacheKey.NormalizePart(input!));
    }

    [Fact]
    public void BuildCapitalCacheKey_StableAcrossCasingAndWhitespace()
    {
        var a = CapitalCacheKey.Build("Main", "https://example.com/", "Capital");
        var b = CapitalCacheKey.Build("  main ", "HTTPS://example.com/", "CAPITAL");
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
