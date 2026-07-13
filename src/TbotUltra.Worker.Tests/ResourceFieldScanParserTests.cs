using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ResourceFieldScanParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ScanStrategies_EmptyInput_ReturnEmptyRows(string? json)
    {
        Assert.Empty(ResourceFieldScanParser.ParseOfficialMap(json));
        Assert.Empty(ResourceFieldScanParser.ParseCompatibilityFallback(json));
    }

    [Fact]
    public void ScanStrategies_PreserveIdenticalRowShape()
    {
        const string json = """
            [{"slotId":7,"fieldType":"crop","name":"Cropland","level":5,"href":"dorf1.php?a=7"}]
            """;

        var official = Assert.Single(ResourceFieldScanParser.ParseOfficialMap(json));
        var compatibility = Assert.Single(ResourceFieldScanParser.ParseCompatibilityFallback(json));

        Assert.Equal(official.SlotId, compatibility.SlotId);
        Assert.Equal(official.FieldType, compatibility.FieldType);
        Assert.Equal(official.Name, compatibility.Name);
        Assert.Equal(official.Level, compatibility.Level);
        Assert.Equal(official.Href, compatibility.Href);
    }

    [Fact]
    public void ScanStrategies_KeepUnknownAndPartialEvidence()
    {
        const string json = """[{"slotId":18,"fieldType":"unknown","level":null}]""";

        var row = Assert.Single(ResourceFieldScanParser.ParseCompatibilityFallback(json));

        Assert.Equal(18, row.SlotId);
        Assert.Equal("unknown", row.FieldType);
        Assert.Null(row.Level);
    }
}
