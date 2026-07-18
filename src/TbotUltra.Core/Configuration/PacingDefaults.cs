namespace TbotUltra.Core.Configuration;

public static class PacingDefaults
{
    public const bool SessionPacingEnabled = true;
    // Run and sleep durations are random picks in [min, max] minutes.
    public const int SessionPacingRunMinMinutes = 15;
    public const int SessionPacingRunMaxMinutes = 50;
    public const int SessionPacingSleepMinMinutes = 10;
    public const int SessionPacingSleepMaxMinutes = 40;
    public const int SessionPacingDailyMaxHours = 16;
    // Daily-max has its own variation, independent of the run/sleep/schedule "Variation" above.
    public const int SessionPacingDailyMaxVariationPercent = 10;
    // Allowed-hours ("Daily hours") boundary jitter: shifts each on/off hour boundary by ±this% of an
    // hour, deterministically per day, so the bot doesn't start/stop at the exact same clock time daily.
    // 0 disables (exact boundaries). Capped at 49% so adjacent boundaries never reorder.
    public const int SessionPacingHoursVariationPercent = 30;

    public const bool ActionPacingEnabled = true;
    public const double ActionPacingTaskMinSeconds = 0.8;
    public const double ActionPacingTaskMaxSeconds = 2.0;
    public const double ActionPacingPageLoadMinSeconds = 0.6;
    public const double ActionPacingPageLoadMaxSeconds = 1.8;
    public const double ActionPacingClickMinSeconds = 0.5;
    public const double ActionPacingClickMaxSeconds = 1.5;
    public const double ActionPacingLoopMinSeconds = 4.0;
    public const double ActionPacingLoopMaxSeconds = 25.0;
    public const double FarmListStepDelayMinSeconds = 1.5;
    public const double FarmListStepDelayMaxSeconds = 4.0;

    // Occasional "step away from the computer" idle pause inserted between loop passes. Sometime within
    // the interval range a random pause of the duration range fires, then the interval reschedules.
    // Enabled by default. Values are minutes (duration min is fractional, so doubles).
    public const bool ActionPacingIdleBreakEnabled = true;
    public const double ActionPacingIdleBreakIntervalMinMinutes = 10.0;
    public const double ActionPacingIdleBreakIntervalMaxMinutes = 60.0;
    public const double ActionPacingIdleBreakDurationMinMinutes = 0.5;
    public const double ActionPacingIdleBreakDurationMaxMinutes = 3.0;

    // Occasional "idle browse": between loop passes, at a random interval, open a non-functional page
    // (map/statistics/reports/messages) and read nothing, so the server-visible page mix looks like a
    // real player poking around instead of only visiting build pages. Disabled by default; interval in
    // minutes. Each page can be toggled independently (all on by default); no enabled page => no browse.
    public const bool ActionPacingIdleBrowseEnabled = false;
    public const double ActionPacingIdleBrowseIntervalMinMinutes = 15.0;
    public const double ActionPacingIdleBrowseIntervalMaxMinutes = 60.0;
    public const bool ActionPacingIdleBrowsePageMap = true;
    public const bool ActionPacingIdleBrowsePageStatistics = true;
    public const bool ActionPacingIdleBrowsePageStatisticsHero = true;
    public const bool ActionPacingIdleBrowsePageStatisticsTop10 = true;
    public const bool ActionPacingIdleBrowsePageStatisticsDefenders = true;
    public const bool ActionPacingIdleBrowsePageStatisticsAttackers = true;
    public const bool ActionPacingIdleBrowsePageReports = true;
    public const bool ActionPacingIdleBrowsePageMessages = true;

    // Delay (seconds) between internal clicks/steps in the auto-collect tasks/daily-quests flows only.
    public const double CollectStepDelayMinSeconds = 0.3;
    public const double CollectStepDelayMaxSeconds = 1.0;

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
    public const int ConstructionLoginFillWindowMinutes = 15;

    // Pre-sleep fill (part of the construction start delay feature): shortly before a session-pacing
    // sleep, pull humanize-deferred construction starts forward so every build slot that CAN be
    // filled is occupied when the sleep begins. The window scales with how many villages have queued
    // construction work (villages * PerVillageMinutes, clamped to [WindowMin, WindowMax]);
    // rescheduled starts land at a random time inside the window, at least MarginMinutes before the
    // sleep. SleepHoldMaxMinutes bounds how long an automatic sleep may wait for a final fill item.
    public const int PreSleepFillPerVillageMinutes = 2;
    public const int PreSleepFillWindowMinMinutes = 15;
    public const int PreSleepFillWindowMaxMinutes = 45;
    public const int PreSleepFillMarginMinutes = 2;
    public const int PreSleepFillSleepHoldMaxMinutes = 3;
}
