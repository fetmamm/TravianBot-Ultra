using System;
using System.Windows;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class BuildingUpgradeTargetWindow : Window
{
    // Returns the cumulative build time + cost up to the given target level, or null when unavailable.
    private readonly Func<int, BuildingNextLevelEstimate?>? _estimateProvider;
    private readonly int _currentLevel;

    public int SelectedTargetLevel { get; private set; }

    public BuildingUpgradeTargetWindow(
        BuildingSlotRow slot,
        int maxLevel,
        Func<int, BuildingNextLevelEstimate?>? estimateProvider = null)
    {
        InitializeComponent();

        _estimateProvider = estimateProvider;
        _currentLevel = slot.UpgradeBaseLevel;
        TitleTextBlock.Text = $"Upgrade {slot.UpgradeName}";
        SubtitleTextBlock.Text = $"{slot.SlotLabel}, level {_currentLevel}. Max level: {maxLevel}.";

        for (var level = _currentLevel + 1; level <= maxLevel; level++)
        {
            TargetLevelComboBox.Items.Add(level);
        }

        TargetLevelComboBox.SelectionChanged += (_, _) => UpdateEstimate();
        TargetLevelComboBox.SelectedIndex = TargetLevelComboBox.Items.Count > 0 ? 0 : -1;
    }

    // Refreshes the estimate box for the currently selected target level. Hidden when no provider or
    // no catalog data is available.
    private void UpdateEstimate()
    {
        var targetLevel = TargetLevelComboBox.SelectedItem as int?;
        var estimate = _estimateProvider is not null && targetLevel is int level
            ? _estimateProvider(level)
            : null;
        if (estimate is null)
        {
            EstimateBorder.Visibility = Visibility.Collapsed;
            return;
        }

        TimeTextBlock.Text = estimate.TimeText;
        EstimateRangeTextBlock.Text = $"Total for levels {_currentLevel + 1}-{targetLevel}";
        WoodTextBlock.Text = estimate.WoodText;
        ClayTextBlock.Text = estimate.ClayText;
        IronTextBlock.Text = estimate.IronText;
        CropTextBlock.Text = estimate.CropText;
        EstimateBorder.Visibility = Visibility.Visible;
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
