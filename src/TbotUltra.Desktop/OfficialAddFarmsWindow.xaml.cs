using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public sealed record OfficialFarmAddPlan(
    Guid SourceListId,
    string SourceListName,
    string TargetName,
    int DesiredCount,
    IReadOnlyList<FarmCoordinate> Coordinates);
public sealed record OfficialFarmAddRunResult(
    int Requested,
    int Added,
    int Duplicates,
    int Failed,
    IReadOnlyList<FarmCoordinate> InvalidCoordinates,
    Guid SourceListId = default,
    string SourceListName = "",
    int OccupiedSkipped = 0);
public sealed record OfficialAddFarmsLoadResult(
    bool Ok,
    string? Message,
    IReadOnlyList<TravcoListStore.TravcoSavedList> SourceLists,
    IReadOnlyList<FarmListSelectionOption> TargetLists,
    IReadOnlySet<string> ExistingCoordinates,
    IReadOnlyList<string>? IncompleteFarmLists = null);

public partial class OfficialAddFarmsWindow : Window
{
    private const int OfficialFarmListCapacity = 100;

    public sealed record SourceOption(Guid Id, string Name, IReadOnlyList<TravcoListStore.TravcoSavedRow> Rows)
    {
        public int SelectedCount => Rows.Count(row => row.Selected);
        public string DisplayName => $"{Name} ({SelectedCount} farms)";

        // An oasis source list is one whose selected rows carry an oasis type.
        public bool IsOasisList => Rows.Any(row => row.Selected && !string.IsNullOrWhiteSpace(row.OasisType));
    }

    public sealed record AddFarmsVillageOption(string Name, int? X, int? Y)
    {
        public bool HasCoordinates => X.HasValue && Y.HasValue;
        public string DisplayName => HasCoordinates ? $"{Name} ({X} | {Y})" : Name;
    }

    // One toggleable oasis type, used to let the user pick which oasis types from the source list to add.
    public sealed class OasisTypeFilter : INotifyPropertyChanged
    {
        private bool _isChecked = true;

        public required string Type { get; init; }

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
    private readonly IReadOnlyList<AddFarmsVillageOption> _villages;
    private readonly string? _selectedVillageName;
    private IReadOnlyList<string> _customTroopTypes = [];
    private List<OasisTypeFilter> _oasisTypeFilters = [];
    private bool _loaded;
    // Dead villages ("no village at these coordinates") seen so far, accumulated from progress so a
    // cancelled run can still offer to remove them from the Travco source list.
    private readonly List<FarmCoordinate> _invalidCoordinatesSoFar = [];
    private int _lastAddedCount;
    // Live "Adding farms" progress overlay state: a stopwatch + 1s timer drive the counting-up Elapsed
    // line; the latest progress snapshot is kept so the timer tick can re-render the full overlay text.
    private readonly Stopwatch _runStopwatch = new();
    private DispatcherTimer? _runElapsedTimer;
    private FarmAddProgress? _lastAddProgress;
    private int _addTotal;

    public OfficialFarmAddRunResult? RunResult { get; private set; }
    // How long the actual add run took (excludes the time the dialog sat waiting for input). Shown in the
    // completion summary.
    public TimeSpan RunDuration { get; private set; }
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
        CancellationToken externalToken,
        IReadOnlyList<AddFarmsVillageOption>? villages = null,
        string? selectedVillageName = null)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _loader = loader;
        _runner = runner;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _villages = villages ?? [];
        _selectedVillageName = selectedVillageName;

        _customTroopTypes = TroopCatalog.ResolveTroopTypesForTribe(tribe);
        AmountComboBox.ItemsSource = Enumerable.Range(1, OfficialFarmListCapacity);
        AmountComboBox.SelectedItem = OfficialFarmListCapacity;
        TroopCountTextBox.Text = Math.Max(1, defaultTroopCount).ToString(CultureInfo.InvariantCulture);

        PopulateVillageComboBox();

