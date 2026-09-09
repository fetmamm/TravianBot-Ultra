using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class IncomingAttackObservationPolicy
{
    public static readonly TimeSpan UnconfirmedSignalRetryInterval = TimeSpan.FromMinutes(10);

    public static bool ShouldReadDetails(
        IncomingAttackSignal signal,
        int? confirmedMovementCount,
        DateTimeOffset? lastReadUtc,
        DateTimeOffset nowUtc,
        bool isNewPlusMarker = false)
    {
        if (confirmedMovementCount.HasValue)
        {
            return signal.Dorf1ArrivalTimesUtc is { } arrivals
                   && arrivals.Count > confirmedMovementCount.Value;
        }

        if (isNewPlusMarker)
        {
            return true;
        }

        if (!lastReadUtc.HasValue)
        {
            return true;
        }

        return nowUtc - lastReadUtc.Value >= UnconfirmedSignalRetryInterval;
    }

    public static bool ShouldKeepPendingSignal(
        IncomingAttackSignal signal,
        bool hasActiveConfirmedMovements,
        bool hasConfirmedMovementHistory,
        DateTimeOffset nowUtc)
    {
        if (signal.Dorf1ArrivalTimesUtc is { Count: > 0 } arrivals)
        {
            return arrivals.Any(arrival => arrival > nowUtc);
        }

        return hasActiveConfirmedMovements || !hasConfirmedMovementHistory;
    }

    public static int CountNewConfirmedAttacks(
        IEnumerable<IncomingAttack> previousAttacks,
        IEnumerable<IncomingAttack> currentAttacks)
    {
        var previousIds = previousAttacks
            .Select(attack => attack.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return currentAttacks
            .Select(attack => attack.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(id => !previousIds.Contains(id));
    }

    public static bool ShouldPlaySound(
        bool soundEnabled,
        int newAttackCount,
        DateTimeOffset? lastSoundUtc,
        TimeSpan cooldown,
        DateTimeOffset nowUtc)
    {
        return soundEnabled
               && newAttackCount > 0
               && (!lastSoundUtc.HasValue || nowUtc - lastSoundUtc.Value >= cooldown);
    }
}
