using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private ComboBox ReinforcementTargetVillageComboBox => ReinforcementsPanelControl.TargetVillage;
    private Button ReinforcementMarkAllTroopsButton => ReinforcementsPanelControl.MarkAllTroopsButton;
    private ItemsControl ReinforcementSourceVillagesItemsControl => ReinforcementsPanelControl.SourceVillages;
    private TextBlock ReinforcementTroopsSummaryTextBlock => ReinforcementsPanelControl.TroopsSummary;
    private TextBox ReinforcementSendMinMinutesTextBox => ReinforcementsPanelControl.SendMinMinutes;
    private TextBox ReinforcementSendMaxMinutesTextBox => ReinforcementsPanelControl.SendMaxMinutes;
    private TextBlock ReinforcementTroopsDetailTextBlock => ReinforcementsPanelControl.TroopsDetail;
    private TextBlock ReinforcementStatusTextBlock => ReinforcementsPanelControl.Status;
    private Button ReinforcementQueueNowButton => ReinforcementsPanelControl.QueueNowButton;
    private Button StartCatapultWavesButton => ReinforcementsPanelControl.CatapultWavesButton;
    private TextBlock CatapultWavesStatusTextBlock => ReinforcementsPanelControl.CatapultWavesStatus;

    internal void OnReinforcementSettingSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ReinforcementSetting_SelectionChanged(sender, e);

    internal void OnMarkAllReinforcementTroopsClicked(object sender, RoutedEventArgs e) =>
        MarkAllReinforcementTroopsButton_Click(sender, e);

    internal void OnChooseReinforcementVillageTroopsClicked(object sender, RoutedEventArgs e) =>
        ChooseReinforcementVillageTroopsButton_Click(sender, e);

    internal void OnReinforcementSettingTextChanged(object sender, RoutedEventArgs e) =>
        ReinforcementSetting_TextChanged(sender, e);

    internal void OnQueueReinforcementsNowClicked(object sender, RoutedEventArgs e) =>
        QueueReinforcementsNowButton_Click(sender, e);

    internal void OnStartCatapultWavesClicked(object sender, RoutedEventArgs e) =>
        StartCatapultWavesButton_Click(sender, e);
}
