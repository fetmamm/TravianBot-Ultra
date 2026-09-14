using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class NavigationTimeoutRecoverySourceTests
{
    [Fact]
    public void StandardNavigation_ValidatesUsableExpectedPageBeforeSurfacingTimeout()
    {
        var source = ReadSource("Services", "Automation", "Core", "TravianClient.Navigation.cs");
        var gotoStart = source.IndexOf("private async Task GotoAsync", StringComparison.Ordinal);
        var gotoEnd = source.IndexOf("private async Task ReloadOrGotoAsync", gotoStart, StringComparison.Ordinal);
        var method = source[gotoStart..gotoEnd];

        Assert.Contains("DidTimedOutNavigationReachUsablePageAsync(pathOrUrl", method, StringComparison.Ordinal);
        Assert.Contains("GOTO timeout recovered", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanContextRotation_ValidatesLoadedGamePageBeforeDiscardingIt()
    {
        var source = ReadSource("Infrastructure", "BrowserSession.cs");
        var rotationStart = source.IndexOf("RotateMainContextFromSavedStateAsync", StringComparison.Ordinal);
        var rotationEnd = source.IndexOf("private BrowserTypeLaunchOptions", rotationStart, StringComparison.Ordinal);
        var method = source[rotationStart..rotationEnd];

        Assert.Contains("IsUsableExpectedGamePageAsync", method, StringComparison.Ordinal);
        Assert.Contains("navigation timeout recovered", method, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var root = Path.Combine(directory.FullName, "src", "TbotUltra.Worker");
            var path = Path.Combine([root, .. parts]);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new DirectoryNotFoundException("Could not locate the TbotUltra.Worker source directory.");
    }
}
