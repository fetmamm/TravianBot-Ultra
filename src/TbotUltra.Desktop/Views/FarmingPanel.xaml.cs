using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class FarmingPanel : UserControl
{
    public static readonly DependencyProperty NextSendDisplayProperty = DependencyProperty.Register(
        nameof(NextSendDisplay),
        typeof(string),
        typeof(FarmingPanel),
        new PropertyMetadata("Next send: --"));

    public FarmingPanel() => InitializeComponent();

    internal Button AnalyzeButton => AnalyzeFarmListsButton;
    internal Button AddFarmsButton => AddFarmsToListButton;
    internal Button CreateListButton => CreateFarmListButton;
    internal TextBlock StatusText => FarmingStatusTextBlock;
    internal Button SendAllButton => FarmListSendAllNowButton;
    internal ItemsControl FarmLists => FarmListsItemsControl;
    internal RadioButton SendListPerListOption => FarmSendListPerListRadioButton;
    internal RadioButton SendSharedScheduleOption => FarmSendSharedScheduleRadioButton;
    internal RadioButton SendAllAtOnceOption => FarmSendAllAtOnceRadioButton;
    internal TextBox DispatchDelayMin => FarmDispatchDelayMinTextBox;
    internal TextBox DispatchDelayMax => FarmDispatchDelayMaxTextBox;
    internal CheckBox DeactivateRedLossesOption => DeactivateRedFarmLossesCheckBox;
    internal CheckBox DeactivateYellowLossesOption => DeactivateYellowFarmLossesCheckBox;
    internal CheckBox DeactivateOasisLossesOption => DeactivateFarmOasisLossesCheckBox;
    internal CheckBox DeactivateRedOasisLossesOption => DeactivateRedFarmOasisLossesCheckBox;
    internal CheckBox DeactivateYellowOasisLossesOption => DeactivateYellowFarmOasisLossesCheckBox;
    internal CheckBox MoveRedLossesOption => MoveRedFarmLossesCheckBox;
    internal CheckBox MoveYellowLossesOption => MoveYellowFarmLossesCheckBox;
    internal ComboBox RedLossDestinationOption => RedFarmLossDestinationComboBox;
    internal ComboBox YellowLossDestinationOption => YellowFarmLossDestinationComboBox;
    internal ContentControl TravcoWorkspaceHost => TravcoToolsHost;

    public string NextSendDisplay
    {
        get => (string)GetValue(NextSendDisplayProperty);
        set => SetValue(NextSendDisplayProperty, value);
    }

    internal void SetNextSendDisplay(string text) => NextSendDisplay = text;

}
