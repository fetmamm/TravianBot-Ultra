using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
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
    private static readonly Brush FullBrush = (Brush)new BrushConverter().ConvertFromString("#B91C1C")!;
    private static readonly Brush DefaultBrush = (Brush)new BrushConverter().ConvertFromString("#111827")!;
    private Dictionary<string, ResourceStorageForecast> _baseForecasts = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _baseForecastCapturedAtUtc;
    private readonly Dictionary<int, int> _pendingTargetBySlot = new();
    private List<ResourceFieldRow> _allFields = [];

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

    public bool UseDenseCroplandLayout => CroplandFields.Count > 6;

    private string _infoText = "Resources not loaded yet.";

    /// <summary>
    /// Status line describing the last resources action (load summary, queued
    /// upgrade, fast level update). Code-behind pushes values here; the
    /// Resources tab binds a TextBlock to it.
    /// </summary>
    public string InfoText
    {
        get => _infoText;
        set => SetProperty(ref _infoText, value);
    }

    /// <summary>Target-level choices for the "upgrade all" combo box (1â€“40).</summary>
    public IReadOnlyList<int> TargetLevelOptions { get; } = Enumerable.Range(1, 40).ToList();

    private int _selectedTargetLevel = 10;

    /// <summary>Selected target level for the bulk resource upgrade action.</summary>
    public int SelectedTargetLevel
    {
        get => _selectedTargetLevel;
        set => SetProperty(ref _selectedTargetLevel, value);
    }

    private bool _actionsEnabled = true;

    /// <summary>
    /// Enable-state for the Resources tab action controls (load, target combo,
    /// upgrade-all, upgrade-to-max). Toggled while a resources action runs.
    /// </summary>
    public bool ActionsEnabled
    {
        get => _actionsEnabled;
        set => SetProperty(ref _actionsEnabled, value);
    }

    private bool _isBuildLowestFirst;
    private bool _isBuildSmart = true;

    /// <summary>
    /// Upgrade-all strategy: build the lowest-level field first. Mutually
    /// exclusive with <see cref="IsBuildSmart"/>.
    /// </summary>
    public bool IsBuildLowestFirst
    {
        get => _isBuildLowestFirst;
        set
        {
            if (SetProperty(ref _isBuildLowestFirst, value) && value)
            {
                IsBuildSmart = false;
            }
        }
    }

    /// <summary>
    /// Upgrade-all strategy: build the field of the resource type with the
    /// lowest current stock first. Mutually exclusive with
    /// <see cref="IsBuildLowestFirst"/>.
    /// </summary>
    public bool IsBuildSmart
    {
        get => _isBuildSmart;
        set
        {
            if (SetProperty(ref _isBuildSmart, value) && value)
            {
                IsBuildLowestFirst = false;
            }
        }
    }

    /// <summary>String form of the build strategy, "smart" or "lowest_first".</summary>
    public string BuildStrategy => IsBuildSmart ? "smart" : "lowest_first";

    /// <summary>Loads the resource build strategy from a freshly read <see cref="BotOptions"/>.</summary>
    public void LoadSettingsFromConfig(BotOptions options)
    {
        if (string.Equals(options.ResourceBuildStrategy, "smart", StringComparison.OrdinalIgnoreCase))
        {
            IsBuildSmart = true;
        }
        else
        {
            IsBuildLowestFirst = true;
        }
    }

    /// <summary>
    /// The canonical flat list of resource-field rows for the active village.
    /// Previously held on a hidden DataGrid in the visual tree; now owned here.
    /// The grouped Wood / Clay / Iron / Cropland columns are derived from it.
    /// </summary>
    public IReadOnlyList<ResourceFieldRow> AllFields => _allFields;

    /// <summary>
    /// Replaces the canonical row list and rebuilds the grouped column
    /// collections from it.
    /// </summary>
    public void SetAllFields(IEnumerable<ResourceFieldRow> rows)
    {
        _allFields = rows.ToList();
        RebuildFieldGroups(_allFields);
    }

    private void RebuildFieldGroups(IEnumerable<ResourceFieldRow> rows)
    {
        WoodFields.Clear();
        ClayFields.Clear();
        IronFields.Clear();
        CroplandFields.Clear();

        foreach (var row in rows.OrderBy(item => item.SlotId))
        {
            GetBucket(row).Add(row);
        }
    }

    public void ApplyStorageForecasts(VillageStatus status)
    {
        var forecasts = status.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);

        _baseForecasts = forecasts;
        _baseForecastCapturedAtUtc = DateTimeOffset.UtcNow;

        // Don't render the freshly-read values here. Doing so updates the bars off the 1s clock
        // beat (at the random moment a function re-reads real resources), which makes the storage
        // counters visibly "blip". Instead we only refresh the extrapolation base above and let the
        // next regular TickLiveForecasts() render it, so the counters keep advancing on a steady
        // once-per-second cadence. When there is no forecast data we still render immediately to
        // clear the bars, since TickLiveForecasts() no-ops on an empty base.
        if (forecasts.Count == 0)
        {
            foreach (var bar in StorageBars)
            {
                ApplyForecast(bar, null);
            }
        }
    }

    public void ResetStorageForecasts()
    {
        _baseForecasts = new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);
        _baseForecastCapturedAtUtc = null;
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

        // No self-throttle here: the caller is the 1s clock timer. A "< 1s since last update"
        // guard caused drift — timer jitter (a tick landing ~0.98s after the previous update)
        // skipped that tick, so storage counters advanced every other second instead of every
        // second. The extrapolation below is idempotent (computed from a fixed base time), so it
        // is safe and cheap to run on every clock tick.
        var nowUtc = DateTimeOffset.UtcNow;
        var elapsedSeconds = Math.Max(0d, (nowUtc - _baseForecastCapturedAtUtc.Value).TotalSeconds);
        foreach (var bar in StorageBars)
        {
            _baseForecasts.TryGetValue(bar.ResourceKey, out var forecast);
            ApplyForecast(bar, ExtrapolateForecast(forecast, elapsedSeconds));
        }
    }

    /// <summary>
    /// Resolves the effective pending upgrade target shown on a resource slot,
    /// reconciling the queue's queued target with this view model's remembered
    /// pending target. Returns null (and forgets the slot) when there is no
    /// higher target than the field's current level. The remembered target lets
    /// a freshly clicked upgrade survive a queue/UI refresh before the queue has
    /// caught up.
    /// </summary>
    public int? ResolveQueuedResourceTarget(int slotId, int currentLevel, IReadOnlyDictionary<int, int> queuedTargetsBySlot)
    {
        var hasQueuedTarget = queuedTargetsBySlot.TryGetValue(slotId, out var queuedTarget) && queuedTarget > 0;
        if (!hasQueuedTarget)
        {
            _pendingTargetBySlot.Remove(slotId);
            return null;
        }

        var effectiveTarget = queuedTarget;
        var hasPendingTarget = _pendingTargetBySlot.TryGetValue(slotId, out var rememberedTarget) && rememberedTarget > 0;
        if (hasPendingTarget && rememberedTarget > effectiveTarget)
        {
            effectiveTarget = rememberedTarget;
        }

        if (effectiveTarget <= currentLevel)
        {
            _pendingTargetBySlot.Remove(slotId);
            return null;
        }

        _pendingTargetBySlot[slotId] = effectiveTarget;
        return effectiveTarget;
    }

    /// <summary>Reads the remembered pending target for a slot, if any.</summary>
    public bool TryGetPendingTarget(int slotId, out int target) => _pendingTargetBySlot.TryGetValue(slotId, out target);

    /// <summary>Remembers a pending upgrade target for a slot.</summary>
    public void RememberPendingTarget(int slotId, int target) => _pendingTargetBySlot[slotId] = target;

    /// <summary>Forgets the remembered pending target for a single slot.</summary>
    public void ForgetPendingTarget(int slotId) => _pendingTargetBySlot.Remove(slotId);

    /// <summary>Forgets all remembered pending targets (e.g. on village switch).</summary>
    public void ClearPendingTargets() => _pendingTargetBySlot.Clear();

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

    private ObservableCollection<ResourceFieldRow> GetBucket(ResourceFieldRow row)
    {
        var fieldType = row.FieldType?.Trim() ?? string.Empty;
        if (fieldType.Contains("wood", StringComparison.OrdinalIgnoreCase))
        {
            return WoodFields;
        }

        if (fieldType.Contains("clay", StringComparison.OrdinalIgnoreCase))
        {
            return ClayFields;
        }

        if (fieldType.Contains("iron", StringComparison.OrdinalIgnoreCase))
        {
            return IronFields;
        }

        if (fieldType.Contains("crop", StringComparison.OrdinalIgnoreCase))
        {
            return CroplandFields;
        }

        return row.SlotId switch
        {
            1 or 5 or 6 or 10 or 16 => WoodFields,
            2 or 4 or 7 or 14 or 17 => ClayFields,
            3 or 8 or 9 or 11 or 15 => IronFields,
            12 or 13 or 18 => CroplandFields,
            _ => CroplandFields,
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

        return $"{Math.Floor(Math.Clamp(percent.Value, 0, 100)).ToString("0", CultureInfo.InvariantCulture)}%";
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
