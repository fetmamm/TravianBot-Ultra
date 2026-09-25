using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowVillageStatusTaskExecutionPort(MainWindow owner)
        : IVillageStatusTaskExecutionPort
    {
        public string? ActiveVillageKey => owner._activeWorkingVillageKey;
        public string? ActiveVillageName => owner._activeWorkingVillageName;
        public string? ResolveCanonicalVillageKey(string? villageKey) =>
            owner._villageSettingsStore.ResolveCanonicalKey(villageKey);
        public string? GetVillageKey(QueueItem item) => owner.GetQueueItemVillageKey(item);
        public string? GetVillageName(QueueItem item) => GetQueueItemVillageName(item);
        public ValueTask DelayBeforeTaskAsync(BotOptions options, CancellationToken cancellationToken) =>
            new(ActionPacer.FromOptions(options, owner.AppendLog).DelayAsync(
                options.ActionPacingTaskMinSeconds,
                options.ActionPacingTaskMaxSeconds,
                cancellationToken,
                "Village scan: before task"));
        public ValueTask ApplyPostTaskCooldownAsync(
            QueueItem item,
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.ApplyPostTaskCooldownAsync(item, options, cancellationToken));
        public void LogRomanLoginFillOutcome(string villageKey, string villageName) =>
            owner.LogRomanLoginFillOutcome(villageKey, villageName);
        public void Log(string message) => owner.AppendLog(message);
    }
}
