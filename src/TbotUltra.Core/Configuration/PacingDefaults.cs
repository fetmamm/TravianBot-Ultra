namespace TbotUltra.Core.Configuration;

public static class PacingDefaults
{
    public const bool SessionPacingEnabled = true;
    public const int SessionPacingMaxRunMinutes = 120;
    public const int SessionPacingSleepMinutes = 30;
    public const int SessionPacingVariationPercent = 30;

    public const bool ActionPacingEnabled = true;
    public const double ActionPacingTaskMinSeconds = 1.0;
    public const double ActionPacingTaskMaxSeconds = 3.0;
    public const double ActionPacingPageLoadMinSeconds = 0.3;
    public const double ActionPacingPageLoadMaxSeconds = 1.0;
    public const double ActionPacingClickMinSeconds = 0.3;
    public const double ActionPacingClickMaxSeconds = 0.8;
    public const double ActionPacingLoopMinSeconds = 2.0;
    public const double ActionPacingLoopMaxSeconds = 5.0;
}
