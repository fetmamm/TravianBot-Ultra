namespace TbotUltra.Core.Configuration;

public static class PacingDefaults
{
    public const bool SessionPacingEnabled = true;
    public const int SessionPacingMaxRunMinutes = 120;
    public const int SessionPacingSleepMinutes = 30;
    public const int SessionPacingVariationPercent = 30;

    public const bool ActionPacingEnabled = true;
    public const double ActionPacingTaskMinSeconds = 1.0;
    public const double ActionPacingTaskMaxSeconds = 4.0;
    public const double ActionPacingPageLoadMinSeconds = 0.5;
    public const double ActionPacingPageLoadMaxSeconds = 1.2;
    public const double ActionPacingClickMinSeconds = 0.3;
    public const double ActionPacingClickMaxSeconds = 0.8;
    public const double ActionPacingLoopMinSeconds = 3.0;
    public const double ActionPacingLoopMaxSeconds = 8.0;

    // Delay (ms) between internal clicks/steps in the auto-collect tasks/daily-quests flows only.
    public const int CollectStepDelayMinMs = 100;
    public const int CollectStepDelayMaxMs = 300;
}
