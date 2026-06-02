using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Models;

public sealed class ResourceTransferVillageItem : INotifyPropertyChanged
{
    private static readonly string[] ResourceKeys = ["wood", "clay", "iron", "crop"];
    private static readonly Brush NeutralPercentBrush = new SolidColorBrush(Color.FromRgb(17, 24, 39));
    private static readonly Brush WarningPercentBrush = new SolidColorBrush(Color.FromRgb(180, 83, 9));
    private static readonly Brush FullPercentBrush = new SolidColorBrush(Color.FromRgb(185, 28, 28));

    private bool _isSource;
    private bool _isTarget;
    private Dictionary<string, ResourceStorageForecast> _baseForecasts = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _baseForecastCapturedAtUtc;
    private string _woodPercentText = "-";
    private string _clayPercentText = "-";
    private string _ironPercentText = "-";
    private string _cropPercentText = "-";
    private double _woodPercentValue;
    private double _clayPercentValue;
    private double _ironPercentValue;
    private double _cropPercentValue;
    private Brush _woodPercentBrush = NeutralPercentBrush;
    private Brush _clayPercentBrush = NeutralPercentBrush;
    private Brush _ironPercentBrush = NeutralPercentBrush;
    private Brush _cropPercentBrush = NeutralPercentBrush;

    public string Name { get; init; } = string.Empty;
    public int? CoordX { get; init; }
    public int? CoordY { get; init; }

    public bool IsSource
    {
        get => _isSource;
        set
        {
            if (_isTarget && value)
            {
                value = false;
            }

            if (_isSource == value)
            {
                return;
            }

            _isSource = value;
            OnPropertyChanged();
        }
    }

    public bool IsTarget
    {
        get => _isTarget;
        set
        {
            if (_isTarget == value)
            {
                return;
            }

            _isTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSelectAsSource));
            OnPropertyChanged(nameof(SourceStatusText));

