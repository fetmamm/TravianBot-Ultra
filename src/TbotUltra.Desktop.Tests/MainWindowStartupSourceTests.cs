using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class MainWindowStartupSourceTests
{
    [Fact]
    public void Constructor_CreatesAutomationDeskBeforeSessionPacingUsesIt()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.xaml.cs"));
        var automationDeskCreated = source.IndexOf(
            "_automationDesk = MainWindowAutomationAdapter.Create",
            StringComparison.Ordinal);
        var sessionPacingInitialized = source.IndexOf("InitializeSessionPacing();", StringComparison.Ordinal);

        Assert.True(automationDeskCreated >= 0);
        Assert.True(
            sessionPacingInitialized > automationDeskCreated,
            "Session pacing reads smart-sleep groups through AutomationDesk during startup.");
    }
}
