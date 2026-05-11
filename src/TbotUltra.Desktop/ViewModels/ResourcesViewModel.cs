using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Resources tab. First slice of the Resources MVVM
/// migration â€” owns just the four per-resource collections that the XAML
/// columns bind to (Wood / Clay / Iron / Cropland). Subsequent commits
/// will fold the pending-target / click-cooldown dictionaries, the active
/// village max-level int, and the pure-logic helpers
/// (RepopulateResourceGroups, GetBucket, ApplyResourceStatusToUi,
/// ResolveQueuedResourceTarget, etc.) here too. Async / service-bound
/// methods will stay on MainWindow.
/// </summary>
public sealed class ResourcesViewModel : BaseViewModel
{
    private const string NotFillingText = "Not filling";
    private static readonly TimeSpan LiveForecastUpdateInterval = TimeSpan.FromSeconds(1);
    private static readonly Brush FullBrush = (Brush)new BrushConverter().ConvertFromString("#B91C1C")!;
    private static readonly Brush DefaultBrush = (Brush)new BrushConverter().ConvertFromString("#111827")!;
    private Dictionary<string, ResourceStorageForecast> _baseForecasts = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _baseForecastCapturedAtUtc;
    private DateTimeOffset _lastLiveForecastUiUpdateUtc = DateTimeOffset.MinValue;

    /// <summary>Resource fields grouped into the Wood column on the Resources tab.</summary>
    public ObservableCollection<ResourceFieldRow> WoodFields { get; } = [];

    /// <summary>Resource fields grouped into the Clay column on the Resources tab.</summary>
    public ObservableCollection<ResourceFieldRow> ClayFields { get; } = [];

    /// <summary>Resource fields grouped into the Iron column on the Resources tab.</summary>
    public ObservableCollection<ResourceFieldRow> IronFields { get; } = [];

    /// <summary>Resource fields grouped into the Cropland column on the Resources tab.</summary>
    public ObservableCollection<ResourceFieldRow> CroplandFields { get; } = [];

    /// <summary>Always-visible storage bars and production cards.</summary>
    public ObservableCollection<ResourceStorageBarItem> StorageBars { get; } =
    [
        CreateBar("wood", "Wood", "#0F766E", "#E4F2F1"),
        CreateBar("clay", "Clay", "#DC4C1D", "#FDECE5"),
        CreateBar("iron", "Iron", "#334155", "#EAF0F4"),
        CreateBar("crop", "Crop", "#C47F00", "#FFF5DF"),
    ];

    public void ApplyStorageForecasts(VillageStatus status)
    {
        var forecasts = status.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);

        _baseForecasts = forecasts;
        _baseForecastCapturedAtUtc = DateTimeOffset.UtcNow;
        _lastLiveForecastUiUpdateUtc = DateTimeOffset.MinValue;

