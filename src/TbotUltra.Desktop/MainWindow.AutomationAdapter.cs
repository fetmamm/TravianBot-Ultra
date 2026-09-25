using TbotUltra.Desktop.Services.Orchestration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Composes the WPF and Worker adapters used by the deep automation module.
    /// MainWindow retains only the resulting AutomationDesk; policy modules remain desk-owned.
    /// </summary>
    private static class MainWindowAutomationAdapter
    {
        internal static AutomationDesk Create(
            MainWindow owner,
            string projectRoot,
            LoopController loopController)
        {
            var passRuntime = new AutomationPassRuntime();
            var idlePacingState = new AutomationIdlePacing();
            var networkBackoff = new AutomationNetworkBackoff();
            var proxyRecovery = new AutomationProxyRecoveryRuntime();
            var sessionRuntime = new AutomationSessionRuntime();
            var villageStatusRoundCoordinator = new VillageStatusRoundCoordinator();
            var queueEligibility = new AutomationQueueEligibility(
                owner._villageSettingsStore,
                () => owner.CurrentGoldClubAvailability,
                new MainWindowAutomationQueueEligibilityPort(owner));
            var queueSelection = new AutomationQueueSelectionCoordinator(
                new MainWindowAutomationQueueSelectionPort(owner, queueEligibility, passRuntime));
            var forecast = new ContinuousAutomationForecastCoordinator(
                new MainWindowContinuousAutomationForecastPort(owner, queueEligibility, queueSelection));
            var runtimeItemPreparation = new ContinuousRuntimeItemPreparation(
                new MainWindowContinuousRuntimeItemPreparationPort(owner, queueEligibility));
            var idlePacing = new ContinuousIdlePacing(
                idlePacingState,
                new MainWindowContinuousIdlePacingPort(owner, passRuntime));
            var villageStatusRoundRuntime = new VillageStatusRoundRuntime(
                new FileVillageStatusRoundStatePort(projectRoot));
            var villageStatusRound = new ContinuousVillageStatusRound(
                villageStatusRoundCoordinator,
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
            var queueItemLifecycle = new AutomationQueueItemLifecycle(
                new MainWindowAutomationQueueItemLifecyclePort(
                    owner,
                    queueEligibility,
                    networkBackoff),
                queueItemPolicies);
            var villageStatusTasks = new VillageStatusTaskExecutionCoordinator(
                new MainWindowVillageStatusTaskExecutionPort(owner),
                passRuntime,
                sessionRuntime,
                queueSelection,
                villageStatusRound,
                queueItemLifecycle);
            var actionExecutor = new AutomationActionExecutor(
                new MainWindowAutomationActionExecutionPort(owner, queueItemLifecycle, passRuntime));
            var continuousPassPort = new MainWindowContinuousAutomationPassPort(owner, actionExecutor);
            var deadlines = new ContinuousAutomationDeadlineCoordinator(
                continuousPassPort,
                passRuntime,
                forecast);
            var continuousPassRuntime = new ContinuousAutomationPassRuntime(
                passRuntime,
                networkBackoff,
                sessionRuntime,
                queueSelection,
                deadlines,
                runtimeItemPreparation,
                idlePacing,
                villageStatusRoundRuntime,
                villageStatusRound);
            var autoQueueRuntime = new AutoQueueAutomationPassRuntime(
                passRuntime,
                queueSelection,
                deadlines,
                villageStatusRound);

            var runtime = new AutomationRuntime(
                passRuntime,
                idlePacingState,
                networkBackoff,
                proxyRecovery,
                sessionRuntime,
                queueEligibility,
                queueSelection,
                forecast,
                deadlines,
                runtimeItemPreparation,
                idlePacing,
                villageStatusRoundRuntime,
                villageStatusRound,
                villageStatusTasks,
                queueItemLifecycle);

            var continuousLoop = new ContextGuardedAutomationModePass(
                owner._accountStore.ActiveAccountName,
                () => owner._botService.BrowserGeneration,
                new ContinuousAutomationPass(
                    continuousPassPort,
                    continuousPassRuntime));
            var autoQueue = new ContextGuardedAutomationModePass(
                owner._accountStore.ActiveAccountName,
                () => owner._botService.BrowserGeneration,
                new AutoQueueAutomationPass(
                    new MainWindowAutoQueueAutomationPassPort(owner, actionExecutor),
                    autoQueueRuntime));

            return new AutomationDesk(
                loopController,
                continuousLoop,
                autoQueue,
                runtime: runtime);
        }
    }
}
