using System.Globalization;
using System.Windows.Input;
using TbotUltra.Desktop.Common;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>Owns command availability for the Settings dialog while the dialog keeps its validation and persistence bridge.</summary>
public sealed class SettingsDialogViewModel : BaseViewModel
{
    private readonly RelayCommand _saveCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _resetSettingsCommand;
    private readonly RelayCommand _resetPacingCommand;
    private readonly RelayCommand _sleepNowCommand;
    private readonly RelayCommand _runVillageStatusSweepNowCommand;
    private readonly RelayCommand _resetDailyGoldSpendingCommand;
    private readonly RelayCommand _resetDailySilverSpendingCommand;
    private bool _sleepNowEnabled;
    private bool _villageStatusSweepEnabled;
    private bool _dailyGoldSpendingResetEnabled;
    private bool _dailySilverSpendingResetEnabled;
    private bool _isDirty;
    private int _changeTrackingSuppressionCount;
    private bool _dontNotifyNewVersion;
    private bool _quickReloginEnabled = true;
    private bool _automaticallyCheckLanguage = true;
    private bool _detailedBrowserLoggingEnabled;
    private bool _startBrowserMinimized;
    private bool _turnOffVideoSound = true;
    private bool _dailyServerResetOverrideEnabled;
    private int _dailyServerResetHour;
    private bool _allowSilverSpending;
    private bool _allowGoldSpending;
    private string _goldLimitText = "100";
    private string _dailyGoldSpendingLimitText = "20";
    private string _silverLimitText = "100";
    private string _dailySilverSpendingLimitText = "10000";
    private int _dailyGoldSpent;
    private int _dailySilverSpent;
    private int _troopTrainingFallbackCooldownSeconds = 120;

    public SettingsDialogViewModel(
        bool sleepNowEnabled,
        bool villageStatusSweepEnabled,
        bool dailyGoldSpendingResetEnabled,
        bool dailySilverSpendingResetEnabled)
    {
        _sleepNowEnabled = sleepNowEnabled;
        _villageStatusSweepEnabled = villageStatusSweepEnabled;
        _dailyGoldSpendingResetEnabled = dailyGoldSpendingResetEnabled;
        _dailySilverSpendingResetEnabled = dailySilverSpendingResetEnabled;
        _saveCommand = new RelayCommand(() => SaveRequested?.Invoke());
        _cancelCommand = new RelayCommand(() => CancelRequested?.Invoke());
        _resetSettingsCommand = new RelayCommand(() => ResetSettingsRequested?.Invoke());
        _resetPacingCommand = new RelayCommand(() => ResetPacingRequested?.Invoke());
        _sleepNowCommand = new RelayCommand(() => SleepNowRequested?.Invoke(), () => _sleepNowEnabled);
        _runVillageStatusSweepNowCommand = new RelayCommand(
            () => VillageStatusSweepNowRequested?.Invoke(),
            () => _villageStatusSweepEnabled);
        _resetDailyGoldSpendingCommand = new RelayCommand(
            () => ResetDailyGoldSpendingRequested?.Invoke(),
            () => _dailyGoldSpendingResetEnabled);
        _resetDailySilverSpendingCommand = new RelayCommand(
            () => ResetDailySilverSpendingRequested?.Invoke(),
            () => _dailySilverSpendingResetEnabled);
    }

    public ICommand SaveCommand => _saveCommand;
    public ICommand CancelCommand => _cancelCommand;
    public ICommand ResetSettingsCommand => _resetSettingsCommand;
    public ICommand ResetPacingCommand => _resetPacingCommand;
    public ICommand SleepNowCommand => _sleepNowCommand;
    public ICommand RunVillageStatusSweepNowCommand => _runVillageStatusSweepNowCommand;
    public ICommand ResetDailyGoldSpendingCommand => _resetDailyGoldSpendingCommand;
    public ICommand ResetDailySilverSpendingCommand => _resetDailySilverSpendingCommand;

    public PacingSettingsViewModel Pacing { get; } = new();

    public ConstructionSettingsViewModel Construction { get; } = new();

    public FarmingSettingsViewModel Farming { get; } = new();

    public PostLoginSettingsViewModel PostLogin { get; } = new();

    public HeroSettingsViewModel Hero { get; } = new();

    public CelebrationSettingsViewModel Celebrations { get; } = new();

