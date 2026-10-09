using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BotTaskRunnerVillageSwitchPolicyTests
{
    [Theory]
    [InlineData("send_farmlists", false)]
    [InlineData("construct_building", true)]
    [InlineData("status", true)]
    public void RequiresTargetVillageSwitch_OnlySkipsAccountWideFarmLists(string taskName, bool expected)
    {
        Assert.Equal(expected, BotTaskRunner.RequiresTargetVillageSwitch(taskName));
    }
}
