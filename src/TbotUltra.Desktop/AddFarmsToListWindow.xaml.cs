using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public sealed record AddFarmsLoadResult(
    bool Ok,
    string? Message,
    IReadOnlyList<FarmListSelectionOption> Options,
    int NatarFarmCount);

public sealed record AddFarmsTarget(string Name, int RequestedFarmCount);

public partial class AddFarmsToListWindow : Window
{
    private readonly string _tribe;
    private readonly int _defaultTroopCount;
    private readonly Func<CancellationToken, Task<AddFarmsLoadResult>> _loader;
    private readonly CancellationTokenSource _loadCts;
    private List<FarmListSelectionOption> _options = [];
    private int _natarFarmCount;
    private bool _loaded;
    private bool _loadCtsDisposed;

    public IReadOnlyList<AddFarmsTarget> Targets { get; private set; } = [];
    public string SelectedTroopType { get; private set; } = string.Empty;
    public int TroopCount { get; private set; }
    public string? LoadFailureMessage { get; private set; }

    public AddFarmsToListWindow(
        string tribe,
        int defaultTroopCount,
        Func<CancellationToken, Task<AddFarmsLoadResult>> loader,
        CancellationToken externalToken)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _tribe = tribe;
        _defaultTroopCount = Math.Max(1, defaultTroopCount);
        _loader = loader;
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

        TroopTypeComboBox.ItemsSource = TroopCatalog.ResolveTroopTypesForTribe(tribe);
        TroopTypeComboBox.SelectedIndex = 0;
        TroopCountTextBox.Text = _defaultTroopCount.ToString();
        FillModeRadioButton.IsChecked = true;
        CustomCountTextBox.Text = _defaultTroopCount.ToString();

        AddFarmsButton.IsEnabled = false;
        Loaded += OnLoaded;
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        if (!_loadCtsDisposed)
        {
            _loadCtsDisposed = true;
            _loadCts.Cancel();
            _loadCts.Dispose();
        }

        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            var result = await _loader(_loadCts.Token);
            if (!result.Ok)
            {
                LoadFailureMessage = result.Message;
                DialogResult = false;
                Close();
                return;
            }

            _options = result.Options.ToList();
            _natarFarmCount = Math.Max(0, result.NatarFarmCount);
            FarmListOptionsListBox.ItemsSource = _options;

            var firstWithSlots = _options.FirstOrDefault(item => item.AvailableSlots > 0);
            if (firstWithSlots is not null)
            {
                firstWithSlots.IsChecked = true;
            }

            LoadingOverlay.Hide();
            RefreshUiState();
        }
        catch (OperationCanceledException)
        {
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            LoadFailureMessage = ex.Message;
            DialogResult = false;
            Close();
        }
    }

    private void LoadingOverlay_Cancelled(object sender, EventArgs e)
    {
        // The overlay already disabled its button and showed "Cancelling…"; we just cancel the load.
        _loadCts.Cancel();
    }

    private void FarmListOptionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        RefreshUiState();
    }

    private void AddFarmsButton_Click(object sender, RoutedEventArgs e)
    {
        var checkedOptions = _options.Where(item => item.IsChecked).ToList();
        if (checkedOptions.Count == 0)
        {
            AppDialog.Show(this, "Check at least one farm list first.", "Add farms", MessageBoxButton.OK, MessageBoxImage.Information);
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

        var targets = checkedOptions
            .Select(option => new AddFarmsTarget(option.Name, ResolveRequestedFarmCount(option)))
            .Where(target => target.RequestedFarmCount > 0)
            .ToList();

        if (targets.Count == 0)
        {
            AppDialog.Show(this, "None of the checked lists need farms with the current amount setting.", "Add farms", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Targets = targets;
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
        _loadCts.Cancel();
        DialogResult = false;
        Close();
    }

    private void RefreshUiState()
    {
        if (FarmListOptionsListBox is null || TroopTypeComboBox is null || TroopCountTextBox is null || AddFarmsButton is null || InfoTextBlock is null)
        {
            return;
        }

        var checkedOptions = _options.Where(item => item.IsChecked).ToList();
        var hasTroop = TroopTypeComboBox.SelectedItem is string troop && !string.IsNullOrWhiteSpace(troop);
        var hasCount = int.TryParse(TroopCountTextBox.Text.Trim(), out var count) && count > 0;
        var totalRequested = checkedOptions.Sum(ResolveRequestedFarmCount);

        AddFarmsButton.IsEnabled = checkedOptions.Count > 0 && hasTroop && hasCount && totalRequested > 0;

        if (checkedOptions.Count == 0)
        {
            InfoTextBlock.Text = "Check one or more lists. They are processed one at a time.";
            return;
        }

        if (!hasCount)
        {
            InfoTextBlock.Text = "Enter a valid troop count (> 0).";
            return;
        }

        InfoTextBlock.Text = $"Natars checked: {_natarFarmCount}. Selected lists: {checkedOptions.Count}. Total farms to add: {Math.Max(0, totalRequested)}.";
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
