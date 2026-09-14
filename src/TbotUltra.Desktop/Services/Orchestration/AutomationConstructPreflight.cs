using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal readonly record struct ConstructPreflightObservation(
    bool CanUseCache,
    VillageStatus? FreshStatus);

internal interface IAutomationConstructPreflightPort
{
    string? GetTargetVillageName(QueueItem item);
    string? GetTargetVillageUrl(QueueItem item);
    string? GetTargetVillageKey(QueueItem item);
    ValueTask<VillageStatus> ReadLiveVillageStatusAsync(
        BotOptions options,
        string? villageName,
        string? villageUrl,
        CancellationToken cancellationToken);
    void ApplyLiveVillageStatus(VillageStatus status, string? villageName);
    VillageStatus? GetCachedBuildingStatus(QueueItem item);
    bool? TravianPlusActive { get; }
    void ClearLoginFillForFullSlots(VillageStatus status, string? villageKey, string source);
    bool MarkDeferred(Guid itemId, TimeSpan delay);
    bool PatchDeferred(
        Guid itemId,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyCollection<string> keysToRemove,
        TimeSpan delay);
    string? GetVillageName(QueueItem item);
    string FormatServerTime(DateTimeOffset value);
    ValueTask RefreshVillageActivityIndicatorsAsync();
    void Log(string message);
}

internal sealed class AutomationConstructPreflight(
    IAutomationConstructPreflightPort port,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal async ValueTask<ConstructPreflightObservation> RefreshTargetStatusAsync(
        QueueItem item,
        BotOptions options,
        CancellationToken cancellationToken)
    {
        if (!IsConstruct(item))
        {
            return new ConstructPreflightObservation(true, null);
        }

        var targetVillageName = port.GetTargetVillageName(item);
        var targetVillageUrl = port.GetTargetVillageUrl(item);
        if (targetVillageName is null && string.IsNullOrWhiteSpace(targetVillageUrl))
        {
            return new ConstructPreflightObservation(true, null);
        }

        try
        {
            port.Log(
                "[construction-preflight] reading live dorf1/dorf2 for construct target village "
                + $"'{targetVillageName ?? targetVillageUrl}' before requirement guard.");
            var status = await port.ReadLiveVillageStatusAsync(
                options,
                targetVillageName,
                targetVillageUrl,
                cancellationToken);
            port.ApplyLiveVillageStatus(status, targetVillageName);
            port.Log(
                $"[construction-preflight] cached live target village "
                + $"'{targetVillageName ?? status.ActiveVillage}': fields={status.ResourceFields.Count}, "
                + $"buildings={status.Buildings.Count}.");
            return new ConstructPreflightObservation(true, status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            port.Log(
                $"[construction-preflight] live target village read failed before construct guard: "
                + $"{exception.Message}. Skipping cached requirement guard; worker will validate the live "
                + "construct page.");
            return new ConstructPreflightObservation(false, null);
        }
    }

    internal async ValueTask<bool> TryHandleQueueFullAsync(
        QueueItem item,
        string logPrefix,
        Stopwatch timer)
    {
        if (!IsConstruct(item))
        {
            return false;
        }

        var status = port.GetCachedBuildingStatus(item);
        if (status is null
            || ConstructionQueueState.ResolveAvailabilityForItem(status, port.TravianPlusActive, item)
                != ConstructionQueueAvailability.Full)
        {
            return false;
        }

        port.ClearLoginFillForFullSlots(
            status,
            port.GetTargetVillageKey(item),
            "construction preflight");

        var now = _timeProvider.GetUtcNow();
        var delay = ConstructionQueueState.ResolveQueueFullRetryDelay(
                status,
                port.TravianPlusActive,
                item,
                now)
            ?? TimeSpan.FromSeconds(60);
        var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonQueueFull,
            [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                ConstructionQueueState.CurrentDeferClassificationVersion,
        };
        payload.Remove(BotOptionPayloadKeys.RequirementDeferCount);
        payload.Remove(BotOptionPayloadKeys.ConstructionLoginFill);
        payload.Remove(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds);

        if (!port.MarkDeferred(item.Id, delay))
        {
            port.Log(
                $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + "live full build queue was detected before requirement repair, but defer could not be persisted");
            return false;
        }

        if (port.PatchDeferred(
                item.Id,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonQueueFull,
                    [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                        ConstructionQueueState.CurrentDeferClassificationVersion,
                },
                [
                    BotOptionPayloadKeys.RequirementDeferCount,
                    BotOptionPayloadKeys.ConstructionLoginFill,
                    BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds,
                ],
                delay))
        {
            item.Payload = payload;
        }
        else
        {
            port.Log(
                $"[construction-queue] preflight queue-full payload persistence failed "
                + $"id={item.Id} task='{item.TaskName}'");
        }

        var villageName = port.GetVillageName(item) ?? status.ActiveVillage ?? "-";
        var retryAt = now + delay;
        var activeCount = ConstructionQueueState.ResolveCurrentActiveConstructions(status, now).Count;
        port.Log(
            $"[construction-preflight] stopped before requirement repair id={item.Id} "
            + $"village='{villageName}' active={activeCount} waitSeconds={delay.TotalSeconds:F0}; "
            + "queue was not modified.");
        port.Log(
            $"[construction] BUILD QUEUE FULL village='{villageName}'. "
            + "Construction order is held until the first active construction finishes. "
            + $"Next retry: {port.FormatServerTime(retryAt)} (in {delay.TotalSeconds:F0}s).");
        await port.RefreshVillageActivityIndicatorsAsync();
        return true;
    }

    private static bool IsConstruct(QueueItem item) =>
        string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase);
}
