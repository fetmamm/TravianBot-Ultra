using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services.Orchestration;

internal readonly record struct QueueItemGuardResult(
    bool Handled,
    bool FreshBuildingsRefreshDone)
{
    internal static QueueItemGuardResult NotHandled { get; } = new(false, false);
}

internal interface IAutomationQueueItemLifecyclePort
{
    bool IsAllowedByAutomationSettings(QueueItem item);
    IDisposable BeginExecutionScope(QueueItem item);
    BotOptions LoadCurrentOptions();
    void MarkDueConstructionForPreSleepFill(QueueItem item);
    void RefreshConstructFasterPayloadForExecution(QueueItem item);
    bool MarkRunning(Guid itemId);
    void RefreshQueueUi(Guid itemId);
    void SetActiveAutomationTask(string? taskName);
    void SetActiveFunctionExecution(string? displayName);
    ValueTask<QueueItemGuardResult> RunPreExecutionGuardsAsync(
        QueueItem item,
        BotOptions options,
        string logPrefix,
        Stopwatch timer,
        CancellationToken cancellationToken);
    BotOptions ApplyQueueItemOptions(BotOptions options, QueueItem item);
    CancellationToken BeginDemolitionOperation(QueueItem item, CancellationToken cancellationToken);
    ValueTask<BotTaskExecutionResult> ExecuteWorkerAsync(
        BotOptions options,
        QueueItem item,
        CancellationToken cancellationToken);
    ValueTask<bool> TryRecoverMissingBuildingUpgradeAsync(
        QueueItem item,
        BotOptions options,
        BotTaskExecutionResult executionResult,
        string logPrefix,
        Stopwatch timer,
        CancellationToken cancellationToken);
    ValueTask<bool> HandleSucceededAsync(
        QueueItem item,
        BotOptions options,
        BotTaskExecutionResult executionResult,
        CancellationToken cancellationToken);
    bool IsLoadBuildingsSnapshot(QueueItem item);
    ValueTask LoadBuildingsSnapshotAsync(CancellationToken cancellationToken);
    void MarkNetworkConnectionHealthy();
    void PublishLastScan();
    bool IsDemolition(QueueItem item);
    bool WasDemolitionStopped(Guid itemId);
    bool MarkDeferred(Guid itemId, TimeSpan delay);
    TimeSpan NextNetworkRetryDelay();
    void MarkNetworkUnavailable(TimeSpan retryDelay);
    ValueTask HoldAccountAutomationAsync(AccountAccessException exception);
    ValueTask HandleUnexpectedTravianLanguageAsync(UnexpectedTravianLanguageException exception);
    ValueTask<bool> HandleTaskSpecificFailureAsync(
        QueueItem item,
        Exception exception,
        string logPrefix,
        Stopwatch timer,
        AutomationRunMode mode);
    void CompleteDemolitionOperation(Guid itemId);
    ValueTask RestoreBuildingsSnapshotAsync(CancellationToken cancellationToken);
    void Log(string message);
}

