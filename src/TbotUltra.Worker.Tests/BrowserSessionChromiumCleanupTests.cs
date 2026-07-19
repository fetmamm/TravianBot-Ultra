using System.Text.Json.Nodes;
using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

/// <summary>
/// The cleanup deletes hundreds of MB from the user's install folder, so the rules that stop it from
/// deleting the wrong thing matter more than the deletion itself.
/// </summary>
public sealed class BrowserSessionChromiumCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"tbot-chromium-cleanup-{Guid.NewGuid():N}");

    [Fact]
    public void RemovesAnOlderRevisionOnceTheExpectedOneIsInstalled()
    {
        var expected = ExpectedRevision();
        CreateChromiumFolder(expected);
        CreateBrowserFolder($"chromium-{Previous(expected)}");
        CreateBrowserFolder($"chromium_headless_shell-{Previous(expected)}");

        Assert.Equal(2, BrowserSession.RemoveOutdatedChromiumRevisions(_root));

        Assert.True(Directory.Exists(BrowserPath($"chromium-{expected}")));
        Assert.False(Directory.Exists(BrowserPath($"chromium-{Previous(expected)}")));
        Assert.False(Directory.Exists(BrowserPath($"chromium_headless_shell-{Previous(expected)}")));
    }

    [Fact]
    public void RemovesNothingWhenTheExpectedRevisionIsNotInstalled()
    {
        // The safety rule: a half-broken install must never be stripped of the only browser present.
        CreateBrowserFolder($"chromium-{Previous(ExpectedRevision())}");

        Assert.Equal(0, BrowserSession.RemoveOutdatedChromiumRevisions(_root));
        Assert.True(Directory.Exists(BrowserPath($"chromium-{Previous(ExpectedRevision())}")));
    }

    [Fact]
    public void LeavesNonChromiumAndNonNumberedFoldersAlone()
    {
        CreateChromiumFolder(ExpectedRevision());
        CreateBrowserFolder("ffmpeg-1011");
        CreateBrowserFolder("winldd-1007");
        CreateBrowserFolder(".links");
        CreateBrowserFolder("chromium-tip-of-tree");

        Assert.Equal(0, BrowserSession.RemoveOutdatedChromiumRevisions(_root));

        Assert.True(Directory.Exists(BrowserPath("ffmpeg-1011")));
        Assert.True(Directory.Exists(BrowserPath("winldd-1007")));
        Assert.True(Directory.Exists(BrowserPath(".links")));
        Assert.True(Directory.Exists(BrowserPath("chromium-tip-of-tree")));
    }

    [Fact]
    public void RemovesNothingWhenTheBrowsersDirectoryDoesNotExist()
    {
        Assert.Equal(0, BrowserSession.RemoveOutdatedChromiumRevisions(_root));
    }

    private string BrowserPath(string folderName) => Path.Combine(_root, "ms-playwright", folderName);

    private void CreateBrowserFolder(string folderName)
        => Directory.CreateDirectory(BrowserPath(folderName));

    private void CreateChromiumFolder(string revision)
    {
        var executableDirectory = Path.Combine(BrowserPath($"chromium-{revision}"), "chrome-win64");
        Directory.CreateDirectory(executableDirectory);
        File.WriteAllText(Path.Combine(executableDirectory, "chrome.exe"), string.Empty);
    }

    private static string ExpectedRevision()
    {
        var metadataPath = Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "browsers.json");
        Assert.True(File.Exists(metadataPath), $"Playwright driver metadata is missing at {metadataPath}.");

        var revision = JsonNode.Parse(File.ReadAllText(metadataPath))?["browsers"]?.AsArray()
            .FirstOrDefault(browser => string.Equals(
                browser?["name"]?.GetValue<string>(), "chromium", StringComparison.OrdinalIgnoreCase))
            ?["revision"]?.GetValue<string>();

        Assert.False(string.IsNullOrWhiteSpace(revision), "No chromium revision found in browsers.json.");
        return revision!;
    }

    private static string Previous(string revision)
    {
        var value = int.Parse(revision, System.Globalization.CultureInfo.InvariantCulture);
        return (value - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
