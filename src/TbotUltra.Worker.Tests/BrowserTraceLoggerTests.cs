using System.Text.RegularExpressions;
using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BrowserTraceLoggerTests
{
    [Fact]
    public void DisabledTrace_WritesNothing()
    {
        var lines = new List<string>();
        var logger = new BrowserTraceLogger(false, lines.Add);

        using (var flow = logger.BeginFlow("queue-1", "login", "account", "03", "worker-execution"))
        using (var navigation = logger.BeginOperation("NAV", "goto", "target=https://example.com/dorf1.php"))
        {
            navigation.Complete();
            flow.Complete();
        }

        Assert.Empty(lines);
    }

    [Fact]
    public void EnabledTrace_WritesOrderedStartEndAndSummaryExactlyOnce()
    {
        var lines = new List<string>();
        var logger = new BrowserTraceLogger(true, lines.Add);

        using (var flow = logger.BeginFlow("queue-123", "status", "account", "03", "worker-execution"))
        {
            using (var read = logger.BeginOperation("READ", "resources", "scope=stock-bar"))
            {
                read.Complete("success", "count=4 wood=100");
                read.Complete("failed");
            }
            logger.Event("CACHE", "villages-hit", "hit", "ageMs=12 count=3");
            flow.Complete("success");
            flow.Complete("failed");
        }

        Assert.Equal(5, lines.Count);
        Assert.Single(lines, line => line.Contains("event=FLOW_START", StringComparison.Ordinal));
        Assert.Single(lines, line => line.Contains("event=FLOW_END", StringComparison.Ordinal));
        Assert.Single(lines, line => line.Contains("event=READ_START", StringComparison.Ordinal));
        Assert.Single(lines, line => line.Contains("event=READ_END", StringComparison.Ordinal));
        Assert.Contains("reads=1", lines[^1]);
        Assert.Contains("cacheHits=1", lines[^1]);

        var sequences = lines
            .Select(line => int.Parse(Regex.Match(line, @"\bseq=(\d+)").Groups[1].Value))
            .ToArray();
        Assert.Equal(Enumerable.Range(1, lines.Count), sequences);
    }

    [Fact]
    public void TraceLine_SanitizesIdentityUrlAndDetails()
    {
        var lines = new List<string>();
        var logger = new BrowserTraceLogger(true, lines.Add);

        using var flow = logger.BeginFlow("queue-1", "login", "test@example.com", "03", "worker-execution");
        logger.Event(
            "ERROR",
            "login",
            "failed",
            "password=hunter2 token=abc123",
            "https://example.com/login?gid=17&csrf=secret");
        flow.Complete("failed");

        var output = string.Join(Environment.NewLine, lines);
        Assert.DoesNotContain("test@example.com", output);
        Assert.DoesNotContain("hunter2", output);
        Assert.DoesNotContain("abc123", output);
        Assert.DoesNotContain("secret", output);
        Assert.Contains("gid=17", output);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("deferred")]
    [InlineData("canceled")]
    [InlineData("failed")]
    public void Flow_RecordsEveryTerminalOutcome(string outcome)
    {
        var lines = new List<string>();
        var logger = new BrowserTraceLogger(true, lines.Add);

        using (var flow = logger.BeginFlow(null, "task", "account", "village", "worker-execution"))
        {
            flow.Complete(outcome);
        }

        var end = Assert.Single(lines, line => line.Contains("event=FLOW_END", StringComparison.Ordinal));
        Assert.Contains($"result={outcome}", end);
    }
}
