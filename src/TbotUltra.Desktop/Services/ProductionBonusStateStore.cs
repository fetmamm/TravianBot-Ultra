using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// One resource's remembered production-bonus timers. <see cref="Bonus"/> is 25, 15, or 0 (none).
/// Times are absolute UTC so the countdowns survive an app restart (recomputed on load).
/// </summary>
public sealed record ProductionBonusResourceTimer(
    string Resource,
    int Bonus,
    DateTimeOffset BonusEndsAtUtc,
    DateTimeOffset NextAttemptAtUtc);

/// <summary>
/// User-editable, account-scoped scheduling settings for the +15% feature.
/// <list type="bullet">
/// <item>Delay window (minutes) added on top of a resource's cooldown so a run does not fire at the exact
/// moment the timer expires (human-like).</item>
/// <item>Daily-reset resolution: <see cref="ResetMode"/> is "auto" (learn the reset hour by watching when
/// the free video becomes available) or "manual" (use <see cref="ManualResetHour"/>). <see cref="LearnedResetHour"/>
/// is the auto-learned server-local hour (null until learned). The last-poll fields track the free-video
/// availability across hourly polls so the unavailable→available transition (= the reset) can be detected.</item>
/// </list>
/// </summary>
public sealed record ProductionBonusSettings(
    int DelayMinMinutes,
    int DelayMaxMinutes,
    string ResetMode,
    int ManualResetHour,
    int? LearnedResetHour,
    int? LastPollServerHour,
    bool LastPollFreeVideoAvailable);

/// <summary>
/// Per-account remembered +15%/+25% production bonus timers (account-wide, four resources) plus the
/// user-editable random-delay settings. The worker reports the live state after each run; the desktop
/// persists absolute end/next-attempt times so the dashboard popup can restore the countdowns and the
/// loop knows when to try again. Modeled on <see cref="TownHallCelebrationStateStore"/>.
/// </summary>
public static class ProductionBonusStateStore
{
    public const int DefaultDelayMinMinutes = 10;
    public const int DefaultDelayMaxMinutes = 60;
    private const int MaxDelayMinutes = 24 * 60;

    public const string ResetModeAuto = "auto";
    public const string ResetModeManual = "manual";
    public const string DefaultResetMode = ResetModeAuto;
    public const int DefaultManualResetHour = 9;

    private static readonly object FileIoLock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class ResourceState
    {
        public string Resource { get; set; } = string.Empty;
        public int Bonus { get; set; }
        public DateTimeOffset BonusEndsAtUtc { get; set; }
        public DateTimeOffset NextAttemptAtUtc { get; set; }
    }

    private sealed class StateFile
    {
        public int DelayMinMinutes { get; set; } = DefaultDelayMinMinutes;
        public int DelayMaxMinutes { get; set; } = DefaultDelayMaxMinutes;
        // Daily-reset scheduling. Server-local whole hours. LearnedResetHour is null until auto-learn
        // observes the free video re-enable; the LastPoll* fields bracket that transition across polls.
        public string ResetMode { get; set; } = DefaultResetMode;
        public int ManualResetHour { get; set; } = DefaultManualResetHour;
        public int? LearnedResetHour { get; set; }
        public int? LastPollServerHour { get; set; }
        public bool LastPollFreeVideoAvailable { get; set; }
        public List<ResourceState> Resources { get; set; } = new();
    }

    public static IReadOnlyList<ProductionBonusResourceTimer> Load(
        string projectRoot,
        string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return Array.Empty<ProductionBonusResourceTimer>();
        }

