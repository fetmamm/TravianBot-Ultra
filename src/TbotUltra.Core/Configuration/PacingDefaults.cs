namespace TbotUltra.Core.Configuration;

public static class PacingDefaults
{
    public const bool SessionPacingEnabled = true;
    public const int SessionPacingMaxRunMinutes = 90;
    public const int SessionPacingSleepMinutes = 45;
    public const int SessionPacingVariationPercent = 40;
    public const int SessionPacingDailyMaxHours = 18;
    // Daily-max has its own variation, independent of the run/sleep/schedule "Variation" above.
    public const int SessionPacingDailyMaxVariationPercent = 10;

    public const bool ActionPacingEnabled = true;
    public const double ActionPacingTaskMinSeconds = 0.8;
    public const double ActionPacingTaskMaxSeconds = 3.0;
    public const double ActionPacingPageLoadMinSeconds = 0.8;
    public const double ActionPacingPageLoadMaxSeconds = 1.8;
    public const double ActionPacingClickMinSeconds = 0.6;
    public const double ActionPacingClickMaxSeconds = 1.8;
    public const double ActionPacingLoopMinSeconds = 4.0;
    public const double ActionPacingLoopMaxSeconds = 25.0;
    public const double FarmListStepDelayMinSeconds = 1.5;
    public const double FarmListStepDelayMaxSeconds = 4.0;

    // Delay (seconds) between internal clicks/steps in the auto-collect tasks/daily-quests flows only.
    public const double CollectStepDelayMinSeconds = 0.8;
    public const double CollectStepDelayMaxSeconds = 2.5;
}
