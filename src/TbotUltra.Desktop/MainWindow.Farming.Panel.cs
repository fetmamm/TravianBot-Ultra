using System.Windows;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private System.Windows.Controls.Button AnalyzeFarmListsButton => FarmingPanelControl.AnalyzeButton;
    private System.Windows.Controls.Button AddFarmsToListButton => FarmingPanelControl.AddFarmsButton;
    private System.Windows.Controls.Button CreateFarmListButton => FarmingPanelControl.CreateListButton;
    private System.Windows.Controls.TextBlock FarmingStatusTextBlock => FarmingPanelControl.StatusText;
    private System.Windows.Controls.Button FarmListSendAllNowButton => FarmingPanelControl.SendAllButton;
    private System.Windows.Controls.ItemsControl FarmListsItemsControl => FarmingPanelControl.FarmLists;
    private System.Windows.Controls.Button CancelFarmingOperationButton => FarmingPanelControl.CancelOperationButton;
    private System.Windows.Controls.TextBlock ManualFarmingExecutionCountTextBlock => FarmingPanelControl.ManualExecutionCount;
    private System.Windows.Controls.Button StartManualFarmingButton => FarmingPanelControl.StartManualButton;
    private System.Windows.Controls.RadioButton FarmSendListPerListRadioButton => FarmingPanelControl.SendListPerListOption;
    private System.Windows.Controls.RadioButton FarmSendAllAtOnceRadioButton => FarmingPanelControl.SendAllAtOnceOption;
    private System.Windows.Controls.TextBox FarmDispatchDelayMinTextBox => FarmingPanelControl.DispatchDelayMin;
    private System.Windows.Controls.TextBox FarmDispatchDelayMaxTextBox => FarmingPanelControl.DispatchDelayMax;
    private System.Windows.Controls.CheckBox DeactivateFarmLossesCheckBox => FarmingPanelControl.DeactivateLossesOption;
    private System.Windows.Controls.CheckBox DeactivateFarmOasisLossesCheckBox => FarmingPanelControl.DeactivateOasisLossesOption;
    private System.Windows.Controls.Button TravcoInactiveSearchButton => FarmingPanelControl.TravcoSearchButton;

    internal void OnAnalyzeFarmListsClicked(object sender, RoutedEventArgs e) => AnalyzeFarmListsButton_Click(sender, e);
    internal void OnAddFarmsToListClicked(object sender, RoutedEventArgs e) => AddFarmsToListButton_Click(sender, e);
    internal void OnCreateFarmListClicked(object sender, RoutedEventArgs e) => CreateFarmListButton_Click(sender, e);
    internal void OnFarmListSendAllNowClicked(object sender, RoutedEventArgs e) => FarmListSendAllNowButton_Click(sender, e);
    internal void OnFarmListSendNowClicked(object sender, RoutedEventArgs e) => FarmListSendNowButton_Click(sender, e);
    internal void OnCancelFarmingOperationClicked(object sender, RoutedEventArgs e) => CancelFarmingOperationButton_Click(sender, e);
    internal void OnStartManualFarmingClicked(object sender, RoutedEventArgs e) => StartManualFarmingButton_Click(sender, e);
    internal void OnFarmingSettingsChanged(object sender, RoutedEventArgs e) => FarmingSettings_Changed(sender, e);
    internal void OnTravcoInactiveSearchClicked(object sender, RoutedEventArgs e) => TravcoInactiveSearchButton_Click(sender, e);
}
