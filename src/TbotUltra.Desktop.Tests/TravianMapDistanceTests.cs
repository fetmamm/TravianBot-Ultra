using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TravianMapDistanceTests
{
    [Theory]
    [InlineData("1|2", 1, 2)]
    [InlineData("( -3 | 4 )", -3, 4)]
    [InlineData("[-5|-6]", -5, -6)]
    public void TryParseCoordinates_AcceptsTravianFormats(string value, int expectedX, int expectedY)
    {
        Assert.True(TravianMapDistance.TryParseCoordinates(value, out var x, out var y));
        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
    }

    [Fact]
    public void CalculateRounded_ReturnsStraightLineDistance()
    {
        Assert.Equal(5, TravianMapDistance.CalculateRounded(0, 0, 3, 4));
    }

    [Fact]
    public void CalculateRounded_WrapsAroundWorldEdges()
    {
        Assert.Equal(4.24, TravianMapDistance.CalculateRounded(-199, -199, 199, 199));
    }
}
