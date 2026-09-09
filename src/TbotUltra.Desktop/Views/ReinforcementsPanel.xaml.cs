using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class ReinforcementsPanel : UserControl
{
    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(
        nameof(Section),
        typeof(string),
        typeof(ReinforcementsPanel),
        new PropertyMetadata("All"));

    public string Section
    {
        get => (string)GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    private MainWindow? _host;

    public ReinforcementsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplySection();
    }

    private void ApplySection()
    {
        if (string.Equals(Section, "All", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SendTroopsTabControl.SelectedItem = Section.ToUpperInvariant() switch
        {
            "INCOMING" => IncomingAttacksTabItem,
            "CATAPULT" => CatapultWavesTabItem,
            _ => ReinforcementsTabItem,
        };
        SendTroopsTabControl.Template = (ControlTemplate)FindResource("ContentOnlyTabControlTemplate");
    }

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
    internal TabControl SendTroopsTabs => SendTroopsTabControl;
    internal TabItem IncomingAttacksTab => IncomingAttacksTabItem;
    internal DataGrid IncomingAttacksGrid => IncomingAttackDataGrid;
    internal ItemsControl IncomingAttackMonitoringVillages => IncomingAttackMonitoringVillageItemsControl;
    internal CheckBox IncomingAttackSoundAlert => IncomingAttackSoundAlertCheckBox;
    internal ComboBox IncomingAttackSoundCooldown => IncomingAttackSoundCooldownComboBox;

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

    private void IncomingAttackMonitoring_Changed(object sender, RoutedEventArgs e) =>
        Host?.OnIncomingAttackMonitoringChanged(sender, e);

    private void ToggleAllIncomingAttackMonitoringButton_Click(object sender, RoutedEventArgs e) =>
        Host?.OnToggleAllIncomingAttackMonitoringClicked(sender, e);

    private void IncomingAttackSoundSetting_Changed(object sender, RoutedEventArgs e) =>
        Host?.OnIncomingAttackSoundSettingChanged(sender, e);

    private void ClearIncomingAttackListButton_Click(object sender, RoutedEventArgs e) =>
        Host?.OnClearIncomingAttackListClicked(sender, e);
}