internal sealed class AutomationQueueItemLifecycle(IAutomationQueueItemLifecyclePort port)
{
    internal async ValueTask<bool> ExecuteAsync(
        QueueItem item,
        BotOptions options,
        string logPrefix,
        AutomationRunMode mode,
        CancellationToken cancellationToken)
    {
        if (!port.IsAllowedByAutomationSettings(item))
        {
            port.Log(
                $"{logPrefix} SKIP task={item.TaskName}, id={item.Id} "
                + "because automation is disabled for its village.");
            return true;
        }

        using var executionScope = port.BeginExecutionScope(item);
        var timer = Stopwatch.StartNew();
        options = RefreshHeroOptions(item, options);
        port.MarkDueConstructionForPreSleepFill(item);
        port.RefreshConstructFasterPayloadForExecution(item);
        port.MarkRunning(item.Id);
        port.RefreshQueueUi(item.Id);
        port.SetActiveAutomationTask(item.TaskName);
        port.SetActiveFunctionExecution(
            string.IsNullOrWhiteSpace(item.DisplayName) ? item.TaskName : item.DisplayName);
        var freshBuildingsRefreshDone = false;

        try
        {
            if (!port.IsAllowedByAutomationSettings(item))
            {
                port.MarkDeferred(item.Id, TimeSpan.Zero);
                port.Log(
                    $"{logPrefix} SKIP task={item.TaskName}, id={item.Id} "
                    + "because automation was disabled for its village before execution.");
                return true;
            }

            var guard = await port.RunPreExecutionGuardsAsync(
                item,
                options,
                logPrefix,
                timer,
                cancellationToken);
            if (guard.Handled)
            {
                freshBuildingsRefreshDone = guard.FreshBuildingsRefreshDone;
                return true;
            }

            var effectiveOptions = port.ApplyQueueItemOptions(options, item);
            var executionToken = port.IsDemolition(item)
                ? port.BeginDemolitionOperation(item, cancellationToken)
                : cancellationToken;
            var executionResult = await port.ExecuteWorkerAsync(effectiveOptions, item, executionToken);
            if (await port.TryRecoverMissingBuildingUpgradeAsync(
                    item,
                    options,
                    executionResult,
                    logPrefix,
                    timer,
                    cancellationToken))
            {
                return true;
            }

            freshBuildingsRefreshDone = await port.HandleSucceededAsync(
                item,
                options,
                executionResult,
                cancellationToken);
            if (port.IsLoadBuildingsSnapshot(item))
            {
                await port.LoadBuildingsSnapshotAsync(cancellationToken);
            }

            port.Log(FormatSuccessLog(logPrefix, timer, item, mode));
            port.MarkNetworkConnectionHealthy();
            if (mode == AutomationRunMode.ContinuousLoop)
            {
                port.PublishLastScan();
            }

            return true;
        }
        catch (OperationCanceledException) when (
            port.IsDemolition(item) && port.WasDemolitionStopped(item.Id))
        {
            port.Log(
                $"{logPrefix} STOPPED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + "canceled before the Official demolish click");
            return false;
        }
        catch (OperationCanceledException)
        {
            port.MarkDeferred(item.Id, TimeSpan.Zero);
            port.Log(
                $"{logPrefix} PAUSED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + "queued item kept for retry");
            return false;
        }
        catch (Exception ex)
        {
            return await HandleFailureAsync(item, ex, logPrefix, timer, mode);
        }
        finally
        {
            if (port.IsDemolition(item))
            {
                port.CompleteDemolitionOperation(item.Id);
            }
            port.SetActiveAutomationTask(null);
            port.SetActiveFunctionExecution(null);
            port.RefreshQueueUi(item.Id);
            if (!cancellationToken.IsCancellationRequested
                && mode == AutomationRunMode.AutoQueue
                && IsBuildingMutationTask(item.TaskName)
                && !freshBuildingsRefreshDone)
            {
                try
                {
                    await port.RestoreBuildingsSnapshotAsync(cancellationToken);
                }
                catch
                {
                    // The UI keeps its previous state when the last-known snapshot cannot be restored.
                }
            }
        }
    }

    private async ValueTask<bool> HandleFailureAsync(
        QueueItem item,
        Exception exception,
        string logPrefix,
        Stopwatch timer,
        AutomationRunMode mode)
    {
        if (exception is AccountAccessException accountAccessException)
        {
            port.MarkDeferred(item.Id, TimeSpan.Zero);
            await port.HoldAccountAutomationAsync(accountAccessException);
            port.Log(
                $"{logPrefix} STOPPED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + "account requires manual review; queued item kept");
            return false;
        }

        if (AutomationNetworkBackoff.IsTransientConnectionFailure(exception))
        {
            var retryDelay = port.NextNetworkRetryDelay();
            port.MarkNetworkUnavailable(retryDelay);
            if (port.MarkDeferred(item.Id, retryDelay))
            {
                port.Log(
                    $"{logPrefix} TRANSIENT {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                    + $"slow/unavailable page; safe retry in {retryDelay.TotalSeconds:F0}s without consuming retries");
                return true;
            }
        }

        if (BrowserFailureClassifier.IsTargetCrash(exception))
        {
            var retryDelay = TimeSpan.FromSeconds(15);
            if (port.MarkDeferred(item.Id, retryDelay))
            {
                port.Log(
                    $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                    + $"browser target crashed; fresh session retry in {retryDelay.TotalSeconds:F0}s");
                return true;
            }
        }

        // Official demolition replaces the current page context. If that navigation race escapes
        // Worker confirmation, retry without consuming the functional retry budget.
        if (port.IsDemolition(item)
            && BrowserFailureClassifier.IsTransientNavigation(exception))
        {
            var retryDelay = TimeSpan.FromSeconds(15);
            if (port.MarkDeferred(item.Id, retryDelay))
            {
                port.Log(
                    $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                    + "demolition page changed while confirming the submitted step; "
                    + $"safe retry in {retryDelay.TotalSeconds:F0}s without consuming retries");
                return true;
            }
        }

        if (exception is UnexpectedTravianLanguageException languageException)
        {
            port.MarkDeferred(item.Id, TimeSpan.Zero);
            port.Log(
                $"{logPrefix} PAUSED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + "Travian language must be English before automation can continue.");
            await port.HandleUnexpectedTravianLanguageAsync(languageException);
            return false;
        }

        // A runtime item with maxRetries=0 would otherwise immediately requeue this programming error.
        if (exception is InvalidOperationException invalidOperation
            && invalidOperation.Message.Contains("different thread owns it", StringComparison.OrdinalIgnoreCase))
        {
            var retryDelay = TimeSpan.FromMinutes(30);
            if (port.MarkDeferred(item.Id, retryDelay))
            {
                port.Log(
                    $"ALARM: task '{item.TaskName}' hit a UI-thread access error "
                    + $"({logPrefix}, {timer.Elapsed.TotalSeconds:F1}s). Deferred "
                    + $"{retryDelay.TotalMinutes:F0} min and will retry — something is wrong, please check.");
                return true;
            }
        }

        return await port.HandleTaskSpecificFailureAsync(
            item,
            exception,
            logPrefix,
            timer,
            mode);
    }

    private BotOptions RefreshHeroOptions(QueueItem item, BotOptions options)
    {
        if (!string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                item.TaskName,
                "spend_hero_attribute_points",
                StringComparison.OrdinalIgnoreCase))
        {
            return options;
        }

        var current = port.LoadCurrentOptions();
        options = options with
        {
            HeroStatPriority = current.HeroStatPriority,
            HeroStatMaximums = current.HeroStatMaximums,
        };
        return string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase)
            ? options with
            {
                HeroMinHpForAdventure = current.HeroMinHpForAdventure,
                HeroAutoRevive = current.HeroAutoRevive,
                HeroAutoAssignPoints = current.HeroAutoAssignPoints,
                HeroAutoUseOintments = current.HeroAutoUseOintments,
                HeroOintmentTargetHpPercent = current.HeroOintmentTargetHpPercent,
                HeroAdventurePickOrder = current.HeroAdventurePickOrder,
                HeroContinuousAdventures = current.HeroContinuousAdventures,
            }
            : options;
    }

    private static string FormatSuccessLog(
        string logPrefix,
        Stopwatch timer,
        QueueItem item,
        AutomationRunMode mode)
    {
        return mode == AutomationRunMode.ContinuousLoop
            ? $"{logPrefix} OK {timer.Elapsed.TotalSeconds:F1}s | queue:{item.TaskName}"
            : $"{logPrefix} OK {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName}";
    }

    private static bool IsBuildingMutationTask(string? taskName) =>
        string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "demolish_building_to_level", StringComparison.OrdinalIgnoreCase);
}
