using System.Windows;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class AddFarmsToListWindow : Window
{
    private readonly List<FarmListSelectionOption> _options;

    public FarmListSelectionOption? SelectedOption { get; private set; }
    public string SelectedTroopType { get; private set; } = string.Empty;
    public int TroopCount { get; private set; }

    public AddFarmsToListWindow(IEnumerable<FarmListSelectionOption> options, string tribe)
    {
        InitializeComponent();
        _options = options?.ToList() ?? [];
        FarmListOptionsListBox.ItemsSource = _options;
        FarmListOptionsListBox.SelectedItem = _options.FirstOrDefault(item => item.AvailableSlots > 0) ?? _options.FirstOrDefault();
        TroopTypeComboBox.ItemsSource = ResolveTroopTypesForTribe(tribe);
        TroopTypeComboBox.SelectedIndex = 0;
        TroopCountTextBox.Text = "1";
        RefreshUiState();
    }

    private void FarmListOptionsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshUiState();
    }

    private void AddFarmsButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = FarmListOptionsListBox.SelectedItem as FarmListSelectionOption;
        if (selected is null)
        {
            AppDialog.Show(this, "Select a farm list first.", "Add farms", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selected.AvailableSlots <= 0)
        {
            AppDialog.Show(this, "This list is already full.", "Add farms", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (TroopTypeComboBox.SelectedItem is not string troopType || string.IsNullOrWhiteSpace(troopType))
        {
            AppDialog.Show(this, "Select troop type.", "Add farms", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!int.TryParse(TroopCountTextBox.Text.Trim(), out var troopCount) || troopCount <= 0)
        {
            AppDialog.Show(this, "Count must be an integer greater than 0.", "Add farms", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedOption = selected;
        SelectedTroopType = troopType;
        TroopCount = troopCount;
        DialogResult = true;
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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void RefreshUiState()
    {
        if (FarmListOptionsListBox is null || TroopTypeComboBox is null || TroopCountTextBox is null || AddFarmsButton is null || InfoTextBlock is null)
        {
            return;
        }

        var selected = FarmListOptionsListBox.SelectedItem as FarmListSelectionOption;
        var hasTroop = TroopTypeComboBox.SelectedItem is string troop && !string.IsNullOrWhiteSpace(troop);
        var hasCount = int.TryParse(TroopCountTextBox.Text.Trim(), out var count) && count > 0;
        AddFarmsButton.IsEnabled = selected is not null && selected.AvailableSlots > 0 && hasTroop && hasCount;
        if (selected is null)
        {
            InfoTextBlock.Text = "Select a list to continue.";
            return;
        }

        if (!hasCount)
        {
            InfoTextBlock.Text = "Enter a valid troop count (> 0).";
            return;
        }

        InfoTextBlock.Text = selected.AvailableSlots > 0
            ? $"{selected.AvailableSlots} slots available in '{selected.Name}'."
            : $"'{selected.Name}' is full.";
    }

    private static List<string> ResolveTroopTypesForTribe(string? tribe)
    {
        var value = (tribe ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("roman"))
        {
            return ["Legionnaire", "Praetorian", "Imperian", "Equites Legati", "Equites Imperatoris", "Equites Caesaris", "Ram", "Fire Catapult", "Senator", "Settler"];
        }

        if (value.Contains("gaul"))
        {
            return ["Phalanx", "Swordsman", "Pathfinder", "Theutates Thunder", "Druidrider", "Haeduan", "Ram", "Trebuchet", "Chieftain", "Settler"];
        }

        if (value.Contains("teuton"))
        {
            return ["Clubswinger", "Spearman", "Axeman", "Scout", "Paladin", "Teutonic Knight", "Ram", "Catapult", "Chief", "Settler"];
        }

        if (value.Contains("hun"))
        {
            return ["Mercenary", "Bowman", "Spotter", "Steppe Rider", "Marksman", "Marauder", "Ram", "Catapult", "Logades", "Settler"];
        }

        if (value.Contains("egypt"))
        {
            return ["Slave Militia", "Ash Warden", "Khopesh Warrior", "Sopdu Explorer", "Anhur Guard", "Resheph Chariot", "Ram", "Stone Catapult", "Nomarch", "Settler"];
        }

        if (value.Contains("spartan"))
        {
            return ["Hoplite", "Sentinel", "Shieldsman", "Twinsteel Therion", "Elpida Rider", "Corinthian Crusher", "Ram", "Ballista", "Ephor", "Settler"];
        }

        return ["Infantry 1", "Infantry 2", "Scout", "Cavalry 1", "Cavalry 2", "Ram", "Catapult"];
    }
}
