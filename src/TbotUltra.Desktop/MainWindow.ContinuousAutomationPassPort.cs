using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowContinuousAutomationPassPort(
        MainWindow owner,
        AutomationActionExecutor actionExecutor)
        : IContinuousAutomationPassPort
    {
        public BotOptions LoadOptions() => owner.LoadBotOptions();

        public long BeginPass() => owner._automationPassRuntime.BeginContinuousPass();

        public bool TryScheduleAutomaticProxyRecovery(BotOptions options) =>
            owner.TryScheduleAutomaticProxyRecovery(options);

        public TimeSpan NetworkBackoffRemaining => owner._automationNetworkBackoff.Remaining;

        public DateTimeOffset VillageMembershipVerificationNotBeforeUtc =>
            owner._villageMembershipVerificationNotBeforeUtc;

        public DateTimeOffset NextKeepAliveAtUtc => owner._automationSessionRuntime.NextKeepAliveAtUtc;

        public bool PrioritizeDeadlineWorkOnWake
        {
            get => owner._automationPassRuntime.PrioritizeDeadlineWorkOnWake;
            set => owner._automationPassRuntime.PrioritizeDeadlineWorkOnWake = value;
        }

        public ValueTask EnsureChromiumInstalledAsync() =>
            new(owner.EnsureChromiumInstalledAsync());

        public ValueTask<bool> EnsureVillageMembershipVerifiedAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.EnsureVillageMembershipVerifiedBeforeAutomationAsync(options, cancellationToken));

        public bool ConsumeImmediateWorkRequest() =>
            owner._automationPassRuntime.ConsumeImmediateWorkRequest();

        public ValueTask MaybeTakeIdleBreakAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            owner._continuousIdlePacing.MaybeTakeBreakAsync(options, cancellationToken);

        public ValueTask MaybeDoIdleBrowseAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            owner._continuousIdlePacing.MaybeBrowseAsync(options, cancellationToken);

        public ValueTask HonorPendingVillageSwitchAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.HonorPendingVillageSwitchAsync(options, cancellationToken));

        public bool ConsumeForceVillageStatusRoundRequest() =>
            owner._villageStatusRoundRuntime.ConsumeForceRequest();

        public ValueTask MaybeRunVillageStatusRoundAsync(
            BotOptions options,
            CancellationToken cancellationToken,
            bool force) =>
            owner._continuousVillageStatusRound.RunIfDueAsync(
                options,
                cancellationToken,
                force);

        public ValueTask EnsureConstructionStatusAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.EnsureContinuousLoopConstructionStatusAsync(options, cancellationToken));

        public ValueTask MaybeAnalyzeNewVillageAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.MaybeAnalyzeNewVillageDuringContinuousLoopAsync(options, cancellationToken));

        public ValueTask EnsureRuntimeItemsAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            owner._continuousRuntimeItemPreparation.PrepareAsync(options, cancellationToken);

        public ValueTask MaybeCheckInboxAsync(CancellationToken cancellationToken) =>
            new(owner.MaybeCheckInboxDuringContinuousLoopAsync(cancellationToken));

        public QueueItem? SelectNextQueueItem() => owner._automationQueueSelection.Select();

        public void LogSmartSleepBlockedByReadyTask(QueueItem item)
        {
            if (!owner._smartSleepSettings.Enabled)
            {
                return;
            }

            var villageName = NormalizeVillageName(GetQueueItemVillageName(item)) ?? "-";
            var villageKey = owner.GetQueueItemVillageKey(item) ?? villageName;
            owner.AppendLoopPickVerbose(
                $"[smart-sleep] blocked by ready task: group={item.Group}, "
                    + $"task='{item.TaskName}', village='{villageName}'.",
                $"smart-sleep:blocker:{item.Group}:{item.TaskName}:{villageKey}");
        }

        public void MarkActivePass() => owner._automationSessionRuntime.MarkActivePass();

        public ValueTask MaybeKeepBrowserFreshAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.MaybeKeepBrowserFreshDuringContinuousLoopAsync(options, cancellationToken));

        public ContinuousAutomationDeadlineSnapshot ReadDeadlines(BotOptions options)
        {
            var now = DateTimeOffset.UtcNow;
            var relevantItems = owner.GetContinuousLoopRelevantQueueItems();
            var nextQueueDeadline = owner.GetContinuousLoopConsideredGroupsInOrder().Count <= 0
                ? null
                : relevantItems
                    .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                    .Select(item => (DateTimeOffset?)item.NextAttemptAt)
                    .Min();
            DateTimeOffset? nextVillageStatusRound = options.VillageStatusSweepEnabled
                ? owner.GetVillageStatusSweepNextScanUtc()
                : null;
            var forecast = owner._continuousAutomationForecast.Resolve(now);
            var nextConstructionAvailability = forecast.Item?.Group == QueueGroup.Construction
                && forecast.State == ContinuousLoopForecastState.Waiting
                ? forecast.ReadyAtUtc
                : null;
            if (nextConstructionAvailability is DateTimeOffset constructionDeadline
                && (nextQueueDeadline is null || constructionDeadline < nextQueueDeadline.Value))
            {
                owner.AppendLoopPickVerbose(
                    $"[loop-pick:verbose] next wake follows humanized construction availability at "
                        + $"'{owner.FormatQueueServerTime(constructionDeadline)}' "
                        + $"for {forecast.Item?.DisplayName ?? forecast.Item?.TaskName}",
                    $"construction-wake:{forecast.Item?.Id}:{constructionDeadline.UtcTicks}");
            }

            var smartSleepItems = relevantItems
                .Where(item => owner._automationPassRuntime.SmartSleepDeadlineGroups.Contains(item.Group))
                .ToList();
            var smartSleepForecast = owner._continuousAutomationForecast.Resolve(
                now,
                queueItemsOverride: smartSleepItems);
            DateTimeOffset? smartSleepConstructionAvailability = null;
            if (smartSleepForecast.Item?.Group == QueueGroup.Construction
                && smartSleepForecast.State == ContinuousLoopForecastState.Waiting)
            {
                smartSleepConstructionAvailability = smartSleepForecast.ReadyAtUtc;
            }

            return new ContinuousAutomationDeadlineSnapshot(
                nextQueueDeadline,
                nextConstructionAvailability,
                nextVillageStatusRound,
                smartSleepItems,
                owner._automationPassRuntime.SmartSleepDeadlineGroups,
                smartSleepConstructionAvailability);
        }

        public bool TryRequestSmartSleep(DateTimeOffset? trustedDeadlineUtc) =>
            owner.TryRequestSmartSleep(trustedDeadlineUtc);

        public bool ShouldPublishIdleHeartbeat(TimeSpan interval) =>
            owner._automationSessionRuntime.ShouldPublishIdleHeartbeat(interval);

        public void Log(string message) => owner.AppendLog(message);

        public string FormatException(Exception exception) => FormatExceptionForLog(exception);

        public ValueTask HoldAccountAutomationAsync(AccountAccessException exception) =>
            new(owner.HoldAccountAutomationAsync(exception));

        public ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationCandidate action,
            CancellationToken cancellationToken) =>
            actionExecutor.ExecuteAsync(AutomationRunMode.ContinuousLoop, action, cancellationToken);
    }
}
