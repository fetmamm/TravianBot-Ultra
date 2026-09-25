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
        : IContinuousAutomationPassPort, IContinuousAutomationDeadlinePort
    {
        public BotOptions LoadOptions() => owner.LoadBotOptions();

        public bool TryScheduleAutomaticProxyRecovery(BotOptions options) =>
            owner.TryScheduleAutomaticProxyRecovery(options);

        public DateTimeOffset VillageMembershipVerificationNotBeforeUtc =>
            owner._villageMembershipVerificationNotBeforeUtc;

        public ValueTask EnsureChromiumInstalledAsync() =>
            new(owner.EnsureChromiumInstalledAsync());

        public ValueTask<bool> EnsureVillageMembershipVerifiedAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.EnsureVillageMembershipVerifiedBeforeAutomationAsync(options, cancellationToken));

        public ValueTask HonorPendingVillageSwitchAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.HonorPendingVillageSwitchAsync(options, cancellationToken));

        public ValueTask EnsureConstructionStatusAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.EnsureContinuousLoopConstructionStatusAsync(options, cancellationToken));

        public ValueTask MaybeAnalyzeNewVillageAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.MaybeAnalyzeNewVillageDuringContinuousLoopAsync(options, cancellationToken));

        public ValueTask MaybeCheckInboxAsync(CancellationToken cancellationToken) =>
            new(owner.MaybeCheckInboxDuringContinuousLoopAsync(cancellationToken));

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

        public ValueTask MaybeKeepBrowserFreshAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.MaybeKeepBrowserFreshDuringContinuousLoopAsync(options, cancellationToken));

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public IReadOnlyList<QueueItem> GetRelevantQueueItems() =>
            owner.GetContinuousLoopRelevantQueueItems();

        public bool HasConsideredGroups => owner.GetContinuousLoopConsideredGroupsInOrder().Count > 0;

        public DateTimeOffset? GetNextVillageStatusRoundUtc(BotOptions options) =>
            options.VillageStatusSweepEnabled ? owner.GetVillageStatusSweepNextScanUtc() : null;

        public bool WakeWhenConstructionQueueClears => owner._smartSleepWakeWhenConstructionQueueClears;

        public bool? TravianPlusActive => owner._travianPlusActive;

        public VillageStatus? GetBuildingStatus(QueueItem item) =>
            owner.ResolveBuildingStatusForQueueItem(item);

        public string GetVillageName(QueueItem item) =>
            NormalizeVillageName(MainWindow.GetQueueItemVillageName(item)) ?? "-";

        public string FormatServerTime(DateTimeOffset value) => owner.FormatQueueServerTime(value);

        public void LogVerbose(string message, string key) => owner.AppendLoopPickVerbose(message, key);

        public bool TryRequestSmartSleep(DateTimeOffset? trustedDeadlineUtc) =>
            owner.TryRequestSmartSleep(trustedDeadlineUtc);

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
