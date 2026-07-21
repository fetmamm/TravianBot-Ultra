using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class FarmingPanel : UserControl
{
    private MainWindow? _host;

    public FarmingPanel() => InitializeComponent();

    private MainWindow? Host => _host ??= Window.GetWindow(this) as MainWindow;

    internal Button AnalyzeButton => AnalyzeFarmListsButton;
    internal Button AddFarmsButton => AddFarmsToListButton;
    internal Button CreateListButton => CreateFarmListButton;
    internal TextBlock StatusText => FarmingStatusTextBlock;
    internal Button SendAllButton => FarmListSendAllNowButton;
    internal ItemsControl FarmLists => FarmListsItemsControl;
    internal Button CancelOperationButton => CancelFarmingOperationButton;
    internal RadioButton SendListPerListOption => FarmSendListPerListRadioButton;
    internal RadioButton SendAllAtOnceOption => FarmSendAllAtOnceRadioButton;
    internal TextBox DispatchDelayMin => FarmDispatchDelayMinTextBox;
    internal TextBox DispatchDelayMax => FarmDispatchDelayMaxTextBox;
    internal CheckBox DeactivateLossesOption => DeactivateFarmLossesCheckBox;
    internal CheckBox DeactivateOasisLossesOption => DeactivateFarmOasisLossesCheckBox;
    internal Button TravcoSearchButton => TravcoInactiveSearchButton;

    private void AnalyzeFarmListsButton_Click(object sender, RoutedEventArgs e) => Host?.OnAnalyzeFarmListsClicked(sender, e);
    private void AddFarmsToListButton_Click(object sender, RoutedEventArgs e) => Host?.OnAddFarmsToListClicked(sender, e);
    private void CreateFarmListButton_Click(object sender, RoutedEventArgs e) => Host?.OnCreateFarmListClicked(sender, e);
    private void FarmListSendAllNowButton_Click(object sender, RoutedEventArgs e) => Host?.OnFarmListSendAllNowClicked(sender, e);
    private void FarmListSendNowButton_Click(object sender, RoutedEventArgs e) => Host?.OnFarmListSendNowClicked(sender, e);
    private void CancelFarmingOperationButton_Click(object sender, RoutedEventArgs e) => Host?.OnCancelFarmingOperationClicked(sender, e);
    private void FarmingSettings_Changed(object sender, RoutedEventArgs e) => Host?.OnFarmingSettingsChanged(sender, e);
    private void TravcoInactiveSearchButton_Click(object sender, RoutedEventArgs e) => Host?.OnTravcoInactiveSearchClicked(sender, e);
}
