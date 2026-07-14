using System.Windows.Controls;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private TextBlock BuildQueueStatusTextBlock => QueuePanelControl.BuildQueueStatus;
    private DataGrid TravianBuildQueueDataGrid => QueuePanelControl.TravianBuildQueue;
    private DataGrid TravianSmithyQueueDataGrid => QueuePanelControl.TravianSmithyQueue;
    private TabControl QueueSectionTabControl => QueuePanelControl.QueueSections;
    private TextBlock QueueInfoTextBlock => QueuePanelControl.QueueInfo;
    private TextBlock QueueSelectedVillageTextBlock => QueuePanelControl.SelectedVillage;
    private Button QueueRemoveButton => QueuePanelControl.RemoveButton;
    private Button QueueMoveUpButton => QueuePanelControl.MoveUpButton;
    private Button QueueMoveDownButton => QueuePanelControl.MoveDownButton;
    private Button QueueRefreshButton => QueuePanelControl.RefreshButton;
    private DataGrid QueueDataGrid => QueuePanelControl.ActiveQueue;
    private TextBlock QueueTotalWoodTextBlock => QueuePanelControl.TotalWood;
    private TextBlock QueueTotalClayTextBlock => QueuePanelControl.TotalClay;
    private TextBlock QueueTotalIronTextBlock => QueuePanelControl.TotalIron;
    private TextBlock QueueTotalCropTextBlock => QueuePanelControl.TotalCrop;
    private TextBlock QueueTotalTimeTextBlock => QueuePanelControl.TotalTime;
    private TextBlock QueueTotalTimeConstructFasterTextBlock => QueuePanelControl.TotalTimeConstructFaster;
    private TabItem HistoryQueueTabItem => QueuePanelControl.HistoryTab;
    private DataGrid QueueHistoryDataGrid => QueuePanelControl.HistoryQueue;
    private Button QueueClearButton => QueuePanelControl.ClearAccountButton;

    internal void OnQueueSectionSelectionChanged(object sender, SelectionChangedEventArgs e) => QueueSectionTabControl_SelectionChanged(sender, e);
    internal void OnQueueRemoveClicked(object sender, System.Windows.RoutedEventArgs e) => QueueRemoveButton_Click(sender, e);
    internal void OnQueueRedoClicked(object sender, System.Windows.RoutedEventArgs e) => QueueRedoButton_Click(sender, e);
    internal void OnQueueMoveUpClicked(object sender, System.Windows.RoutedEventArgs e) => QueueMoveUpButton_Click(sender, e);
    internal void OnQueueMoveDownClicked(object sender, System.Windows.RoutedEventArgs e) => QueueMoveDownButton_Click(sender, e);
    internal void OnQueueRefreshClicked(object sender, System.Windows.RoutedEventArgs e) => QueueRefreshButton_Click(sender, e);
    internal void OnClearVillageQueueClicked(object sender, System.Windows.RoutedEventArgs e) => ClearVillageQueueButton_Click(sender, e);
    internal void OnQueueClearClicked(object sender, System.Windows.RoutedEventArgs e) => QueueClearButton_Click(sender, e);
    internal void OnQueuePopoutClicked(object sender, System.Windows.RoutedEventArgs e) => QueuePopoutButton_Click(sender, e);
}
