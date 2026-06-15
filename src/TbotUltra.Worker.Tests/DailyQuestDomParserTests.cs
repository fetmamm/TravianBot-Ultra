using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class DailyQuestDomParserTests
{
    [Theory]
    [InlineData("daily_quests.txt")]
    [InlineData("daily_quests_1.txt")]
    public void HasClaimableDailyQuests_DetectsTopbarIndicator_InSavedDom(string fileName)
    {
        var html = TestDomFixtures.Read(fileName);

        Assert.True(DailyQuestDomParser.HasClaimableDailyQuests(html));
    }

    [Fact]
    public void HasClaimableDailyQuests_ReturnsFalse_WhenDailyQuestIndicatorIsMissing()
    {
        const string html = """
            <a class="dailyQuests" href="#" accesskey="7">
                <div class="inlineIcon">Daily quests</div>
            </a>
            """;

        Assert.False(DailyQuestDomParser.HasClaimableDailyQuests(html));
    }
}
