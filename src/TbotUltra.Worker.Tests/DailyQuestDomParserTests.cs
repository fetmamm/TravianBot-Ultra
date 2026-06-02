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
        var html = File.ReadAllText(Path.Combine(FindRepoRoot(), "temp_build_out", "DOM", fileName));

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

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TbotUltra.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
