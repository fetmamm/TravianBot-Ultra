using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TaskRewardDomParserTests
{
    [Theory]
    [InlineData("<div class='newQuestSpeechBubble'></div>")]
    [InlineData("<button id='questmasterButton' class='claimable'></button>")]
    public void HasClaimableTasks_NormalPage_DetectsQuestmasterMarkers(string html)
    {
        Assert.True(TaskRewardDomParser.HasClaimableTasks(html, isTasksPage: false));
    }

    [Fact]
    public void HasClaimableTasks_TasksPage_IgnoresStaleQuestmasterMarkerAfterCollection()
    {
        const string html = """
            <div class="newQuestSpeechBubble"></div>
            <button class="textButtonV2 collect collected" disabled>Collect</button>
            """;

        Assert.False(TaskRewardDomParser.HasClaimableTasks(html, isTasksPage: true));
    }

    [Fact]
    public void HasClaimableTasks_TasksPage_DetectsEnabledCollectButton()
    {
        const string html = "<button class='textButtonV2 collect'>Collect</button>";

        Assert.True(TaskRewardDomParser.HasClaimableTasks(html, isTasksPage: true));
    }
}
