using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class OasisListNamingTests
{
    [Fact]
    public void CreateName_UsesAllTypesName()
    {
        Assert.Equal("Map Oasis", OasisListNaming.CreateName(OasisListNaming.TypeOrder.ToList(), []));
    }

    [Fact]
    public void CreateName_UsesDisplayOrderForSelectedTypes()
    {
        Assert.Equal(
            "Map Oasis_Clay_Iron",
            OasisListNaming.CreateName(["Iron", "Clay"], []));
    }

    [Fact]
    public void CreateName_AppendsNextAvailableSuffixCaseInsensitively()
    {
        var result = OasisListNaming.CreateName(
            ["Wood"],
            ["map oasis_wood", "Map Oasis_Wood 2", "MAP OASIS_WOOD 3"]);

        Assert.Equal("Map Oasis_Wood 4", result);
    }
}
