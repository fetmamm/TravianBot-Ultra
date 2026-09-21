using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private Views.DashboardPanel DashboardPanelControl => DashboardHubPanelControl.DashboardPanel;
    private TextBlock VillagesInfoTextBlock => DashboardPanelControl.VillagesInfo;
    private TextBlock LastScanInfoTextBlock => DashboardPanelControl.LastScanInfo;
    private Ellipse AutomationLoopRunStateDot => DashboardPanelControl.AutomationRunStateDot;
    private TextBlock AutomationLoopRunStateTextBlock => DashboardPanelControl.AutomationRunStateText;
    private ListBox AutomationLoopListBox => DashboardPanelControl.AutomationLoopList;
    private CheckBox AutoCollectTasksCheckBox => DashboardPanelControl.AutoCollectTasks;
    private CheckBox AutoCollectDailyQuestsCheckBox => DashboardPanelControl.AutoCollectDailyQuests;
    private CheckBox ProductionBonusVideoCheckBox => DashboardPanelControl.ProductionBonusVideo;
    private CheckBox VillageStatusSweepCheckBox => DashboardPanelControl.VillageStatusSweep;
    private ItemsControl DashboardVillageList => DashboardPanelControl.VillageList;

    internal void OnDashboardVillageTabSelected()
    {
        // Queue owns the build-time estimate projection. Rebuild it locally before the overview panel is
        // created or reused, so opening Village overview always starts from the same totals as Queue.
        RefreshQueueUi();
        EnsureDashboardVillagePanels();
    }
    internal void OnDashboardClearTimersClicked(object sender, RoutedEventArgs e) => DashboardClearTimersButton_Click(sender, e);
    internal void OnAutomationLoopToggleClicked(object sender, RoutedEventArgs e) => AutomationLoopToggleButton_Click(sender, e);
    internal void OnAutomationLoopPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => AutomationLoopListBox_PreviewMouseLeftButtonDown(sender, e);
    internal void OnAutomationLoopPreviewMouseMove(object sender, MouseEventArgs e) => AutomationLoopListBox_PreviewMouseMove(sender, e);
    internal void OnAutomationLoopDragOver(object sender, DragEventArgs e) => AutomationLoopListBox_DragOver(sender, e);
    internal void OnAutomationLoopDrop(object sender, DragEventArgs e) => AutomationLoopListBox_Drop(sender, e);
    internal void OnAutoCollectTasksSettingChanged(object sender, RoutedEventArgs e) => AutoCollectTasksSetting_Changed(sender, e);
    internal void OnAutoCollectDailyQuestsSettingChanged(object sender, RoutedEventArgs e) => AutoCollectDailyQuestsSetting_Changed(sender, e);
    internal void OnGoldSpendingSettingsClicked(object sender, RoutedEventArgs e) => GoldSpendingSettingsButton_Click(sender, e);
    internal void OnProductionBonusVideoSettingChanged(object sender, RoutedEventArgs e) => ProductionBonusVideoSetting_Changed(sender, e);
    internal void OnProductionBonusSettingsClicked(object sender, RoutedEventArgs e) => ProductionBonusSettingsButton_Click(sender, e);
    internal void OnVillageStatusSweepSettingChanged(object sender, RoutedEventArgs e) => VillageStatusSweepSetting_Changed(sender, e);
    internal void OnVillageStatusSweepSettingsClicked(object sender, RoutedEventArgs e) => VillageStatusSweepSettingsButton_Click(sender, e);
    internal void OnIncomingAttackClicked(object sender, RoutedEventArgs e) => IncomingAttackButton_Click(sender, e);
    internal void OnDashboardVillageQueueClicked(object sender, RoutedEventArgs e) => DashboardVillageQueueButton_Click(sender, e);
}
