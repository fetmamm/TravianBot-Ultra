using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BrowserSessionConsentStorageTests
{
    [Theory]
    [InlineData("__cmpconsent")]
    [InlineData("cmp-data")]
    [InlineData("cookieConsent")]
    [InlineData("euconsent-v2")]
    [InlineData("usprivacy")]
    [InlineData("IABTCF_TCString")]
    [InlineData("addtl_consent")]
    [InlineData("gdprApplies")]
    [InlineData("gpp_sid")]
    [InlineData("__gads")]
    [InlineData("__gpi")]
    [InlineData("_gac_UA_123")]
    [InlineData("_gcl_au")]
    public void IsConsentStorageName_MatchesConsentAndAdKeys(string name)
    {
        Assert.True(BrowserSession.IsConsentStorageName(name));
    }

    [Theory]
    [InlineData("travian_session")]
    [InlineData("PHPSESSID")]
    [InlineData("login")]
    [InlineData("activeVillage")]
    [InlineData("sidebarState")]
    [InlineData("t4_auth")]
    public void IsConsentStorageName_DoesNotMatchNormalTravianKeys(string name)
    {
        Assert.False(BrowserSession.IsConsentStorageName(name));
    }
}
