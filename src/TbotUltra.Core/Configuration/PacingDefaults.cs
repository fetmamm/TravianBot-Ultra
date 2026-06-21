namespace TbotUltra.Core.Configuration;

public static class PacingDefaults
{
    public const bool SessionPacingEnabled = true;
    public const int SessionPacingMaxRunMinutes = 90;
    public const int SessionPacingSleepMinutes = 45;
    public const int SessionPacingVariationPercent = 40;
    public const int SessionPacingDailyMaxHours = 18;

    public const bool ActionPacingEnabled = true;
    public const double ActionPacingTaskMinSeconds = 3.0;
    public const double ActionPacingTaskMaxSeconds = 8.0;
    public const double ActionPacingPageLoadMinSeconds = 1.0;
    public const double ActionPacingPageLoadMaxSeconds = 2.5;
    public const double ActionPacingClickMinSeconds = 0.8;
    public const double ActionPacingClickMaxSeconds = 2.0;
    public const double ActionPacingLoopMinSeconds = 10.0;
    public const double ActionPacingLoopMaxSeconds = 30.0;
    public const double FarmListStepDelayMinSeconds = 1.5;
    public const double FarmListStepDelayMaxSeconds = 4.0;

    // Delay (ms) between internal clicks/steps in the auto-collect tasks/daily-quests flows only.
    public const int CollectStepDelayMinMs = 800;
    public const int CollectStepDelayMaxMs = 2500;
}
