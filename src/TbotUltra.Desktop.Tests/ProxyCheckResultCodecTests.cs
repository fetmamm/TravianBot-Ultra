using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ProxyCheckResultCodecTests
{
    [Fact]
    public void ParseLookupResponse_ReadsIpLocationAndIsp()
    {
        const string json = """
            { "success": true, "ip": "203.0.113.4", "city": "Stockholm", "region": "Stockholm", "country": "Sweden", "isp": "Example ISP" }
            """;

        var result = ProxyCheckResultCodec.ParseLookupResponse(json);

        Assert.Equal("203.0.113.4", result.Ip);
        Assert.Equal("Stockholm, Stockholm, Sweden", result.Location);
        Assert.Equal("Example ISP", result.Isp);
    }

    [Fact]
    public void ParseLookupResponse_UsesUnknownForMissingOptionalValues()
    {
        var result = ProxyCheckResultCodec.ParseLookupResponse("{ \"success\": true }");

        Assert.Equal("unknown", result.Ip);
        Assert.Equal("unknown", result.Location);
        Assert.Equal("unknown", result.Isp);
    }

    [Fact]
    public void SuccessAndFailurePayloads_RoundTrip()
    {
        var successRaw = ProxyCheckResultCodec.BuildSuccess(
            new ProxyCheckInfo("1.2.3.4", "Sweden", "ISP"),
            "Proxy (socks5://***:1080)",
            "123 ms");
        var failureRaw = ProxyCheckResultCodec.BuildFailure("Timed out", "Direct");

        Assert.True(ProxyCheckResultCodec.TryParseSuccess(successRaw, out var success));
        Assert.Equal("123 ms", success.Latency);
        Assert.True(ProxyCheckResultCodec.TryParseFailure(failureRaw, out var failure));
        Assert.Equal(ProxyCheckResultCodec.LookupTarget, failure.Target);
    }

    [Fact]
    public void SummarizeError_KeepsOnlyFirstLine()
    {
        Assert.Equal("First", ProxyCheckResultCodec.SummarizeError("First\r\nSecond"));
        Assert.Equal("Unknown error.", ProxyCheckResultCodec.SummarizeError(null));
    }
}
