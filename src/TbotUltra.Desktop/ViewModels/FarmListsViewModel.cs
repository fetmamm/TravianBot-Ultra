using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the farm-lists panel: owns the farm-list status rows,
/// the placeholder-row invariant, and panel commands. The service-bound
/// send/refresh/scan flows stay hosted by MainWindow during the transition.
/// </summary>
public sealed class FarmListsViewModel : BaseViewModel
{
    private readonly RelayCommand _analyzeCommand;
    private readonly RelayCommand _addFarmsCommand;
    private readonly RelayCommand _createFarmListCommand;
    private readonly RelayCommand _sendAllNowCommand;
    private readonly RelayCommand<FarmListStatusRow> _sendNowCommand;
    private bool _canAnalyze = true;
    private bool _canManageLists;
    private bool _canCreate = true;
    private bool _canSendAll;
    private int _settingsNotificationSuppressionCount;
    private string _sendMode = FarmingDefaults.SendModeListPerList;
    private string _dispatchDelayMinMinutes = "15";
    private string _dispatchDelayMaxMinutes = "30";
    private bool _deactivateRedLosses;
    private bool _deactivateYellowLosses;
    private bool _deactivateOasisLosses;
    private bool _deactivateRedOasisLosses;
    private bool _deactivateYellowOasisLosses;
    private bool _moveRedLosses;
    private bool _moveYellowLosses;
    private FarmLossDestinationOption? _selectedRedLossDestination;
    private FarmLossDestinationOption? _selectedYellowLossDestination;

    public FarmListsViewModel()
    {
        _analyzeCommand = new RelayCommand(() => AnalyzeRequested?.Invoke(), () => _canAnalyze);
        _addFarmsCommand = new RelayCommand(() => AddFarmsRequested?.Invoke(), () => _canManageLists);
        _createFarmListCommand = new RelayCommand(() => CreateFarmListRequested?.Invoke(), () => _canCreate);
        _sendAllNowCommand = new RelayCommand(() => SendAllNowRequested?.Invoke(), () => _canSendAll);
        _sendNowCommand = new RelayCommand<FarmListStatusRow>(row => SendNowRequested?.Invoke(row), row => _canManageLists && row.CanSendNow);
    }

    /// <summary>
    /// Farm-list status rows shown on the farming tab. Created once and mutated
    /// in place so the panel's ItemsSource assignment stays stable.
    /// </summary>
    public ObservableCollection<FarmListStatusRow> FarmLists { get; } = [];

    public ICommand AnalyzeCommand => _analyzeCommand;
    public ICommand AddFarmsCommand => _addFarmsCommand;
    public ICommand CreateFarmListCommand => _createFarmListCommand;
    public ICommand SendAllNowCommand => _sendAllNowCommand;
    public ICommand SendNowCommand => _sendNowCommand;

    public event Action? AnalyzeRequested;
    public event Action? AddFarmsRequested;
    public event Action? CreateFarmListRequested;
    public event Action? SendAllNowRequested;
    public event Action<FarmListStatusRow>? SendNowRequested;
    public event Action? SettingsChanged;
    public event Action? MoveRedLossesEnabledRequested;
    public event Action? MoveYellowLossesEnabledRequested;

    /// <summary>Applies MainWindow's global session/busy gate to panel commands.</summary>
    public void UpdateCommandAvailability(bool canAnalyze, bool canManageLists, bool canCreate, bool canSendAll)
    {
        _canAnalyze = canAnalyze;
        _canManageLists = canManageLists;
        _canCreate = canCreate;
        _canSendAll = canSendAll;
        _analyzeCommand.RaiseCanExecuteChanged();
        _addFarmsCommand.RaiseCanExecuteChanged();
        _createFarmListCommand.RaiseCanExecuteChanged();
        _sendAllNowCommand.RaiseCanExecuteChanged();
        _sendNowCommand.RaiseCanExecuteChanged();
    }

    public string SendMode
    {
        get => _sendMode;
        set
        {
            var normalized = FarmingDefaults.NormalizeSendMode(value);
            if (SetProperty(ref _sendMode, normalized))
            {
                OnPropertyChanged(nameof(UseIndividualSchedules));
                OnPropertyChanged(nameof(UseSharedSchedule));
                OnPropertyChanged(nameof(SendAllLists));
                OnPropertyChanged(nameof(DispatchModeDescription));
                OnPropertyChanged(nameof(DispatchIntervalTitle));
                OnPropertyChanged(nameof(DispatchIntervalDescription));
                OnSettingsChanged();
            }
        }
    }

