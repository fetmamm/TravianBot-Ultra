using System.Windows;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class BuildingUpgradeTargetWindow : Window
{
    public int SelectedTargetLevel { get; private set; }

    public BuildingUpgradeTargetWindow(BuildingSlotRow slot, int maxLevel)
    {
        InitializeComponent();

        var currentLevel = slot.UpgradeBaseLevel;
        TitleTextBlock.Text = $"Upgrade {slot.UpgradeName}";
        SubtitleTextBlock.Text = $"{slot.SlotLabel}, level {currentLevel}. Max level: {maxLevel}.";

        for (var level = currentLevel + 1; level <= maxLevel; level++)
        {
            TargetLevelComboBox.Items.Add(level);
        }

        TargetLevelComboBox.SelectedIndex = TargetLevelComboBox.Items.Count > 0 ? 0 : -1;
    }

    private void QueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (TargetLevelComboBox.SelectedItem is not int level)
        {
            return;
        }

        SelectedTargetLevel = level;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
