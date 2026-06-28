using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ProxyParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("user:pass@")] // credentials only, no host
    public void TryBuild_ReturnsFalseForEmptyOrHostless(string? input)
    {
        Assert.False(ProxyParser.TryBuild(input, out var proxy, out _));
        Assert.Null(proxy);
    }

    [Fact]
    public void TryBuild_BareHostPort_SetsServerOnly()
    {
        Assert.True(ProxyParser.TryBuild("1.2.3.4:8080", out var proxy, out var warning));
        Assert.Equal("1.2.3.4:8080", proxy!.Server);
        Assert.Null(proxy.Username);
        Assert.Null(proxy.Password);
        Assert.Null(warning);
    }

    [Fact]
    public void TryBuild_SchemeHostPort_KeepsScheme()
    {
        Assert.True(ProxyParser.TryBuild("socks5://1.2.3.4:1080", out var proxy, out _));
        Assert.Equal("socks5://1.2.3.4:1080", proxy!.Server);
        Assert.Null(proxy.Username);
    }

    [Fact]
    public void TryBuild_InlineCredentials_SplitsUserAndPass()
    {
        Assert.True(ProxyParser.TryBuild("user:pass@1.2.3.4:8080", out var proxy, out var warning));
        Assert.Equal("1.2.3.4:8080", proxy!.Server);
        Assert.Equal("user", proxy.Username);
        Assert.Equal("pass", proxy.Password);
        Assert.Null(warning);
    }

    [Fact]
    public void TryBuild_SchemeWithInlineCredentials_SplitsCorrectly()
    {
        Assert.True(ProxyParser.TryBuild("http://user:pass@1.2.3.4:8080", out var proxy, out _));
        Assert.Equal("http://1.2.3.4:8080", proxy!.Server);
        Assert.Equal("user", proxy.Username);
        Assert.Equal("pass", proxy.Password);
    }

    [Fact]
    public void TryBuild_CredentialsWithoutColon_UsesUsernameOnly()
    {
        Assert.True(ProxyParser.TryBuild("user@1.2.3.4:8080", out var proxy, out _));
        Assert.Equal("1.2.3.4:8080", proxy!.Server);
        Assert.Equal("user", proxy.Username);
        Assert.Null(proxy.Password);
    }

    [Fact]
    public void TryBuild_SocksWithCredentials_ReturnsWarning()
    {
        Assert.True(ProxyParser.TryBuild("socks5://user:pass@1.2.3.4:1080", out var proxy, out var warning));
        Assert.Equal("socks5://1.2.3.4:1080", proxy!.Server);
        Assert.NotNull(warning);
    }

    [Fact]
    public void MaskForLog_HidesPassword()
    {
        Assert.Equal("http://user:***@1.2.3.4:8080", ProxyParser.MaskForLog("http://user:pass@1.2.3.4:8080"));
        Assert.Equal("user:***@1.2.3.4:8080", ProxyParser.MaskForLog("user:pass@1.2.3.4:8080"));
    }

    [Fact]
    public void MaskForLog_NoCredentials_ReturnsAsIs()
    {
        Assert.Equal("1.2.3.4:8080", ProxyParser.MaskForLog("1.2.3.4:8080"));
        Assert.Equal("(empty)", ProxyParser.MaskForLog("   "));
    }

    [Theory]
    [InlineData("net::ERR_PROXY_CONNECTION_FAILED at https://...", true)]
    [InlineData("net::ERR_TUNNEL_CONNECTION_FAILED", true)]
    [InlineData("Page.goto: net::ERR_PROXY_AUTH_REQUESTED", true)]
    [InlineData("net::ERR_NAME_NOT_RESOLVED", false)]
    [InlineData("Timeout 30000ms exceeded", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeProxyError_DetectsProxyCodes(string? message, bool expected)
    {
        Assert.Equal(expected, ProxyParser.LooksLikeProxyError(message));
    }
}
