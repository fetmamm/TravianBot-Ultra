using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ConstructFasterRoutingTests
{
    [Fact]
    public void BuildPath_ResourceFieldUsesSlotWithoutBuildingGid()
    {
        Assert.Equal("/build.php?id=7", TravianClient.BuildConstructFasterPath(7, gid: null));
    }

    [Fact]
    public void BuildPath_BuildingKeepsGid()
    {
        Assert.Equal("/build.php?id=23&gid=28", TravianClient.BuildConstructFasterPath(23, gid: 28));
    }

    [Theory]
    [InlineData(ConstructionKind.Resource, "/dorf1.php")]
    [InlineData(ConstructionKind.Building, "/dorf2.php")]
    public void VerificationPath_MatchesConstructionKind(ConstructionKind kind, string expected)
    {
        Assert.Equal(expected, TravianClient.ResolveConstructFasterVerificationPath(kind));
    }
}
