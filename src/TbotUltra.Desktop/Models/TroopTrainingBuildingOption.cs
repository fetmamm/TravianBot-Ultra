using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TbotUltra.Core.Travian;

namespace TbotUltra.Desktop.Models;

public sealed class TroopTrainingBuildingOption : INotifyPropertyChanged
{
    private bool _isEnabled;
    private string _selectedTroop = string.Empty;
    private string _maxQueueMode = "no_limit";
    private string _amountMode = "maximum";
    private int _keepResourcesPercent = 10;
    private string _runMode = "min_troops";
    private int _minimumTroops = 1;
    private int _minimumResourcesPercent = 50;
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
                : "min_troops";
            if (string.Equals(_runMode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _runMode = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesMinimumTroopsMode));
            OnPropertyChanged(nameof(UsesResourcePercentMode));
            OnPropertyChanged(nameof(IsMinimumTroopsMode));
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
            var normalized = Math.Clamp(value, 1, 100);
            if (_minimumResourcesPercent == normalized)
            {
                return;
            }

            _minimumResourcesPercent = normalized;
            OnPropertyChanged();
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
    public bool UsesMinimumTroopsMode => string.Equals(RunMode, "min_troops", StringComparison.OrdinalIgnoreCase);
    public bool UsesResourcePercentMode => string.Equals(RunMode, "resource_percent", StringComparison.OrdinalIgnoreCase);

    public bool IsMinimumTroopsMode
    {
        get => UsesMinimumTroopsMode;
        set
        {
            if (value)
            {
                RunMode = "min_troops";
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

    public string QueueBadgeBackground => !Exists
        ? "#E5E7EB"
        : IsQueueReadyToRun
            ? "#DCFCE7"
            : "#FEF3C7";

    public string QueueBadgeBorderBrush => !Exists
        ? "#9CA3AF"
        : IsQueueReadyToRun
            ? "#22C55E"
            : "#F59E0B";

    public string QueueBadgeForeground => !Exists
        ? "#4B5563"
        : IsQueueReadyToRun
            ? "#15803D"
            : "#B45309";

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
