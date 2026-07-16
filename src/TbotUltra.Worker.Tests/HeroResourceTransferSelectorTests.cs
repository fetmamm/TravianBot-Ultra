using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroResourceTransferSelectorTests
{
    [Theory]
    [InlineData(1, "contract_building1")]
    [InlineData(17, "contract_building17")]
    [InlineData(23, "contract_building23")]
    [InlineData(46, "contract_building46")]
    public void ConstructScopeId_TargetsExactBuildingRowAcrossCategories(int gid, string expected)
    {
        Assert.Equal(expected, TravianClient.BuildHeroTransferConstructScopeId(gid));
    }

    [Fact]
    public void ConstructScopeId_RejectsMissingOrInvalidGid()
    {
        Assert.Null(TravianClient.BuildHeroTransferConstructScopeId(null));
        Assert.Null(TravianClient.BuildHeroTransferConstructScopeId(0));
    }
}
