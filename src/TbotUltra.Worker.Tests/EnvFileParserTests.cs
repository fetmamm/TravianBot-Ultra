using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class EnvFileParserTests : IDisposable
{
    private readonly string _envPath;

    public EnvFileParserTests()
    {
        _envPath = Path.Combine(Path.GetTempPath(), $"tbot-envparser-{Guid.NewGuid():N}.env");
    }

    [Fact]
    public void ReadValues_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(EnvFileParser.ReadValues(_envPath));
    }

    [Fact]
    public void ReadValues_SkipsBlankLinesCommentsAndLinesWithoutEquals()
    {
        File.WriteAllText(_envPath, string.Join('\n',
            "# a comment",
            string.Empty,
            "   ",
            "not-a-key-value-line",
            "KEY=value"));

        var values = EnvFileParser.ReadValues(_envPath);

        Assert.Single(values);
        Assert.Equal("value", values["KEY"]);
    }

    [Fact]
    public void ReadValues_TrimsKeyAndValueWhitespace()
    {
        File.WriteAllText(_envPath, "  KEY  =   value  ");

        var values = EnvFileParser.ReadValues(_envPath);

        Assert.Equal("value", values["KEY"]);
    }

    [Fact]
    public void ReadValues_StripsSurroundingQuotes()
    {
        File.WriteAllText(_envPath, string.Join('\n',
            "DQ=\"double\"",
            "SQ='single'"));

        var values = EnvFileParser.ReadValues(_envPath);

        Assert.Equal("double", values["DQ"]);
        Assert.Equal("single", values["SQ"]);
    }

    [Fact]
    public void ReadValues_PreservesEqualsInsideValue()
    {
        // Proxy-auth / base64 style values contain '=' after the first separator; only the first
        // '=' splits the pair, so the remainder must survive intact.
        File.WriteAllText(_envPath, "TBOT_ALICE_PROXY_SERVER=user:p=ss@1.2.3.4:8080");

        var values = EnvFileParser.ReadValues(_envPath);

        Assert.Equal("user:p=ss@1.2.3.4:8080", values["TBOT_ALICE_PROXY_SERVER"]);
    }

    [Fact]
    public void ReadValues_KeysAreCaseInsensitive_LastWriteWins()
    {
        File.WriteAllText(_envPath, string.Join('\n',
            "Key=first",
            "KEY=second"));

        var values = EnvFileParser.ReadValues(_envPath);

        Assert.Single(values);
        Assert.Equal("second", values["key"]);
    }

    [Fact]
    public void ReadValues_SkipsEmptyKey()
    {
        File.WriteAllText(_envPath, "=orphan");

        Assert.Empty(EnvFileParser.ReadValues(_envPath));
    }

    [Fact]
    public void FormatValue_ReadValues_RoundTripsSpecialCharacters()
    {
        const string expected = "  a=b#c\\\"'\\path\nnext  ";
        File.WriteAllText(_envPath, $"PASSWORD={EnvFileParser.FormatValue(expected)}");

        var values = EnvFileParser.ReadValues(_envPath);

        Assert.Equal(expected, values["PASSWORD"]);
    }

    public void Dispose()
    {
        if (File.Exists(_envPath))
        {
            File.Delete(_envPath);
        }
    }
}
