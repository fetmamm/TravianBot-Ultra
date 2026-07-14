using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TownHallCelebrationSelectorTests
{
    [Fact]
    public void StartLinkSelector_ExcludesGenericResearchAndHeroTransferLinks()
    {
        var selector = TravianClient.TownHallCelebrationStartLinkSelector;

        Assert.DoesNotContain("a.research", selector, StringComparison.Ordinal);
        Assert.DoesNotContain("resource.transfer", selector, StringComparison.Ordinal);
        Assert.Contains("a=1", selector, StringComparison.Ordinal);
    }
}