    public int TroopTrainingFallbackCooldownSeconds
    {
        get => _troopTrainingFallbackCooldownSeconds;
        set => SetProperty(ref _troopTrainingFallbackCooldownSeconds, value switch
        {
            10 or 30 or 60 or 120 or 300 or 600 => value,
            _ => 30,
        });
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public bool DontNotifyNewVersion
    {
        get => _dontNotifyNewVersion;
        set => SetProperty(ref _dontNotifyNewVersion, value);
    }

    public bool QuickReloginEnabled
    {
        get => _quickReloginEnabled;
        set => SetProperty(ref _quickReloginEnabled, value);
    }

    public bool AutomaticallyCheckLanguage
    {
        get => _automaticallyCheckLanguage;
        set => SetProperty(ref _automaticallyCheckLanguage, value);
    }

    public bool DetailedBrowserLoggingEnabled
    {
        get => _detailedBrowserLoggingEnabled;
        set => SetProperty(ref _detailedBrowserLoggingEnabled, value);
    }

    public bool StartBrowserMinimized
    {
        get => _startBrowserMinimized;
        set => SetProperty(ref _startBrowserMinimized, value);
    }

    public bool TurnOffVideoSound
    {
        get => _turnOffVideoSound;
        set => SetProperty(ref _turnOffVideoSound, value);
    }

    public bool DailyServerResetOverrideEnabled
    {
        get => _dailyServerResetOverrideEnabled;
        set => SetProperty(ref _dailyServerResetOverrideEnabled, value);
    }

    public int DailyServerResetHour
    {
        get => _dailyServerResetHour;
        set => SetProperty(ref _dailyServerResetHour, Math.Clamp(value, 0, 23));
    }

    public bool AllowSilverSpending
    {
        get => _allowSilverSpending;
        set => SetProperty(ref _allowSilverSpending, value);
    }

    public bool AllowGoldSpending
    {
        get => _allowGoldSpending;
        set => SetProperty(ref _allowGoldSpending, value);
    }

    public string GoldLimitText
    {
        get => _goldLimitText;
        set => SetProperty(ref _goldLimitText, value);
    }

    public string DailyGoldSpendingLimitText
    {
        get => _dailyGoldSpendingLimitText;
        set
        {
            if (SetProperty(ref _dailyGoldSpendingLimitText, value))
            {
                OnPropertyChanged(nameof(DailyGoldSpendingUsageText));
            }
        }
    }

    public string SilverLimitText
    {
        get => _silverLimitText;
        set => SetProperty(ref _silverLimitText, value);
    }

    public string DailySilverSpendingLimitText
    {
        get => _dailySilverSpendingLimitText;
        set
        {
            if (SetProperty(ref _dailySilverSpendingLimitText, value))
            {
                OnPropertyChanged(nameof(DailySilverSpendingUsageText));
            }
        }
    }

    public int DailyGoldSpent
    {
        get => _dailyGoldSpent;
        set
        {
            if (SetProperty(ref _dailyGoldSpent, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(DailyGoldSpendingUsageText));
            }
        }
    }

    public int DailySilverSpent
    {
        get => _dailySilverSpent;
        set
        {
            if (SetProperty(ref _dailySilverSpent, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(DailySilverSpendingUsageText));
            }
        }
    }

    public string DailyGoldSpendingUsageText => FormatDailySpendingUsage(DailyGoldSpent, DailyGoldSpendingLimitText);

    public string DailySilverSpendingUsageText => FormatDailySpendingUsage(DailySilverSpent, DailySilverSpendingLimitText);

    public event Action? SaveRequested;
    public event Action? CancelRequested;
    public event Action? ResetSettingsRequested;
    public event Action? ResetPacingRequested;
    public event Action? SleepNowRequested;
    public event Action? VillageStatusSweepNowRequested;
    public event Action? ResetDailyGoldSpendingRequested;
    public event Action? ResetDailySilverSpendingRequested;

    public void SetVillageStatusSweepRunning(bool running)
    {
        _villageStatusSweepEnabled = !running;
        _runVillageStatusSweepNowCommand.RaiseCanExecuteChanged();
    }

    public IDisposable SuppressChangeTracking()
    {
        _changeTrackingSuppressionCount++;
        return new ChangeTrackingSuppression(this);
    }

    public void MarkChanged()
    {
        if (_changeTrackingSuppressionCount == 0)
        {
            IsDirty = true;
        }
    }

    public void ResetChangeTracking() => IsDirty = false;

    private static string FormatDailySpendingUsage(int spent, string? limitText)
    {
        return int.TryParse(limitText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            ? $"{spent} / {limit}"
            : $"{spent} / ?";
    }

    private sealed class ChangeTrackingSuppression(SettingsDialogViewModel owner) : IDisposable
    {
        public void Dispose()
        {
            owner._changeTrackingSuppressionCount = Math.Max(0, owner._changeTrackingSuppressionCount - 1);
        }
    }
}
