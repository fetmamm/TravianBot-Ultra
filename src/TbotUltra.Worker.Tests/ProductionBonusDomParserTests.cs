using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ProductionBonusDomParserTests
{
    [Theory]
    [InlineData("07:59:53", 28793)]
    [InlineData("03:52:15", 13935)]
    [InlineData("71:04:12", 255852)]
    [InlineData("1:02:03:04", 93784)] // day:hour:min:sec
    [InlineData("00:00:05", 5)]
    [InlineData("", 0)]
    [InlineData("garbage", 0)]
    public void ParseTimerToSeconds_ParsesClockFormats(string timer, int expected)
    {
        Assert.Equal(expected, ProductionBonusDomParser.ParseTimerToSeconds(timer));
    }

    [Fact]
    public void ParseTimerToSeconds_StripsBidiMarkers()
    {
        // Travian wraps the digits in directional isolates; the parser must ignore them.
        var wrapped = "‭07:59:53‬";
        Assert.Equal(28793, ProductionBonusDomParser.ParseTimerToSeconds(wrapped));
    }

    [Fact]
    public void Classify_MapsBoxesToStates_ForAllFourResources()
    {
        var boxes = new[]
        {
            new ProductionBonusDomParser.ProductionBonusBox("lumber", true, 25, "03:52:15", false, false),
            new ProductionBonusDomParser.ProductionBonusBox("clay", true, 15, "07:59:53", false, false),
            new ProductionBonusDomParser.ProductionBonusBox("iron", false, 0, "", true, true),
            // crop missing entirely -> should classify as none.
        };

        var states = ProductionBonusDomParser.Classify(boxes);

        var lumber = states.Single(s => s.Resource == "lumber");
        Assert.Equal(25, lumber.Bonus);
        Assert.Equal(13935, lumber.RemainingSeconds);
        Assert.Equal(13935 + ProductionBonusDomParser.NextAttemptAfter25BufferSeconds, lumber.NextAttemptSeconds);
        Assert.False(lumber.CanActivate);

        var clay = states.Single(s => s.Resource == "clay");
        Assert.Equal(15, clay.Bonus);
        Assert.Equal(28793, clay.RemainingSeconds);
        Assert.Equal(ProductionBonusDomParser.NextAttemptAfterDailyResetSeconds, clay.NextAttemptSeconds);

        var iron = states.Single(s => s.Resource == "iron");
        Assert.Equal(0, iron.Bonus);
        Assert.True(iron.CanActivate);

        var crop = states.Single(s => s.Resource == "crop");
        Assert.Equal(0, crop.Bonus);
        Assert.False(crop.CanActivate);
        Assert.Equal(ProductionBonusDomParser.CooldownRetrySeconds, crop.NextAttemptSeconds);
    }

    [Fact]
    public void Classify_DisabledPurpleVideo_WaitsForDailyReset()
    {
        var boxes = new[]
        {
            new ProductionBonusDomParser.ProductionBonusBox("iron", false, 0, "", true, false),
        };

        var states = ProductionBonusDomParser.Classify(boxes);

        var iron = states.Single(s => s.Resource == "iron");
        Assert.Equal(0, iron.Bonus);
        Assert.False(iron.CanActivate);
        Assert.Equal(ProductionBonusDomParser.NextAttemptAfterDailyResetSeconds, iron.NextAttemptSeconds);
    }

    [Fact]
    public void ParseBoxesJson_ReadsSerializedShape()
    {
        var json = """
        [
          {"resource":"lumber","active":true,"percent":25,"timer":"03:52:15","purplePresent":false,"purpleEnabled":false},
          {"resource":"iron","active":false,"percent":0,"timer":"","purplePresent":true,"purpleEnabled":true}
        ]
        """;

        var boxes = ProductionBonusDomParser.ParseBoxesJson(json);

        Assert.Equal(2, boxes.Count);
        Assert.True(ProductionBonusDomParser.AnyActivatable(boxes));
        var iron = boxes.Single(b => b.Resource == "iron");
        Assert.True(iron.PurpleEnabled);
    }

    [Fact]
    public void ParseBoxesJson_ReturnsEmpty_OnGarbage()
    {
        Assert.Empty(ProductionBonusDomParser.ParseBoxesJson("not json"));
        Assert.Empty(ProductionBonusDomParser.ParseBoxesJson(null));
    }

    [Fact]
    public void BuildAndParseResultToken_RoundTrips()
    {
        var states = new[]
        {
            new ProductionBonusDomParser.ProductionBonusResourceState("lumber", 25, 13935, 14235, false),
            new ProductionBonusDomParser.ProductionBonusResourceState("clay", 15, 28793, ProductionBonusDomParser.NextAttemptAfterDailyResetSeconds, false),
            new ProductionBonusDomParser.ProductionBonusResourceState("iron", 0, 0, 14400, false),
            new ProductionBonusDomParser.ProductionBonusResourceState("crop", 25, 9942, 10242, false),
        };

        var token = ProductionBonusDomParser.BuildResultToken(states);
        Assert.StartsWith("production_bonus=", token);

        // Embedded in a larger free-text result string, just like the worker emits.
        var parsed = ProductionBonusDomParser.ParseResultToken($"Production bonus: processed 4 resource(s). {token}");

        Assert.Equal(4, parsed.Count);
        Assert.Equal(15, parsed.Single(s => s.Resource == "clay").Bonus);
        Assert.Equal(28793, parsed.Single(s => s.Resource == "clay").RemainingSeconds);
        Assert.Equal(ProductionBonusDomParser.NextAttemptAfterDailyResetSeconds, parsed.Single(s => s.Resource == "clay").NextAttemptSeconds);
        Assert.Equal(0, parsed.Single(s => s.Resource == "iron").Bonus);
    }

    [Fact]
    public void BuildAndParseServerUtcOffsetToken_RoundTrips()
    {
        var token = ProductionBonusDomParser.BuildServerUtcOffsetToken(TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromHours(1), ProductionBonusDomParser.ParseServerUtcOffsetToken($"x {token}"));
        Assert.Null(ProductionBonusDomParser.ParseServerUtcOffsetToken("x"));
    }

    [Fact]
    public void ParseResultToken_ReturnsEmpty_WhenTokenAbsent()
    {
        Assert.Empty(ProductionBonusDomParser.ParseResultToken("Production bonus: nothing happened."));
        Assert.Empty(ProductionBonusDomParser.ParseResultToken(null));
    }
}
