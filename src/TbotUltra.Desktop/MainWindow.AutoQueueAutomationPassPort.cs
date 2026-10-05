using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutoQueueAutomationPassPort(
        MainWindow owner,
        AutomationActionExecutor actionExecutor)
        : IAutoQueueAutomationPassPort
    {
        public BotOptions LoadOptionsWithSelectedVillage() =>
            owner.ApplySelectedVillageToOptions(owner.LoadBotOptions());

        public ValueTask HonorPendingVillageSwitchAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.HonorPendingVillageSwitchAsync(options, cancellationToken));

        public bool TryRequestSmartSleep(DateTimeOffset? trustedDeadlineUtc) =>
            owner.TryRequestSmartSleep(trustedDeadlineUtc);

        public ValueTask MaybeParkOnDorf2WhileIdleAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.MaybeParkOnDorf2WhileIdleAsync(options, cancellationToken));

        public void Log(string message) => owner.AppendLog(message);

        public ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationCandidate action,
            CancellationToken cancellationToken) =>
            actionExecutor.ExecuteAsync(AutomationRunMode.AutoQueue, action, cancellationToken);
    }
}
