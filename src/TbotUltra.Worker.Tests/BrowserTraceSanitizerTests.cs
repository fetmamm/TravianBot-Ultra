using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BrowserTraceSanitizerTests
{
    [Fact]
    public void SanitizeUrl_RedactsCredentialsAndSensitiveQueryValues_ButKeepsOperationalValues()
    {
        var result = BrowserTraceSanitizer.SanitizeUrl(
            "https://user:proxy-pass@example.com/build.php?id=23&gid=22&x=-12&y=34&amount=1250&csrf=secret-token&sid=session-value#private");

        Assert.DoesNotContain("user", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proxy-pass", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session-value", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=23", result);
        Assert.Contains("gid=22", result);
        Assert.Contains("x=-12", result);
        Assert.Contains("y=34", result);
        Assert.Contains("amount=1250", result);
        Assert.DoesNotContain("#private", result);
    }

    [Fact]
    public void SanitizeText_RedactsPasswordsTokensProxyCredentialsAndEmailAddresses()
    {
        var result = BrowserTraceSanitizer.SanitizeText(
            "email=test@example.com password=hunter2 authorization=BearerSecret proxy=https://proxy-user:proxy-pass@proxy.example token=abc123 Bearer xyz.123");

        Assert.DoesNotContain("test@example.com", result);
        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("BearerSecret", result);
        Assert.DoesNotContain("proxy-user", result);
        Assert.DoesNotContain("proxy-pass", result);
        Assert.DoesNotContain("abc123", result);
        Assert.DoesNotContain("xyz.123", result);
    }

    [Theory]
    [InlineData("password", "secret", "value=<redacted> length=6")]
    [InlineData("village coordinates", "-12|34", "value=-12|34 length=6")]
    [InlineData("message", "hello world", "value=<redacted> length=11")]
    [InlineData("building name", "Academy", "value=<omitted> length=7")]
    public void FormatInputValue_OnlyIncludesSafeOperationalValues(
        string field,
        string value,
        string expected)
        => Assert.Equal(expected, BrowserTraceSanitizer.FormatInputValue(field, value));
}