        foreach (var bar in StorageBars)
        {
            forecasts.TryGetValue(bar.ResourceKey, out var forecast);
            ApplyForecast(bar, forecast);
        }
    }

    public void ResetStorageForecasts()
    {
        _baseForecasts = new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);
        _baseForecastCapturedAtUtc = null;
        _lastLiveForecastUiUpdateUtc = DateTimeOffset.MinValue;
        foreach (var bar in StorageBars)
        {
            ApplyForecast(bar, null);
        }
    }

    public void TickLiveForecasts()
    {
        if (_baseForecastCapturedAtUtc is null || _baseForecasts.Count == 0)
        {
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        if (_lastLiveForecastUiUpdateUtc != DateTimeOffset.MinValue
            && nowUtc - _lastLiveForecastUiUpdateUtc < LiveForecastUpdateInterval)
        {
            return;
        }

        var elapsedSeconds = Math.Max(0d, (nowUtc - _baseForecastCapturedAtUtc.Value).TotalSeconds);
        foreach (var bar in StorageBars)
        {
            _baseForecasts.TryGetValue(bar.ResourceKey, out var forecast);
            ApplyForecast(bar, ExtrapolateForecast(forecast, elapsedSeconds));
        }

        _lastLiveForecastUiUpdateUtc = nowUtc;
    }

    private static ResourceStorageBarItem CreateBar(string key, string name, string barColor, string trackColor)
    {
        return new ResourceStorageBarItem
        {
            ResourceKey = key,
            DisplayName = name,
            BarBrush = (Brush)new BrushConverter().ConvertFromString(barColor)!,
            TrackBrush = (Brush)new BrushConverter().ConvertFromString(trackColor)!,
        };
    }

    private static void ApplyForecast(ResourceStorageBarItem bar, ResourceStorageForecast? forecast)
    {
        var isFull = forecast?.Capacity is > 0 && forecast.Current is not null && forecast.Current.Value >= forecast.Capacity.Value;
        var percent = NormalizePercent(forecast?.PercentOfCapacity);
        var percentText = FormatPercentText(forecast?.PercentOfCapacity, isFull);
        var currentMaxText = FormatCurrentMaxText(forecast?.Current, forecast?.Capacity);
        var productionText = FormatProductionText(forecast?.ProductionPerHour);
        var timeUntilFullText = FormatTimeUntilFull(forecast?.SecondsToFull, forecast?.ProductionPerHour, forecast?.Current, forecast?.Capacity);

        bar.PercentValue = percent;
        bar.PercentText = percentText;
        bar.CurrentMaxText = currentMaxText;
        bar.ProductionText = productionText;
        bar.TimeUntilFullText = timeUntilFullText;
        bar.IsFull = isFull;
        bar.StatusBrush = isFull ? FullBrush : DefaultBrush;
        bar.TooltipText = BuildTooltipText(percentText, currentMaxText, productionText, timeUntilFullText);
    }

    private static ResourceStorageForecast? ExtrapolateForecast(ResourceStorageForecast? forecast, double elapsedSeconds)
    {
        if (forecast is null)
        {
            return null;
        }

        var current = forecast.Current;
        var capacity = forecast.Capacity;
        var productionPerHour = forecast.ProductionPerHour;

        if (current is not null && productionPerHour is not null && !double.IsNaN(productionPerHour.Value) && !double.IsInfinity(productionPerHour.Value))
        {
            var delta = productionPerHour.Value / 3600d * elapsedSeconds;
            var projected = (long)Math.Round(current.Value + delta, MidpointRounding.AwayFromZero);
            if (capacity is > 0)
            {
                projected = Math.Clamp(projected, 0L, capacity.Value);
            }
            else
            {
                projected = Math.Max(0L, projected);
            }

            current = projected;
        }

        double? percent = null;
        if (capacity is > 0 && current is not null)
        {
            percent = Math.Clamp((double)current.Value / capacity.Value * 100d, 0d, 100d);
        }

        int? secondsToFull = null;
        if (capacity is > 0 && current is not null && productionPerHour is > 0)
        {
            var remaining = Math.Max(0L, capacity.Value - current.Value);
            var computedSeconds = Math.Ceiling((remaining / productionPerHour.Value) * 3600d);
            secondsToFull = computedSeconds >= int.MaxValue
                ? int.MaxValue
                : (int)computedSeconds;
        }

        return forecast with
        {
            Current = current,
            PercentOfCapacity = percent,
            SecondsToFull = secondsToFull,
        };
    }

    private static double NormalizePercent(double? percent)
    {
        if (percent is null || double.IsNaN(percent.Value) || double.IsInfinity(percent.Value))
        {
            return 0;
        }

        return Math.Clamp(percent.Value, 0, 100);
    }

    private static string FormatPercentText(double? percent, bool isFull)
    {
        if (isFull)
        {
            return "Full";
        }

        if (percent is null || double.IsNaN(percent.Value) || double.IsInfinity(percent.Value))
        {
            return "-";
        }

        return $"{Math.Round(Math.Clamp(percent.Value, 0, 100), MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%";
    }

    private static string FormatCurrentMaxText(long? current, long? capacity)
    {
        var currentText = FormatGroupedNumber(current);
        var capacityText = FormatGroupedNumber(capacity);
        return $"{currentText}/{capacityText}";
    }

    private static string FormatProductionText(double? productionPerHour)
    {
        if (productionPerHour is null || double.IsNaN(productionPerHour.Value) || double.IsInfinity(productionPerHour.Value))
        {
            return "-/h";
        }

        return $"{FormatGroupedNumber((long)Math.Round(productionPerHour.Value, MidpointRounding.AwayFromZero))}/h";
    }

    private static string FormatTimeUntilFull(int? secondsToFull, double? productionPerHour, long? current, long? capacity)
    {
        if (capacity is > 0 && current is not null && current.Value >= capacity.Value)
        {
            return "Full";
        }

        if (productionPerHour is null || productionPerHour <= 0)
        {
            return NotFillingText;
        }

        if (secondsToFull is null || secondsToFull <= 0)
        {
            return "Soon";
        }

        return FormatDuration(secondsToFull.Value);
    }

    private static string BuildTooltipText(string percentText, string currentMaxText, string productionText, string timeUntilFullText)
    {
        return string.Join(Environment.NewLine,
        [
            $"Filled: {percentText}",
            $"Storage: {currentMaxText}",
            $"Production: {productionText}",
            $"Time until full: {timeUntilFullText}",
        ]);
    }

    private static string FormatGroupedNumber(long? value)
    {
        if (value is null)
        {
            return "-";
        }

        return value.Value.ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ");
    }

    private static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0m";
        }

        var span = TimeSpan.FromSeconds(totalSeconds);
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{Math.Max(1, span.Minutes)}m";
    }
}
