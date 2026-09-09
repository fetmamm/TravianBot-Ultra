using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ProxyWorkflowSourceTests
{
    [Fact]
    public void ProxyFinder_UsesRequestedDefaultsAndGithubButtonText()
    {
        var codeBehind = ReadDesktopSource("ProxyFinderWindow.xaml.cs");
        var stateStore = ReadDesktopServiceSource("ProxyFinderStateStore.cs");
        var xaml = ReadDesktopSource("ProxyFinderWindow.xaml");

        Assert.Contains("SelectComboByTag(ParallelComboBox, \"500\", \"500\")", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SelectComboByTag(TopComboBox, \"20\", \"20\")", codeBehind, StringComparison.Ordinal);
        Assert.Contains("public string Parallel { get; set; } = \"500\";", stateStore, StringComparison.Ordinal);
        Assert.Contains("public string Top { get; set; } = \"20\";", stateStore, StringComparison.Ordinal);
        Assert.Contains("Content=\"Proxy list (Github)\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ProxyFinderListDownload_UsesCancelableBusyOverlayForWholeOperation()
    {
        var source = ReadDesktopSource("ProxyFinderWindow.xaml.cs");
        var method = MethodBody(source, "private async Task LoadProxyListAsync");

        Assert.Contains("BusyOverlay.ShowCancel = true;", method, StringComparison.Ordinal);
        Assert.Contains("BusyOverlay.Show(\"Loading proxy list\"", method, StringComparison.Ordinal);
        Assert.Contains("finally", method, StringComparison.Ordinal);
        Assert.Contains("BusyOverlay.Hide();", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingFinderAfterAddingProxy_ReloadsAccountProxyDropdown()
    {
        var source = ReadDesktopSource("AccountsWindow.xaml.cs");
        var method = MethodBody(source, "private void ProxyFinderButton_Click");
        var showDialog = method.IndexOf("finder.ShowDialog()", StringComparison.Ordinal);
        var reload = method.IndexOf("ReloadProxyLibraryEntries();", StringComparison.Ordinal);

        Assert.True(showDialog >= 0 && reload > showDialog, "The account proxy library must reload whenever Finder closes.");
    }

    [Fact]
    public void ProxyLibraryDelete_PersistsTheSingleEntryImmediately()
    {
        var source = ReadDesktopSource("ProxyLibraryWindow.xaml.cs");
        var method = MethodBody(source, "private void DeleteRowButton_Click");

        Assert.Contains("_store.Remove(entry.Id)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ProxyLibraryClose_DoesNotResetDialogResultAfterPromptSaveClosedTheWindow()
    {
        var source = ReadDesktopSource("ProxyLibraryWindow.xaml.cs");
        var method = MethodBody(source, "private void CloseButton_Click");
        var prompt = method.IndexOf("PromptToSaveUnsavedChanges()", StringComparison.Ordinal);
        var alreadyClosingGuard = method.IndexOf("if (_isClosing)", StringComparison.Ordinal);
        var dialogResult = method.IndexOf("DialogResult = false", StringComparison.Ordinal);

        Assert.True(prompt >= 0 && alreadyClosingGuard > prompt && dialogResult > alreadyClosingGuard,
            "Close must return when prompt-save already closed the modal window.");
    }

    [Fact]
    public void ProxyLibraryCheck_RequiresTravianReachability()
    {
        var source = ReadDesktopSource("ProxyLibraryWindow.xaml.cs");
        var method = MethodBody(source, "private async Task CheckProxiesAsync");

        Assert.Contains("FilterReachableAsync", method, StringComparison.Ordinal);
        Assert.Contains("TravianTargetUrl", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckIp_UsesTheApplicationPlaywrightEnvironmentBeforeCreatingTheDriver()
    {
        var source = ReadDesktopServiceSource("ProxyCheckService.cs");
        var method = MethodBody(source, "internal static async Task<string> CheckIpAsync");
        var configure = method.IndexOf("BrowserSession.ConfigureLocalPlaywrightEnvironment(projectRoot)", StringComparison.Ordinal);
        var create = method.IndexOf("Playwright.CreateAsync()", StringComparison.Ordinal);

        Assert.True(configure >= 0 && configure < create, "The IP check must resolve the app's bundled browser before Playwright starts.");
    }

    [Fact]
    public void CompletedIpCheck_UsesSoftGreenContinueButton()
    {
        var source = ReadDesktopSource("AccountsWindow.xaml.cs");
        var method = MethodBody(source, "private void ShowProxyCheckOverlay");

        Assert.Contains("FindResource(\"SuccessBgBrush\")", method, StringComparison.Ordinal);
        Assert.Contains("FindResource(\"SuccessBorderBrush\")", method, StringComparison.Ordinal);
        Assert.Contains("FindResource(\"SuccessTextBrush\")", method, StringComparison.Ordinal);
    }

    private static string ReadDesktopSource(string fileName)
    {
        var root = ProjectRootLocator.FindProjectRoot();
        return File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", fileName));
    }

    private static string ReadDesktopServiceSource(string fileName)
    {
        var root = ProjectRootLocator.FindProjectRoot();
        return File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "Services", fileName));
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {signature}.");
        var nextMethod = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return nextMethod < 0 ? source[start..] : source[start..nextMethod];
    }
}
