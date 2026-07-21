using System.Collections.ObjectModel;
using System.Windows;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MapOasisSettingsWindow : Window
{
    public ObservableCollection<VillageSelectionItem> Villages { get; }
    public VillageSelectionItem? SelectedVillage { get; set; }
    public MapOasisScanRequest? Request { get; private set; }

    public MapOasisSettingsWindow(
        IEnumerable<VillageSelectionItem> villages,
        VillageSelectionItem? selectedVillage)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        Villages = new ObservableCollection<VillageSelectionItem>(villages);
        SelectedVillage = selectedVillage ?? Villages.FirstOrDefault();
        DataContext = this;
        UpdateEnabledControls();
    }

    private void StartingPoint_Changed(object sender, RoutedEventArgs e) => UpdateEnabledControls();

    private void Area_Changed(object sender, RoutedEventArgs e) => UpdateEnabledControls();

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
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var centerX = 0;
        var centerY = 0;
        if (SpecificVillageRadioButton.IsChecked == true)
        {
            if (SelectedVillage?.CoordX is null || SelectedVillage.CoordY is null)
            {
                MessageBox.Show(this, "Select a village with coordinates.", "Analyze map oasis", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            centerX = SelectedVillage.CoordX.Value;
            centerY = SelectedVillage.CoordY.Value;
        }

        var scope = RadiusRadioButton.IsChecked == true
            ? MapOasisScanScope.Radius
            : MapOasisScanScope.WholeMap;
        var radius = MapOasisScanRequest.DefaultRadius;
        if (scope == MapOasisScanScope.Radius
            && (!int.TryParse(RadiusTextBox.Text.Trim(), out radius) || radius < 1 || radius > 200))
        {
            MessageBox.Show(this, "Radius must be a whole number from 1 to 200.", "Analyze map oasis", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Request = new MapOasisScanRequest(
            centerX,
            centerY,
            scope,
            radius,
            FastSpeedRadioButton.IsChecked == true ? MapOasisScanSpeed.Fast : MapOasisScanSpeed.Normal);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