        lock (FileIoLock)
        {
            // Best-effort for display: a missing or unreadable file just shows no timers.
            var file = ReadFileOrNull(projectRoot, accountName);
            if (file?.Resources is null)
            {
                return Array.Empty<ProductionBonusResourceTimer>();
            }

            return file.Resources
                .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Resource))
                .Select(entry => new ProductionBonusResourceTimer(
                    entry.Resource.ToLowerInvariant(),
                    entry.Bonus,
                    entry.BonusEndsAtUtc.ToUniversalTime(),
                    entry.NextAttemptAtUtc.ToUniversalTime()))
                .ToList();
        }
    }

    public static void Save(
        string projectRoot,
        string? accountName,
        IReadOnlyList<ProductionBonusResourceTimer> timers)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        lock (FileIoLock)
        {
            // Preserve the user's delay settings when overwriting the timers.
            var file = ReadFileOrNull(projectRoot, accountName) ?? new StateFile();
            file.Resources = timers
                .Where(timer => timer is not null && !string.IsNullOrWhiteSpace(timer.Resource))
                .Select(timer => new ResourceState
                {
                    Resource = timer.Resource.ToLowerInvariant(),
                    Bonus = timer.Bonus,
                    BonusEndsAtUtc = timer.BonusEndsAtUtc.ToUniversalTime(),
                    NextAttemptAtUtc = timer.NextAttemptAtUtc.ToUniversalTime(),
                })
                .ToList();

            WriteFile(projectRoot, accountName, file);
        }
    }

    // Clears the remembered timers but keeps the user's delay settings.
    public static void Clear(string projectRoot, string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFileOrNull(projectRoot, accountName);
            if (file is null)
            {
                return;
            }

            file.Resources = new List<ResourceState>();
            WriteFile(projectRoot, accountName, file);
        }
    }

    private static ProductionBonusSettings DefaultSettings()
        => new(DefaultDelayMinMinutes, DefaultDelayMaxMinutes, DefaultResetMode, DefaultManualResetHour, null, null, false);

    public static ProductionBonusSettings LoadSettings(string projectRoot, string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return DefaultSettings();
        }

        lock (FileIoLock)
        {
            var file = ReadFileOrNull(projectRoot, accountName);
            if (file is null)
            {
                return DefaultSettings();
            }

            var (min, max) = NormalizeDelay(file.DelayMinMinutes, file.DelayMaxMinutes);
            return new ProductionBonusSettings(
                min,
                max,
                NormalizeResetMode(file.ResetMode),
                Math.Clamp(file.ManualResetHour, 0, 23),
                NormalizeHourOrNull(file.LearnedResetHour),
                NormalizeHourOrNull(file.LastPollServerHour),
                file.LastPollFreeVideoAvailable);
        }
    }

    public static void SaveSettings(string projectRoot, string? accountName, int delayMinMinutes, int delayMaxMinutes)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        var (min, max) = NormalizeDelay(delayMinMinutes, delayMaxMinutes);
        lock (FileIoLock)
        {
            var file = ReadFileOrNull(projectRoot, accountName) ?? new StateFile();
            file.DelayMinMinutes = min;
            file.DelayMaxMinutes = max;
            WriteFile(projectRoot, accountName, file);
        }
    }

    // Persists the user's daily-reset choice (auto/manual + manual hour). Never touches the learned hour.
    public static void SaveResetSettings(string projectRoot, string? accountName, string resetMode, int manualResetHour)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFileOrNull(projectRoot, accountName) ?? new StateFile();
            file.ResetMode = NormalizeResetMode(resetMode);
            file.ManualResetHour = Math.Clamp(manualResetHour, 0, 23);
            WriteFile(projectRoot, accountName, file);
        }
    }

    // Persists the auto-learn progress: the learned reset hour (null while still learning) and the last
    // free-video poll (server hour + whether it was available) used to detect the reset transition.
    public static void SaveLearnState(
        string projectRoot,
        string? accountName,
        int? learnedResetHour,
        int? lastPollServerHour,
        bool lastPollFreeVideoAvailable)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        lock (FileIoLock)
        {
            var file = ReadFileOrNull(projectRoot, accountName) ?? new StateFile();
            file.LearnedResetHour = NormalizeHourOrNull(learnedResetHour);
            file.LastPollServerHour = NormalizeHourOrNull(lastPollServerHour);
            file.LastPollFreeVideoAvailable = lastPollFreeVideoAvailable;
            WriteFile(projectRoot, accountName, file);
        }
    }

    private static string NormalizeResetMode(string? mode)
        => string.Equals(mode, ResetModeManual, StringComparison.OrdinalIgnoreCase) ? ResetModeManual : ResetModeAuto;

    private static int? NormalizeHourOrNull(int? hour)
        => hour is int value && value is >= 0 and <= 23 ? value : null;

    // Clamps to a sane range and guarantees min <= max.
    public static (int Min, int Max) NormalizeDelay(int min, int max)
    {
        min = Math.Clamp(min, 0, MaxDelayMinutes);
        max = Math.Clamp(max, 0, MaxDelayMinutes);
        if (min > max)
        {
            (min, max) = (max, min);
        }

        return (min, max);
    }

    /// <summary>
    /// True when the feature should attempt a run now: no remembered state yet, or at least one
    /// resource's next-attempt time has passed. When the state file exists but cannot be read (transient
    /// OneDrive/AV lock or corruption), returns false — an unknown state must not trigger a run.
    /// </summary>
    public static bool ShouldAttemptNow(string projectRoot, string? accountName, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return false;
        }

        lock (FileIoLock)
        {
            var outcome = TryReadFile(projectRoot, accountName, out var file);
            if (outcome == ReadOutcome.Missing)
            {
                return true; // first run — nothing remembered yet
            }

            if (outcome == ReadOutcome.Error || file is null)
            {
                return false; // unknown state — stay conservative
            }

            var resources = file.Resources ?? new List<ResourceState>();
            if (resources.Count == 0)
            {
                return true;
            }

            return resources.Any(entry => entry is not null && entry.NextAttemptAtUtc.ToUniversalTime() <= nowUtc);
        }
    }

    private enum ReadOutcome
    {
        Missing,
        Loaded,
        Error,
    }

    private static StateFile? ReadFileOrNull(string projectRoot, string accountName)
        => TryReadFile(projectRoot, accountName, out var file) == ReadOutcome.Loaded ? file : null;

    private static ReadOutcome TryReadFile(string projectRoot, string accountName, out StateFile? file)
    {
        file = null;
        var path = AccountStoragePaths.ProductionBonusStatePath(projectRoot, accountName);
        if (!File.Exists(path))
        {
            return ReadOutcome.Missing;
        }

        string raw;
        try
        {
            raw = ReadAllTextWithRetry(path);
        }
        catch
        {
            return ReadOutcome.Error; // could not read after retries (locked file, etc.)
        }

        try
        {
            file = JsonSerializer.Deserialize<StateFile>(raw, SerializerOptions);
            return file is null ? ReadOutcome.Error : ReadOutcome.Loaded;
        }
        catch (JsonException)
        {
            return ReadOutcome.Error; // corrupt JSON
        }
    }

    // Retries transient IOException/UnauthorizedAccessException (OneDrive/AV holding the file), per the
    // OneDrive file-IO rule in the engineering notes. Writes go through AtomicFile which already retries.
    private static string ReadAllTextWithRetry(string path)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(40 * attempt);
            }
        }
    }

    private static void WriteFile(string projectRoot, string accountName, StateFile file)
    {
        var path = AccountStoragePaths.ProductionBonusStatePath(projectRoot, accountName);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
    }
}
