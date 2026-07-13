using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class ReinforcementsPanel : UserControl
{
    private MainWindow? _host;

    public ReinforcementsPanel() => InitializeComponent();

    private MainWindow? Host => _host ??= Window.GetWindow(this) as MainWindow;

    internal ComboBox TargetVillage => ReinforcementTargetVillageComboBox;
    internal Button MarkAllTroopsButton => ReinforcementMarkAllTroopsButton;
    internal ItemsControl SourceVillages => ReinforcementSourceVillagesItemsControl;
    internal TextBlock TroopsSummary => ReinforcementTroopsSummaryTextBlock;
    internal TextBox SendMinMinutes => ReinforcementSendMinMinutesTextBox;
    internal TextBox SendMaxMinutes => ReinforcementSendMaxMinutesTextBox;
    internal TextBlock TroopsDetail => ReinforcementTroopsDetailTextBlock;
    internal TextBlock Status => ReinforcementStatusTextBlock;
    internal Button QueueNowButton => ReinforcementQueueNowButton;
    internal Button CatapultWavesButton => StartCatapultWavesButton;
    internal TextBlock CatapultWavesStatus => CatapultWavesStatusTextBlock;

    private void ReinforcementSetting_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Host?.OnReinforcementSettingSelectionChanged(sender, e);

    private void MarkAllReinforcementTroopsButton_Click(object sender, RoutedEventArgs e) =>
        Host?.OnMarkAllReinforcementTroopsClicked(sender, e);

    private void ChooseReinforcementVillageTroopsButton_Click(object sender, RoutedEventArgs e) =>
        Host?.OnChooseReinforcementVillageTroopsClicked(sender, e);

    private void ReinforcementSetting_TextChanged(object sender, RoutedEventArgs e) =>
        Host?.OnReinforcementSettingTextChanged(sender, e);

    private void QueueReinforcementsNowButton_Click(object sender, RoutedEventArgs e) =>
        Host?.OnQueueReinforcementsNowClicked(sender, e);

    private void StartCatapultWavesButton_Click(object sender, RoutedEventArgs e) =>
        Host?.OnStartCatapultWavesClicked(sender, e);
}
