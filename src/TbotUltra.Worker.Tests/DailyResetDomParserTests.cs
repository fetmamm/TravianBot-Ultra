using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class DailyResetDomParserTests
{
    [Theory]
    [InlineData("(Next reset at 13:00. Make sure to collect your reward before!)", 13)]
    [InlineData("<div>(Next reset at 09:00. Make sure to collect your reward before!)</div>", 9)]
    [InlineData("Next reset at 0:00.", 0)]
    [InlineData("Next reset at 23:59.", 23)]
    public void TryParseResetHourFromDialogHtml_ReadsHour(string html, int expected)
    {
        Assert.Equal(expected, DailyResetDomParser.TryParseResetHourFromDialogHtml(html));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<div>no reset line here</div>")]
    [InlineData("Next reset at 25:00.")]
    public void TryParseResetHourFromDialogHtml_ReturnsNull_WhenAbsentOrInvalid(string? html)
    {
        Assert.Null(DailyResetDomParser.TryParseResetHourFromDialogHtml(html));
    }

    [Fact]
    public void Token_RoundTrips()
    {
        var token = DailyResetDomParser.BuildResetHourToken(13);

        Assert.Equal("daily_reset_hour=13", token);
        Assert.Equal(13, DailyResetDomParser.TryParseResetHourToken($"Collected 2 daily quest reward(s). {token}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Collected 2 daily quest reward(s).")]
    public void TryParseResetHourToken_ReturnsNull_WhenNoToken(string? message)
    {
        Assert.Null(DailyResetDomParser.TryParseResetHourToken(message));
    }
}
