using System.Windows;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class AddFarmsToListWindow : Window
{
    private readonly List<FarmListSelectionOption> _options;
    private readonly int _natarFarmCount;

    public FarmListSelectionOption? SelectedOption { get; private set; }
    public string SelectedTroopType { get; private set; } = string.Empty;
    public int TroopCount { get; private set; }
    public int RequestedFarmCount { get; private set; }

    public AddFarmsToListWindow(IEnumerable<FarmListSelectionOption> options, string tribe, int natarFarmCount)
    {
        InitializeComponent();
        _options = options?.ToList() ?? [];
        _natarFarmCount = Math.Max(0, natarFarmCount);
        FarmListOptionsListBox.ItemsSource = _options;
        FarmListOptionsListBox.SelectedItem = _options.FirstOrDefault(item => item.AvailableSlots > 0) ?? _options.FirstOrDefault();
        TroopTypeComboBox.ItemsSource = TroopCatalog.ResolveTroopTypesForTribe(tribe);
        TroopTypeComboBox.SelectedIndex = 0;
        TroopCountTextBox.Text = "1";
        FillModeRadioButton.IsChecked = true;
        CustomCountTextBox.Text = "1";
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
        RequestedFarmCount = ResolveRequestedFarmCount(selected);
        if (RequestedFarmCount <= 0)
        {
            AppDialog.Show(this, "Requested farm count must be greater than 0.", "Add farms", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
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

    private void FillModeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        RefreshUiState();
    }

    private void AllModeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        RefreshUiState();
    }

    private void CheckedModeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        RefreshUiState();
    }

    private void CustomModeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        RefreshUiState();
    }

    private void CustomCountTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
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
        var requested = selected is null ? 0 : ResolveRequestedFarmCount(selected);
        AddFarmsButton.IsEnabled = selected is not null && hasTroop && hasCount && requested > 0;
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

        InfoTextBlock.Text = $"Natars checked: {_natarFarmCount}. Requested: {Math.Max(0, requested)}. Slots free: {selected.AvailableSlots}.";
    }

    private int ResolveRequestedFarmCount(FarmListSelectionOption selected)
    {
        if (AllModeRadioButton.IsChecked == true || CheckedModeRadioButton.IsChecked == true)
        {
            return _natarFarmCount;
        }

        if (FillModeRadioButton.IsChecked == true)
        {
            return selected.AvailableSlots;
        }

        if (!int.TryParse(CustomCountTextBox.Text.Trim(), out var customCount))
        {
            return 0;
        }

        return Math.Max(0, customCount);
    }
}
