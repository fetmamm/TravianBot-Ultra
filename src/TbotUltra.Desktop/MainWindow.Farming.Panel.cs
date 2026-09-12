namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private System.Windows.Controls.Button AnalyzeFarmListsButton => FarmingPanelControl.AnalyzeButton;
    private System.Windows.Controls.Button AddFarmsToListButton => FarmingPanelControl.AddFarmsButton;
    private System.Windows.Controls.Button CreateFarmListButton => FarmingPanelControl.CreateListButton;
    private System.Windows.Controls.TextBlock FarmingStatusTextBlock => FarmingPanelControl.StatusText;
    private System.Windows.Controls.Button FarmListSendAllNowButton => FarmingPanelControl.SendAllButton;
    private System.Windows.Controls.ItemsControl FarmListsItemsControl => FarmingPanelControl.FarmLists;
    private System.Windows.Controls.RadioButton FarmSendListPerListRadioButton => FarmingPanelControl.SendListPerListOption;
    private System.Windows.Controls.RadioButton FarmSendSharedScheduleRadioButton => FarmingPanelControl.SendSharedScheduleOption;
    private System.Windows.Controls.RadioButton FarmSendAllAtOnceRadioButton => FarmingPanelControl.SendAllAtOnceOption;
    private System.Windows.Controls.TextBox FarmDispatchDelayMinTextBox => FarmingPanelControl.DispatchDelayMin;
    private System.Windows.Controls.TextBox FarmDispatchDelayMaxTextBox => FarmingPanelControl.DispatchDelayMax;
    private System.Windows.Controls.CheckBox DeactivateRedFarmLossesCheckBox => FarmingPanelControl.DeactivateRedLossesOption;
    private System.Windows.Controls.CheckBox DeactivateYellowFarmLossesCheckBox => FarmingPanelControl.DeactivateYellowLossesOption;
    private System.Windows.Controls.CheckBox DeactivateFarmOasisLossesCheckBox => FarmingPanelControl.DeactivateOasisLossesOption;
    private System.Windows.Controls.CheckBox DeactivateRedFarmOasisLossesCheckBox => FarmingPanelControl.DeactivateRedOasisLossesOption;
    private System.Windows.Controls.CheckBox DeactivateYellowFarmOasisLossesCheckBox => FarmingPanelControl.DeactivateYellowOasisLossesOption;
    private System.Windows.Controls.CheckBox MoveRedFarmLossesCheckBox => FarmingPanelControl.MoveRedLossesOption;
    private System.Windows.Controls.CheckBox MoveYellowFarmLossesCheckBox => FarmingPanelControl.MoveYellowLossesOption;
    private System.Windows.Controls.ComboBox RedFarmLossDestinationComboBox => FarmingPanelControl.RedLossDestinationOption;
    private System.Windows.Controls.ComboBox YellowFarmLossDestinationComboBox => FarmingPanelControl.YellowLossDestinationOption;
}
