using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingSlotsWindowSourceTests
{
    [Fact]
    public void BuildingSlotsWindow_StaysTopmostAndOpensModelessly()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingSlotsWindow.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "MainWindow.Buildings.cs"));
        var templatesWindow = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml.cs"));

        Assert.Contains("Topmost=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("new BuildingSlotsWindow { Owner = this }.Show();", mainWindow, StringComparison.Ordinal);
        Assert.Contains("new BuildingSlotsWindow { Owner = this }.Show();", templatesWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("new BuildingSlotsWindow { Owner = this }.ShowDialog();", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("new BuildingSlotsWindow { Owner = this }.ShowDialog();", templatesWindow, StringComparison.Ordinal);
    }
}
