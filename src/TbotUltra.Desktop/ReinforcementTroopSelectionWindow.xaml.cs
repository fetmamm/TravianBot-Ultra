using System.Collections.ObjectModel;
using System.Windows;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class ReinforcementTroopSelectionWindow : Window
{
    private readonly ObservableCollection<ReinforcementTroopRuleItem> _troopRules;
    public bool SyncSettingsRequested { get; private set; }

    public ReinforcementTroopSelectionWindow(ObservableCollection<ReinforcementTroopRuleItem> troopRules, string villageName)
    {
        InitializeComponent();
        _troopRules = troopRules;
        VillageNameTextBlock.Text = villageName;
        TroopRulesItemsControl.ItemsSource = troopRules;
    }

    private void MarkAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var rule in _troopRules)
        {
            rule.IsEnabled = true;
        }
    }

    private void UnmarkAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var rule in _troopRules)
        {
            rule.IsEnabled = false;
        }
    }

    private void SyncSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SyncSettingsRequested = true;
        DialogResult = true;
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
