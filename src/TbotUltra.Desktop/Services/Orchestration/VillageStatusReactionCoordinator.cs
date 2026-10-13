namespace TbotUltra.Desktop.Services.Orchestration;

internal readonly record struct VillageStatusCollectionResult<TStatus>(
    TStatus Status,
    bool ShouldContinue,
    int Attempts);

internal interface IVillageStatusReactionPort<TVillage, TStatus>
{
    bool StopRequested { get; }

    ValueTask<TStatus> ReadBaseStatusAsync(TVillage village, CancellationToken cancellationToken);

    ValueTask PublishBaseStatusAsync(
        TVillage village,
        TStatus status,
        CancellationToken cancellationToken);

    ValueTask RefreshInboxAsync(CancellationToken cancellationToken);

    ValueTask<VillageStatusCollectionResult<TStatus>> CollectRewardsAsync(
        TVillage village,
        TStatus status,
        CancellationToken cancellationToken);

    ValueTask<TStatus> RefreshOptionalStatusesAsync(
        TVillage village,
        TStatus status,
        CancellationToken cancellationToken);

    ValueTask RefreshDeferredWaitsAsync(TStatus status, CancellationToken cancellationToken);

    ValueTask ReconcileRuntimeItemsAsync(TVillage village, CancellationToken cancellationToken);

    ValueTask<bool> ExecuteReadyTasksAsync(
        TVillage village,
        int collectionAttempts,
        CancellationToken cancellationToken);
}

internal sealed class VillageStatusReactionCoordinator<TVillage, TStatus>
{
    internal async ValueTask<VillageStatusRoundVisitResult> RunAsync(
        TVillage village,
        bool refreshInbox,
        bool inboxStatusChecked,
        IVillageStatusReactionPort<TVillage, TStatus> port,
        CancellationToken cancellationToken)
    {
        var status = await port.ReadBaseStatusAsync(village, cancellationToken).ConfigureAwait(false);
        await port.PublishBaseStatusAsync(village, status, cancellationToken).ConfigureAwait(false);

        if (port.StopRequested)
        {
            return new VillageStatusRoundVisitResult(false, false);
        }

        var inboxCheckedDuringVisit = false;
        if (refreshInbox && !inboxStatusChecked)
        {
            inboxCheckedDuringVisit = true;
            await port.RefreshInboxAsync(cancellationToken).ConfigureAwait(false);
        }

        if (port.StopRequested)
        {
            return new VillageStatusRoundVisitResult(false, inboxCheckedDuringVisit);
        }

        var collection = await port.CollectRewardsAsync(village, status, cancellationToken)
            .ConfigureAwait(false);
        if (!collection.ShouldContinue || port.StopRequested)
        {
            return new VillageStatusRoundVisitResult(false, inboxCheckedDuringVisit);
        }

        status = await port.RefreshOptionalStatusesAsync(
                village,
                collection.Status,
                cancellationToken)
            .ConfigureAwait(false);
        if (port.StopRequested)
        {
            return new VillageStatusRoundVisitResult(false, inboxCheckedDuringVisit);
        }
        await port.RefreshDeferredWaitsAsync(status, cancellationToken).ConfigureAwait(false);
        if (port.StopRequested)
        {
            return new VillageStatusRoundVisitResult(false, inboxCheckedDuringVisit);
        }
        await port.ReconcileRuntimeItemsAsync(village, cancellationToken).ConfigureAwait(false);
        if (port.StopRequested)
        {
            return new VillageStatusRoundVisitResult(false, inboxCheckedDuringVisit);
        }
        var shouldContinue = await port.ExecuteReadyTasksAsync(
                village,
                collection.Attempts,
                cancellationToken)
            .ConfigureAwait(false);
        return new VillageStatusRoundVisitResult(shouldContinue, inboxCheckedDuringVisit);
    }
}

internal sealed class DelegateVillageStatusReactionPort<TVillage, TStatus>(
    Func<TVillage, CancellationToken, ValueTask<TStatus>> readBaseStatusAsync,
    Func<TVillage, TStatus, CancellationToken, ValueTask> publishBaseStatusAsync,
    Func<CancellationToken, ValueTask> refreshInboxAsync,
    Func<TVillage, TStatus, CancellationToken, ValueTask<VillageStatusCollectionResult<TStatus>>> collectRewardsAsync,
    Func<TVillage, TStatus, CancellationToken, ValueTask<TStatus>> refreshOptionalStatusesAsync,
    Func<TStatus, CancellationToken, ValueTask> refreshDeferredWaitsAsync,
    Func<TVillage, CancellationToken, ValueTask> reconcileRuntimeItemsAsync,
    Func<TVillage, int, CancellationToken, ValueTask<bool>> executeReadyTasksAsync,
    Func<bool>? stopRequested = null)
    : IVillageStatusReactionPort<TVillage, TStatus>
{
    public bool StopRequested => stopRequested?.Invoke() == true;

    public ValueTask<TStatus> ReadBaseStatusAsync(TVillage village, CancellationToken cancellationToken) =>
        readBaseStatusAsync(village, cancellationToken);

    public ValueTask PublishBaseStatusAsync(
        TVillage village,
        TStatus status,
        CancellationToken cancellationToken) =>
        publishBaseStatusAsync(village, status, cancellationToken);

    public ValueTask RefreshInboxAsync(CancellationToken cancellationToken) =>
        refreshInboxAsync(cancellationToken);

    public ValueTask<VillageStatusCollectionResult<TStatus>> CollectRewardsAsync(
        TVillage village,
        TStatus status,
        CancellationToken cancellationToken) =>
        collectRewardsAsync(village, status, cancellationToken);

    public ValueTask<TStatus> RefreshOptionalStatusesAsync(
        TVillage village,
        TStatus status,
        CancellationToken cancellationToken) =>
        refreshOptionalStatusesAsync(village, status, cancellationToken);

    public ValueTask RefreshDeferredWaitsAsync(TStatus status, CancellationToken cancellationToken) =>
        refreshDeferredWaitsAsync(status, cancellationToken);

    public ValueTask ReconcileRuntimeItemsAsync(TVillage village, CancellationToken cancellationToken) =>
        reconcileRuntimeItemsAsync(village, cancellationToken);

    public ValueTask<bool> ExecuteReadyTasksAsync(
        TVillage village,
        int collectionAttempts,
        CancellationToken cancellationToken) =>
        executeReadyTasksAsync(village, collectionAttempts, cancellationToken);
}
