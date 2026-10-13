using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class TroopsHubPanel : UserControl
{
    public TroopsHubPanel() => InitializeComponent();

    internal event Action? UpgradeTroopsSelected;
    internal bool IsUpgradeTroopsSelected => ReferenceEquals(TroopsTabControl.SelectedItem, UpgradeTroopsTabItem);
    internal TroopsPanel UpgradeTroopsPanel => UpgradeTroopsContent;

    private void TroopsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, TroopsTabControl) && IsUpgradeTroopsSelected)
        {
            UpgradeTroopsSelected?.Invoke();
        }
    }

    internal ReinforcementsPanel ReinforcementsPanel => ReinforcementsContent;
    internal ReinforcementsPanel CatapultWavesPanel => CatapultWavesContent;
    internal DataGrid IncomingAttacksGrid => IncomingAttacksContent.IncomingAttacksGrid;
    internal ItemsControl IncomingAttackMonitoringVillages => IncomingAttacksContent.IncomingAttackMonitoringVillages;
    internal CheckBox IncomingAttackSoundAlert => IncomingAttacksContent.IncomingAttackSoundAlert;
    internal ComboBox IncomingAttackSoundCooldown => IncomingAttacksContent.IncomingAttackSoundCooldown;
    internal TroopEvasionPanel EvasionPanel => EvasionContent;

    internal void SelectIncomingAttacks()
    {
        TroopsTabControl.SelectedItem = IncomingAttacksTabItem;
    }
}
