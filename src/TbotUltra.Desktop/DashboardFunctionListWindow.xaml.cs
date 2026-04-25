using System.Windows;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class DashboardFunctionListWindow : Window
{
    private readonly List<DashboardFunctionOption> _options;

    public IReadOnlyDictionary<string, bool> SelectedVisibility { get; private set; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public DashboardFunctionListWindow(IEnumerable<DashboardFunctionOption> options)
    {
        InitializeComponent();
        _options = options?.Select(option => new DashboardFunctionOption
        {
            Key = option.Key,
            Label = option.Label,
            IsVisible = option.IsVisible,
        }).ToList() ?? [];
        FunctionOptionsListBox.ItemsSource = _options;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedVisibility = _options.ToDictionary(
            option => option.Key,
            option => option.IsVisible,
            StringComparer.OrdinalIgnoreCase);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var option in _options)
        {
            option.IsVisible = true;
        }

        FunctionOptionsListBox.Items.Refresh();
    }

    private void UnselectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var option in _options)
        {
            option.IsVisible = false;
        }

        FunctionOptionsListBox.Items.Refresh();
    }
}
