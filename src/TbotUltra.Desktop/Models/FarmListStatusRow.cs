using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Models;

public sealed class FarmListStatusRow : INotifyPropertyChanged
{
    private bool _isEnabled;
    private string _name = string.Empty;
    private string _villageName = string.Empty;
    private string _villageHeaderText = string.Empty;
    private int _villageOrdinal = -1;
    private string? _listId;
    private int _activeFarmCount;
    private int _totalFarmCount;
    private int? _capacity;
    private int? _remainingSeconds;
    private DateTimeOffset? _lastSentAtUtc;
    private DateTimeOffset? _nextSendAtUtc;
    private string _intervalMinMinutesText = string.Empty;
    private string _intervalMaxMinutesText = string.Empty;
    private bool _lastSendFailed;
    private bool _showLastSentTimer = true;
    private bool _lastSentLimitEnabled = true;
    private int _lastSentLimitHours = 24;
    private bool _isProcessing;
    private bool _isPlaceholder;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            OnPropertyChanged();
        }
    }

    // Display name of the owning village. Kept for reference; the UI groups by VillageOrdinal and labels
    // the heading with VillageHeaderText (name + coordinates when resolvable).
    public string VillageName
    {
        get => _villageName;
        set
        {
            var normalized = value ?? string.Empty;
            if (_villageName == normalized)
            {
                return;
            }

            _villageName = normalized;
            OnPropertyChanged();
        }
    }

    // Grouping key: the ordinal of the owning .villageWrapper on the farm page. Two villages that share a
    // display name still get distinct ordinals, so they stay as separate groups. -1 for the placeholder.
    public int VillageOrdinal
    {
        get => _villageOrdinal;
        set
        {
            if (_villageOrdinal == value)
            {
                return;
            }

            _villageOrdinal = value;
            OnPropertyChanged();
        }
    }

    // Heading label shown for the village group: the village name, with coordinates appended when they can
    // be resolved unambiguously ("Name (x | y)"). Empty for the placeholder group, whose header is hidden.
    public string VillageHeaderText
    {
        get => _villageHeaderText;
        set
        {
            var normalized = value ?? string.Empty;
            if (_villageHeaderText == normalized)
            {
                return;
            }

            _villageHeaderText = normalized;
            OnPropertyChanged();
        }
    }

    // Stable Travian farm-list id (lid). Used to keep the selection matched after a village/list
    // rename — the display Name changes but the lid does not. May be null for lists where the lid
    // could not be resolved from the page.
    public string? ListId
    {
        get => _listId;
        set
        {
            if (_listId == value)
            {
                return;
            }

            _listId = value;
            OnPropertyChanged();
        }
    }

    public int ActiveFarmCount
    {
        get => _activeFarmCount;
        set
        {
            if (_activeFarmCount == value)
            {
                return;
            }

            _activeFarmCount = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(FarmCountText));
            OnPropertyChanged(nameof(FarmCountCompactText));
            OnPropertyChanged(nameof(FillPercent));
        }
    }

    public int TotalFarmCount
    {
        get => _totalFarmCount;
        set
        {
            if (_totalFarmCount == value)
            {
                return;
            }

            _totalFarmCount = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(FarmCountText));
            OnPropertyChanged(nameof(FarmCountCompactText));
            OnPropertyChanged(nameof(FillPercent));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ReadyText));
            OnPropertyChanged(nameof(ActionText));
            OnPropertyChanged(nameof(CanSendNow));
        }
    }

    public int? Capacity
    {
        get => _capacity;
        set
        {
            var normalized = value is > 0 ? value : null;
            if (_capacity == normalized)
            {
                return;
            }

            _capacity = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FarmCountText));
            OnPropertyChanged(nameof(FarmCountCompactText));
            OnPropertyChanged(nameof(FillPercent));
        }
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
            OnPropertyChanged(nameof(CanSendNow));
            OnPropertyChanged(nameof(LastSentText));
            OnPropertyChanged(nameof(NextSendText));
        }
    }

    public DateTimeOffset? LastSentAtUtc
    {
        get => _lastSentAtUtc;
        set
        {
            if (_lastSentAtUtc == value)
            {
                return;
            }

            _lastSentAtUtc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLastSent));
            OnPropertyChanged(nameof(LastSentText));
        }
    }

    public bool LastSendFailed
    {
        get => _lastSendFailed;
        set
        {
            if (_lastSendFailed == value)
            {
                return;
            }

            _lastSendFailed = value;
            OnPropertyChanged();
        }
    }

    public bool HasLastSent => LastSentAtUtc is not null;

    public DateTimeOffset? NextSendAtUtc
    {
        get => _nextSendAtUtc;
        set
        {
            if (_nextSendAtUtc == value)
            {
                return;
            }

            _nextSendAtUtc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NextSendText));
        }
    }

    public string IntervalMinMinutesText
    {
        get => _intervalMinMinutesText;
        set
        {
            if (_intervalMinMinutesText == value)
            {
                return;
            }

            _intervalMinMinutesText = value?.Trim() ?? string.Empty;
            OnPropertyChanged();
            NotifyIntervalValidationChanged();
        }
    }

    public string IntervalMaxMinutesText
    {
        get => _intervalMaxMinutesText;
        set
        {
            if (_intervalMaxMinutesText == value)
            {
                return;
            }

            _intervalMaxMinutesText = value?.Trim() ?? string.Empty;
            OnPropertyChanged();
            NotifyIntervalValidationChanged();
        }
    }

    public bool HasValidInterval => TryGetDispatchInterval(out _, out _);

    public bool HasIntervalError => !TryGetDispatchInterval(out _, out _);

    public string IntervalErrorText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IntervalMinMinutesText) &&
                string.IsNullOrWhiteSpace(IntervalMaxMinutesText))
            {
                return "Min and Max are required.";
            }

            if (!int.TryParse(IntervalMinMinutesText, out var min) || min <= 0 ||
                !int.TryParse(IntervalMaxMinutesText, out var max) || max <= 0)
            {
                return "Enter positive Min and Max values.";
            }

            return max < min ? "Max must be greater than or equal to Min." : string.Empty;
        }
    }

    public string NextSendText
    {
        get
        {
            if (!IsEnabled)
            {
                return "Disabled";
            }

            if (NextSendAtUtc is null)
            {
                return "Due now";
            }

            var remaining = NextSendAtUtc.Value - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return "Due now";
            }

            return remaining.TotalHours >= 1
                ? $"Next {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"Next {remaining.Minutes:00}:{remaining.Seconds:00}";
        }
    }

    public bool TryGetDispatchInterval(out int? minMinutes, out int? maxMinutes)
    {
        minMinutes = null;
        maxMinutes = null;
        if (!int.TryParse(IntervalMinMinutesText, out var min) || min <= 0 ||
            !int.TryParse(IntervalMaxMinutesText, out var max) || max < min)
        {
            return false;
        }

        minMinutes = min;
        maxMinutes = max;
        return true;
    }

    public bool ShowLastSentTimer
    {
        get => _showLastSentTimer;
        set
        {
            if (_showLastSentTimer == value)
            {
                return;
            }

            _showLastSentTimer = value;
            OnPropertyChanged();
        }
    }

    public bool LastSentLimitEnabled
    {
        get => _lastSentLimitEnabled;
        set
        {
            if (_lastSentLimitEnabled == value)
            {
                return;
            }

            _lastSentLimitEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastSentText));
        }
    }

    public int LastSentLimitHours
    {
        get => _lastSentLimitHours;
        set
        {
            var normalized = Math.Clamp(value, 1, 120);
            if (_lastSentLimitHours == normalized)
            {
                return;
            }

            _lastSentLimitHours = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastSentText));
        }
    }

    public bool IsPlaceholder
    {
        get => _isPlaceholder;
        set
        {
            if (_isPlaceholder == value)
            {
                return;
            }

            _isPlaceholder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFarmList));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(FarmCountText));
            OnPropertyChanged(nameof(FarmCountCompactText));
            OnPropertyChanged(nameof(ReadyText));
            OnPropertyChanged(nameof(ActionText));
            OnPropertyChanged(nameof(CanSendNow));
        }
    }

    public bool HasFarmList => !IsPlaceholder;

    public bool IsEmpty => HasFarmList && TotalFarmCount == 0;

    public int? RemainingSeconds
    {
        get => _remainingSeconds;
        set
        {
            var normalized = value.HasValue ? Math.Max(0, value.Value) : (int?)null;
            if (_remainingSeconds == normalized)
            {
                return;
            }

            _remainingSeconds = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTimer));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(TimerText));
            OnPropertyChanged(nameof(CanSendNow));
        }
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (_isProcessing == value)
            {
                return;
            }

            _isProcessing = value;
            OnPropertyChanged();
        }
    }

    public string FarmCountText => HasFarmList
        ? $"{TotalFarmCount}/{Capacity ?? TotalFarmCount} farms"
        : string.Empty;

    public string FarmCountCompactText => HasFarmList
        ? $"{TotalFarmCount}/{Capacity ?? TotalFarmCount}"
        : string.Empty;

    public double FillPercent
    {
        get
        {
            var capacity = Capacity ?? TotalFarmCount;
            if (capacity <= 0)
            {
                return 0;
            }

            return Math.Max(0, Math.Min(100, (double)TotalFarmCount / capacity * 100));
        }
    }

    public bool HasTimer => RemainingSeconds is > 0;

    public bool IsReady => !HasTimer;

    public string ReadyText => !HasFarmList ? string.Empty : IsEmpty ? "Empty" : "Ready";

    public string LastSentText
    {
        get
        {
            if (!IsEnabled || LastSentAtUtc is null)
            {
                return "00:00:00";
            }

            var elapsed = DateTimeOffset.UtcNow - LastSentAtUtc.Value;
            var displayLimitHours = LastSentLimitEnabled ? LastSentLimitHours : 120;
            if (elapsed > TimeSpan.FromHours(displayLimitHours))
            {
                return $"{displayLimitHours}h+";
            }

            return $"{Math.Max(0, (int)elapsed.TotalHours):00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
    }

    public string ActionText => IsEmpty ? "Empty" : "Send";

    public string TimerText
    {
        get
        {
            if (!HasTimer || RemainingSeconds is null)
            {
                return "00:00";
            }

            var ts = TimeSpan.FromSeconds(RemainingSeconds.Value);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{ts.Minutes:00}:{ts.Seconds:00}";
        }
    }

    public bool CanSendNow => HasFarmList && !IsEmpty && IsEnabled && IsReady && HasValidInterval;

    public bool TickOneSecond()
    {
        var changed = false;
        if (LastSentAtUtc is not null)
        {
            OnPropertyChanged(nameof(LastSentText));
            changed = true;
        }

        if (NextSendAtUtc is not null)
        {
            OnPropertyChanged(nameof(NextSendText));
            changed = true;
        }

        if (!HasTimer || RemainingSeconds is null)
        {
            return changed;
        }

        RemainingSeconds = RemainingSeconds.Value - 1;
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void NotifyIntervalValidationChanged()
    {
        OnPropertyChanged(nameof(HasValidInterval));
        OnPropertyChanged(nameof(HasIntervalError));
        OnPropertyChanged(nameof(IntervalErrorText));
        OnPropertyChanged(nameof(CanSendNow));
    }
}
