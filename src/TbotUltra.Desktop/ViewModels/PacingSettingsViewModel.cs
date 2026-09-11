using System.Globalization;
using System.Collections.ObjectModel;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Common;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>Owns editable action-pacing values while SettingsWindow retains validation and persistence.</summary>
public sealed class PacingSettingsViewModel : BaseViewModel
{
    private string _taskMinSeconds = Format(PacingDefaults.ActionPacingTaskMinSeconds);
    private string _taskMaxSeconds = Format(PacingDefaults.ActionPacingTaskMaxSeconds);
    private string _pageLoadMinSeconds = Format(PacingDefaults.ActionPacingPageLoadMinSeconds);
    private string _pageLoadMaxSeconds = Format(PacingDefaults.ActionPacingPageLoadMaxSeconds);
    private string _clickMinSeconds = Format(PacingDefaults.ActionPacingClickMinSeconds);
    private string _clickMaxSeconds = Format(PacingDefaults.ActionPacingClickMaxSeconds);
    private string _loopMinSeconds = Format(PacingDefaults.ActionPacingLoopMinSeconds);
    private string _loopMaxSeconds = Format(PacingDefaults.ActionPacingLoopMaxSeconds);
    private int _shortVillageDeferSeconds = PacingDefaults.ShortVillageDeferSeconds;
    private string _farmListStepDelayMinSeconds = Format(PacingDefaults.FarmListStepDelayMinSeconds);
    private string _farmListStepDelayMaxSeconds = Format(PacingDefaults.FarmListStepDelayMaxSeconds);
    private string _collectStepDelayMinSeconds = Format(PacingDefaults.CollectStepDelayMinSeconds);
    private string _collectStepDelayMaxSeconds = Format(PacingDefaults.CollectStepDelayMaxSeconds);
    private bool _idleBreakEnabled = PacingDefaults.ActionPacingIdleBreakEnabled;
    private string _idleBreakIntervalMinMinutes = Format(PacingDefaults.ActionPacingIdleBreakIntervalMinMinutes);
    private string _idleBreakIntervalMaxMinutes = Format(PacingDefaults.ActionPacingIdleBreakIntervalMaxMinutes);
    private string _idleBreakDurationMinMinutes = Format(PacingDefaults.ActionPacingIdleBreakDurationMinMinutes);
    private string _idleBreakDurationMaxMinutes = Format(PacingDefaults.ActionPacingIdleBreakDurationMaxMinutes);
    private bool _idleBrowseEnabled = PacingDefaults.ActionPacingIdleBrowseEnabled;
    private string _idleBrowseIntervalMinMinutes = Format(PacingDefaults.ActionPacingIdleBrowseIntervalMinMinutes);
    private string _idleBrowseIntervalMaxMinutes = Format(PacingDefaults.ActionPacingIdleBrowseIntervalMaxMinutes);
    private bool _idleBrowsePageMap = PacingDefaults.ActionPacingIdleBrowsePageMap;
    private bool _idleBrowsePageStatistics = PacingDefaults.ActionPacingIdleBrowsePageStatistics;
    private bool _idleBrowsePageStatisticsHero = PacingDefaults.ActionPacingIdleBrowsePageStatisticsHero;
    private bool _idleBrowsePageStatisticsTop10 = PacingDefaults.ActionPacingIdleBrowsePageStatisticsTop10;
    private bool _idleBrowsePageStatisticsDefenders = PacingDefaults.ActionPacingIdleBrowsePageStatisticsDefenders;
    private bool _idleBrowsePageStatisticsAttackers = PacingDefaults.ActionPacingIdleBrowsePageStatisticsAttackers;
    private bool _idleBrowsePageReports = PacingDefaults.ActionPacingIdleBrowsePageReports;
    private bool _idleBrowsePageMessages = PacingDefaults.ActionPacingIdleBrowsePageMessages;
    private bool _continuousKeepAliveEnabled = PacingDefaults.ContinuousKeepAliveEnabled;
    private string _continuousKeepAliveMinMinutes = PacingDefaults.ContinuousKeepAliveMinMinutes.ToString(CultureInfo.InvariantCulture);
    private string _continuousKeepAliveMaxMinutes = PacingDefaults.ContinuousKeepAliveMaxMinutes.ToString(CultureInfo.InvariantCulture);
    private bool _sessionPacingEnabled = PacingDefaults.SessionPacingEnabled;
    private bool _smartSleepEnabled = PacingDefaults.SmartSleepEnabled;
    private string _smartSleepMinimumOpportunityMinutes = PacingDefaults.SmartSleepMinimumOpportunityMinutes.ToString(CultureInfo.InvariantCulture);
    private string _smartSleepWakeBeforeMinutes = PacingDefaults.SmartSleepWakeBeforeMinutes.ToString(CultureInfo.InvariantCulture);
    private string _smartSleepWakeAfterMinutes = PacingDefaults.SmartSleepWakeAfterMinutes.ToString(CultureInfo.InvariantCulture);
    private string _smartSleepFallbackMinMinutes = PacingDefaults.SmartSleepFallbackMinMinutes.ToString(CultureInfo.InvariantCulture);
    private string _smartSleepFallbackMaxMinutes = PacingDefaults.SmartSleepFallbackMaxMinutes.ToString(CultureInfo.InvariantCulture);
    private string _sessionRunMinMinutes = PacingDefaults.SessionPacingRunMinMinutes.ToString(CultureInfo.InvariantCulture);
    private string _sessionRunMaxMinutes = PacingDefaults.SessionPacingRunMaxMinutes.ToString(CultureInfo.InvariantCulture);
    private string _sessionSleepMinMinutes = PacingDefaults.SessionPacingSleepMinMinutes.ToString(CultureInfo.InvariantCulture);
    private string _sessionSleepMaxMinutes = PacingDefaults.SessionPacingSleepMaxMinutes.ToString(CultureInfo.InvariantCulture);
    private int _sessionDailyMaxHours = PacingDefaults.SessionPacingDailyMaxHours;
    private int _sessionDailyMaxVariationPercent = PacingDefaults.SessionPacingDailyMaxVariationPercent;
    private int _sessionHoursVariationPercent = PacingDefaults.SessionPacingHoursVariationPercent;
    private bool _villageStatusSweepEnabled = PacingDefaults.VillageStatusSweepEnabled;
    private bool _villageStatusSweepDorf1Enabled = true;
    private bool _villageStatusSweepDorf2Enabled;
    private bool _villageStatusSweepSmithyEnabled;
    private bool _villageStatusSweepBarracksEnabled;
    private bool _villageStatusSweepStableEnabled;
    private bool _villageStatusSweepWorkshopEnabled;
    private bool _villageStatusSweepTownHallEnabled;
    private bool _villageStatusSweepBreweryEnabled;
    private string _villageStatusSweepRoundMinMinutes = PacingDefaults.VillageStatusSweepRoundMinMinutes.ToString(CultureInfo.InvariantCulture);
    private string _villageStatusSweepRoundMaxMinutes = PacingDefaults.VillageStatusSweepRoundMaxMinutes.ToString(CultureInfo.InvariantCulture);
    private string _villageStatusSweepVillageMinSeconds = Format(PacingDefaults.VillageStatusSweepVillageMinSeconds);
    private string _villageStatusSweepVillageMaxSeconds = Format(PacingDefaults.VillageStatusSweepVillageMaxSeconds);

