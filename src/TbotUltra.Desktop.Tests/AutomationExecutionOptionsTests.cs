using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationExecutionOptionsTests
{
    [Fact]
    public void WithoutImplicitVillageTarget_ClearsOnlyExecutionTarget()
    {
        var source = new BotOptions
        {
            ServerName = "Test",
            BaseUrl = "https://example.com",
            TargetVillageName = "Viewed village",
            TargetVillageUrl = "dorf1.php?newdid=2",
            HeroMinHpForAdventure = 55,
        };

        var result = AutomationExecutionOptions.WithoutImplicitVillageTarget(source);

        Assert.Empty(result.TargetVillageName);
        Assert.Empty(result.TargetVillageUrl);
        Assert.Equal(source.ServerName, result.ServerName);
        Assert.Equal(source.BaseUrl, result.BaseUrl);
        Assert.Equal(source.HeroMinHpForAdventure, result.HeroMinHpForAdventure);
    }

    [Fact]
    public void QueuePayload_CanApplyExplicitVillageAfterTargetIsCleared()
    {
        var source = new BotOptions
        {
            ServerName = "Test",
            BaseUrl = "https://example.com",
            TargetVillageName = "Viewed village",
            TargetVillageUrl = "dorf1.php?newdid=2",
        };
        var executionOptions = AutomationExecutionOptions.WithoutImplicitVillageTarget(source);

        var result = BotOptionsPayloadApplier.Apply(executionOptions, new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.TargetVillageName] = "Task village",
            [BotOptionPayloadKeys.TargetVillageUrl] = "dorf1.php?newdid=1",
        });

        Assert.Equal("Task village", result.TargetVillageName);
        Assert.Equal("dorf1.php?newdid=1", result.TargetVillageUrl);
    }
}
