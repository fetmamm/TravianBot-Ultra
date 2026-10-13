using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public sealed class StorageMasterPlanSettingsRow(
    VillageSettingsStore.VillageKeyInfo village,
    bool isEnabled) : INotifyPropertyChanged
{
    private bool _isEnabled = isEnabled;

    public VillageSettingsStore.VillageKeyInfo Village { get; } = village;
    public string VillageName => Village.Name;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class StorageMasterPlanSettingsWindow : Window
{
    public ObservableCollection<StorageMasterPlanSettingsRow> Rows { get; }

    public StorageMasterPlanSettingsWindow(IReadOnlyList<StorageMasterPlanSettingsRow> rows)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        Rows = new ObservableCollection<StorageMasterPlanSettingsRow>(rows);
        DataContext = this;
    }

    private void ToggleAllButton_Click(object sender, RoutedEventArgs e)
    {
        var enable = Rows.Any(row => !row.IsEnabled);
        foreach (var row in Rows)
        {
            row.IsEnabled = enable;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
