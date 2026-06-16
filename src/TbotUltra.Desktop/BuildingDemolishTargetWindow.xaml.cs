using System.Windows;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class BuildingDemolishTargetWindow : Window
{
    public int SelectedTargetLevel { get; private set; }

    public BuildingDemolishTargetWindow(BuildingSlotRow slot)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        var currentLevel = slot.Level ?? 0;
        TitleTextBlock.Text = $"Demolish {slot.Name}";
        SubtitleTextBlock.Text = $"{slot.SlotLabel}, level {slot.LevelLabel}.";

        for (var level = Math.Max(0, currentLevel - 1); level >= 0; level--)
        {
            TargetLevelComboBox.Items.Add(level);
        }

        TargetLevelComboBox.SelectedIndex = TargetLevelComboBox.Items.Count > 0 ? 0 : -1;
    }

    private void DemolishWholeButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedTargetLevel = 0;
        DialogResult = true;
        Close();
    }

    private void QueueTargetButton_Click(object sender, RoutedEventArgs e)
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