    public PacingSettingsViewModel()
    {
        for (var hour = 0; hour < 24; hour++)
        {
            SessionAllowedHours.Add(new PacingHourOptionViewModel(hour));
        }
    }

    public ObservableCollection<PacingHourOptionViewModel> SessionAllowedHours { get; } = [];

    public string TaskMinSeconds { get => _taskMinSeconds; set => SetProperty(ref _taskMinSeconds, value); }
    public string TaskMaxSeconds { get => _taskMaxSeconds; set => SetProperty(ref _taskMaxSeconds, value); }
    public string PageLoadMinSeconds { get => _pageLoadMinSeconds; set => SetProperty(ref _pageLoadMinSeconds, value); }
    public string PageLoadMaxSeconds { get => _pageLoadMaxSeconds; set => SetProperty(ref _pageLoadMaxSeconds, value); }
    public string ClickMinSeconds { get => _clickMinSeconds; set => SetProperty(ref _clickMinSeconds, value); }
    public string ClickMaxSeconds { get => _clickMaxSeconds; set => SetProperty(ref _clickMaxSeconds, value); }
    public string LoopMinSeconds { get => _loopMinSeconds; set => SetProperty(ref _loopMinSeconds, value); }
    public string LoopMaxSeconds { get => _loopMaxSeconds; set => SetProperty(ref _loopMaxSeconds, value); }
    public int ShortVillageDeferSeconds
    {
        get => _shortVillageDeferSeconds;
        set => SetProperty(ref _shortVillageDeferSeconds, PacingDefaults.NormalizeShortVillageDeferSeconds(value));
    }
    public string FarmListStepDelayMinSeconds { get => _farmListStepDelayMinSeconds; set => SetProperty(ref _farmListStepDelayMinSeconds, value); }
    public string FarmListStepDelayMaxSeconds { get => _farmListStepDelayMaxSeconds; set => SetProperty(ref _farmListStepDelayMaxSeconds, value); }
    public string CollectStepDelayMinSeconds { get => _collectStepDelayMinSeconds; set => SetProperty(ref _collectStepDelayMinSeconds, value); }
    public string CollectStepDelayMaxSeconds { get => _collectStepDelayMaxSeconds; set => SetProperty(ref _collectStepDelayMaxSeconds, value); }
    public bool IdleBreakEnabled { get => _idleBreakEnabled; set => SetProperty(ref _idleBreakEnabled, value); }
    public string IdleBreakIntervalMinMinutes { get => _idleBreakIntervalMinMinutes; set => SetProperty(ref _idleBreakIntervalMinMinutes, value); }
    public string IdleBreakIntervalMaxMinutes { get => _idleBreakIntervalMaxMinutes; set => SetProperty(ref _idleBreakIntervalMaxMinutes, value); }
    public string IdleBreakDurationMinMinutes { get => _idleBreakDurationMinMinutes; set => SetProperty(ref _idleBreakDurationMinMinutes, value); }
    public string IdleBreakDurationMaxMinutes { get => _idleBreakDurationMaxMinutes; set => SetProperty(ref _idleBreakDurationMaxMinutes, value); }
    public bool IdleBrowseEnabled { get => _idleBrowseEnabled; set => SetProperty(ref _idleBrowseEnabled, value); }
    public string IdleBrowseIntervalMinMinutes { get => _idleBrowseIntervalMinMinutes; set => SetProperty(ref _idleBrowseIntervalMinMinutes, value); }
    public string IdleBrowseIntervalMaxMinutes { get => _idleBrowseIntervalMaxMinutes; set => SetProperty(ref _idleBrowseIntervalMaxMinutes, value); }
    public bool IdleBrowsePageMap { get => _idleBrowsePageMap; set => SetProperty(ref _idleBrowsePageMap, value); }
    public bool IdleBrowsePageStatistics { get => _idleBrowsePageStatistics; set => SetProperty(ref _idleBrowsePageStatistics, value); }
    public bool IdleBrowsePageStatisticsHero { get => _idleBrowsePageStatisticsHero; set => SetProperty(ref _idleBrowsePageStatisticsHero, value); }
    public bool IdleBrowsePageStatisticsTop10 { get => _idleBrowsePageStatisticsTop10; set => SetProperty(ref _idleBrowsePageStatisticsTop10, value); }
    public bool IdleBrowsePageStatisticsDefenders { get => _idleBrowsePageStatisticsDefenders; set => SetProperty(ref _idleBrowsePageStatisticsDefenders, value); }
    public bool IdleBrowsePageStatisticsAttackers { get => _idleBrowsePageStatisticsAttackers; set => SetProperty(ref _idleBrowsePageStatisticsAttackers, value); }
    public bool IdleBrowsePageReports { get => _idleBrowsePageReports; set => SetProperty(ref _idleBrowsePageReports, value); }
    public bool IdleBrowsePageMessages { get => _idleBrowsePageMessages; set => SetProperty(ref _idleBrowsePageMessages, value); }
    public bool ContinuousKeepAliveEnabled { get => _continuousKeepAliveEnabled; set => SetProperty(ref _continuousKeepAliveEnabled, value); }
    public string ContinuousKeepAliveMinMinutes { get => _continuousKeepAliveMinMinutes; set => SetProperty(ref _continuousKeepAliveMinMinutes, value); }
    public string ContinuousKeepAliveMaxMinutes { get => _continuousKeepAliveMaxMinutes; set => SetProperty(ref _continuousKeepAliveMaxMinutes, value); }
    public bool SessionPacingEnabled
    {
        get => _sessionPacingEnabled;
        set
        {
            if (SetProperty(ref _sessionPacingEnabled, value) && value && _smartSleepEnabled)
            {
                _smartSleepEnabled = false;
                OnPropertyChanged(nameof(SmartSleepEnabled));
            }
        }
    }
    public bool SmartSleepEnabled
    {
        get => _smartSleepEnabled;
        set
        {
            if (SetProperty(ref _smartSleepEnabled, value) && value && _sessionPacingEnabled)
            {
                _sessionPacingEnabled = false;
                OnPropertyChanged(nameof(SessionPacingEnabled));
            }
        }
    }
    public string SmartSleepMinimumOpportunityMinutes { get => _smartSleepMinimumOpportunityMinutes; set => SetProperty(ref _smartSleepMinimumOpportunityMinutes, value); }
    public string SmartSleepWakeBeforeMinutes { get => _smartSleepWakeBeforeMinutes; set => SetProperty(ref _smartSleepWakeBeforeMinutes, value); }
    public string SmartSleepWakeAfterMinutes { get => _smartSleepWakeAfterMinutes; set => SetProperty(ref _smartSleepWakeAfterMinutes, value); }
    public string SmartSleepFallbackMinMinutes { get => _smartSleepFallbackMinMinutes; set => SetProperty(ref _smartSleepFallbackMinMinutes, value); }
    public string SmartSleepFallbackMaxMinutes { get => _smartSleepFallbackMaxMinutes; set => SetProperty(ref _smartSleepFallbackMaxMinutes, value); }
    public string SessionRunMinMinutes { get => _sessionRunMinMinutes; set => SetProperty(ref _sessionRunMinMinutes, value); }
    public string SessionRunMaxMinutes { get => _sessionRunMaxMinutes; set => SetProperty(ref _sessionRunMaxMinutes, value); }
    public string SessionSleepMinMinutes { get => _sessionSleepMinMinutes; set => SetProperty(ref _sessionSleepMinMinutes, value); }
    public string SessionSleepMaxMinutes { get => _sessionSleepMaxMinutes; set => SetProperty(ref _sessionSleepMaxMinutes, value); }
    public int SessionDailyMaxHours { get => _sessionDailyMaxHours; set => SetProperty(ref _sessionDailyMaxHours, Math.Clamp(value, 0, 24)); }
    public int SessionDailyMaxVariationPercent { get => _sessionDailyMaxVariationPercent; set => SetProperty(ref _sessionDailyMaxVariationPercent, Math.Clamp(value, 0, 50)); }
    public int SessionHoursVariationPercent { get => _sessionHoursVariationPercent; set => SetProperty(ref _sessionHoursVariationPercent, Math.Clamp(value, 0, 49)); }
    public bool VillageStatusSweepEnabled { get => _villageStatusSweepEnabled; set => SetProperty(ref _villageStatusSweepEnabled, value); }
    public bool VillageStatusSweepDorf1Enabled { get => _villageStatusSweepDorf1Enabled; set => SetProperty(ref _villageStatusSweepDorf1Enabled, value); }
    public bool VillageStatusSweepDorf2Enabled
    {
        get => _villageStatusSweepDorf2Enabled;
        set
        {
            if (!SetProperty(ref _villageStatusSweepDorf2Enabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(VillageStatusSweepDorf2DetailsEnabled));
            if (!value)
            {
                VillageStatusSweepSmithyEnabled = false;
                VillageStatusSweepBarracksEnabled = false;
                VillageStatusSweepStableEnabled = false;
                VillageStatusSweepWorkshopEnabled = false;
                VillageStatusSweepTownHallEnabled = false;
                VillageStatusSweepBreweryEnabled = false;
            }
        }
    }

    public bool VillageStatusSweepDorf2DetailsEnabled => VillageStatusSweepDorf2Enabled;
    public bool VillageStatusSweepSmithyEnabled { get => _villageStatusSweepSmithyEnabled; set => SetProperty(ref _villageStatusSweepSmithyEnabled, value); }
    public bool VillageStatusSweepBarracksEnabled { get => _villageStatusSweepBarracksEnabled; set => SetProperty(ref _villageStatusSweepBarracksEnabled, value); }
    public bool VillageStatusSweepStableEnabled { get => _villageStatusSweepStableEnabled; set => SetProperty(ref _villageStatusSweepStableEnabled, value); }
    public bool VillageStatusSweepWorkshopEnabled { get => _villageStatusSweepWorkshopEnabled; set => SetProperty(ref _villageStatusSweepWorkshopEnabled, value); }
    public bool VillageStatusSweepTownHallEnabled { get => _villageStatusSweepTownHallEnabled; set => SetProperty(ref _villageStatusSweepTownHallEnabled, value); }
    public bool VillageStatusSweepBreweryEnabled { get => _villageStatusSweepBreweryEnabled; set => SetProperty(ref _villageStatusSweepBreweryEnabled, value); }
    public string VillageStatusSweepRoundMinMinutes { get => _villageStatusSweepRoundMinMinutes; set => SetProperty(ref _villageStatusSweepRoundMinMinutes, value); }
    public string VillageStatusSweepRoundMaxMinutes { get => _villageStatusSweepRoundMaxMinutes; set => SetProperty(ref _villageStatusSweepRoundMaxMinutes, value); }
    public string VillageStatusSweepVillageMinSeconds { get => _villageStatusSweepVillageMinSeconds; set => SetProperty(ref _villageStatusSweepVillageMinSeconds, value); }
    public string VillageStatusSweepVillageMaxSeconds { get => _villageStatusSweepVillageMaxSeconds; set => SetProperty(ref _villageStatusSweepVillageMaxSeconds, value); }

    public void ResetDefaults()
    {
        TaskMinSeconds = Format(PacingDefaults.ActionPacingTaskMinSeconds);
        TaskMaxSeconds = Format(PacingDefaults.ActionPacingTaskMaxSeconds);
        PageLoadMinSeconds = Format(PacingDefaults.ActionPacingPageLoadMinSeconds);
        PageLoadMaxSeconds = Format(PacingDefaults.ActionPacingPageLoadMaxSeconds);
        ClickMinSeconds = Format(PacingDefaults.ActionPacingClickMinSeconds);
        ClickMaxSeconds = Format(PacingDefaults.ActionPacingClickMaxSeconds);
        LoopMinSeconds = Format(PacingDefaults.ActionPacingLoopMinSeconds);
        LoopMaxSeconds = Format(PacingDefaults.ActionPacingLoopMaxSeconds);
        ShortVillageDeferSeconds = PacingDefaults.ShortVillageDeferSeconds;
        FarmListStepDelayMinSeconds = Format(PacingDefaults.FarmListStepDelayMinSeconds);
        FarmListStepDelayMaxSeconds = Format(PacingDefaults.FarmListStepDelayMaxSeconds);
        CollectStepDelayMinSeconds = Format(PacingDefaults.CollectStepDelayMinSeconds);
        CollectStepDelayMaxSeconds = Format(PacingDefaults.CollectStepDelayMaxSeconds);
        IdleBreakEnabled = PacingDefaults.ActionPacingIdleBreakEnabled;
        IdleBreakIntervalMinMinutes = Format(PacingDefaults.ActionPacingIdleBreakIntervalMinMinutes);
        IdleBreakIntervalMaxMinutes = Format(PacingDefaults.ActionPacingIdleBreakIntervalMaxMinutes);
        IdleBreakDurationMinMinutes = Format(PacingDefaults.ActionPacingIdleBreakDurationMinMinutes);
        IdleBreakDurationMaxMinutes = Format(PacingDefaults.ActionPacingIdleBreakDurationMaxMinutes);
        IdleBrowseEnabled = PacingDefaults.ActionPacingIdleBrowseEnabled;
        IdleBrowseIntervalMinMinutes = Format(PacingDefaults.ActionPacingIdleBrowseIntervalMinMinutes);
        IdleBrowseIntervalMaxMinutes = Format(PacingDefaults.ActionPacingIdleBrowseIntervalMaxMinutes);
        IdleBrowsePageMap = PacingDefaults.ActionPacingIdleBrowsePageMap;
        IdleBrowsePageStatistics = PacingDefaults.ActionPacingIdleBrowsePageStatistics;
        IdleBrowsePageStatisticsHero = PacingDefaults.ActionPacingIdleBrowsePageStatisticsHero;
        IdleBrowsePageStatisticsTop10 = PacingDefaults.ActionPacingIdleBrowsePageStatisticsTop10;
        IdleBrowsePageStatisticsDefenders = PacingDefaults.ActionPacingIdleBrowsePageStatisticsDefenders;
        IdleBrowsePageStatisticsAttackers = PacingDefaults.ActionPacingIdleBrowsePageStatisticsAttackers;
        IdleBrowsePageReports = PacingDefaults.ActionPacingIdleBrowsePageReports;
        IdleBrowsePageMessages = PacingDefaults.ActionPacingIdleBrowsePageMessages;
        ContinuousKeepAliveEnabled = PacingDefaults.ContinuousKeepAliveEnabled;
        ContinuousKeepAliveMinMinutes = PacingDefaults.ContinuousKeepAliveMinMinutes.ToString(CultureInfo.InvariantCulture);
        ContinuousKeepAliveMaxMinutes = PacingDefaults.ContinuousKeepAliveMaxMinutes.ToString(CultureInfo.InvariantCulture);
        SessionPacingEnabled = PacingDefaults.SessionPacingEnabled;
        SmartSleepEnabled = PacingDefaults.SmartSleepEnabled;
        SmartSleepMinimumOpportunityMinutes = PacingDefaults.SmartSleepMinimumOpportunityMinutes.ToString(CultureInfo.InvariantCulture);
        SmartSleepWakeBeforeMinutes = PacingDefaults.SmartSleepWakeBeforeMinutes.ToString(CultureInfo.InvariantCulture);
        SmartSleepWakeAfterMinutes = PacingDefaults.SmartSleepWakeAfterMinutes.ToString(CultureInfo.InvariantCulture);
        SmartSleepFallbackMinMinutes = PacingDefaults.SmartSleepFallbackMinMinutes.ToString(CultureInfo.InvariantCulture);
        SmartSleepFallbackMaxMinutes = PacingDefaults.SmartSleepFallbackMaxMinutes.ToString(CultureInfo.InvariantCulture);
        SessionRunMinMinutes = PacingDefaults.SessionPacingRunMinMinutes.ToString(CultureInfo.InvariantCulture);
        SessionRunMaxMinutes = PacingDefaults.SessionPacingRunMaxMinutes.ToString(CultureInfo.InvariantCulture);
        SessionSleepMinMinutes = PacingDefaults.SessionPacingSleepMinMinutes.ToString(CultureInfo.InvariantCulture);
        SessionSleepMaxMinutes = PacingDefaults.SessionPacingSleepMaxMinutes.ToString(CultureInfo.InvariantCulture);
        SessionDailyMaxHours = PacingDefaults.SessionPacingDailyMaxHours;
        SessionDailyMaxVariationPercent = PacingDefaults.SessionPacingDailyMaxVariationPercent;
        SessionHoursVariationPercent = PacingDefaults.SessionPacingHoursVariationPercent;
        SetSessionAllowedHours(Enumerable.Range(0, 24));
        VillageStatusSweepEnabled = PacingDefaults.VillageStatusSweepEnabled;
        VillageStatusSweepDorf1Enabled = true;
        VillageStatusSweepDorf2Enabled = false;
        VillageStatusSweepRoundMinMinutes = PacingDefaults.VillageStatusSweepRoundMinMinutes.ToString(CultureInfo.InvariantCulture);
        VillageStatusSweepRoundMaxMinutes = PacingDefaults.VillageStatusSweepRoundMaxMinutes.ToString(CultureInfo.InvariantCulture);
        VillageStatusSweepVillageMinSeconds = Format(PacingDefaults.VillageStatusSweepVillageMinSeconds);
        VillageStatusSweepVillageMaxSeconds = Format(PacingDefaults.VillageStatusSweepVillageMaxSeconds);
    }

    public void SetSessionAllowedHours(IEnumerable<int> allowedHours)
    {
        var allowed = allowedHours.Where(hour => hour is >= 0 and <= 23).ToHashSet();
        foreach (var hour in SessionAllowedHours)
        {
            hour.IsSelected = allowed.Contains(hour.Hour);
        }
    }

    public IReadOnlyList<int> GetSelectedSessionHours() => SessionAllowedHours
        .Where(hour => hour.IsSelected)
        .Select(hour => hour.Hour)
        .ToList();

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
