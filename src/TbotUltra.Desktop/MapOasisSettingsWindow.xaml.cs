using System.Collections.ObjectModel;
using System.Windows;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MapOasisSettingsWindow : Window
{
    private readonly bool _hasPreviousScan;

    public ObservableCollection<VillageSelectionItem> Villages { get; }
    public VillageSelectionItem? SelectedVillage { get; set; }
    public MapOasisScanRequest? Request { get; private set; }

    public MapOasisSettingsWindow(
        IEnumerable<VillageSelectionItem> villages,
        VillageSelectionItem? selectedVillage,
        bool hasPreviousScan = false)
    {
        _hasPreviousScan = hasPreviousScan;
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        Villages = new ObservableCollection<VillageSelectionItem>(villages);
        SelectedVillage = selectedVillage ?? Villages.FirstOrDefault();
        DataContext = this;
        UpdateEnabledControls();
    }

    private void StartingPoint_Changed(object sender, RoutedEventArgs e) => UpdateEnabledControls();

    private void Area_Changed(object sender, RoutedEventArgs e) => UpdateEnabledControls();

    private void EstimateInput_Changed(object sender, RoutedEventArgs e) => UpdateRequestEstimate();

    private void UpdateEnabledControls()
    {
        if (VillageComboBox is not null)
        {
            VillageComboBox.IsEnabled = SpecificVillageRadioButton.IsChecked == true;
        }

        if (RadiusTextBox is not null)
        {
            RadiusTextBox.IsEnabled = RadiusRadioButton.IsChecked == true;
        }

        UpdateRequestEstimate();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadRequest(out var request, out var error))
        {
            MessageBox.Show(this, error, "Analyze map oasis", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var estimate = MapOasisScanEstimate.Calculate(request!);
        var warning = BuildConfirmationMessage(request!, estimate, _hasPreviousScan);
        if (warning is not null)
        {
            var result = AppDialog.ShowCustom(
                this,
                warning,
                "Confirm map oasis scan",
                [("Continue", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel,
                MessageBoxResult.Cancel,
                accentResult: MessageBoxResult.Yes);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        Request = request;
        DialogResult = true;
        Close();
    }

    internal static string? BuildConfirmationMessage(
        MapOasisScanRequest request,
        MapOasisScanEstimate estimate,
        bool hasPreviousScan)
    {
        var warnings = new List<string>();
        if (request.Scope == MapOasisScanScope.WholeMap)
        {
            warnings.Add(
                $"A whole-map scan can require {estimate.Zoom3Requests} requests at zoom 3, " +
                $"{estimate.Zoom2Requests} at zoom 2, or {estimate.Zoom1Requests} at zoom 1.");
        }

        if (hasPreviousScan)
        {
            warnings.Add(
                "A previous scan exists for this account and server. Oasis positions are normally unchanged, " +
                "but scanning again can refresh animals.");
        }

        return warnings.Count == 0
            ? null
            : string.Join("\n\n", warnings) + "\n\nContinue with the scan?";
    }

    private void UpdateRequestEstimate()
    {
        if (RequestEstimateTextBlock is null)
        {
            return;
        }

        if (!TryReadRequest(out var request, out _))
        {
            RequestEstimateTextBlock.Text = "Estimated requests are available after valid coordinates and radius are selected.";
            return;
        }

        var estimate = MapOasisScanEstimate.Calculate(request!);
        RequestEstimateTextBlock.Text =
            $"Estimated requests — zoom 3: {estimate.Zoom3Requests}; " +
            $"fallback zoom 2: {estimate.Zoom2Requests}; zoom 1: {estimate.Zoom1Requests}.";
    }

    private bool TryReadRequest(out MapOasisScanRequest? request, out string error)
    {
        request = null;
        error = string.Empty;
        var centerX = 0;
        var centerY = 0;
        if (SpecificVillageRadioButton?.IsChecked == true)
        {
            var selectedVillage = VillageComboBox?.SelectedItem as VillageSelectionItem ?? SelectedVillage;
            if (selectedVillage?.CoordX is null || selectedVillage.CoordY is null)
            {
                error = "Select a village with coordinates.";
                return false;
            }

            centerX = selectedVillage.CoordX.Value;
            centerY = selectedVillage.CoordY.Value;
        }

        var scope = RadiusRadioButton?.IsChecked == true
            ? MapOasisScanScope.Radius
            : MapOasisScanScope.WholeMap;
        var radius = MapOasisScanRequest.DefaultRadius;
        if (scope == MapOasisScanScope.Radius
            && (!int.TryParse(RadiusTextBox?.Text.Trim(), out radius) || radius < 1 || radius > 200))
        {
            error = "Radius must be a whole number from 1 to 200.";
            return false;
        }

        request = new MapOasisScanRequest(
            centerX,
            centerY,
            scope,
            radius,
            FastSpeedRadioButton?.IsChecked == true ? MapOasisScanSpeed.Fast : MapOasisScanSpeed.Normal);
        return true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
