using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Models;

public sealed class TroopTrainingBuildingOption : INotifyPropertyChanged
{
    private bool _isEnabled;
    private string _selectedTroop = string.Empty;
    private string _maxQueueMode = "no_limit";
    private string _amountMode = "maximum";
    private int _keepResourcesPercent = 10;
    private string _runMode = "timed";
    private int _minimumTroops = 1;
    private int _minimumResourcesPercent = 50;
    private int _timedMinMinutes = 30;
    private int _timedMaxMinutes = 120;
    private bool _checkWood = true;
    private bool _checkClay = true;
    private bool _checkIron = true;
    private bool _checkCrop = true;
    private bool _exists;
    private int? _queueRemainingSeconds;
    private string _queueStatusText = "Queue not loaded.";

    public TroopTrainingBuildingType BuildingType { get; init; }
    public string Title { get; init; } = string.Empty;
    public ObservableCollection<string> TroopOptions { get; } = [];

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

    public string SelectedTroop
    {
        get => _selectedTroop;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_selectedTroop, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _selectedTroop = normalized;
            OnPropertyChanged();
        }
    }

    public string MaxQueueMode
    {
        get => _maxQueueMode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "no_limit" : value.Trim();
            if (string.Equals(_maxQueueMode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _maxQueueMode = normalized;
            OnPropertyChanged();
        }
    }

    public string AmountMode
    {
        get => _amountMode;
        set
        {
            var normalized = string.Equals(value, "keep_resources", StringComparison.OrdinalIgnoreCase)
                ? "keep_resources"
                : "maximum";
            if (string.Equals(_amountMode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _amountMode = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesKeepResourcesMode));
        }
    }

    public int KeepResourcesPercent
    {
        get => _keepResourcesPercent;
        set
        {
            var normalized = Math.Clamp(value, 0, 95);
            if (_keepResourcesPercent == normalized)
            {
                return;
            }

            _keepResourcesPercent = normalized;
            OnPropertyChanged();
        }
    }

    public string RunMode
    {
        get => _runMode;
        set
        {
            var normalized = string.Equals(value, "resource_percent", StringComparison.OrdinalIgnoreCase)
                ? "resource_percent"
                : "timed";
            if (string.Equals(_runMode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _runMode = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesTimedMode));
            OnPropertyChanged(nameof(UsesResourcePercentMode));
            OnPropertyChanged(nameof(IsTimedMode));
            OnPropertyChanged(nameof(IsResourcePercentMode));
        }
    }

    public int MinimumTroops
    {
        get => _minimumTroops;
        set
        {
            var normalized = Math.Max(1, value);
            if (_minimumTroops == normalized)
            {
                return;
            }

            _minimumTroops = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MinimumTroopsText));
        }
    }

    public string MinimumTroopsText
    {
        get => MinimumTroops.ToString();
        set
        {
            if (!int.TryParse(value?.Trim(), out var parsed))
            {
                return;
            }

            MinimumTroops = parsed;
        }
    }

    public int MinimumResourcesPercent
    {
        get => _minimumResourcesPercent;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (_minimumResourcesPercent == normalized)
            {
                return;
            }

            _minimumResourcesPercent = normalized;
            OnPropertyChanged();
        }
    }

    public int TimedMinMinutes
    {
        get => _timedMinMinutes;
        set
        {
            var normalized = Math.Max(1, value);
            if (_timedMinMinutes == normalized)
            {
                return;
            }

            _timedMinMinutes = normalized;
            if (_timedMaxMinutes < normalized)
            {
                _timedMaxMinutes = normalized;
                OnPropertyChanged(nameof(TimedMaxMinutes));
                OnPropertyChanged(nameof(TimedMaxMinutesText));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(TimedMinMinutesText));
        }
    }

    public string TimedMinMinutesText
    {
        get => TimedMinMinutes.ToString();
        set
        {
            if (!int.TryParse(value?.Trim(), out var parsed))
            {
                return;
            }

            TimedMinMinutes = parsed;
        }
    }

    public int TimedMaxMinutes
    {
        get => _timedMaxMinutes;
        set
        {
            var normalized = Math.Max(Math.Max(1, _timedMinMinutes), value);
            if (_timedMaxMinutes == normalized)
            {
                return;
            }

            _timedMaxMinutes = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TimedMaxMinutesText));
        }
    }

    public string TimedMaxMinutesText
    {
        get => TimedMaxMinutes.ToString();
        set
        {
            if (!int.TryParse(value?.Trim(), out var parsed))
            {
                return;
            }

            TimedMaxMinutes = parsed;
        }
    }

    public bool CheckWood
    {
        get => _checkWood;
        set
        {
            if (_checkWood == value)
            {
                return;
            }

            _checkWood = value;
            OnPropertyChanged();
        }
    }

    public bool CheckClay
    {
        get => _checkClay;
        set
        {
            if (_checkClay == value)
            {
                return;
            }

            _checkClay = value;
            OnPropertyChanged();
        }
    }

    public bool CheckIron
    {
        get => _checkIron;
        set
        {
            if (_checkIron == value)
            {
                return;
            }

            _checkIron = value;
            OnPropertyChanged();
        }
    }

    public bool CheckCrop
    {
        get => _checkCrop;
        set
        {
            if (_checkCrop == value)
            {
                return;
            }

            _checkCrop = value;
            OnPropertyChanged();
        }
    }

    public bool Exists
    {
        get => _exists;
        set
        {
            if (_exists == value)
            {
                return;
            }

            _exists = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(QueueTimerText));
        }
    }

    public int? QueueRemainingSeconds
    {
        get => _queueRemainingSeconds;
        set
        {
            var normalized = value.HasValue ? Math.Max(0, value.Value) : (int?)null;
            if (_queueRemainingSeconds == normalized)
            {
                return;
            }

            _queueRemainingSeconds = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(QueueTimerText));
        }
    }

    // Absolute finish of the building's training queue (the in-game `under_progress` countdown), anchored
    // to server time. When set, the dashboard countdown is recomputed from it each tick instead of being
    // blindly decremented — so it stays accurate and survives missed/off-village reads (the queue is the
    // source of truth, same model as construction timers).
    public TimerSnapshot? QueueFinish { get; set; }

    public string QueueStatusText
    {
        get => _queueStatusText;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Queue not loaded." : value.Trim();
            if (string.Equals(_queueStatusText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _queueStatusText = normalized;
            OnPropertyChanged();
        }
    }

    public bool UsesKeepResourcesMode => string.Equals(AmountMode, "keep_resources", StringComparison.OrdinalIgnoreCase);
    public bool UsesTimedMode => string.Equals(RunMode, "timed", StringComparison.OrdinalIgnoreCase);
    public bool UsesResourcePercentMode => string.Equals(RunMode, "resource_percent", StringComparison.OrdinalIgnoreCase);

    public bool IsTimedMode
    {
        get => UsesTimedMode;
        set
        {
            if (value)
            {
                RunMode = "timed";
            }
        }
    }

    public bool IsResourcePercentMode
    {
        get => UsesResourcePercentMode;
        set
        {
            if (value)
            {
                RunMode = "resource_percent";
            }
        }
    }

    public Brush QueueBadgeBackground => !Exists
        ? ThemeColors.Brush("ControlBackgroundBrush")
        : IsQueueReadyToRun
            ? ThemeColors.Brush("SuccessBgBrush")
            : ThemeColors.Brush("WarningBgBrush");

    public Brush QueueBadgeBorderBrush => !Exists
        ? ThemeColors.Brush("BorderMutedBrush")
        : IsQueueReadyToRun
            ? ThemeColors.Brush("SuccessBorderBrush")
            : ThemeColors.Brush("WarningBorderBrush");

    public Brush QueueBadgeForeground => !Exists
        ? ThemeColors.Brush("TextMutedBrush")
        : IsQueueReadyToRun
            ? ThemeColors.Brush("SuccessTextBrush")
            : ThemeColors.Brush("WarningTextBrush");

    public bool IsQueueReadyToRun
    {
        get
        {
            if (!Exists)
            {
                return false;
            }

            if (QueueRemainingSeconds is not > 0)
            {
                return true;
            }

            var maxQueueSeconds = ResolveMaxQueueSeconds();
            if (!maxQueueSeconds.HasValue)
            {
                return true;
            }

            return QueueRemainingSeconds.Value <= maxQueueSeconds.Value;
        }
    }

    public string QueueTimerText
    {
        get
        {
            if (!Exists)
            {
                return "00:00h";
            }

            if (QueueRemainingSeconds is not > 0)
            {
                return "00:00h";
            }

            var time = TimeSpan.FromSeconds(QueueRemainingSeconds.Value);
            var totalHours = (int)Math.Floor(time.TotalHours);
            return totalHours > 99
                ? $"{Math.Min(totalHours, 999):000}:{time.Minutes:00}h"
                : $"{totalHours:00}:{time.Minutes:00}h";
        }
    }

    public bool TickOneSecond()
    {
        if (QueueRemainingSeconds is not > 0)
        {
            return false;
        }

        QueueRemainingSeconds = Math.Max(0, QueueRemainingSeconds.Value - 1);
        return true;
    }

    /// <summary>
    /// Recomputes the displayed queue countdown from the absolute <see cref="QueueFinish"/> against the
    /// supplied server-time clock (source of truth). Falls back to a plain 1s decrement when no finish is
    /// known. Clears the finish once it has elapsed so a stale snapshot can't keep re-showing 0.
    /// </summary>
    public void Tick(DateTimeOffset serverNow)
    {
        if (QueueFinish is null)
        {
            TickOneSecond();
            return;
        }

        var remaining = QueueFinish.RemainingSecondsAt(serverNow);
        QueueRemainingSeconds = remaining > 0 ? remaining : null;
        if (remaining <= 0)
        {
            QueueFinish = null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (string.Equals(propertyName, nameof(Exists), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(QueueRemainingSeconds), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(MaxQueueMode), StringComparison.Ordinal))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueBadgeBackground)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueBadgeBorderBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueBadgeForeground)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsQueueReadyToRun)));
        }
    }

    private int? ResolveMaxQueueSeconds()
    {
        if (string.IsNullOrWhiteSpace(MaxQueueMode)
            || string.Equals(MaxQueueMode, "no_limit", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(MaxQueueMode, out var hours) && hours > 0
            ? hours * 60 * 60
            : null;
    }
}
