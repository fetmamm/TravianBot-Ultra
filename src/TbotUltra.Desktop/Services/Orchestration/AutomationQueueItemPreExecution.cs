using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IAutomationQueueItemPreExecution
{
    ValueTask<QueueItemGuardResult> RunAsync(
        QueueItem item,
        BotOptions options,
        string logPrefix,
        Stopwatch timer,
        CancellationToken cancellationToken);
}

internal sealed class AutomationQueueItemPreExecution(
    IAutomationConstructionRequirementGuard constructionRequirementGuard,
    IAutomationConstructLiveReconciliation constructLiveReconciliation,
    IAutomationConstructPreflight constructPreflight) : IAutomationQueueItemPreExecution
{
    public async ValueTask<QueueItemGuardResult> RunAsync(
        QueueItem item,
        BotOptions options,
        string logPrefix,
        Stopwatch timer,
        CancellationToken cancellationToken)
    {
        if (constructionRequirementGuard.TryHandleUpgradeWaitingForConstruct(item, logPrefix, timer))
        {
            return new QueueItemGuardResult(true, false);
        }

        var constructRefresh = await constructPreflight.RefreshTargetStatusAsync(
            item,
            options,
            cancellationToken);
        if (constructRefresh.FreshStatus is not null
            && constructLiveReconciliation.TryHandleExistingConstruct(
                item,
                constructRefresh.FreshStatus,
                logPrefix,
                timer))
        {
            return new QueueItemGuardResult(true, true);
        }

        if (constructRefresh.FreshStatus is not null
            && constructLiveReconciliation.TryHandleOccupiedSlot(
                item,
                constructRefresh.FreshStatus,
                logPrefix,
                timer))
        {
            return new QueueItemGuardResult(true, true);
        }

        if (constructRefresh.CanUseCache
            && await constructPreflight.TryHandleQueueFullAsync(item, logPrefix, timer))
        {
            return new QueueItemGuardResult(true, true);
        }

        if (constructRefresh.CanUseCache
            && await constructionRequirementGuard.TryHandleAsync(item, logPrefix, timer))
        {
            return new QueueItemGuardResult(true, true);
        }

        return QueueItemGuardResult.NotHandled;
    }
}
