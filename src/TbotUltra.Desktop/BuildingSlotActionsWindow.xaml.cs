using System.Windows;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class BuildingSlotActionsWindow : Window
{
    public event EventHandler? UpgradeOneLevelRequested;

    public BuildingSlotAction SelectedAction { get; private set; } = BuildingSlotAction.None;

    public BuildingSlotActionsWindow(
        BuildingSlotRow slot,
        bool canDemolish,
        string demolishRequirementText,
        BuildingNextLevelEstimate? nextLevel = null)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        TitleTextBlock.Text = $"{slot.SlotLabel} actions";
        DemolishButton.IsEnabled = slot.IsOccupied && canDemolish;
        DemolishRequirementTextBlock.Text = demolishRequirementText;
        DemolishRequirementTextBlock.Visibility = canDemolish || string.IsNullOrWhiteSpace(demolishRequirementText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ApplyState(slot, nextLevel);
    }

    // Refreshes the slot-dependent parts of the popup (subtitle, upgrade buttons and the next-level
    // estimate) so it stays responsive when an upgrade is queued without closing the window.
    public void ApplyState(BuildingSlotRow slot, BuildingNextLevelEstimate? nextLevel)
    {
        SubtitleTextBlock.Text = slot.IsOccupied
            ? slot.IsMaxLevel
                ? $"{slot.Name} level {slot.LevelLabel} (max)"
                : $"{slot.Name} level {slot.LevelLabel}"
            : slot.HasPendingConstruct
                ? $"{slot.PendingConstructName} queued for construction"
            : "Empty slot";

        var canUpgrade = slot.CanQueueUpgrade && !slot.IsMaxLevel;
        BuildBuildingButton.IsEnabled = !slot.IsOccupied && !slot.HasPendingConstruct;
        UpgradeButton.IsEnabled = canUpgrade;
        UpgradeOneLevelButton.IsEnabled = canUpgrade;
        UpgradeToMaxButton.IsEnabled = canUpgrade;

        if (nextLevel is not null)
        {
            NextLevelTitleTextBlock.Text = $"Upgrade to level {nextLevel.Level}";
            NextLevelTimeTextBlock.Text = nextLevel.TimeText;
            NextLevelWoodTextBlock.Text = nextLevel.WoodText;
            NextLevelClayTextBlock.Text = nextLevel.ClayText;
            NextLevelIronTextBlock.Text = nextLevel.IronText;
            NextLevelCropTextBlock.Text = nextLevel.CropText;
            NextLevelEstimateBorder.Visibility = Visibility.Visible;
        }
        else
        {
            NextLevelEstimateBorder.Visibility = Visibility.Collapsed;
        }
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

    private void UpgradeOneLevelButton_Click(object sender, RoutedEventArgs e)
    {
        UpgradeOneLevelRequested?.Invoke(this, EventArgs.Empty);
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

// Next-level build estimate shown in the slot popup. Strings are pre-formatted by the caller so the
// window stays a dumb view (colors are applied per-resource in XAML).
public sealed record BuildingNextLevelEstimate(
    int Level,
    string TimeText,
    string WoodText,
    string ClayText,
    string IronText,
    string CropText);

public enum BuildingSlotAction
{
    None = 0,
    BuildBuilding = 1,
    Upgrade = 2,
    UpgradeToMax = 3,
    Demolish = 4,
}
