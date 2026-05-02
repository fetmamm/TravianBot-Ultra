using System.Windows;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class BuildingSlotActionsWindow : Window
{
    public BuildingSlotAction SelectedAction { get; private set; } = BuildingSlotAction.None;

    public BuildingSlotActionsWindow(BuildingSlotRow slot)
    {
        InitializeComponent();

        TitleTextBlock.Text = $"{slot.SlotLabel} actions";
        SubtitleTextBlock.Text = slot.IsOccupied
            ? slot.IsMaxLevel
                ? $"{slot.Name} level {slot.LevelLabel} (max)"
                : $"{slot.Name} level {slot.LevelLabel}"
            : "Empty slot";

        BuildBuildingButton.IsEnabled = !slot.IsOccupied;
        UpgradeButton.IsEnabled = slot.IsOccupied && !slot.IsMaxLevel;
        UpgradeToMaxButton.IsEnabled = slot.IsOccupied && !slot.IsMaxLevel;
        DemolishButton.IsEnabled = slot.IsOccupied;
    }

    private void BuildBuildingButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = BuildingSlotAction.BuildBuilding;
        DialogResult = true;
        Close();
    }

    private void UpgradeButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = BuildingSlotAction.Upgrade;
        DialogResult = true;
        Close();
    }

    private void UpgradeToMaxButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = BuildingSlotAction.UpgradeToMax;
        DialogResult = true;
        Close();
    }

    private void DemolishButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = BuildingSlotAction.Demolish;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

public enum BuildingSlotAction
{
    None = 0,
    BuildBuilding = 1,
    Upgrade = 2,
    UpgradeToMax = 3,
    Demolish = 4,
}