        AddButton.IsEnabled = false;
        UpdateTroopControls();
        RefreshState();
        Loaded += OnLoaded;
    }

    // Fills the "Distance from village" dropdown and pre-selects the village currently active in the
    // main window so distance is computed against it by default. Only villages with coordinates qualify.
    private void PopulateVillageComboBox()
    {
        var options = _villages.Where(village => village.HasCoordinates).ToList();
        FromVillageComboBox.ItemsSource = options;
        if (options.Count == 0)
        {
            return;
        }

        var preselected = options.FirstOrDefault(village =>
            string.Equals(village.Name, _selectedVillageName, StringComparison.OrdinalIgnoreCase));
        FromVillageComboBox.SelectedItem = preselected ?? options[0];
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        _runCts.Cancel();
        _runCts.Dispose();
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(() => OnLoadedAsync(sender, e), LogUiGuardError);

    private async Task OnLoadedAsync(object sender, RoutedEventArgs e)
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
            ShowIncompleteReadWarning(result.IncompleteFarmLists);
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
            // No destination farm list is checked by default — the user picks which one(s) to add to.

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

    // Warns when the farm-list analysis could not read every target coordinate, so the user knows the
    // "don't add duplicates" check may miss farms in the listed lists and can re-run Analyze.
    private void ShowIncompleteReadWarning(IReadOnlyList<string>? incompleteFarmLists)
    {
        if (incompleteFarmLists is null || incompleteFarmLists.Count == 0)
        {
            IncompleteReadWarningBorder.Visibility = Visibility.Collapsed;
            return;
        }

        IncompleteReadWarningTextBlock.Text =
            $"Warning: {incompleteFarmLists.Count} farm list(s) could not be fully read "
            + $"({string.Join(", ", incompleteFarmLists)}). Duplicate protection may miss farms in those "
            + "lists. Close this dialog and re-run Analyze Farmlists if you need full protection.";
        IncompleteReadWarningBorder.Visibility = Visibility.Visible;
    }

    private void InputChanged(object sender, RoutedEventArgs e) => RefreshState();
    private void TargetCheckBox_Changed(object sender, RoutedEventArgs e) => RefreshState();

    private void SourceListComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RebuildOasisFilter();
        RefreshState();
    }

    // Shows the oasis type/occupied filter only for oasis source lists, populated with the distinct
    // oasis types present in the selected list. Hidden for villages lists.
    private void RebuildOasisFilter()
    {
        if (OasisSingleTypesItemsControl is null || OasisComboTypesItemsControl is null || OasisFilterBorder is null)
        {
            return;
        }

        foreach (var existing in _oasisTypeFilters)
        {
            existing.PropertyChanged -= OasisTypeFilter_PropertyChanged;
        }

        var source = SourceListComboBox?.SelectedItem as SourceOption;
        var types = source is { IsOasisList: true }
            ? source.Rows
                .Where(row => row.Selected && !string.IsNullOrWhiteSpace(row.OasisType))
                .Select(row => row.OasisType!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        _oasisTypeFilters = types.Select(type => new OasisTypeFilter { Type = type }).ToList();
        foreach (var filter in _oasisTypeFilters)
        {
            filter.PropertyChanged += OasisTypeFilter_PropertyChanged;
        }

        // Single-resource types go on the top row, two-resource combos (e.g. "Wood+Crop") on the bottom.
        OasisSingleTypesItemsControl.ItemsSource = _oasisTypeFilters.Where(filter => !filter.Type.Contains('+')).ToList();
        OasisComboTypesItemsControl.ItemsSource = _oasisTypeFilters.Where(filter => filter.Type.Contains('+')).ToList();
        OasisFilterBorder.Visibility = _oasisTypeFilters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OasisTypeFilter_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshState();

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
        TroopTypeComboBox.ItemsSource = custom ? _customTroopTypes : ["Default"];
        TroopTypeComboBox.SelectedIndex = TroopTypeComboBox.Items.Count > 0 ? 0 : -1;
        TroopTypeComboBox.IsEnabled = custom;
        TroopCountTextBox.IsEnabled = custom;
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(() => AddButtonClickAsync(sender, e), LogUiGuardError);

    private async Task AddButtonClickAsync(object sender, RoutedEventArgs e)
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

        var total = plans.Sum(plan => plan.DesiredCount);
        _addTotal = total;
        _lastAddProgress = null;
        _runStopwatch.Restart();
        _runElapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runElapsedTimer.Tick += (_, _) => UpdateAddingFarmsOverlayText();
        _runElapsedTimer.Start();
        LoadingOverlay.Show("Adding farms", string.Empty);
        UpdateAddingFarmsOverlayText();
        var progress = new Progress<FarmAddProgress>(value =>
        {
            _lastAddProgress = value;
            UpdateAddingFarmsOverlayText();
            _lastAddedCount = value.AddedCount;
            if (value.InvalidCoordinate is { } invalid && !_invalidCoordinatesSoFar.Contains(invalid))
            {
                _invalidCoordinatesSoFar.Add(invalid);
            }
        });

        try
        {
            var result = await _runner(plans, useDefaultTroops, troopType, troopCount, progress, _runCts.Token);
            RunDuration = _runStopwatch.Elapsed;
            var source = (SourceOption)SourceListComboBox.SelectedItem;
            RunResult = result with
            {
                SourceListId = source.Id,
                SourceListName = source.Name,
            };
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            RunDuration = _runStopwatch.Elapsed;
            LoadingOverlay.Hide();
            // Cancelled mid-run: still surface the dead villages found so far so the caller can offer to
            // remove them from the Travco source list (otherwise cancelling would silently keep retrying
            // the same non-existent coordinates on the next run).
            if (_invalidCoordinatesSoFar.Count > 0 && SourceListComboBox.SelectedItem is SourceOption cancelledSource)
            {
                RunResult = new OfficialFarmAddRunResult(
                    Requested: 0,
                    Added: _lastAddedCount,
                    Duplicates: 0,
                    Failed: _invalidCoordinatesSoFar.Count,
                    InvalidCoordinates: _invalidCoordinatesSoFar.ToList(),
                    SourceListId: cancelledSource.Id,
                    SourceListName: cancelledSource.Name);
                DialogResult = true;
            }
            else
            {
                DialogResult = false;
            }

            Close();
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            AppDialog.Show(this, ex.Message, "Add farms failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _runStopwatch.Stop();
            _runElapsedTimer?.Stop();
            _runElapsedTimer = null;
        }
    }

    // Renders the live "Adding farms" overlay text from the latest progress snapshot plus the counting-up
    // elapsed time. Called both by the progress callback (new counts) and the 1s timer (elapsed only).
    private void UpdateAddingFarmsOverlayText()
    {
        var p = _lastAddProgress;
        var total = Math.Max(1, p?.TotalCount ?? _addTotal);
        var processed = Math.Clamp(p?.ProcessedCount ?? 0, 0, total);
        var percent = (double)processed / total * 100;
        LoadingOverlay.IsIndeterminate = false;
        LoadingOverlay.ProgressValue = percent;
        LoadingOverlay.Text =
            $"{percent:0}% complete\n" +
            $"Farms checked: {processed}/{total} - {total - processed} remaining\n" +
            $"Added villages: {p?.AddedCount ?? 0}\n" +
            $"Current list: {(p?.FarmListName ?? "-")}\n" +
            $"Invalid: {(p?.NotFoundCount ?? 0)}\n" +
            $"Occupied (oasis) skipped: {(p?.OccupiedOasisSkippedCount ?? 0)}\n" +
            $"Elapsed: {FormatElapsed(_runStopwatch.Elapsed)}";
    }

    private static string FormatElapsed(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";

    private void LogUiGuardError(string message)
    {
        LoadFailureMessage = message;
        LoadingOverlay.Hide();
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
        var requested = plans.Sum(plan => plan.DesiredCount);
        var selectedTargets = plans.Count;
        AmountComboBox.IsEnabled = AmountModeRadioButton.IsChecked == true;
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

        (int X, int Y)? referenceVillage = FromVillageComboBox?.SelectedItem is AddFarmsVillageOption village
            && village.X.HasValue && village.Y.HasValue
            ? (village.X.Value, village.Y.Value)
            : null;

        // Oasis source lists expose extra filters: which oasis types to include and whether to allow
        // occupied oases. Villages lists pass these as "no filter".
        IReadOnlySet<string>? oasisTypes = null;
        var includeOccupied = true;
        if (source.IsOasisList && _oasisTypeFilters.Count > 0)
        {
            oasisTypes = _oasisTypeFilters
                .Where(filter => filter.IsChecked)
                .Select(filter => filter.Type)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            includeOccupied = IncludeOccupiedCheckBox?.IsChecked == true;
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
                source.Rows.Count,
                order,
                populationMode,
                populationLimit,
                maximumDistance,
                SkipDuplicatesCheckBox.IsChecked == true,
                referenceVillage,
                oasisTypes,
                includeOccupied,
                SkipLowPopVillagesCheckBox.IsChecked == true,
                requireUnoccupiedOasis: source.IsOasisList && !includeOccupied);
            if (coordinates.Count == 0)
            {
                continue;
            }

            plans.Add(new OfficialFarmAddPlan(source.Id, source.Name, target.Name, amount, coordinates));
            if (SkipDuplicatesCheckBox.IsChecked == true)
            {
                foreach (var coordinate in coordinates.Take(amount))
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
