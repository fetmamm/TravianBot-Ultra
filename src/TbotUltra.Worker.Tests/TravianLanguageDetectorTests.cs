using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TravianLanguageDetectorTests
{
    [Fact]
    public void ExtractLanguageFromHtml_ReturnsEnglishFromDomSample()
    {
        var html = """
            <!doctype html>
            <html lang="en-US">
            <body data-language="en-US">
            <script>Travian.Game.language = "en-US";</script>
            </body>
            </html>
            """;

        Assert.Equal("en-US", TravianClient.ExtractLanguageFromHtmlForTests(html));
    }

    [Fact]
    public void ExtractLanguageFromHtml_ReturnsSwedishFromDomSample()
    {
        var html = """
            <!doctype html>
            <html lang="sv-SE">
            <body data-language="sv-SE">
            <script>Travian.Game.language = "sv-SE";</script>
            </body>
            </html>
            """;

        Assert.Equal("sv-SE", TravianClient.ExtractLanguageFromHtmlForTests(html));
    }

    [Fact]
    public void ExtractLanguageFromHtml_PrefersTravianGameLanguage()
    {
        var html = """
            <!doctype html>
            <html lang="en-US">
            <body data-language="en-US">
            <script>Travian.Game.language = "sv-SE";</script>
            </body>
            </html>
            """;

        Assert.Equal("sv-SE", TravianClient.ExtractLanguageFromHtmlForTests(html));
    }

    [Theory]
    [InlineData("en-US", true)]
    [InlineData(" en-US ", true)]
    [InlineData("sv-SE", false)]
    [InlineData("", false)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    public void IsExpectedLanguage_AllowsOnlyEnglish(string? language, bool expected)
    {
        Assert.Equal(expected, TravianClient.IsExpectedLanguageForTests(language));
    }
}
