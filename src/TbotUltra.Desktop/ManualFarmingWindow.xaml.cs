using System.Windows;
using TbotUltra.Core.Travian;

namespace TbotUltra.Desktop;

public partial class ManualFarmingWindow : Window
{
    private static readonly int[] VarianceOptions = [0, 5, 10, 20, 50];

    public string SelectedTroopType { get; private set; } = string.Empty;
    public long TroopCount { get; private set; }
    public int TroopVariancePercent { get; private set; } = 10;
    public bool IsRaid { get; private set; } = true;
    public string NatarVillageSelection { get; private set; } = "farm_villages";
    public Action<long, int>? PreferenceChanged { get; init; }

    public ManualFarmingWindow(string tribe, string natarVillageSelection, long troopCount, int troopVariancePercent)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        TroopTypeComboBox.ItemsSource = TroopCatalog.ResolveTroopTypesForTribe(tribe);
        TroopTypeComboBox.SelectedIndex = 0;
        VarianceComboBox.ItemsSource = VarianceOptions.Select(value => $"{value}%").ToList();
        TroopCountTextBox.Text = Math.Max(1L, troopCount).ToString();
        TroopVariancePercent = NormalizeVariancePercent(troopVariancePercent);
        VarianceComboBox.SelectedItem = $"{TroopVariancePercent}%";
        RaidRadioButton.IsChecked = true;
        AllVillagesRadioButton.IsChecked = string.Equals(natarVillageSelection, "all_villages", StringComparison.OrdinalIgnoreCase);
        FarmVillagesRadioButton.IsChecked = !string.Equals(natarVillageSelection, "all_villages", StringComparison.OrdinalIgnoreCase);
        RefreshUiState();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (TroopTypeComboBox.SelectedItem is not string troopType || string.IsNullOrWhiteSpace(troopType))
        {
            AppDialog.Show(this, "Select troop type.", "Manual farming", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!long.TryParse(TroopCountTextBox.Text.Trim(), out var troopCount) || troopCount <= 0)
        {
            AppDialog.Show(this, "Count must be an integer greater than 0.", "Manual farming", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedTroopType = troopType;
        TroopCount = troopCount;
        TroopVariancePercent = ReadSelectedVariancePercent();
        IsRaid = RaidRadioButton.IsChecked == true;
        NatarVillageSelection = AllVillagesRadioButton.IsChecked == true ? "all_villages" : "farm_villages";
        NotifyPreferenceChanged();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        NotifyPreferenceChanged();
        DialogResult = false;
        Close();
    }

    private void TroopTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshUiState();
    }

    private void TroopCountTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RefreshUiState();
    }

    private void AttackTypeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        RefreshUiState();
    }

    private void VarianceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        TroopVariancePercent = ReadSelectedVariancePercent();
        RefreshUiState();
    }

    private void TargetSelectionRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        RefreshUiState();
    }

    private void RefreshUiState()
    {
        if (TroopTypeComboBox is null || TroopCountTextBox is null || StartButton is null || InfoTextBlock is null)
        {
            return;
        }

        var hasTroop = TroopTypeComboBox.SelectedItem is string troop && !string.IsNullOrWhiteSpace(troop);
        var hasCount = long.TryParse(TroopCountTextBox.Text.Trim(), out var count) && count > 0;
        StartButton.IsEnabled = hasTroop && hasCount;

        if (!hasTroop)
        {
            InfoTextBlock.Text = "Select troop type.";
            return;
        }

        if (!hasCount)
        {
            InfoTextBlock.Text = "Enter a valid troop count (> 0).";
            return;
        }

        var modeText = RaidRadioButton.IsChecked == true ? "Raid" : "Normal attack";
        var targetText = AllVillagesRadioButton.IsChecked == true ? "all villages from the Natars profile" : "farm villages only";
        var variationText = ReadSelectedVariancePercent() <= 0 ? "no random variation" : $"randomized by +/-{ReadSelectedVariancePercent()}%";
        InfoTextBlock.Text = $"Every analyzed Natar target will be sent as {modeText} using {targetText} with {variationText}.";
        UpdateEffectiveRangeText(count);
    }

    private int ReadSelectedVariancePercent()
    {
        var selectedText = VarianceComboBox?.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            return 10;
        }

        var digits = new string(selectedText.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value)
            ? NormalizeVariancePercent(value)
            : 10;
    }

    private void UpdateEffectiveRangeText(long troopCount)
    {
        if (EffectiveRangeTextBlock is null)
        {
            return;
        }

        var variance = ReadSelectedVariancePercent();
        var minAmount = Math.Max(1L, (long)Math.Floor(troopCount * (100d - variance) / 100d));
        var maxAmount = Math.Max(minAmount, (long)Math.Ceiling(troopCount * (100d + variance) / 100d));
        EffectiveRangeTextBlock.Text = $"{minAmount}-{maxAmount}";
    }

    private void NotifyPreferenceChanged()
    {
        if (!long.TryParse(TroopCountTextBox.Text.Trim(), out var troopCount) || troopCount <= 0)
        {
            troopCount = 1;
        }

        PreferenceChanged?.Invoke(troopCount, ReadSelectedVariancePercent());
    }

    private static int NormalizeVariancePercent(int value)
    {
        return value switch
        {
            0 or 5 or 10 or 20 or 50 => value,
            _ => 10,
        };
    }
}
