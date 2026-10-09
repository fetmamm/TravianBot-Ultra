using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class DemolitionDashboardSourceTests
{
    [Fact]
    public void DashboardDoesNotCreateDemolitionToggleButKeepsQueuedDemolitionConsidered()
    {
        var root = RepoRoot();
        var dashboardSource = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "MainWindow.AutomationLoop.Ui.cs"));
        var loopSource = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "MainWindow.ContinuousLoop.cs"));
        var loadStart = dashboardSource.IndexOf("private void LoadAutomationLoopTasks", StringComparison.Ordinal);
        var loadEnd = dashboardSource.IndexOf("private (List<string>? EnabledGroups", loadStart, StringComparison.Ordinal);
        var loadMethod = dashboardSource[loadStart..loadEnd];

        Assert.Contains("QueueGroup.Demolish", loadMethod, StringComparison.Ordinal);
        Assert.Contains("group is QueueGroup.NpcTrade or QueueGroup.Account or QueueGroup.Demolish", loadMethod, StringComparison.Ordinal);
        Assert.Contains("groups.Add(QueueGroup.Demolish)", loopSource, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TbotUltra.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
