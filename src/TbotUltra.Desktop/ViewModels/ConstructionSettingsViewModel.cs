using System.Globalization;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Common;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>Owns editable Construction settings while SettingsWindow keeps validation and persistence.</summary>
public sealed class ConstructionSettingsViewModel : BaseViewModel
{
    private bool _mainBuildingRebuildEnabled = ConstructionDefaults.MainBuildingRebuildEnabled;
    private int _mainBuildingRebuildTargetLevel = ConstructionDefaults.MainBuildingRebuildTargetLevel;
    private int _storageUpgradeLevelsAhead = ConstructionDefaults.StorageUpgradeLevelsAhead;
    private bool _cropShortageRecoveryEnabled = ConstructionDefaults.CropShortageRecoveryEnabled;
    private bool _humanizeDelayEnabled = PacingDefaults.ConstructionHumanizeDelayEnabled;
    private string _queuePercentMin = Format(PacingDefaults.ConstructionHumanizeQueuePercentMin);
    private string _queuePercentMax = Format(PacingDefaults.ConstructionHumanizeQueuePercentMax);
    private string _maxDelayMinutes = Format(PacingDefaults.ConstructionHumanizeMaxDelayMinutes);
    private string _noPlusDelayMinMinutes = Format(PacingDefaults.ConstructionHumanizeNoPlusMinMinutes);
    private string _noPlusDelayMaxMinutes = Format(PacingDefaults.ConstructionHumanizeNoPlusMaxMinutes);
    private string _demolishDelayMinMinutes = DemolishDefaults.DefaultDelayMinMinutes.ToString(CultureInfo.InvariantCulture);
    private string _demolishDelayMaxMinutes = DemolishDefaults.DefaultDelayMaxMinutes.ToString(CultureInfo.InvariantCulture);

    public bool MainBuildingRebuildEnabled { get => _mainBuildingRebuildEnabled; set => SetProperty(ref _mainBuildingRebuildEnabled, value); }

    public int MainBuildingRebuildTargetLevel
    {
        get => _mainBuildingRebuildTargetLevel;
        set => SetProperty(ref _mainBuildingRebuildTargetLevel, ConstructionDefaults.NormalizeMainBuildingRebuildTargetLevel(value));
    }

    public int StorageUpgradeLevelsAhead
    {
        get => _storageUpgradeLevelsAhead;
        set => SetProperty(ref _storageUpgradeLevelsAhead, ConstructionDefaults.NormalizeStorageUpgradeLevelsAhead(value));
    }

    public bool HumanizeDelayEnabled { get => _humanizeDelayEnabled; set => SetProperty(ref _humanizeDelayEnabled, value); }
    public bool CropShortageRecoveryEnabled { get => _cropShortageRecoveryEnabled; set => SetProperty(ref _cropShortageRecoveryEnabled, value); }
    public string QueuePercentMin { get => _queuePercentMin; set => SetProperty(ref _queuePercentMin, value); }
    public string QueuePercentMax { get => _queuePercentMax; set => SetProperty(ref _queuePercentMax, value); }
    public string MaxDelayMinutes { get => _maxDelayMinutes; set => SetProperty(ref _maxDelayMinutes, value); }
    public string NoPlusDelayMinMinutes { get => _noPlusDelayMinMinutes; set => SetProperty(ref _noPlusDelayMinMinutes, value); }
    public string NoPlusDelayMaxMinutes { get => _noPlusDelayMaxMinutes; set => SetProperty(ref _noPlusDelayMaxMinutes, value); }
    public string DemolishDelayMinMinutes { get => _demolishDelayMinMinutes; set => SetProperty(ref _demolishDelayMinMinutes, value); }
    public string DemolishDelayMaxMinutes { get => _demolishDelayMaxMinutes; set => SetProperty(ref _demolishDelayMaxMinutes, value); }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
