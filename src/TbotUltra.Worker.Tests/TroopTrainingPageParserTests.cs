using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TroopTrainingPageParserTests
{
    [Fact]
    public void ParseTroopUnitBuildInfo_ParsesFullPayload()
    {
        var info = TroopTrainingPageParser.ParseTroopUnitBuildInfo(
            """{"found":true,"canTrain":true,"troopType":"Clubswinger","woodCost":95,"clayCost":75,"ironCost":40,"cropCost":40}""");

        Assert.True(info.Found);
        Assert.True(info.CanTrain);
        Assert.Equal("Clubswinger", info.TroopType);
        Assert.Equal(95, info.WoodCost);
        Assert.Equal(75, info.ClayCost);
        Assert.Equal(40, info.IronCost);
        Assert.Equal(40, info.CropCost);
    }

    [Fact]
    public void ParseTroopUnitBuildInfo_NotFoundPayloadOmitsOtherFields()
    {
        var info = TroopTrainingPageParser.ParseTroopUnitBuildInfo("""{"found":false}""");

        Assert.False(info.Found);
        Assert.False(info.CanTrain);
        Assert.Equal(string.Empty, info.TroopType);
        Assert.Equal(0, info.WoodCost);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void ParseTroopUnitBuildInfo_InvalidPayloadReturnsNotFound(string? rawJson)
    {
        var info = TroopTrainingPageParser.ParseTroopUnitBuildInfo(rawJson);

        Assert.False(info.Found);
        Assert.False(info.CanTrain);
        Assert.Equal(string.Empty, info.TroopType);
    }

    [Fact]
    public void ParseTroopUnitBuildInfo_NullTroopTypeBecomesEmpty()
    {
        var info = TroopTrainingPageParser.ParseTroopUnitBuildInfo(
            """{"found":true,"canTrain":false,"troopType":null}""");

        Assert.True(info.Found);
        Assert.False(info.CanTrain);
        Assert.Equal(string.Empty, info.TroopType);
    }

    [Fact]
    public void ParseTroopTrainingQueue_ParsesRowsAndDropsEmptyText()
    {
        var items = TroopTrainingPageParser.ParseTroopTrainingQueue(
            """[{"text":"Train 5 Clubswinger","timeLeft":"0:12:30"},{"text":"","timeLeft":"0:01:00"},{"text":"Train 2 Spearman","timeLeft":null}]""");

        Assert.Equal(2, items.Count);
        Assert.Equal("Train 5 Clubswinger", items[0].Text);
        Assert.Equal("0:12:30", items[0].TimeLeft);
        Assert.Equal("Train 2 Spearman", items[1].Text);
        Assert.Null(items[1].TimeLeft);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseTroopTrainingQueue_EmptyPayloadReturnsNoItems(string? rawJson)
    {
        Assert.Empty(TroopTrainingPageParser.ParseTroopTrainingQueue(rawJson));
    }

    [Fact]
    public void ParseTroopTrainingQueue_JsonNullReturnsNoItems()
    {
        Assert.Empty(TroopTrainingPageParser.ParseTroopTrainingQueue("null"));
    }
}
