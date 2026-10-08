using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class QueuePanelSourceTests
{
    [Fact]
    public void QueueTables_UseTheSharedWorkingDataGridCellAndHeaderStyles()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "Views", "QueuePanel.xaml"));

        var localCellStyles = System.Xml.Linq.XDocument.Parse(xaml)
            .Descendants()
            .Where(element => element.Name.LocalName == "DataGrid.CellStyle")
            .Select(element => Assert.Single(element.Elements()))
            .ToList();
        Assert.All(localCellStyles, style =>
            Assert.Equal("{StaticResource {x:Type DataGridCell}}", style.Attribute("BasedOn")?.Value));
        Assert.DoesNotContain("<DataGrid.ColumnHeaderStyle>", xaml, StringComparison.Ordinal);
        Assert.Contains("DataGridTextColumn Header=\"Group\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DataGridTextColumn Header=\"Village\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DataGridTextColumn Header=\"Task\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DataGridTextColumn Header=\"Time\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QueueMoveToTopButton\" Content=\"⇈\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QueueMoveToBottomButton\" Content=\"⇊\"", xaml, StringComparison.Ordinal);
        Assert.True(
            xaml.IndexOf("x:Name=\"QueueMoveDownButton\"", StringComparison.Ordinal)
            < xaml.IndexOf("x:Name=\"QueueMoveToTopButton\"", StringComparison.Ordinal));
        Assert.True(
            xaml.IndexOf("x:Name=\"QueueMoveToTopButton\"", StringComparison.Ordinal)
            < xaml.IndexOf("x:Name=\"QueueMoveToBottomButton\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ActiveQueue_UsesExtendedFullRowSelection()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "Views", "QueuePanel.xaml"));

        Assert.Contains("SelectionMode=\"Extended\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionUnit=\"FullRow\"", xaml, StringComparison.Ordinal);
    }
}
