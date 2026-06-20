using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop;

public sealed record TownHallOverviewResult(
    string VillageKey,
    string VillageName,
    bool IsTownHallEnabled,
    string Mode);

public sealed class TownHallOverviewRow : INotifyPropertyChanged
{
    private bool _isTownHallEnabled;
    private string _mode;

    public TownHallOverviewRow(string villageKey, string villageName, bool isTownHallEnabled, string mode)
    {
        VillageKey = villageKey;
        VillageName = villageName;
        _isTownHallEnabled = isTownHallEnabled;
        _mode = TownHallCelebrationDefaults.NormalizeMode(mode);
    }

    public string VillageKey { get; }
    public string VillageName { get; }

    public bool IsTownHallEnabled
    {
        get => _isTownHallEnabled;
        set
        {
            if (_isTownHallEnabled == value)
            {
                return;
            }

            _isTownHallEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool IsSmall
    {
        get => string.Equals(_mode, TownHallCelebrationDefaults.Small, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                SetMode(TownHallCelebrationDefaults.Small);
            }
        }
    }

    public bool IsBig
    {
        get => string.Equals(_mode, TownHallCelebrationDefaults.Big, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                SetMode(TownHallCelebrationDefaults.Big);
            }
        }
    }

    public string Mode => _mode;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetMode(string mode)
    {
        var normalized = TownHallCelebrationDefaults.NormalizeMode(mode);
        if (string.Equals(_mode, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _mode = normalized;
        OnPropertyChanged(nameof(IsSmall));
        OnPropertyChanged(nameof(IsBig));
        OnPropertyChanged(nameof(Mode));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public partial class TownHallOverviewWindow : Window
{
    public ObservableCollection<TownHallOverviewRow> Rows { get; }

    public IReadOnlyList<TownHallOverviewResult> Results { get; private set; } =
        Array.Empty<TownHallOverviewResult>();

    public TownHallOverviewWindow(IReadOnlyList<TownHallOverviewRow> rows)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        Rows = new ObservableCollection<TownHallOverviewRow>(rows);
        DataContext = this;
        SubtitleTextBlock.Text = $"{Rows.Count} village(s)";
    }

    private IReadOnlyList<TownHallOverviewResult> BuildResults()
    {
        return Rows
            .Select(row => new TownHallOverviewResult(
                row.VillageKey,
                row.VillageName,
                row.IsTownHallEnabled,
                row.Mode))
            .ToList();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Results = BuildResults();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
