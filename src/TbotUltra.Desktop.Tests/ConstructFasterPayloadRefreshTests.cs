using System.Collections.Generic;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructFasterPayloadRefreshTests
{
    [Fact]
    public void Apply_RefreshesExistingQueuePayloadFromCurrentSettings()
    {
        var payload = new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.ConstructFasterEnabled] = "false",
            [BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled] = "true",
            [BotOptionPayloadKeys.ConstructFasterMinBuildMinutes] = "30",
            [BotOptionPayloadKeys.ConstructFasterRandomEnabled] = "false",
            [BotOptionPayloadKeys.ConstructFasterRandomChancePercent] = "50",
        };
        var options = new BotOptions
        {
            ConstructFasterMinBuildTimeEnabled = false,
            ConstructFasterMinBuildMinutes = 45,
            ConstructFasterRandomEnabled = true,
            ConstructFasterRandomChancePercent = 80,
        };

        ConstructFasterPayloadRefresh.Apply(
            payload,
            options,
            villageKey: "xy:1|2",
            villageName: "940",
            isConstructFasterEnabledByKey: (key, _) => key == "xy:1|2");

        Assert.Equal("true", payload[BotOptionPayloadKeys.ConstructFasterEnabled]);
        Assert.Equal("false", payload[BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled]);
        Assert.Equal("45", payload[BotOptionPayloadKeys.ConstructFasterMinBuildMinutes]);
        Assert.Equal("true", payload[BotOptionPayloadKeys.ConstructFasterRandomEnabled]);
        Assert.Equal("80", payload[BotOptionPayloadKeys.ConstructFasterRandomChancePercent]);
    }

    [Fact]
    public void Apply_FallsBackToVillageNameWhenKeyIsUnknown()
    {
        var payload = new Dictionary<string, string>();
        var options = new BotOptions();

        ConstructFasterPayloadRefresh.Apply(
            payload,
            options,
            villageKey: "did:10",
            villageName: "940",
            isConstructFasterEnabledByKey: (key, _) => key == "name:940");

        Assert.Equal("true", payload[BotOptionPayloadKeys.ConstructFasterEnabled]);
    }
}
