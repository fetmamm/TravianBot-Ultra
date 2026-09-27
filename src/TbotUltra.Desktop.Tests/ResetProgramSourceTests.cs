using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ResetProgramSourceTests
{
    [Fact]
    public void ResetProgram_ReturnsTheUiAndRuntimeToLoggedOutStartupStateWithoutClearingTheQueue()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Session.cs"));
        var methodStart = source.IndexOf(
            "private async Task ResetProgramInternalAsync()",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "    private async Task StopAllAutomationAndWaitAsync()",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodBody = source[methodStart..methodEnd];

        Assert.Contains("await _botService.ShutdownAsync(AppendLog);", methodBody, StringComparison.Ordinal);
        Assert.Contains("ClosePopupWindows(stopTravcoTools: false);", methodBody, StringComparison.Ordinal);
        Assert.Contains("ResetSessionPacing();", methodBody, StringComparison.Ordinal);
        Assert.Contains("ClearAccountScopedUiState(clearQueue: false);", methodBody, StringComparison.Ordinal);
        Assert.Contains("LoadConfigToUi();", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearAccountScopedUiState(clearQueue: true);", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_botService.ClearQueue();", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetProgramButton_UsesTheSoftDangerPalette()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.xaml"));
        var buttonStart = xaml.IndexOf("x:Name=\"ResetProgramButton\"", StringComparison.Ordinal);
        var buttonEnd = xaml.IndexOf("/>", buttonStart, StringComparison.Ordinal);

        Assert.True(buttonStart >= 0 && buttonEnd > buttonStart);
        var button = xaml[buttonStart..buttonEnd];

        Assert.Contains("Background=\"{DynamicResource DangerBgBrush}\"", button, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{DynamicResource DangerTextBrush}\"", button, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{DynamicResource DangerBorderBrush}\"", button, StringComparison.Ordinal);
        Assert.DoesNotContain("WarningButtonBgBrush", button, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetProgram_PreservesReusableTravcoControlLifetime()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var sessionSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Session.cs"));
        var toolbarSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Toolbar.cs"));
        var resetStart = sessionSource.IndexOf("private async Task ResetProgramInternalAsync()", StringComparison.Ordinal);
        var resetEnd = sessionSource.IndexOf("private async Task StopAllAutomationAndWaitAsync()", resetStart, StringComparison.Ordinal);
        var reset = sessionSource[resetStart..resetEnd];

        Assert.Contains("ClosePopupWindows(stopTravcoTools: false);", reset, StringComparison.Ordinal);
        Assert.Contains("private void ClosePopupWindows(bool stopTravcoTools = true)", toolbarSource, StringComparison.Ordinal);
        Assert.Contains("if (stopTravcoTools)", toolbarSource, StringComparison.Ordinal);
    }
}
