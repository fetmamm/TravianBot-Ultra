namespace TbotUltra.Desktop.Services.Orchestration;

/// <summary>
/// Composition-time component set owned exclusively by <see cref="AutomationDesk"/>.
/// It contains no policy or forwarding behavior; the desk exposes cohesive operations.
/// </summary>
internal sealed record AutomationRuntime(
    AutomationPassRuntime Pass,
    AutomationIdlePacing IdlePacingState,
    AutomationNetworkBackoff NetworkBackoff,
    AutomationProxyRecoveryRuntime ProxyRecovery,
    AutomationSessionRuntime Session,
    AutomationQueueEligibility QueueEligibility,
    AutomationQueueSelectionCoordinator QueueSelection,
    ContinuousAutomationForecastCoordinator Forecast,
    ContinuousAutomationDeadlineCoordinator Deadlines,
    ContinuousRuntimeItemPreparation RuntimeItemPreparation,
    ContinuousIdlePacing IdlePacing,
    VillageStatusRoundRuntime VillageStatusRoundState,
    ContinuousVillageStatusRound VillageStatusRound,
    VillageStatusTaskExecutionCoordinator VillageStatusTasks,
    AutomationQueueItemLifecycle QueueItemLifecycle);
