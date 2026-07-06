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
/// User-editable, account-scoped scheduling settings for the +15% feature: the random delay window (in
/// minutes) added on top of a resource's cooldown so a new "watch video" run does not fire at the exact
/// moment the timer expires (human-like).
/// </summary>
public sealed record ProductionBonusSettings(int DelayMinMinutes, int DelayMaxMinutes);

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
            var file = ReadFile(projectRoot, accountName);
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
            var file = ReadFile(projectRoot, accountName) ?? new StateFile();
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
            var file = ReadFile(projectRoot, accountName);
            if (file is null)
            {
                return;
            }

            file.Resources = new List<ResourceState>();
            WriteFile(projectRoot, accountName, file);
        }
    }

    public static ProductionBonusSettings LoadSettings(string projectRoot, string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return new ProductionBonusSettings(DefaultDelayMinMinutes, DefaultDelayMaxMinutes);
        }

        lock (FileIoLock)
        {
            var file = ReadFile(projectRoot, accountName);
            if (file is null)
            {
                return new ProductionBonusSettings(DefaultDelayMinMinutes, DefaultDelayMaxMinutes);
            }

            var (min, max) = NormalizeDelay(file.DelayMinMinutes, file.DelayMaxMinutes);
            return new ProductionBonusSettings(min, max);
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
            var file = ReadFile(projectRoot, accountName) ?? new StateFile();
            file.DelayMinMinutes = min;
            file.DelayMaxMinutes = max;
            WriteFile(projectRoot, accountName, file);
        }
    }

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
    /// resource's next-attempt time has passed.
    /// </summary>
    public static bool ShouldAttemptNow(
        IReadOnlyList<ProductionBonusResourceTimer> timers,
        DateTimeOffset nowUtc)
    {
        if (timers.Count == 0)
        {
            return true;
        }

        return timers.Any(timer => timer.NextAttemptAtUtc <= nowUtc);
    }

    private static StateFile? ReadFile(string projectRoot, string accountName)
    {
        var path = AccountStoragePaths.ProductionBonusStatePath(projectRoot, accountName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StateFile>(File.ReadAllText(path), SerializerOptions);
        }
        catch
        {
            return null;
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
