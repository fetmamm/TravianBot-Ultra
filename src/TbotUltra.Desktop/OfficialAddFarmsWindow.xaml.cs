using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public sealed record OfficialFarmAddPlan(string TargetName, IReadOnlyList<FarmCoordinate> Coordinates);
public sealed record OfficialFarmAddRunResult(int Requested, int Added, int Duplicates, int Failed);
public sealed record OfficialAddFarmsLoadResult(
    bool Ok,
    string? Message,
    IReadOnlyList<TravcoListStore.TravcoSavedList> SourceLists,
    IReadOnlyList<FarmListSelectionOption> TargetLists,
    IReadOnlySet<string> ExistingCoordinates);

public partial class OfficialAddFarmsWindow : Window
{
    private const int OfficialFarmListCapacity = 100;

    public sealed record SourceOption(Guid Id, string Name, IReadOnlyList<TravcoListStore.TravcoSavedRow> Rows)
    {
        public int SelectedCount => Rows.Count(row => row.Selected);
        public string DisplayName => $"{Name} ({SelectedCount} farms)";
    }

    public sealed class TargetOption : INotifyPropertyChanged
    {
        private bool _isChecked;

        public required string Name { get; init; }
        public int FarmCount { get; init; }
        public int Capacity { get; init; } = OfficialFarmListCapacity;
        public string CountText => $"{FarmCount}/{Capacity}";

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                {
                    return;
                }

                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private IReadOnlySet<string> _existingCoordinates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Func<CancellationToken, Task<OfficialAddFarmsLoadResult>> _loader;
    private readonly Func<
        IReadOnlyList<OfficialFarmAddPlan>,
        bool,
        string,
        int,
        IProgress<FarmAddProgress>,
        CancellationToken,
        Task<OfficialFarmAddRunResult>> _runner;
    private readonly CancellationTokenSource _runCts;
    private bool _loaded;

    public OfficialFarmAddRunResult? RunResult { get; private set; }
    public string? LoadFailureMessage { get; private set; }

