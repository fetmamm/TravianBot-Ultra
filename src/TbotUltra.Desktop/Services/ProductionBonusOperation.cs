using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

internal enum ProductionBonusApplicationStatus
{
    Applied,
    AccountDeletionPending,
    Deferred,
    Failed,
}

internal sealed record ProductionBonusApplicationResult(
    ProductionBonusApplicationStatus Status,
    string Message,
    IReadOnlyList<ProductionBonusResourceTimer> Timers,
    DateTimeOffset? NextDeadlineUtc = null,
    bool StateChanged = false);

internal static class ProductionBonusOperation
{
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(30);

    internal static ProductionBonusApplicationResult Apply(
        string projectRoot,
        string? accountName,
        ProductionBonusOutcome? outcome,
        DateTimeOffset nowUtc,
        TimeSpan fallbackServerUtcOffset,
        TimeSpan delay,
        int? dailyResetHour)
    {
        if (outcome is null)
        {
            return ApplyFailureBackoff(projectRoot, accountName, nowUtc, "Production bonus returned no outcome.");
        }

        if (outcome.Status == ProductionBonusOutcomeStatus.AccountDeletionPending)
        {
            return new ProductionBonusApplicationResult(
                ProductionBonusApplicationStatus.AccountDeletionPending,
                outcome.Message,
                []);
        }

        if (outcome.Status == ProductionBonusOutcomeStatus.Deferred)
        {
            return new ProductionBonusApplicationResult(
                ProductionBonusApplicationStatus.Deferred,
                outcome.Message,
                [],
                outcome.RetryAtUtc);
        }

        if (!outcome.HasObservation)
        {
            return ApplyFailureBackoff(projectRoot, accountName, nowUtc, outcome.Message);
        }

        var serverUtcOffset = outcome.ServerUtcOffset ?? fallbackServerUtcOffset;
        var timers = outcome.Resources
            .Select(resource => new ProductionBonusResourceTimer(
                resource.Resource,
                resource.Bonus,
                nowUtc.AddSeconds(resource.RemainingSeconds),
                ProductionBonusScheduleCalculator.ResolveNextAttemptUtc(
                    resource,
                    nowUtc,
                    serverUtcOffset,
                    delay,
                    dailyResetHour)))
            .ToList();

        ProductionBonusStateStore.Save(projectRoot, accountName, timers);
        return new ProductionBonusApplicationResult(
            ProductionBonusApplicationStatus.Applied,
            outcome.Message,
            timers,
            timers.Count == 0 ? null : timers.Min(timer => timer.NextAttemptAtUtc),
            StateChanged: true);
    }

    private static ProductionBonusApplicationResult ApplyFailureBackoff(
        string projectRoot,
        string? accountName,
        DateTimeOffset nowUtc,
        string message)
    {
        var existing = ProductionBonusStateStore.Load(projectRoot, accountName);
        if (existing.Count > 0)
        {
            return new ProductionBonusApplicationResult(
                ProductionBonusApplicationStatus.Failed,
                message,
                existing,
                existing.Min(timer => timer.NextAttemptAtUtc));
        }

        var nextDeadlineUtc = nowUtc.Add(FailureBackoff);
        var timers = ProductionBonusResources.All
            .Select(resource => new ProductionBonusResourceTimer(resource, 0, nowUtc, nextDeadlineUtc))
            .ToList();
        ProductionBonusStateStore.Save(projectRoot, accountName, timers);
        return new ProductionBonusApplicationResult(
            ProductionBonusApplicationStatus.Failed,
            message,
            timers,
            nextDeadlineUtc,
            StateChanged: true);
    }
}
