using System.Globalization;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class CreateFarmListsWindow : Window
{
    private static readonly Regex VillageIdPattern =
        new(@"[?&]newdid=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<
        FarmListCreateRequest,
        IProgress<FarmListCreateProgress>,
        CancellationToken,
        Task<FarmListCreateBatchResult>> _runner;
    private readonly CancellationTokenSource _runCts;
    private readonly ObservableCollection<FarmListNameEntry> _listNameEntries = [];

    public FarmListCreateBatchResult? RunResult { get; private set; }

    public CreateFarmListsWindow(
        string tribe,
        IReadOnlyList<VillageSelectionItem> villages,
        Func<
            FarmListCreateRequest,
            IProgress<FarmListCreateProgress>,
            CancellationToken,
            Task<FarmListCreateBatchResult>> runner,
        CancellationToken externalToken)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _runner = runner;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

        ListNameFieldsItemsControl.ItemsSource = _listNameEntries;
        ListCountComboBox.ItemsSource = Enumerable.Range(1, 100);
        ListCountComboBox.SelectedItem = 1;
        SyncListNameFields();
        VillageComboBox.ItemsSource = villages;
        VillageComboBox.SelectedItem = villages.FirstOrDefault(village => village.IsCapital)
                                       ?? villages.FirstOrDefault();
        TroopTypeComboBox.ItemsSource = TroopCatalog.ResolveTroopTypesForTribe(tribe);
        TroopTypeComboBox.SelectedIndex = TroopTypeComboBox.Items.Count > 0 ? 0 : -1;
        RefreshState();
    }

    protected override void OnClosed(EventArgs e)
    {
        _runCts.Cancel();
        _runCts.Dispose();
        base.OnClosed(e);
    }

    private void InputChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        if (ReferenceEquals(sender, ListCountComboBox))
        {
            SyncListNameFields();
        }

        RefreshState();
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildRequest(out var request, out var validation))
        {
            ValidationTextBlock.Text = validation;
            return;
        }

        LoadingOverlay.Show("Analyzing farmlists", "Reading current farmlists and checking names...");
        var progress = new Progress<FarmListCreateProgress>(value =>
        {
            LoadingOverlay.Title = value.Phase;
            LoadingOverlay.Text = value.Phase.StartsWith("Analyzing", StringComparison.OrdinalIgnoreCase)
                ? "Reading current farmlists and checking names..."
                : $"Creating {value.ProcessedCount}/{value.TotalCount} farmlists" +
                  (string.IsNullOrWhiteSpace(value.FarmListName) ? string.Empty : $"\nCurrent: {value.FarmListName}");
        });

        try
        {
            RunResult = await _runner(request, progress, _runCts.Token);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            ValidationTextBlock.Text = ex.Message;
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
        if (CreateButton is null || ValidationTextBlock is null)
        {
            return;
        }

        var count = ListCountComboBox.SelectedItem is int selectedCount ? selectedCount : 0;
        CreateButton.Content = $"Create {count} farmlist{(count == 1 ? string.Empty : "s")}";
        CreateButton.IsEnabled = TryBuildRequest(out _, out var validation);
        ValidationTextBlock.Text = validation;
    }

    private bool TryBuildRequest(out FarmListCreateRequest request, out string validation)
    {
        request = new FarmListCreateRequest([], string.Empty, null, string.Empty, 0);
        validation = string.Empty;
        if (ListCountComboBox?.SelectedItem is not int count)
        {
            validation = "Select how many farmlists to create.";
            return false;
        }

        var names = _listNameEntries
            .Take(count)
            .Select(entry => entry.Name.Trim())
            .ToList();
        if (names.Any(string.IsNullOrWhiteSpace))
        {
            validation = $"Fill all {count} farm list name field{(count == 1 ? string.Empty : "s")}.";
            return false;
        }

        names = names
            .Select(name => name.Trim())
            .ToList();

        if (names.Any(name => name.Length > 30))
        {
            validation = "Farm list names can contain at most 30 characters.";
            return false;
        }

        var duplicate = names
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            validation = $"Farm list name '{duplicate}' is entered more than once.";
            return false;
        }

        if (VillageComboBox?.SelectedItem is not VillageSelectionItem village)
        {
            validation = "Select a village.";
            return false;
        }

        if (TroopTypeComboBox?.SelectedItem is not string troopType)
        {
            validation = "Select one default troop type.";
            return false;
        }

        if (!int.TryParse(TroopCountTextBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var troopCount)
            || troopCount <= 0)
        {
            validation = "Default troop count must be a positive number.";
            return false;
        }

        var villageIdMatch = VillageIdPattern.Match(village.Url ?? string.Empty);
        request = new FarmListCreateRequest(
            names,
            village.Name,
            villageIdMatch.Success ? villageIdMatch.Groups[1].Value : null,
            troopType,
            troopCount);
        return true;
    }

    private void SyncListNameFields()
    {
        if (ListCountComboBox?.SelectedItem is not int count)
        {
            return;
        }

        while (_listNameEntries.Count < count)
        {
            _listNameEntries.Add(new FarmListNameEntry(_listNameEntries.Count + 1));
        }

        while (_listNameEntries.Count > count)
        {
            _listNameEntries.RemoveAt(_listNameEntries.Count - 1);
        }
    }

    private sealed class FarmListNameEntry(int index)
    {
        public string Label { get; } = $"List {index}";
        public string Name { get; set; } = string.Empty;
    }
}
