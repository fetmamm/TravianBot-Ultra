using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class TownHallQueueSettings : INotifyPropertyChanged
{
    private bool _isRestartDelayEnabled;
    private bool _isTwo;
    private string _delayMinMinutes;
    private string _delayMaxMinutes;

    public TownHallQueueSettings(bool restartDelayEnabled, int count, double delayMinMinutes, double delayMaxMinutes)
    {
        _isRestartDelayEnabled = restartDelayEnabled;
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

    public bool IsRestartDelayEnabled
    {
        get => _isRestartDelayEnabled;
        set
        {
            if (_isRestartDelayEnabled == value)
            {
                return;
            }

            _isRestartDelayEnabled = value;
            OnPropertyChanged();
        }
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RestartDelaySettings : INotifyPropertyChanged
{
    private bool _isEnabled;
    private readonly double _defaultMinMinutes;
    private readonly double _defaultMaxMinutes;
    private string _delayMinMinutes;
    private string _delayMaxMinutes;

    public RestartDelaySettings(
        bool isEnabled,
        double delayMinMinutes,
        double delayMaxMinutes,
        double defaultMinMinutes,
        double defaultMaxMinutes)
    {
        _isEnabled = isEnabled;
        _defaultMinMinutes = defaultMinMinutes;
        _defaultMaxMinutes = defaultMaxMinutes;
        _delayMinMinutes = FormatMinutes(delayMinMinutes);
        _delayMaxMinutes = FormatMinutes(delayMaxMinutes);
    }

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
            OnPropertyChanged();
        }
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

    public double ResolvedDelayMinMinutes => Math.Max(0, ParseMinutes(_delayMinMinutes, _defaultMinMinutes));

    public double ResolvedDelayMaxMinutes =>
        Math.Max(ResolvedDelayMinMinutes, ParseMinutes(_delayMaxMinutes, _defaultMaxMinutes));

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatMinutes(double value) =>
        Math.Max(0, value).ToString("0.##", CultureInfo.InvariantCulture);

    private static double ParseMinutes(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : fallback;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
