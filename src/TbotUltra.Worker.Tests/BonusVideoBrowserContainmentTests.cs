using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BonusVideoBrowserContainmentTests
{
    [Fact]
    public void IsolatedBonusBrowser_BlocksPopupEscapeBeforeCreatingItsPage()
    {
        var source = ReadBonusVideoSource();
        var contextCreated = source.IndexOf("videoBrowser.NewContextAsync", StringComparison.Ordinal);
        var suppressionInstalled = source.IndexOf(
            "AddInitScriptAsync(IsolatedBonusVideoPopupSuppressionScript)",
            contextCreated,
            StringComparison.Ordinal);
        var pageCreated = source.IndexOf("videoContext.NewPageAsync", contextCreated, StringComparison.Ordinal);

        Assert.Contains("IsolatedBonusVideoPopupSuppressionScript", source, StringComparison.Ordinal);
        Assert.Contains("window.open", ReadBrowserSessionSource(), StringComparison.Ordinal);
        Assert.True(suppressionInstalled > contextCreated, "The isolated context must install popup suppression.");
        Assert.True(pageCreated > suppressionInstalled, "Popup suppression must be active before the page is created.");
    }

    [Fact]
    public void IsolatedBonusBrowser_KeepsChromesNativePopupBlockerEnabled()
    {
        Assert.Contains(
            "CreateChromiumLaunchOptions(keepNativePopupBlocker: true, startMinimized: true)",
            ReadBonusVideoSource(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void IsolatedBonusBrowser_ReassertsMinimizedWindowStateAfterPageCreation()
    {
        var source = ReadBonusVideoSource();
        var pageCreated = source.IndexOf("videoContext.NewPageAsync", StringComparison.Ordinal);
        var minimized = source.IndexOf("MinimizeBrowserWindowAsync", pageCreated, StringComparison.Ordinal);
        var actionStarted = source.IndexOf("action(page, phaseTimeout.Token)", pageCreated, StringComparison.Ordinal);

        Assert.True(minimized > pageCreated, "The isolated browser must be minimized after its page is created.");
        Assert.True(actionStarted > minimized, "The browser must be minimized before the video action starts.");
    }

    [Fact]
    public void IsolatedBonusBrowser_MinimizeTimeoutIsBestEffort()
    {
        var source = ReadBonusVideoSource();
        var methodStart = source.IndexOf(
            "private async Task MinimizeBrowserWindowAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private void SetBonusVideoCooldown", methodStart, StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        Assert.Contains(
            "MinimizeBrowserWindowAsync(videoContext, page, cancellationToken)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("IsolatedBonusVideoMinimizeTimeout", method, StringComparison.Ordinal);
        Assert.Contains("!cancellationToken.IsCancellationRequested", method, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "--start-maximized", "--start-minimized")]
    [InlineData(true, "--start-minimized", "--start-maximized")]
    public void ChromiumLaunchArguments_UseOnlyTheRequestedWindowState(
        bool startMinimized,
        string expected,
        string forbidden)
    {
        var arguments = BrowserSession.CreateChromiumLaunchArguments(startMinimized);

        Assert.Contains(expected, arguments);
        Assert.DoesNotContain(forbidden, arguments);
    }

    [Fact]
    public void MainBrowser_RemainsMaximized()
    {
        Assert.Contains(
            "startMinimized: false",
            ReadBrowserSessionSource(),
            StringComparison.Ordinal);
    }

    private static string ReadBonusVideoSource()
        => ReadSourceFile("BrowserSession.BonusVideo.cs");

    private static string ReadBrowserSessionSource()
        => ReadSourceFile("BrowserSession.cs");

    private static string ReadSourceFile(string fileName)
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
