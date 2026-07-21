using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static void ThrowIfTaskBlocked(string taskName, string result)
    {
        if (!IsBlockedTaskResult(result)) return;
        if (IsPermanentlyBlockedTaskResult(result))
            throw new TaskBlockedPermanentlyException($"Task '{taskName}' blocked permanently: {result}");
        if (TryExtractQueueWaitSeconds(result, out var waitSeconds))
            throw new TaskWaitException(waitSeconds, $"Task '{taskName}' waiting: {result}", DeriveTaskWaitReason(result));
        throw new InvalidOperationException($"Task '{taskName}' could not execute successfully: {result}");
    }

    internal static string? DeriveTaskWaitReason(string result)
    {
        if (result.Contains("hero_reviving", StringComparison.OrdinalIgnoreCase)) return TaskWaitReasons.HeroReviving;
        if (result.Contains("Hero is away", StringComparison.OrdinalIgnoreCase)) return TaskWaitReasons.HeroAway;
        if (result.Contains("adventure_skipped_hp_too_low", StringComparison.OrdinalIgnoreCase) || result.Contains("Hero HP too low", StringComparison.OrdinalIgnoreCase)) return TaskWaitReasons.HeroHpTooLow;
        return result.Contains("queued", StringComparison.OrdinalIgnoreCase) ? TaskWaitReasons.WorkQueued : null;
    }

    internal static ConstructionTaskOutcome ClassifyConstructionTaskResult(string taskName, string? result)
    {
        if (!IsConstructionTaskResult(taskName) || string.IsNullOrWhiteSpace(result)) return ConstructionTaskOutcome.None;
        if (IsBlockedTaskResult(result)) return ConstructionTaskOutcome.WaitingOrBlocked;
        var value = result.ToLowerInvariant();
        if (value.Contains("already exists at slot", StringComparison.Ordinal)) return ConstructionTaskOutcome.AlreadyExists;
        if ((string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase) || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)) && value.Contains("is empty", StringComparison.Ordinal) && value.Contains("construct the building before upgrading", StringComparison.Ordinal)) return ConstructionTaskOutcome.MissingBuilding;
        if (value.Contains("queued", StringComparison.Ordinal) || value.Contains("still in progress", StringComparison.Ordinal) || value.Contains("active construction detected", StringComparison.Ordinal) || value.Contains("build queue contains", StringComparison.Ordinal)) return ConstructionTaskOutcome.QueuedOrInProgress;
        if (value.Contains("reached level", StringComparison.Ordinal) || value.Contains("reached max level", StringComparison.Ordinal) || value.Contains("constructed ", StringComparison.Ordinal) || value.Contains("confirmed level", StringComparison.Ordinal)) return ConstructionTaskOutcome.ConfirmedComplete;
        if (value.Contains("already at level", StringComparison.Ordinal) || value.Contains("already at max", StringComparison.Ordinal) || (value.Contains("target ", StringComparison.Ordinal) && value.Contains(" reached", StringComparison.Ordinal))) return ConstructionTaskOutcome.AlreadySatisfied;
        return ConstructionTaskOutcome.UnknownSuccess;
    }

    private static bool IsConstructionTaskResult(string taskName) =>
        string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase);

    private static void ThrowIfTroopsGroupBlocked(string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return;
        if (result.Contains("Smithy not found in this village", StringComparison.OrdinalIgnoreCase))
            throw new TaskBlockedPermanentlyException($"Task 'upgrade_troops_at_smithy' blocked permanently: troops_blocked=smithy_missing | {result}");
        if (result.Contains("Smithy:", StringComparison.OrdinalIgnoreCase) && result.Contains("All done", StringComparison.OrdinalIgnoreCase))
            throw new TaskBlockedPermanentlyException($"Task 'upgrade_troops_at_smithy' blocked permanently: troops_blocked=all_done | {result}");
    }

    internal static bool IsBlockedTaskResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return false;
        var value = result.ToLowerInvariant();
        return value.Contains(" blocked ") || value.Contains("blocked (") || value.Contains("queue_wait_seconds=") || value.Contains("cannot be built yet") || value.Contains("cannot be upgraded yet") || value.Contains("is not listed by the server") || value.Contains("cannot be built in slot") || value.Contains("reports max level reached");
    }

    private static bool IsPermanentlyBlockedTaskResult(string result)
    {
        var value = result.ToLowerInvariant();
        return value.Contains("reports max level reached") || value.Contains("is not listed by the server") || value.Contains("cannot be built in slot");
    }

    private static bool TryExtractQueueWaitSeconds(string result, out int seconds)
    {
        seconds = 0;
        const string token = "queue_wait_seconds=";
        var index = result.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;
        var start = index + token.Length;
        var end = start;
        while (end < result.Length && (char.IsDigit(result[end]) || result[end] == '-')) end++;
        if (end == start || !int.TryParse(result.AsSpan(start, end - start), out var parsed)) return false;
        seconds = parsed;
        return true;
    }
}
