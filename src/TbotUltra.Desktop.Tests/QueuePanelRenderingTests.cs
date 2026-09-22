using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Desktop.Views;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

[Collection(WpfSmokeCollection.Name)]
public sealed class QueuePanelRenderingTests
{
    private readonly WpfSmokeFixture _wpf;

    public QueuePanelRenderingTests(WpfSmokeFixture wpf)
    {
        _wpf = wpf;
    }

    [Fact]
    public void ActiveQueue_WithEstimatedRows_RendersHeadersAndCellText()
    {
        _wpf.Run(() =>
        {
            var viewModel = new TravianQueueViewModel();
            var panel = new QueuePanel { DataContext = viewModel };
            var hiddenTab = new TabItem { Header = "Other", Content = new TextBlock { Text = "Other" } };
            var queueTab = new TabItem { Header = "Queue", Content = panel };
            var host = new TabControl { Items = { hiddenTab, queueTab }, SelectedItem = hiddenTab };
            host.Measure(new Size(1200, 700));
            host.Arrange(new Rect(0, 0, 1200, 700));
            host.UpdateLayout();

            // This is the real lifecycle: queue projection changes while the user is on Buildings,
            // then Queue is selected later.
            viewModel.ApplyActiveQueueRows(
            [
                new QueueItemRow
                {
                    Id = Guid.NewGuid(),
                    Group = QueueGroup.Construction,
                    GroupName = "Construction",
                    VillageName = "ABC",
                    VillageKey = "xy:61|22",
                    DisplayName = "Upgrade Marketplace to level 18",
                    Status = QueueStatus.Pending,
                    HasEstimate = true,
                    BuildTimeText = "6h 3m",
                    WoodText = "55,815",
                    ClayText = "61,685",
                    IronText = "59,275",
                    CropText = "27,790",
                },
            ]);

            host.SelectedItem = queueTab;
            host.UpdateLayout();

            var renderedText = FindVisualChildren<TextBlock>(panel)
                .Where(text => text.Visibility == Visibility.Visible && text.ActualWidth > 0)
                .Select(text => text.Text)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Contains("Group", renderedText);
            Assert.Contains("Construction", renderedText);
            Assert.Contains("ABC", renderedText);
            Assert.Contains("Upgrade Marketplace to level 18", renderedText);
            Assert.Contains("6h 3m", renderedText);
            Assert.Contains("55,815", renderedText);
        });
    }

    [Fact]
    public void ActiveQueue_ColumnsHaveNonCollapsibleMinimumWidthsAndCanBeEnlarged()
    {
        _wpf.Run(() =>
        {
            var viewModel = new TravianQueueViewModel();
            viewModel.ApplyActiveQueueRows(
            [
                new QueueItemRow
                {
                    Id = Guid.NewGuid(),
                    Group = QueueGroup.Construction,
                    GroupName = "Construction",
                    VillageName = "BRE",
                    DisplayName = "Upgrade all resources to level 5",
                    Status = QueueStatus.Pending,
                    HasEstimate = true,
                    BuildTimeText = "1h 38m",
                    WoodText = "13,285",
                    ClayText = "14,275",
                    IronText = "10,855",
                    CropText = "7,945",
                },
            ]);
            var panel = new QueuePanel { DataContext = viewModel };
            panel.Measure(new Size(940, 650));
            panel.Arrange(new Rect(0, 0, 940, 650));
            panel.UpdateLayout();

            var queueGrid = FindVisualChildren<DataGrid>(panel)
                .Single(grid => ReferenceEquals(grid.ItemsSource, viewModel.ActiveQueueRows));

            Assert.True(queueGrid.CanUserResizeColumns);
            Assert.Collection(
                queueGrid.Columns,
                column => Assert.True(column.MinWidth >= 90),
                column => Assert.True(column.MinWidth >= 100),
                column => Assert.True(column.MinWidth >= 330),
                column => Assert.True(column.MinWidth >= 85),
                column => Assert.True(column.MinWidth >= 90),
                column => Assert.True(column.MinWidth >= 247.5));
        });
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
}
