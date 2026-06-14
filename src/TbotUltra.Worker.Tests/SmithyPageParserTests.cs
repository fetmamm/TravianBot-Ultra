using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class SmithyPageParserTests
{
    // Mirrors the raw object the Worker eval emits per Smithy `.research` row.
    private static string Row(
        string name,
        string unitClass,
        string buttonOnClick = "",
        string levelText = "Level 0",
        string buttonValue = "",
        string errorText = "",
        bool hasResearchButton = false,
        bool fullyDeveloped = false,
        int? errorWaitSeconds = null)
    {
        string J(string v) => System.Text.Json.JsonSerializer.Serialize(v);
        return "{"
            + $"\"name\":{J(name)},"
            + $"\"unitClass\":{J(unitClass)},"
            + $"\"buttonOnClick\":{J(buttonOnClick)},"
            + $"\"levelText\":{J(levelText)},"
            + $"\"buttonValue\":{J(buttonValue)},"
            + $"\"errorText\":{J(errorText)},"
            + $"\"errorWaitSeconds\":{(errorWaitSeconds.HasValue ? errorWaitSeconds.Value.ToString() : "null")},"
            + $"\"hasResearchButton\":{(hasResearchButton ? "true" : "false")},"
            + $"\"fullyDeveloped\":{(fullyDeveloped ? "true" : "false")}"
            + "}";
    }

    [Fact]
    public void ParseRows_ImproveRow_IsImprovableWithUnitAndTKey()
    {
        var json = "[" + Row(
            name: "Phalanx",
            unitClass: "unit u21",
            buttonOnClick: "this.disabled = true; window.location.href = '/build.php?id=21&gid=13&action=research&t=t1&checksum=de7e5d'; return false;",
            levelText: "Level 0",
            buttonValue: "Improve",
            hasResearchButton: true) + "]";

        var rows = SmithyPageParser.ParseRows(json);

        var row = Assert.Single(rows);
        Assert.Equal("Phalanx", row.Name);
        Assert.Equal(21, row.UnitId);
        Assert.Equal("t1", row.TKey);
        Assert.Equal(0, row.CurrentLevel);
        Assert.True(row.CanImprove);
        Assert.False(row.ResearchInProgress);
        Assert.False(row.NoResources);
        Assert.False(row.Maxed);
        Assert.Equal("u21", row.Key);
    }

    [Fact]
    public void ParseRows_ExchangeResourcesRow_IsNoResources()
    {
        var json = "[" + Row(
            name: "Swordsman",
            unitClass: "unit u22",
            levelText: "Level 0",
            buttonValue: "Exchange resources",
            errorText: "Enough resources on 04.06. at 18:25.") + "]";

        var row = Assert.Single(SmithyPageParser.ParseRows(json));
        Assert.True(row.NoResources);
        Assert.False(row.CanImprove);
    }

    [Fact]
    public void ParseRows_NoResourcesRow_ParsesResourceWaitSeconds()
    {
        var json = "[" + Row(
            name: "Swordsman",
            unitClass: "unit u22",
            levelText: "Level 0",
            buttonValue: "Exchange resources",
            errorText: "Enough resources on 07.06. at 09:16.",
            errorWaitSeconds: 1180) + "]";

        var row = Assert.Single(SmithyPageParser.ParseRows(json));
        Assert.True(row.NoResources);
        Assert.Equal(1180, row.ResourceWaitSeconds);
        Assert.Equal(SmithyTroopOutcome.NoResources, SmithyPageParser.Classify(row, new SmithyTroopTarget("u22", 20)));
    }

    [Fact]
    public void ParseRows_ResearchInProgressRow_IsInProgress()
    {
        var json = "[" + Row(
            name: "Phalanx",
            unitClass: "unit u21",
            levelText: "Level 1",
            errorText: "Research is already being conducted.") + "]";

        var row = Assert.Single(SmithyPageParser.ParseRows(json));
        Assert.True(row.ResearchInProgress);
        Assert.False(row.CanImprove);
    }

    [Fact]
    public void ParseRows_MaxLevelRow_IsMaxed()
    {
        var json = "[" + Row(name: "Phalanx", unitClass: "unit u21", levelText: "Level 20") + "]";
        var row = Assert.Single(SmithyPageParser.ParseRows(json));
        Assert.True(row.Maxed);
        Assert.Equal(20, row.CurrentLevel);
    }

    [Fact]
    public void ParseRows_MalformedJson_ReturnsEmpty()
    {
        Assert.Empty(SmithyPageParser.ParseRows("not json"));
        Assert.Empty(SmithyPageParser.ParseRows(""));
        Assert.Empty(SmithyPageParser.ParseRows(null));
    }

    [Fact]
    public void Classify_Improve_WhenButtonAvailableAndBelowTarget()
    {
        var row = new SmithyTroopRow("Phalanx", 21, "t1", CurrentLevel: 3, CanImprove: true, ResearchInProgress: false, NoResources: false, Maxed: false);
        Assert.Equal(SmithyTroopOutcome.Improve, SmithyPageParser.Classify(row, new SmithyTroopTarget("u21", 20)));
    }

    [Fact]
    public void Classify_AlreadyAtTarget_WhenLevelMeetsTarget()
    {
        var row = new SmithyTroopRow("Phalanx", 21, "t1", CurrentLevel: 10, CanImprove: true, ResearchInProgress: false, NoResources: false, Maxed: false);
        Assert.Equal(SmithyTroopOutcome.AlreadyAtTarget, SmithyPageParser.Classify(row, new SmithyTroopTarget("u21", 10)));
    }

    [Fact]
    public void Classify_Maxed_TakesPriorityOverTarget()
    {
        var row = new SmithyTroopRow("Phalanx", 21, "t1", CurrentLevel: 20, CanImprove: false, ResearchInProgress: false, NoResources: false, Maxed: true);
        Assert.Equal(SmithyTroopOutcome.Maxed, SmithyPageParser.Classify(row, new SmithyTroopTarget("u21", 20)));
    }

    [Fact]
    public void Classify_NotResearched_WhenRowMissing()
    {
        Assert.Equal(SmithyTroopOutcome.NotResearched, SmithyPageParser.Classify(null, new SmithyTroopTarget("u21", 20)));
    }

    [Fact]
    public void FindRowForTarget_MatchesByUnitIdThenTKey()
    {
        var rows = new List<SmithyTroopRow>
        {
            new("Phalanx", 21, "t1", 0, true, false, false, false),
            new("Pathfinder", 23, "t3", 0, true, false, false, false),
        };

        Assert.Equal("Pathfinder", SmithyPageParser.FindRowForTarget(rows, new SmithyTroopTarget("u23", 20))!.Name);
        Assert.Equal("Phalanx", SmithyPageParser.FindRowForTarget(rows, new SmithyTroopTarget("t1", 20))!.Name);
        Assert.Null(SmithyPageParser.FindRowForTarget(rows, new SmithyTroopTarget("u29", 20)));
    }

    [Fact]
    public void Payload_RoundTripsCompactForm()
    {
        var targets = new List<SmithyTroopTarget>
        {
            new("u21", 20, "Phalanx"),
            new("u24", 10, "Theutates Thunder"),
        };

        var serialized = new SmithyUpgradePayload(targets).Serialize();
        Assert.Equal("u21=20;u24=10", serialized);

        var parsed = SmithyUpgradePayload.Parse(serialized);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("u21", parsed[0].Key);
        Assert.Equal(20, parsed[0].TargetLevel);
        Assert.Equal("u24", parsed[1].Key);
        Assert.Equal(10, parsed[1].TargetLevel);
    }

    [Fact]
    public void Payload_Parse_EmptyOrInvalid_ReturnsEmpty()
    {
        Assert.Empty(SmithyUpgradePayload.Parse(null));
        Assert.Empty(SmithyUpgradePayload.Parse(""));
        Assert.Empty(SmithyUpgradePayload.Parse("garbage;=;u21="));
    }

    [Fact]
    public void Payload_Parse_ClampsLevelToValidRange()
    {
        var parsed = SmithyUpgradePayload.Parse("u21=99;u22=0");
        Assert.Equal(20, parsed[0].TargetLevel);
        Assert.Equal(1, parsed[1].TargetLevel);
    }
}
