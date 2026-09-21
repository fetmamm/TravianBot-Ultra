using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class QueuePanel : UserControl
{
    private MainWindow? _host;

    public QueuePanel() => InitializeComponent();

    private MainWindow? Host => _host ??= Window.GetWindow(this) as MainWindow;

    internal TextBlock BuildQueueStatus => BuildQueueStatusTextBlock;
    internal DataGrid TravianBuildQueue => TravianBuildQueueDataGrid;
    internal DataGrid TravianSmithyQueue => TravianSmithyQueueDataGrid;
    internal TabControl QueueSections => QueueSectionTabControl;
    internal TabItem ActiveTab => ActiveQueueTabItem;
    internal Button RemoveButton => QueueRemoveButton;
    internal Button MoveUpButton => QueueMoveUpButton;
    internal Button MoveDownButton => QueueMoveDownButton;
    internal Button MoveToTopButton => QueueMoveToTopButton;
    internal Button MoveToBottomButton => QueueMoveToBottomButton;
    internal Button RefreshButton => QueueRefreshButton;
    internal DataGrid ActiveQueue => QueueDataGrid;
    internal TextBlock TotalWood => QueueTotalWoodTextBlock;
    internal TextBlock TotalClay => QueueTotalClayTextBlock;
    internal TextBlock TotalIron => QueueTotalIronTextBlock;
    internal TextBlock TotalCrop => QueueTotalCropTextBlock;
    internal TextBlock TotalTime => QueueTotalTimeTextBlock;
    internal TextBlock TotalTimeConstructFaster => QueueTotalTimeConstructFasterTextBlock;
    internal TabItem HistoryTab => HistoryQueueTabItem;
    internal DataGrid HistoryQueue => QueueHistoryDataGrid;
    internal Button ClearAccountButton => QueueClearButton;

    private void QueueSectionTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.OnQueueSectionSelectionChanged(sender, e);
}