    public OfficialAddFarmsWindow(
        string tribe,
        int defaultTroopCount,
        Func<CancellationToken, Task<OfficialAddFarmsLoadResult>> loader,
        Func<
            IReadOnlyList<OfficialFarmAddPlan>,
            bool,
            string,
            int,
            IProgress<FarmAddProgress>,
            CancellationToken,
            Task<OfficialFarmAddRunResult>> runner,
        CancellationToken externalToken)
    {
        InitializeComponent();
        _loader = loader;
        _runner = runner;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

        TroopTypeComboBox.ItemsSource = TroopCatalog.ResolveTroopTypesForTribe(tribe);
        AmountComboBox.ItemsSource = Enumerable.Range(1, OfficialFarmListCapacity);
        AmountComboBox.SelectedItem = OfficialFarmListCapacity;
        TroopCountTextBox.Text = Math.Max(1, defaultTroopCount).ToString(CultureInfo.InvariantCulture);

        TroopTypeComboBox.SelectedIndex = TroopTypeComboBox.Items.Count > 0 ? 0 : -1;
        AddButton.IsEnabled = false;
        UpdateTroopControls();
        RefreshState();
        Loaded += OnLoaded;
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        _runCts.Cancel();
        _runCts.Dispose();
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        LoadingOverlay.Show("Loading farmlists", "Analyzing current farmlists...");
        try
        {
            var result = await _loader(_runCts.Token);
            if (!result.Ok)
            {
                LoadFailureMessage = result.Message;
                DialogResult = false;
                Close();
                return;
            }

            _existingCoordinates = result.ExistingCoordinates;
            SourceListComboBox.ItemsSource = result.SourceLists
                .Select(list => new SourceOption(list.Id, list.Name, list.Rows))
                .Where(option => option.SelectedCount > 0)
                .ToList();
            TargetListsListBox.ItemsSource = result.TargetLists
                .Select(list => new TargetOption
                {
                    Name = list.Name,
                    FarmCount = list.TotalFarmCount,
                    Capacity = Math.Min(OfficialFarmListCapacity, list.Capacity ?? OfficialFarmListCapacity),
                })
                .ToList();

            SourceListComboBox.SelectedIndex = SourceListComboBox.Items.Count > 0 ? 0 : -1;
            if (TargetListsListBox.Items.Count > 0 && TargetListsListBox.Items[0] is TargetOption first)
            {
                first.IsChecked = true;
            }

            LoadingOverlay.Hide();
            RefreshState();
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

    private void InputChanged(object sender, RoutedEventArgs e) => RefreshState();
    private void TargetCheckBox_Changed(object sender, RoutedEventArgs e) => RefreshState();

    private void TroopModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTroopControls();
        RefreshState();
    }

    private void UpdateTroopControls()
    {
        if (TroopTypeComboBox is null || TroopCountTextBox is null)
        {
            return;
        }

        var custom = IsCustomTroops();
        TroopTypeComboBox.IsEnabled = custom;
        TroopCountTextBox.IsEnabled = custom;
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var plans = BuildPlans();
        var useDefaultTroops = !IsCustomTroops();
        var troopType = TroopTypeComboBox.SelectedItem as string ?? string.Empty;
        var troopCount = int.TryParse(TroopCountTextBox.Text, out var parsedCount) ? parsedCount : 0;
        if (plans.Count == 0 || (!useDefaultTroops && (string.IsNullOrWhiteSpace(troopType) || troopCount <= 0)))
        {
            AppDialog.Show(this, "No farms match the selected settings.", "Add farms", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var total = plans.Sum(plan => plan.Coordinates.Count);
        LoadingOverlay.Show("Adding farms", $"Adding 0/{total} villages");
        var progress = new Progress<FarmAddProgress>(value =>
        {
            LoadingOverlay.Text =
                $"Adding {value.ProcessedCount}/{value.TotalCount} villages\n" +
                $"Current list: {value.FarmListName}. Added: {value.AddedCount}.";
        });

        try
        {
            RunResult = await _runner(plans, useDefaultTroops, troopType, troopCount, progress, _runCts.Token);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            LoadingOverlay.Hide();
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            AppDialog.Show(this, ex.Message, "Add farms failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadingOverlay_Cancelled(object sender, EventArgs e) => _runCts.Cancel();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _runCts.Cancel();
        DialogResult = false;
        Close();
    }

    private void RefreshState()
    {
        if (SummaryTextBlock is null || AddButton is null)
        {
            return;
        }

        var sourceCount = (SourceListComboBox.SelectedItem as SourceOption)?.SelectedCount ?? 0;
        var plans = BuildPlans();
        var requested = plans.Sum(plan => plan.Coordinates.Count);
        var selectedTargets = plans.Count;
        SourceCountTextBlock.Text = sourceCount.ToString(CultureInfo.InvariantCulture);
        SummaryTextBlock.Text =
            $"Selected destination lists: {selectedTargets}. Villages scheduled: {requested}. " +
            $"Analyzed existing farm coordinates: {_existingCoordinates.Count}.";
        AddButton.IsEnabled = requested > 0
                              && (!IsCustomTroops()
                                  || (TroopTypeComboBox.SelectedItem is string
                                      && int.TryParse(TroopCountTextBox.Text, out var count)
                                      && count > 0));
    }

    private List<OfficialFarmAddPlan> BuildPlans()
    {
        if (SourceListComboBox?.SelectedItem is not SourceOption source
            || TargetListsListBox?.ItemsSource is not IEnumerable<TargetOption> targetOptions)
        {
            return [];
        }

        if (!TryReadFilters(out var order, out var populationMode, out var populationLimit, out var maximumDistance))
        {
            return [];
        }

        var workingExisting = new HashSet<string>(_existingCoordinates, StringComparer.OrdinalIgnoreCase);
        var plans = new List<OfficialFarmAddPlan>();
        foreach (var target in targetOptions.Where(option => option.IsChecked))
        {
            var availableSlots = Math.Max(0, OfficialFarmListCapacity - target.FarmCount);
            var amount = FillModeRadioButton.IsChecked == true
                ? availableSlots
                : Math.Min(availableSlots, AmountComboBox.SelectedItem is int selectedAmount ? selectedAmount : 0);
            if (amount <= 0)
            {
                continue;
            }

            var coordinates = OfficialFarmSelection.Filter(
                source.Rows,
                workingExisting,
                amount,
                order,
                populationMode,
                populationLimit,
                maximumDistance,
                SkipDuplicatesCheckBox.IsChecked == true);
            if (coordinates.Count == 0)
            {
                continue;
            }

            plans.Add(new OfficialFarmAddPlan(target.Name, coordinates));
            if (SkipDuplicatesCheckBox.IsChecked == true)
            {
                foreach (var coordinate in coordinates)
                {
                    workingExisting.Add($"{coordinate.X}|{coordinate.Y}");
                }
            }
        }

        return plans;
    }

    private bool TryReadFilters(
        out string order,
        out string populationMode,
        out long populationLimit,
        out double? maximumDistance)
    {
        order = (OrderComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "distance_asc";
        populationMode = (PopulationFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        populationLimit = 0;
        maximumDistance = null;
        if (!string.Equals(populationMode, "all", StringComparison.OrdinalIgnoreCase)
            && (!long.TryParse(PopulationTextBox.Text, out populationLimit) || populationLimit < 0))
        {
            return false;
        }

        if (DistanceFilterCheckBox.IsChecked == true)
        {
            if (!double.TryParse(DistanceTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance)
                || distance < 0)
            {
                return false;
            }

            maximumDistance = distance;
        }

        return true;
    }

    private bool IsCustomTroops() =>
        string.Equals(
            (TroopModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            "custom",
            StringComparison.OrdinalIgnoreCase);
}
