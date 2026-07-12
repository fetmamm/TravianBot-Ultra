namespace TbotUltra.Core.Configuration;

public static class PacingDefaults
{
    public const bool SessionPacingEnabled = true;
    // Run and sleep durations are random picks in [min, max] minutes.
    public const int SessionPacingRunMinMinutes = 40;
    public const int SessionPacingRunMaxMinutes = 100;
    public const int SessionPacingSleepMinMinutes = 20;
    public const int SessionPacingSleepMaxMinutes = 60;
    public const int SessionPacingDailyMaxHours = 16;
    // Daily-max has its own variation, independent of the run/sleep/schedule "Variation" above.
    public const int SessionPacingDailyMaxVariationPercent = 10;
    // Allowed-hours ("Daily hours") boundary jitter: shifts each on/off hour boundary by ±this% of an
    // hour, deterministically per day, so the bot doesn't start/stop at the exact same clock time daily.
    // 0 disables (exact boundaries). Capped at 49% so adjacent boundaries never reorder.
    public const int SessionPacingHoursVariationPercent = 20;

    public const bool ActionPacingEnabled = true;
    public const double ActionPacingTaskMinSeconds = 0.8;
    public const double ActionPacingTaskMaxSeconds = 2.0;
    public const double ActionPacingPageLoadMinSeconds = 0.6;
    public const double ActionPacingPageLoadMaxSeconds = 1.6;
    public const double ActionPacingClickMinSeconds = 0.4;
    public const double ActionPacingClickMaxSeconds = 1.4;
    public const double ActionPacingLoopMinSeconds = 4.0;
    public const double ActionPacingLoopMaxSeconds = 25.0;
    public const double FarmListStepDelayMinSeconds = 1.5;
    public const double FarmListStepDelayMaxSeconds = 4.0;

    // Occasional "step away from the computer" idle pause inserted between loop passes. Sometime within
    // the interval range a random pause of the duration range fires, then the interval reschedules.
    // Enabled by default. Values are minutes (duration min is fractional, so doubles).
    public const bool ActionPacingIdleBreakEnabled = true;
    public const double ActionPacingIdleBreakIntervalMinMinutes = 20.0;
    public const double ActionPacingIdleBreakIntervalMaxMinutes = 75.0;
    public const double ActionPacingIdleBreakDurationMinMinutes = 0.5;
    public const double ActionPacingIdleBreakDurationMaxMinutes = 3.0;

    // Occasional "idle browse": between loop passes, at a random interval, open a non-functional page
    // (map/statistics/reports/messages) and read nothing, so the server-visible page mix looks like a
    // real player poking around instead of only visiting build pages. Enabled by default; interval in
    // minutes. Each page can be toggled independently (all on by default); no enabled page => no browse.
    public const bool ActionPacingIdleBrowseEnabled = true;
    public const double ActionPacingIdleBrowseIntervalMinMinutes = 15.0;
    public const double ActionPacingIdleBrowseIntervalMaxMinutes = 60.0;
    public const bool ActionPacingIdleBrowsePageMap = true;
    public const bool ActionPacingIdleBrowsePageStatistics = true;
    public const bool ActionPacingIdleBrowsePageReports = true;
    public const bool ActionPacingIdleBrowsePageMessages = true;

    // Delay (seconds) between internal clicks/steps in the auto-collect tasks/daily-quests flows only.
    public const double CollectStepDelayMinSeconds = 0.6;
    public const double CollectStepDelayMaxSeconds = 2.0;

    // Human-like pause before starting the next construction.
    // Enabled by default. Queue case (a build is already running and the next is placed in the
    // Travian queue): wait a random percentage of the running build's remaining time — well under
    // 100%, so the queued build is still placed before the current one finishes (no lost progress).
    // Capped at MaxDelayMinutes. No-Plus case (only one slot; next build starts only once the
    // current finishes): a random value in the [NoPlusMin, NoPlusMax] minute range instead.
    public const bool ConstructionHumanizeDelayEnabled = true;
    public const double ConstructionHumanizeQueuePercentMin = 5.0;
    public const double ConstructionHumanizeQueuePercentMax = 20.0;
    public const double ConstructionHumanizeMaxDelayMinutes = 25.0;
    public const double ConstructionHumanizeNoPlusMinMinutes = 0.5;
    public const double ConstructionHumanizeNoPlusMaxMinutes = 3.0;
}
