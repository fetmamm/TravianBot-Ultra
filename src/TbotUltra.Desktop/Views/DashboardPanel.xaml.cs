using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace TbotUltra.Desktop.Views;

public partial class DashboardPanel : UserControl
{
    private MainWindow? _host;

    public DashboardPanel() => InitializeComponent();

    private MainWindow? Host => _host ??= Window.GetWindow(this) as MainWindow;

    internal TextBlock VillagesInfo => VillagesInfoTextBlock;
    internal TextBlock LastScanInfo => LastScanInfoTextBlock;
    internal Ellipse AutomationRunStateDot => AutomationLoopRunStateDot;
    internal TextBlock AutomationRunStateText => AutomationLoopRunStateTextBlock;
    internal ListBox AutomationLoopList => AutomationLoopListBox;
    internal CheckBox AutoCollectTasks => AutoCollectTasksCheckBox;
    internal CheckBox AutoCollectDailyQuests => AutoCollectDailyQuestsCheckBox;
    internal CheckBox ProductionBonusVideo => ProductionBonusVideoCheckBox;
    internal CheckBox VillageStatusSweep => VillageStatusSweepCheckBox;
    internal ItemsControl VillageList => DashboardVillageList;

    private void DashboardClearTimersButton_Click(object sender, RoutedEventArgs e) => Host?.OnDashboardClearTimersClicked(sender, e);
    private void AutomationLoopToggleButton_Click(object sender, RoutedEventArgs e) => Host?.OnAutomationLoopToggleClicked(sender, e);
    private void AutomationLoopListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Host?.OnAutomationLoopPreviewMouseLeftButtonDown(sender, e);
    private void AutomationLoopListBox_PreviewMouseMove(object sender, MouseEventArgs e) => Host?.OnAutomationLoopPreviewMouseMove(sender, e);
    private void AutomationLoopListBox_DragOver(object sender, DragEventArgs e) => Host?.OnAutomationLoopDragOver(sender, e);
    private void AutomationLoopListBox_Drop(object sender, DragEventArgs e) => Host?.OnAutomationLoopDrop(sender, e);
    private void AutoCollectTasksSetting_Changed(object sender, RoutedEventArgs e) => Host?.OnAutoCollectTasksSettingChanged(sender, e);
    private void AutoCollectDailyQuestsSetting_Changed(object sender, RoutedEventArgs e) => Host?.OnAutoCollectDailyQuestsSettingChanged(sender, e);
    private void GoldSpendingSettingsButton_Click(object sender, RoutedEventArgs e) => Host?.OnGoldSpendingSettingsClicked(sender, e);
    private void ProductionBonusVideoSetting_Changed(object sender, RoutedEventArgs e) => Host?.OnProductionBonusVideoSettingChanged(sender, e);
    private void ProductionBonusSettingsButton_Click(object sender, RoutedEventArgs e) => Host?.OnProductionBonusSettingsClicked(sender, e);
    private void VillageStatusSweepSetting_Changed(object sender, RoutedEventArgs e) => Host?.OnVillageStatusSweepSettingChanged(sender, e);
    private void VillageStatusSweepSettingsButton_Click(object sender, RoutedEventArgs e) => Host?.OnVillageStatusSweepSettingsClicked(sender, e);
    private void IncomingAttackButton_Click(object sender, RoutedEventArgs e) => Host?.OnIncomingAttackClicked(sender, e);
    private void VillageQueueButton_Click(object sender, RoutedEventArgs e) => Host?.OnDashboardVillageQueueClicked(sender, e);
}