            if (_isTarget)
            {
                IsSource = false;
            }
        }
    }

    public bool CanSelectAsSource => !IsTarget;

    public string SourceStatusText => IsTarget ? "Target" : string.Empty;

    public string WoodPercentText
    {
        get => _woodPercentText;
        private set => SetText(ref _woodPercentText, value);
    }

    public double WoodPercentValue
    {
        get => _woodPercentValue;
        private set => SetValue(ref _woodPercentValue, value);
    }

    public Brush WoodPercentBrush
    {
        get => _woodPercentBrush;
        private set => SetBrush(ref _woodPercentBrush, value);
    }

    public string ClayPercentText
    {
        get => _clayPercentText;
        private set => SetText(ref _clayPercentText, value);
    }

    public double ClayPercentValue
    {
        get => _clayPercentValue;
        private set => SetValue(ref _clayPercentValue, value);
    }

    public Brush ClayPercentBrush
    {
        get => _clayPercentBrush;
        private set => SetBrush(ref _clayPercentBrush, value);
    }

    public string IronPercentText
    {
        get => _ironPercentText;
        private set => SetText(ref _ironPercentText, value);
    }

    public double IronPercentValue
    {
        get => _ironPercentValue;
        private set => SetValue(ref _ironPercentValue, value);
    }

    public Brush IronPercentBrush
    {
        get => _ironPercentBrush;
        private set => SetBrush(ref _ironPercentBrush, value);
    }

    public string CropPercentText
    {
        get => _cropPercentText;
        private set => SetText(ref _cropPercentText, value);
    }

    public double CropPercentValue
    {
        get => _cropPercentValue;
        private set => SetValue(ref _cropPercentValue, value);
    }

    public Brush CropPercentBrush
    {
        get => _cropPercentBrush;
        private set => SetBrush(ref _cropPercentBrush, value);
    }

    public string DisplayName
    {
        get
        {
            if (CoordX.HasValue && CoordY.HasValue)
            {
                return $"{Name} ({CoordX} | {CoordY})";
            }

            return Name;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyResourceStatus(VillageStatus status)
    {
        var forecasts = BuildForecastLookup(status);
        if (!forecasts.Values.Any(HasUsableStorageForecast))
        {
            return;
        }

        _baseForecasts = forecasts;
        _baseForecastCapturedAtUtc = DateTimeOffset.UtcNow;
        TickResourceForecasts();
    }

    private static bool HasUsableStorageForecast(ResourceStorageForecast forecast)
    {
        return forecast.Current is not null
            || forecast.Capacity is not null
            || forecast.PercentOfCapacity is not null
            || forecast.ProductionPerHour is not null;
    }

    public void ApplyResourceStatusFrom(ResourceTransferVillageItem source)
    {
        _baseForecasts = new Dictionary<string, ResourceStorageForecast>(source._baseForecasts, StringComparer.OrdinalIgnoreCase);
        _baseForecastCapturedAtUtc = source._baseForecastCapturedAtUtc;
        WoodPercentText = source.WoodPercentText;
        ClayPercentText = source.ClayPercentText;
        IronPercentText = source.IronPercentText;
        CropPercentText = source.CropPercentText;
        WoodPercentValue = source.WoodPercentValue;
        ClayPercentValue = source.ClayPercentValue;
        IronPercentValue = source.IronPercentValue;
        CropPercentValue = source.CropPercentValue;
        WoodPercentBrush = source.WoodPercentBrush;
        ClayPercentBrush = source.ClayPercentBrush;
        IronPercentBrush = source.IronPercentBrush;
        CropPercentBrush = source.CropPercentBrush;
    }

    public void TickResourceForecasts()
    {
        if (_baseForecastCapturedAtUtc is null || _baseForecasts.Count == 0)
        {
            return;
        }

        var elapsedSeconds = Math.Max(0d, (DateTimeOffset.UtcNow - _baseForecastCapturedAtUtc.Value).TotalSeconds);
        ApplyPercent("wood", elapsedSeconds, value =>
        {
            WoodPercentText = FormatPercentText(value);
            WoodPercentValue = NormalizePercentValue(value);
            WoodPercentBrush = ResolvePercentBrush(value);
        });
        ApplyPercent("clay", elapsedSeconds, value =>
        {
            ClayPercentText = FormatPercentText(value);
            ClayPercentValue = NormalizePercentValue(value);
            ClayPercentBrush = ResolvePercentBrush(value);
        });
        ApplyPercent("iron", elapsedSeconds, value =>
        {
            IronPercentText = FormatPercentText(value);
            IronPercentValue = NormalizePercentValue(value);
            IronPercentBrush = ResolvePercentBrush(value);
        });
        ApplyPercent("crop", elapsedSeconds, value =>
        {
            CropPercentText = FormatPercentText(value);
            CropPercentValue = NormalizePercentValue(value);
            CropPercentBrush = ResolvePercentBrush(value);
        });
    }

    private void ApplyPercent(string key, double elapsedSeconds, Action<double?> apply)
    {
        apply(ResolvePercent(key, elapsedSeconds));
    }

    private static Dictionary<string, ResourceStorageForecast> BuildForecastLookup(VillageStatus status)
    {
        var forecasts = status.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in ResourceKeys)
        {
            if (forecasts.TryGetValue(key, out var forecast) && forecast.Capacity is not null && forecast.Current is not null)
            {
                continue;
            }

            status.Resources.TryGetValue(key, out var raw);
            var current = TryParseResourceValue(raw) ?? forecast?.Current;
            var capacity = forecast?.Capacity
                ?? (string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                    ? status.GranaryCapacity
                    : status.WarehouseCapacity);
            var production = forecast?.ProductionPerHour;
            double? percent = null;
            if (capacity is > 0 && current is not null)
            {
                percent = Math.Clamp((double)current.Value / capacity.Value * 100d, 0d, 100d);
            }

            forecasts[key] = new ResourceStorageForecast(
                ResourceKey: key,
                Current: current,
                Capacity: capacity,
                PercentOfCapacity: percent,
                ProductionPerHour: production,
                SecondsToFull: forecast?.SecondsToFull);
        }

        return forecasts;
    }

    private double? ResolvePercent(string key, double elapsedSeconds)
    {
        if (!_baseForecasts.TryGetValue(key, out var forecast))
        {
            return null;
        }

        var current = forecast.Current;
        if (current is not null && forecast.ProductionPerHour is double production
            && !double.IsNaN(production) && !double.IsInfinity(production))
        {
            var projected = current.Value + production / 3600d * elapsedSeconds;
            current = (long)Math.Round(projected, MidpointRounding.AwayFromZero);
        }

        if (forecast.Capacity is not > 0 || current is null)
        {
            return forecast.PercentOfCapacity;
        }

        return Math.Clamp((double)current.Value / forecast.Capacity.Value * 100d, 0d, 100d);
    }

    private static string FormatPercentText(double? percent)
    {
        if (percent is null || double.IsNaN(percent.Value) || double.IsInfinity(percent.Value))
        {
            return "-";
        }

        var clamped = Math.Clamp(percent.Value, 0d, 100d);
        if (clamped >= 100d)
        {
            return "Full";
        }

        return $"{Math.Floor(clamped).ToString("0", CultureInfo.InvariantCulture)}%";
    }

    private static double NormalizePercentValue(double? percent)
    {
        if (percent is null || double.IsNaN(percent.Value) || double.IsInfinity(percent.Value))
        {
            return 0d;
        }

        return Math.Clamp(percent.Value, 0d, 100d);
    }

    private static Brush ResolvePercentBrush(double? percent)
    {
        if (percent is null || double.IsNaN(percent.Value) || double.IsInfinity(percent.Value))
        {
            return NeutralPercentBrush;
        }

        if (percent.Value >= 95d)
        {
            return FullPercentBrush;
        }

        return percent.Value >= 90d ? WarningPercentBrush : NeutralPercentBrush;
    }

    private static long? TryParseResourceValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Replace(" ", string.Empty).Replace("'", string.Empty).Replace(",", string.Empty).Trim();
        return long.TryParse(normalized, out var parsed) ? parsed : null;
    }

    private void SetText(ref string storage, string value, [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(storage, value, StringComparison.Ordinal))
        {
            return;
        }

        storage = value;
        OnPropertyChanged(propertyName);
    }

    private void SetValue(ref double storage, double value, [CallerMemberName] string? propertyName = null)
    {
        if (Math.Abs(storage - value) < 0.01d)
        {
            return;
        }

        storage = value;
        OnPropertyChanged(propertyName);
    }

    private void SetBrush(ref Brush storage, Brush value, [CallerMemberName] string? propertyName = null)
    {
        if (ReferenceEquals(storage, value))
        {
            return;
        }

        storage = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
