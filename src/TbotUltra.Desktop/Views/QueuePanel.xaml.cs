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
    internal TextBlock QueueInfo => QueueInfoTextBlock;
    internal TextBlock SelectedVillage => QueueSelectedVillageTextBlock;
    internal Button RemoveButton => QueueRemoveButton;
    internal Button MoveUpButton => QueueMoveUpButton;
    internal Button MoveDownButton => QueueMoveDownButton;
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
    private void QueueRemoveButton_Click(object sender, RoutedEventArgs e) => Host?.OnQueueRemoveClicked(sender, e);
    private void QueueRedoButton_Click(object sender, RoutedEventArgs e) => Host?.OnQueueRedoClicked(sender, e);
    private void QueueMoveUpButton_Click(object sender, RoutedEventArgs e) => Host?.OnQueueMoveUpClicked(sender, e);
    private void QueueMoveDownButton_Click(object sender, RoutedEventArgs e) => Host?.OnQueueMoveDownClicked(sender, e);
    private void QueueRefreshButton_Click(object sender, RoutedEventArgs e) => Host?.OnQueueRefreshClicked(sender, e);
    private void ClearVillageQueueButton_Click(object sender, RoutedEventArgs e) => Host?.OnClearVillageQueueClicked(sender, e);
    private void QueueClearButton_Click(object sender, RoutedEventArgs e) => Host?.OnQueueClearClicked(sender, e);
    private void QueuePopoutButton_Click(object sender, RoutedEventArgs e) => Host?.OnQueuePopoutClicked(sender, e);
}
