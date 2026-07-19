using System.Text.Json.Nodes;
using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

/// <summary>
/// The guard decides whether Chromium needs downloading. It must key on the exact build revision the
/// referenced Playwright package expects: a leftover folder from an older package version made the old
/// any-revision check report "installed", so the install was skipped and the launch then failed on a
/// missing executable.
/// </summary>
public sealed class BrowserSessionChromiumInstallGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"tbot-chromium-guard-{Guid.NewGuid():N}");

    [Fact]
    public void ReportsInstalled_WhenTheExpectedRevisionIsPresent()
    {
        CreateChromiumFolder(ExpectedRevision());

        Assert.True(BrowserSession.ChromiumAlreadyInstalled(_root));
    }

    [Fact]
    public void ReportsInstalled_WithLegacyWindowsArchiveName()
    {
        CreateChromiumFolder(ExpectedRevision(), "chrome-win");

        Assert.True(BrowserSession.ChromiumAlreadyInstalled(_root));
    }

    [Fact]
    public void ReportsMissing_WhenOnlyAnOlderRevisionIsPresent()
    {
        // The exact regression: Playwright upgraded, ms-playwright still holds the previous revision.
        CreateChromiumFolder(PreviousRevision());

        Assert.False(BrowserSession.ChromiumAlreadyInstalled(_root));
    }

    [Fact]
    public void ReportsMissing_WhenTheBrowsersDirectoryDoesNotExist()
    {
        Assert.False(BrowserSession.ChromiumAlreadyInstalled(_root));
    }

    private void CreateChromiumFolder(string revision, string archiveName = "chrome-win64")
    {
        var executableDirectory = Path.Combine(_root, "ms-playwright", $"chromium-{revision}", archiveName);
        Directory.CreateDirectory(executableDirectory);
        File.WriteAllText(Path.Combine(executableDirectory, "chrome.exe"), string.Empty);
    }

    /// <summary>
    /// Read the revision straight from the driver metadata shipped next to the tests, so the expectation
    /// follows package upgrades instead of pinning a number that would need editing every bump.
    /// </summary>
    private static string ExpectedRevision()
    {
        var metadataPath = Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "browsers.json");
        Assert.True(File.Exists(metadataPath), $"Playwright driver metadata is missing at {metadataPath}.");

        var browsers = JsonNode.Parse(File.ReadAllText(metadataPath))?["browsers"]?.AsArray();
        Assert.NotNull(browsers);

        var revision = browsers!
            .FirstOrDefault(browser => string.Equals(
                browser?["name"]?.GetValue<string>(), "chromium", StringComparison.OrdinalIgnoreCase))
            ?["revision"]?.GetValue<string>();

        Assert.False(string.IsNullOrWhiteSpace(revision), "No chromium revision found in browsers.json.");
        return revision!;
    }

    private static string PreviousRevision()
    {
        var revision = int.Parse(ExpectedRevision(), System.Globalization.CultureInfo.InvariantCulture);
        return (revision - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
