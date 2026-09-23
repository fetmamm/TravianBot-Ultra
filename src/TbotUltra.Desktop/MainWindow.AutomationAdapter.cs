using TbotUltra.Desktop.Services.Orchestration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Composes the WPF and Worker adapters used by the deep automation module.
    /// MainWindow only retains the returned projections needed by presentation code.
    /// </summary>
    private sealed class MainWindowAutomationAdapter
    {
        internal MainWindowAutomationAdapter(MainWindow owner, string projectRoot)
        {
            QueueSelection = new AutomationQueueSelectionCoordinator(
                new MainWindowAutomationQueueSelectionPort(owner));
            Forecast = new ContinuousAutomationForecastCoordinator(
                new MainWindowContinuousAutomationForecastPort(owner));
            RuntimeItemPreparation = new ContinuousRuntimeItemPreparation(
                new MainWindowContinuousRuntimeItemPreparationPort(owner));
            IdlePacing = new ContinuousIdlePacing(
                owner._automationIdlePacing,
                new MainWindowContinuousIdlePacingPort(owner));
            VillageStatusRoundRuntime = new VillageStatusRoundRuntime(
                new FileVillageStatusRoundStatePort(projectRoot));
            VillageStatusRound = new ContinuousVillageStatusRound(
                owner._villageStatusRoundCoordinator,
                new MainWindowVillageStatusRoundPort(owner));

            var constructionRequirementGuard = new AutomationConstructionRequirementGuard(
                new MainWindowAutomationConstructionRequirementGuardPort(owner));
            var constructLiveReconciliation = new AutomationConstructLiveReconciliation(
                new MainWindowAutomationConstructLiveReconciliationPort(owner));
            var constructPreflight = new AutomationConstructPreflight(
                new MainWindowAutomationConstructPreflightPort(owner));
            var queueItemPolicies = new AutomationQueueItemPolicies(
                new AutomationQueueItemPreExecution(
                    constructionRequirementGuard,
                    constructLiveReconciliation,
                    constructPreflight),
                new AutomationMissingBuildingUpgradeRecovery(
                    new MainWindowAutomationMissingBuildingUpgradeRecoveryPort(owner)),
                new AutomationQueueItemSuccess(new MainWindowAutomationQueueItemSuccessPort(owner)),
                new AutomationQueueItemFailure(new MainWindowAutomationQueueItemFailurePort(owner)));
            QueueItemLifecycle = new AutomationQueueItemLifecycle(
                new MainWindowAutomationQueueItemLifecyclePort(owner),
                queueItemPolicies);

            var actionExecutor = new AutomationActionExecutor(
                new MainWindowAutomationActionExecutionPort(owner, QueueItemLifecycle));
            ContinuousLoop = new ContextGuardedAutomationModePass(
                owner._accountStore.ActiveAccountName,
                () => owner._botService.BrowserGeneration,
                new ContinuousAutomationPass(
                    new MainWindowContinuousAutomationPassPort(owner, actionExecutor)));
            AutoQueue = new ContextGuardedAutomationModePass(
                owner._accountStore.ActiveAccountName,
                () => owner._botService.BrowserGeneration,
                new AutoQueueAutomationPass(
                    new MainWindowAutoQueueAutomationPassPort(owner, actionExecutor)));
        }

        internal AutomationQueueSelectionCoordinator QueueSelection { get; }

        internal ContinuousAutomationForecastCoordinator Forecast { get; }

        internal ContinuousRuntimeItemPreparation RuntimeItemPreparation { get; }

        internal ContinuousIdlePacing IdlePacing { get; }

        internal VillageStatusRoundRuntime VillageStatusRoundRuntime { get; }

        internal ContinuousVillageStatusRound VillageStatusRound { get; }

        internal AutomationQueueItemLifecycle QueueItemLifecycle { get; }

        internal IAutomationModePassPort ContinuousLoop { get; }

        internal IAutomationModePassPort AutoQueue { get; }
    }
}
