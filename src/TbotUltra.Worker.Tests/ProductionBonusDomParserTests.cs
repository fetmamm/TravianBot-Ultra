using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ProductionBonusDomParserTests
{
    [Fact]
    public void FindUnconfirmedActivations_RequiresExactResourceActivation()
    {
        var boxes = new[]
        {
            new ProductionBonusDomParser.ProductionBonusBox("lumber", true, 15, "07:59:53", false, false),
            new ProductionBonusDomParser.ProductionBonusBox("clay", false, 0, "", true, true),
            new ProductionBonusDomParser.ProductionBonusBox("iron", true, 25, "03:52:15", false, false),
            new ProductionBonusDomParser.ProductionBonusBox("crop", true, 0, "", false, false),
        };

        var unconfirmed = ProductionBonusDomParser.FindUnconfirmedActivations(
            new[] { "lumber", "clay", "iron", "crop" },
            boxes);

        Assert.Equal(new[] { "clay", "crop" }, unconfirmed);
    }

    [Fact]
    public void AccountDeletionPending_DetectsOfficialSidebarNotice()
    {
        var html = TestDomFixtures.Read("account_deletion_pending.txt");

        Assert.True(AccountDeletionDomParser.IsPending(html));
        Assert.False(AccountDeletionDomParser.IsPending(html.Replace("infoType_22", "infoType_21")));
    }

    [Theory]
    [InlineData("07:59:53", 28793)]
    [InlineData("03:52:15", 13935)]
    [InlineData("71:04:12", 255852)]
    [InlineData("1:02:03:04", 93784)] // day:hour:min:sec
    [InlineData("5d 15:52:56", 489176)] // Travian long form: "Nd hh:mm:ss"
    [InlineData("6d 10:38:52", 556732)]
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
        Assert.Equal(ProductionBonusNextAttemptKind.RelativeDelay, lumber.NextAttemptKind);
        Assert.Equal(13935 + ProductionBonusDomParser.NextAttemptAfter25BufferSeconds, lumber.RetryAfterSeconds);
        Assert.False(lumber.CanActivate);

        var clay = states.Single(s => s.Resource == "clay");
        Assert.Equal(15, clay.Bonus);
        Assert.Equal(28793, clay.RemainingSeconds);
        Assert.Equal(ProductionBonusNextAttemptKind.DailyReset, clay.NextAttemptKind);

        var iron = states.Single(s => s.Resource == "iron");
        Assert.Equal(0, iron.Bonus);
        Assert.True(iron.CanActivate);

        var crop = states.Single(s => s.Resource == "crop");
        Assert.Equal(0, crop.Bonus);
        Assert.False(crop.CanActivate);
        Assert.Equal(ProductionBonusDomParser.CooldownRetrySeconds, crop.RetryAfterSeconds);
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
        Assert.Equal(ProductionBonusNextAttemptKind.DailyReset, iron.NextAttemptKind);
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
    public void HasCompleteResourceSet_RequiresAllFourUniqueResources()
    {
        var complete = ProductionBonusResources.All
            .Select(resource => new ProductionBonusDomParser.ProductionBonusBox(resource, false, 0, "", true, true))
            .ToList();

        Assert.True(ProductionBonusDomParser.HasCompleteResourceSet(complete));
        Assert.False(ProductionBonusDomParser.HasCompleteResourceSet(complete.Take(3).ToList()));
        Assert.False(ProductionBonusDomParser.HasCompleteResourceSet(complete.Append(complete[0]).ToList()));
    }

    [Fact]
    public void TypedOutcome_ExposesScanActivationDecisionWithoutTextParsing()
    {
        var states = new[]
        {
            new ProductionBonusResourceState(
                "lumber", 0, 0, ProductionBonusNextAttemptKind.Immediate, 0, true),
            new ProductionBonusResourceState(
                "clay", 0, 0, ProductionBonusNextAttemptKind.Immediate, 0, true),
        };

        var outcome = ProductionBonusOutcome.Observed(
            "scan complete",
            states,
            TimeSpan.FromHours(1),
            freeVideoAvailable: true);

        Assert.True(outcome.ShouldActivateAfterScan);
        Assert.Equal(TimeSpan.FromHours(1), outcome.ServerUtcOffset);
    }

    [Fact]
    public void TypedOutcome_CarriesBatchAttemptsAndUnconfirmedResources()
    {
        var outcome = ProductionBonusOutcome.Observed(
            "batch complete",
            [],
            TimeSpan.Zero,
            freeVideoAvailable: true,
            attemptedResources: ["lumber", "clay"],
            unconfirmedResources: ["clay"]);

        Assert.Equal(["lumber", "clay"], outcome.AttemptedResources);
        Assert.Equal(["clay"], outcome.UnconfirmedResources);
    }
}
