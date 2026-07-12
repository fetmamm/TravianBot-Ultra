using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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

// Account-wide celebration-queue settings shown in the box at the bottom of the popup: how many
// celebrations to keep active (one, or two with one queued via Plus) and the random restart delay applied
// after a celebration frees a slot. Global — not per village.
public sealed class TownHallQueueSettings : INotifyPropertyChanged
{
    private bool _isTwo;
    private string _delayMinMinutes;
    private string _delayMaxMinutes;

    public TownHallQueueSettings(int count, double delayMinMinutes, double delayMaxMinutes)
    {
        _isTwo = TownHallCelebrationDefaults.NormalizeCount(count) >= TownHallCelebrationDefaults.MaxCount;
        _delayMinMinutes = FormatMinutes(delayMinMinutes);
        _delayMaxMinutes = FormatMinutes(delayMaxMinutes);
    }

    public bool IsOne
    {
        get => !_isTwo;
        set { if (value) { SetTwo(false); } }
    }

    public bool IsTwo
    {
        get => _isTwo;
        set { if (value) { SetTwo(true); } }
    }

    public string DelayMinMinutes
    {
        get => _delayMinMinutes;
        set { _delayMinMinutes = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string DelayMaxMinutes
    {
        get => _delayMaxMinutes;
        set { _delayMaxMinutes = value ?? string.Empty; OnPropertyChanged(); }
    }

    public int Count => _isTwo ? TownHallCelebrationDefaults.MaxCount : TownHallCelebrationDefaults.MinCount;

    public double ResolvedDelayMinMinutes =>
        Math.Max(0, ParseMinutes(_delayMinMinutes, TownHallCelebrationDefaults.DefaultRestartDelayMinMinutes));

    public double ResolvedDelayMaxMinutes =>
        Math.Max(ResolvedDelayMinMinutes, ParseMinutes(_delayMaxMinutes, TownHallCelebrationDefaults.DefaultRestartDelayMaxMinutes));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetTwo(bool value)
    {
        if (_isTwo == value)
        {
            return;
        }

        _isTwo = value;
        OnPropertyChanged(nameof(IsOne));
        OnPropertyChanged(nameof(IsTwo));
    }

    private static string FormatMinutes(double value) =>
        Math.Max(0, value).ToString("0.##", CultureInfo.InvariantCulture);

    private static double ParseMinutes(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : fallback;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public partial class TownHallOverviewWindow : Window
{
    public ObservableCollection<TownHallOverviewRow> Rows { get; }

    public TownHallQueueSettings Queue { get; }

    public IReadOnlyList<TownHallOverviewResult> Results { get; private set; } =
        Array.Empty<TownHallOverviewResult>();

    public TownHallOverviewWindow(
        IReadOnlyList<TownHallOverviewRow> rows,
        int celebrationCount,
        double restartDelayMinMinutes,
        double restartDelayMaxMinutes)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        Rows = new ObservableCollection<TownHallOverviewRow>(rows);
        Queue = new TownHallQueueSettings(celebrationCount, restartDelayMinMinutes, restartDelayMaxMinutes);
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
