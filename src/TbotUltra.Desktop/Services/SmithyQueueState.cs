using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class SmithyQueueState
{
    public static SmithyUpgradeStatus PreserveKnownActiveQueue(
        SmithyUpgradeStatus incoming,
        SmithyUpgradeStatus? existing,
        DateTimeOffset now)
    {
        if (ResolveActiveUpgrades(incoming, now).Count > 0
            || existing is null
            || ResolveActiveUpgrades(existing, now).Count == 0)
        {
            return incoming;
        }

        return incoming with
        {
            ActiveUpgradeCount = existing.ActiveUpgradeCount,
            RemainingSeconds = existing.RemainingSeconds,
            ActiveUpgradeRemainingSeconds = existing.ActiveUpgradeRemainingSeconds,
            RemainingText = existing.RemainingText,
            StatusText = existing.StatusText,
            ActiveUpgradeFinishes = existing.ActiveUpgradeFinishes,
            ActiveUpgrades = existing.ActiveUpgrades,
        };
    }

    public static IReadOnlyList<ActiveSmithyUpgrade> ResolveActiveUpgrades(
        SmithyUpgradeStatus? status,
        DateTimeOffset now)
    {
        if (status is null)
        {
            return [];
        }

        if (status.ActiveUpgrades is { Count: > 0 })
        {
            return status.ActiveUpgrades
                .Where(entry => entry.Finish is null
                    ? entry.TimeLeftSeconds is > 0
                    : !entry.Finish.IsFinishedAt(now))
                .Select(entry => entry with
                {
                    TimeLeftSeconds = entry.Finish?.RemainingSecondsAt(now) ?? entry.TimeLeftSeconds,
                })
                .OrderBy(entry => entry.TimeLeftSeconds)
                .ToList();
        }

        return (status.ActiveUpgradeFinishes ?? [])
            .Where(finish => !finish.IsFinishedAt(now))
            .Select(finish => new ActiveSmithyUpgrade(
                "Smithy upgrade",
                null,
                finish.RemainingSecondsAt(now),
                finish))
            .OrderBy(entry => entry.TimeLeftSeconds)
            .ToList();
    }
}
