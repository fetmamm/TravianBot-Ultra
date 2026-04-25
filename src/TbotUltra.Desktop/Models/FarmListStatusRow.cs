using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Models;

public sealed class FarmListStatusRow : INotifyPropertyChanged
{
    private bool _isEnabled;
    private string _name = string.Empty;
    private int _activeFarmCount;
    private int _totalFarmCount;
    private int? _remainingSeconds;

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
            OnPropertyChanged(nameof(CanSendNow));
        }
    }

    public string FarmCountText => $"{ActiveFarmCount}/{TotalFarmCount} farms";

    public double FillPercent
    {
        get
        {
            if (TotalFarmCount <= 0)
            {
                return 0;
            }

            return Math.Max(0, Math.Min(100, (double)ActiveFarmCount / TotalFarmCount * 100));
        }
    }

    public bool HasTimer => RemainingSeconds is > 0;

    public bool IsReady => !HasTimer;

    public string ReadyText => "Ready";

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

    public bool CanSendNow => IsEnabled && IsReady;

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
