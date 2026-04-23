using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ServerDiscoveryServiceTests
{
    [Fact]
    public void ParseServersFromHtml_ParsesServerCards()
    {
        const string html = """
            <html>
              <body>
                <article class="server-card">
                  <h3 class="server-title">VIP x200</h3>
                  <a href="https://vip.ss-travi.com/login.php">Play</a>
                </article>
                <article class="server-card">
                  <h3 class="server-title">ELITE ×50000</h3>
                  <a href="https://elt.ss-travi.com/index.php">Play</a>
                </article>
              </body>
            </html>
            """;

        var parsed = ServerDiscoveryService.ParseServersFromHtml(html);

        Assert.Equal(2, parsed.Count);
        Assert.Contains(parsed, item => item.BaseUrl == "https://vip.ss-travi.com");
        Assert.Contains(parsed, item => item.BaseUrl == "https://elt.ss-travi.com");
    }

    [Fact]
    public void ParseServersFromHtml_FallsBackToAnchors_WhenNoCardsFound()
    {
        const string html = """
            <html>
              <body>
                <a href="https://mega.ss-travi.com/index.php">Join now</a>
              </body>
            </html>
            """;

        var parsed = ServerDiscoveryService.ParseServersFromHtml(html);

        Assert.Single(parsed);
        Assert.Equal("https://mega.ss-travi.com", parsed[0].BaseUrl);
    }
}
