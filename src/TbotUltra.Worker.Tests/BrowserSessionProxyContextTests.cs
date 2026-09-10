using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BrowserSessionProxyContextTests
{
    [Fact]
    public void EveryBrowserContext_ReceivesTheAccountProxyExplicitly()
    {
        var source = ReadBrowserSessionSource("BrowserSession.cs")
            + ReadBrowserSessionSource("BrowserSession.BonusVideo.cs");

        Assert.Equal(3, CountOccurrences(source, "new BrowserNewContextOptions"));
        Assert.Equal(3, CountOccurrences(source, "Proxy = ResolveContextProxy(),"));
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string ReadBrowserSessionSource(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(
                directory.FullName,
                "src",
                "TbotUltra.Worker",
                "Infrastructure",
                fileName);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new DirectoryNotFoundException($"Could not locate {fileName}.");
    }
}
