using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public sealed record ConstructFasterSettingsResult(
    VillageSettingsStore.VillageKeyInfo Village,
    bool IsEnabled);

public sealed class ConstructFasterSettingsRow : INotifyPropertyChanged
{
    private bool _isEnabled;

    public ConstructFasterSettingsRow(VillageSettingsStore.VillageKeyInfo village, bool isEnabled)
    {
        Village = village;
        _isEnabled = isEnabled;
    }

    public VillageSettingsStore.VillageKeyInfo Village { get; }
    public string VillageName => Village.Name;
    public bool IsCapital => Village.IsCapital;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public partial class ConstructFasterSettingsWindow : Window
{
    public ObservableCollection<ConstructFasterSettingsRow> Rows { get; }

    public int MinimumBuildMinutes { get; private set; }
    public bool RandomEnabled { get; private set; }
    public int RandomChancePercent { get; private set; }

    public IReadOnlyList<ConstructFasterSettingsResult> Results { get; private set; } =
        Array.Empty<ConstructFasterSettingsResult>();

    public ConstructFasterSettingsWindow(
        IReadOnlyList<ConstructFasterSettingsRow> rows,
        int minimumBuildMinutes,
        bool randomEnabled,
        int randomChancePercent)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        Rows = new ObservableCollection<ConstructFasterSettingsRow>(rows);
        MinimumBuildMinutes = Math.Max(0, minimumBuildMinutes);
        RandomEnabled = randomEnabled;
        RandomChancePercent = Math.Clamp(randomChancePercent, 0, 100);

        DataContext = this;
        SubtitleTextBlock.Text = $"{Rows.Count} village(s)";
        MinimumBuildMinutesTextBox.Text = MinimumBuildMinutes.ToString();
        RandomEnabledCheckBox.IsChecked = RandomEnabled;
        SelectChanceItem(RandomChancePercent);
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
        if (!int.TryParse(MinimumBuildMinutesTextBox.Text.Trim(), out var minMinutes))
        {
            minMinutes = 30;
        }

        MinimumBuildMinutes = Math.Max(0, minMinutes);
        RandomEnabled = RandomEnabledCheckBox.IsChecked == true;
        RandomChancePercent = ReadSelectedChancePercent();
        Results = Rows
            .Select(row => new ConstructFasterSettingsResult(row.Village, row.IsEnabled))
            .ToList();

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private int ReadSelectedChancePercent()
    {
        if (RandomChanceComboBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var value))
        {
            return Math.Clamp(value, 0, 100);
        }

        return 50;
    }

    private void SelectChanceItem(int chancePercent)
    {
        var normalized = Math.Clamp((chancePercent / 10) * 10, 0, 100);
        foreach (var item in RandomChanceComboBox.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse(item.Tag?.ToString(), out var value) && value == normalized)
            {
                RandomChanceComboBox.SelectedItem = item;
                return;
            }
        }

        RandomChanceComboBox.SelectedIndex = 5;
    }
}