    public bool UseIndividualSchedules
    {
        get => string.Equals(SendMode, FarmingDefaults.SendModeListPerList, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                SendMode = FarmingDefaults.SendModeListPerList;
            }
        }
    }

    public bool UseSharedSchedule
    {
        get => string.Equals(SendMode, FarmingDefaults.SendModeSharedSchedule, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                SendMode = FarmingDefaults.SendModeSharedSchedule;
            }
        }
    }

    public bool SendAllLists
    {
        get => string.Equals(SendMode, FarmingDefaults.SendModeAllAtOnce, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                SendMode = FarmingDefaults.SendModeAllAtOnce;
            }
        }
    }

    public string DispatchModeDescription => SendMode switch
    {
        FarmingDefaults.SendModeSharedSchedule => "Only enabled lists run, all on the shared interval.",
        FarmingDefaults.SendModeAllAtOnce => "Travian Start all sends every account list, ignoring UI toggles.",
        _ => "Only enabled lists run, each on its own interval.",
    };

    public string DispatchIntervalTitle => UseIndividualSchedules ? "Default interval" : "Shared interval";

    public string DispatchIntervalDescription => UseIndividualSchedules
        ? "Prefills lists when they are first loaded; existing values stay independent."
        : "A random delay is selected before the next shared run.";

    public string DispatchDelayMinMinutes
    {
        get => _dispatchDelayMinMinutes;
        set
        {
            if (SetProperty(ref _dispatchDelayMinMinutes, value))
            {
                OnSettingsChanged();
            }
        }
    }

    public string DispatchDelayMaxMinutes
    {
        get => _dispatchDelayMaxMinutes;
        set
        {
            if (SetProperty(ref _dispatchDelayMaxMinutes, value))
            {
                OnSettingsChanged();
            }
        }
    }

    public bool DeactivateRedLosses
    {
        get => _deactivateRedLosses;
        set
        {
            if (!SetProperty(ref _deactivateRedLosses, value))
            {
                return;
            }

            if (!value && MoveRedLosses)
            {
                using (SuppressSettingsNotifications())
                {
                    MoveRedLosses = false;
                }
            }

            OnPropertyChanged(nameof(CanMoveRedLosses));
            OnSettingsChanged();
        }
    }

    public bool DeactivateYellowLosses
    {
        get => _deactivateYellowLosses;
        set
        {
            if (!SetProperty(ref _deactivateYellowLosses, value))
                return;
            if (!value && MoveYellowLosses)
            {
                using (SuppressSettingsNotifications())
                {
                    MoveYellowLosses = false;
                }
            }
            OnPropertyChanged(nameof(CanMoveYellowLosses));
            OnSettingsChanged();
        }
    }

    public bool DeactivateOasisLosses
    {
        get => _deactivateOasisLosses;
        set
        {
            if (!SetProperty(ref _deactivateOasisLosses, value))
                return;
            if (value && !_deactivateRedOasisLosses && !_deactivateYellowOasisLosses)
            {
                SetProperty(ref _deactivateRedOasisLosses, true, nameof(DeactivateRedOasisLosses));
                SetProperty(ref _deactivateYellowOasisLosses, true, nameof(DeactivateYellowOasisLosses));
            }
            else if (!value)
            {
                SetProperty(ref _deactivateRedOasisLosses, false, nameof(DeactivateRedOasisLosses));
                SetProperty(ref _deactivateYellowOasisLosses, false, nameof(DeactivateYellowOasisLosses));
            }
            OnSettingsChanged();
        }
    }

    public bool DeactivateRedOasisLosses
    {
        get => _deactivateRedOasisLosses;
        set
        {
            if (!SetProperty(ref _deactivateRedOasisLosses, value))
                return;
            SyncOasisMasterFromColors();
            OnSettingsChanged();
        }
    }

    public bool DeactivateYellowOasisLosses
    {
        get => _deactivateYellowOasisLosses;
        set
        {
            if (!SetProperty(ref _deactivateYellowOasisLosses, value))
                return;
            SyncOasisMasterFromColors();
            OnSettingsChanged();
        }
    }

    public bool MoveRedLosses
    {
        get => _moveRedLosses;
        set
        {
            if (!SetProperty(ref _moveRedLosses, value))
                return;
            OnSettingsChanged();
            if (value && _settingsNotificationSuppressionCount == 0)
                MoveRedLossesEnabledRequested?.Invoke();
        }
    }

    public bool MoveYellowLosses
    {
        get => _moveYellowLosses;
        set
        {
            if (!SetProperty(ref _moveYellowLosses, value))
                return;
            OnSettingsChanged();
            if (value && _settingsNotificationSuppressionCount == 0)
                MoveYellowLossesEnabledRequested?.Invoke();
        }
    }

    public bool CanMoveRedLosses => DeactivateRedLosses;
    public bool CanMoveYellowLosses => DeactivateYellowLosses;

    public ObservableCollection<FarmLossDestinationOption> LossDestinations { get; } = [];

    public FarmLossDestinationOption? SelectedRedLossDestination
    {
        get => _selectedRedLossDestination;
        set
        {
            if (SetProperty(ref _selectedRedLossDestination, value))
            {
                OnSettingsChanged();
            }
        }
    }

    public FarmLossDestinationOption? SelectedYellowLossDestination
    {
        get => _selectedYellowLossDestination;
        set
        {
            if (SetProperty(ref _selectedYellowLossDestination, value))
                OnSettingsChanged();
        }
    }

    /// <summary>
    /// Replaces server-derived destination options without treating the refresh as a user setting change.
    /// The collection instance remains stable for the panel binding.
    /// </summary>
    public void ReplaceLossDestinations(
        IEnumerable<FarmLossDestinationOption> destinations,
        FarmLossDestinationOption? selectedRedDestination,
        FarmLossDestinationOption? selectedYellowDestination)
    {
        using var suppress = SuppressSettingsNotifications();
        LossDestinations.Clear();
        foreach (var destination in destinations)
        {
            LossDestinations.Add(destination);
        }

        SelectedRedLossDestination = selectedRedDestination;
        SelectedYellowLossDestination = selectedYellowDestination;
    }

    public void LoadSettings(
        string sendMode,
        int dispatchDelayMinMinutes,
        int dispatchDelayMaxMinutes,
        bool deactivateRedLosses,
        bool deactivateYellowLosses,
        bool deactivateRedOasisLosses,
        bool deactivateYellowOasisLosses,
        bool moveRedLosses,
        bool moveYellowLosses)
    {
        using var suppress = SuppressSettingsNotifications();
        SendMode = sendMode;
        DispatchDelayMinMinutes = dispatchDelayMinMinutes.ToString();
        DispatchDelayMaxMinutes = dispatchDelayMaxMinutes.ToString();
        DeactivateRedLosses = deactivateRedLosses;
        DeactivateYellowLosses = deactivateYellowLosses;
        DeactivateRedOasisLosses = deactivateRedOasisLosses;
        DeactivateYellowOasisLosses = deactivateYellowOasisLosses;
        MoveRedLosses = deactivateRedLosses && moveRedLosses;
        MoveYellowLosses = deactivateYellowLosses && moveYellowLosses;
    }

    private void SyncOasisMasterFromColors()
    {
        SetProperty(
            ref _deactivateOasisLosses,
            _deactivateRedOasisLosses || _deactivateYellowOasisLosses,
            nameof(DeactivateOasisLosses));
    }

    private IDisposable SuppressSettingsNotifications()
    {
        _settingsNotificationSuppressionCount++;
        return new SettingsNotificationSuppression(this);
    }

    private void OnSettingsChanged()
    {
        if (_settingsNotificationSuppressionCount == 0)
        {
            SettingsChanged?.Invoke();
        }
    }

    private sealed class SettingsNotificationSuppression(FarmListsViewModel owner) : IDisposable
    {
        public void Dispose()
        {
            owner._settingsNotificationSuppressionCount = Math.Max(0, owner._settingsNotificationSuppressionCount - 1);
        }
    }

    public static bool IsRealRow(FarmListStatusRow row) => !row.IsPlaceholder;

    /// <summary>
    /// Keeps exactly one placeholder row while no real farm lists are loaded,
    /// and none once real rows exist.
    /// </summary>
    public void EnsurePlaceholderRow()
    {
        if (FarmLists.Any(IsRealRow))
        {
            foreach (var row in FarmLists.Where(row => row.IsPlaceholder).ToList())
            {
                FarmLists.Remove(row);
            }

            return;
        }

        if (!FarmLists.Any(row => row.IsPlaceholder))
        {
            FarmLists.Add(new FarmListStatusRow
            {
                IsPlaceholder = true,
                IsEnabled = false,
            });
        }
    }

    /// <summary>Status line for the farming tab under the current rows.</summary>
    public string DescribeStatus()
    {
        var realFarmLists = FarmLists.Where(IsRealRow).ToList();
        if (realFarmLists.Count <= 0)
        {
            return "No farm lists loaded. Click Analyze Farmlists.";
        }

        var readyCount = realFarmLists.Count(item => item.IsReady && !item.IsEmpty);
        return $"Loaded {realFarmLists.Count} farm list(s). Ready: {readyCount}.";
    }
}
