using System;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private async Task<(bool BuildingsStatusRead, bool StorageStatusRead)> RefreshConstructionStatusAfterBuildingMutationAsync(
        QueueItem item,
        CancellationToken cancellationToken)
    {
        // A confirmed construct/upgrade redirects to Dorf2. Reuse that already-loaded overview so the
        // next level can open directly from it instead of forcing Dorf2 -> Dorf1 -> Dorf2. The quick read
        // is accepted only when all 22 slots, an authoritative construction queue and the exact target
        // village coordinates are present. Any uncertainty retains the old full refresh as the fallback.
        try
        {
            var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
            var currentStatus = await _botService.ReadCurrentBuildingOverviewStatusAsync(
                options,
                AppendLog,
                cancellationToken);
            var expectedVillageKey = GetQueueItemVillageKey(item);
            if (!ConstructionMutationRefreshPolicy.CanUseCurrentDorf2Snapshot(currentStatus, expectedVillageKey))
            {
                throw new InvalidOperationException(
                    $"Current Dorf2 snapshot was incomplete or belonged to another village " +
                    $"(expected={expectedVillageKey ?? "-"}, " +
                    $"observed={ResolveStatusVillageKey(currentStatus) ?? "-"}, " +
                    $"slots={currentStatus.Buildings.Count}, " +
                    $"queueAuthoritative={currentStatus.ActiveConstructionsFromOverview}).");
            }

            await Dispatcher.InvokeAsync(() =>
            {
                var existing = ResolveBuildingStatusForQueueItem(item);
                var merged = existing is null
                    ? currentStatus
                    : ConstructionMutationRefreshPolicy.MergeCurrentDorf2Snapshot(existing, currentStatus);
                SetActiveWorkingVillageFromStatus(merged);
                CacheVillageStatus(merged);
                ReconcilePendingBuildingQueueWithLiveStatus(merged);
                if (!IsStatusForSelectedVillage(merged))
                {
                    return;
                }

                _lastBuildingStatus = merged;
                ApplyVillageStatusToUi(merged);
                PopulateBuildingsTab(merged);
            });

            AppendLog(
                $"[construction-refresh] reused authoritative current Dorf2 after '{item.TaskName}'; " +
                "skipped Dorf1 navigation.");
            return (BuildingsStatusRead: true, StorageStatusRead: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog(
                $"[construction-refresh] current Dorf2 refresh failed ({ex.Message}); " +
                "falling back to full Dorf1+Dorf2 status.");
            await RefreshConstructionStatusAsync(cancellationToken);
            return (BuildingsStatusRead: true, StorageStatusRead: false);
        }
    }

    // dorf1 counterpart of RefreshConstructionStatusAfterBuildingMutationAsync: re-reads the just-worked
    // village's resource fields (dorf1) and caches them keyed by that village's coordinates, then repaints
    // the resource UI only when it is the selected village. The browser is already on the worked village
    // (forceCurrentVillage), so this stays village-specific — a resource upgrade in village B never writes
    // village A's rows. Returns true when the read succeeded.
    private async Task<bool> RefreshResourceStatusAfterResourceMutationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
            var status = await ReadVillageStatusWithRetryAsync(
                options,
                cancellationToken,
                resourceOnly: true,
                forceCurrentVillage: true);
            await Dispatcher.InvokeAsync(() =>
            {
                SetActiveWorkingVillageFromStatus(status);
                CacheVillageStatus(status);
                if (IsStatusForSelectedVillage(status))
                {
                    ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
                }
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"[resource-refresh] full dorf1 read after resource task failed: {ex.Message}");
            return false;
        }
    }

    private bool PatchDeferredQueuePayload(
        QueueItem item,
        Dictionary<string, string> updatedPayload,
        TimeSpan? delay = null)
    {
        var keysToRemove = item.Payload.Keys
            .Where(key => !updatedPayload.ContainsKey(key))
            .ToArray();
        return _botService.PatchDeferredQueueItem(item.Id, updatedPayload, keysToRemove, delay);
    }

}
