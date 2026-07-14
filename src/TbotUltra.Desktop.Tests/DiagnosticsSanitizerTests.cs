using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class DiagnosticsSanitizerTests
{
    [Fact]
    public void SanitizeJson_RedactsSecretsAndPersonalValues()
    {
        const string json = """
            {
              "password": "secret-password",
              "email": "person@example.com",
              "proxyEndpoint": "192.0.2.10:8080",
              "host": "192.0.2.11",
              "port": 1080,
              "nested": {
                "token": "secret-token",
                "server": "https://example.invalid"
              }
            }
            """;

        var result = DiagnosticsSanitizer.SanitizeJson(json);

        Assert.DoesNotContain("secret-password", result, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.com", result, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10:8080", result, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.11", result, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", result, StringComparison.Ordinal);
        Assert.Contains("https://example.invalid", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeText_RedactsEmailJwtProxyAndAccountIdentifier()
    {
        const string jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhYmMifQ.signature_value";
        var input = $"person@example.com {jwt} socks5://user:pass@192.0.2.10:8080 user_gmail_com_ts1_example";

        var result = DiagnosticsSanitizer.SanitizeText(input);

        Assert.DoesNotContain("person@example.com", result, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, result, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", result, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10:8080", result, StringComparison.Ordinal);
        Assert.DoesNotContain("user_gmail_com_ts1_example", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeJson_SanitizesStringArraysWithoutInvalidatingEnumeration()
    {
        const string json = """
            {
              "entries": ["person@example.com", "status"]
            }
            """;

        var result = DiagnosticsSanitizer.SanitizeJson(json);

        Assert.DoesNotContain("person@example.com", result, StringComparison.Ordinal);
        Assert.Contains("redacted-email", result, StringComparison.Ordinal);
        Assert.Contains("status", result, StringComparison.Ordinal);
    }
}
