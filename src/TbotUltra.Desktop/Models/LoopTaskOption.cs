using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Models;

public sealed class LoopTaskOption : INotifyPropertyChanged
{
    private bool _isEnabled;
    private bool _isVisible = true;
    private bool _isBlocked;
    private string _blockedText = "Blocked";
    private int _order;
    private bool _isRunning;
    private int _queuedCount;
    private string _stateText = "Idle";
    private string _detailText = string.Empty;
    private int? _remainingSeconds;

    public string TaskName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

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
            OnPropertyChanged(nameof(HasTimer));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(BadgeText));
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            OnPropertyChanged();
        }
    }

    public int Order
    {
        get => _order;
        set
        {
            if (_order == value)
            {
                return;
            }

            _order = value;
            OnPropertyChanged();
        }
    }

    public bool IsBlocked
    {
        get => _isBlocked;
        set
        {
            if (_isBlocked == value)
            {
                return;
            }

            _isBlocked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTimer));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(BadgeText));
        }
    }

    public string BlockedText
    {
        get => _blockedText;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Blocked" : value.Trim();
            if (string.Equals(_blockedText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _blockedText = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BadgeText));
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            OnPropertyChanged();
        }
    }

    public int QueuedCount
    {
        get => _queuedCount;
        set
        {
            if (_queuedCount == value)
            {
                return;
            }

            _queuedCount = Math.Max(0, value);
            OnPropertyChanged();
        }
    }

    public string StateText
    {
        get => _stateText;
        set
        {
            if (string.Equals(_stateText, value, StringComparison.Ordinal))
            {
                return;
            }

            _stateText = value;
            OnPropertyChanged();
        }
    }

    public string DetailText
    {
        get => _detailText;
        set
        {
            if (string.Equals(_detailText, value, StringComparison.Ordinal))
            {
                return;
            }

            _detailText = value;
            OnPropertyChanged();
        }
    }

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
            OnPropertyChanged(nameof(ReadyText));
        }
    }

    public bool HasTimer => IsEnabled && !IsBlocked && RemainingSeconds is > 0;

    public bool IsReady => IsEnabled && !HasTimer && !IsBlocked;

    public string ReadyText => "Ready";

    public string BadgeText => IsBlocked ? BlockedText : IsEnabled ? ReadyText : "Disabled";

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

    public bool TickOneSecond()
    {
        if (!HasTimer || RemainingSeconds is null)
        {
            return false;
        }

        RemainingSeconds = RemainingSeconds.Value - 1;
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
