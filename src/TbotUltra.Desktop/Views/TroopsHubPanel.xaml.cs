using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class TroopsHubPanel : UserControl
{
    public TroopsHubPanel() => InitializeComponent();

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
