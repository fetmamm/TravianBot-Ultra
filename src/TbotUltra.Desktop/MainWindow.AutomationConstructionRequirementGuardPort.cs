using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutomationConstructionRequirementGuardPort(MainWindow owner)
        : IAutomationConstructionRequirementGuardPort
    {
        public AutomationConstructionRequirementContext GetContext(QueueItem item)
        {
            var context = owner.ResolveConstructRequirementContextForQueueItem(item);
            return new AutomationConstructionRequirementContext(context.Status, context.SameVillageItems);
        }
        public bool MarkDeferred(Guid itemId, TimeSpan delay) =>
            owner._botService.MarkQueueItemDeferred(itemId, delay);
        public bool PatchDeferred(
            Guid itemId,
            IReadOnlyDictionary<string, string> values,
            IReadOnlyCollection<string> keysToRemove) =>
            owner._botService.PatchDeferredQueueItem(itemId, values, keysToRemove);
        public bool MarkPermanentlyFailed(Guid itemId) =>
            owner._botService.MarkQueueItemPermanentlyFailed(itemId);
        public IReadOnlyList<QueueItem> GetQueueItems() => owner._botService.GetQueueItemsForDisplay();
        public bool UpdatePendingQueueItem(
            Guid itemId,
            Dictionary<string, string> payload,
            int priority,
            TimeSpan delay) =>
            owner._botService.UpdatePendingQueueItem(itemId, payload, priority, delay);
        public QueueItem Enqueue(
            string taskName,
            Dictionary<string, string> payload,
            int priority,
            int maxRetries) =>
            owner._botService.Enqueue(taskName, payload, priority, maxRetries);
        public void RequestQueueUiRefresh(Guid? selectedItemId = null) =>
            owner.RequestQueueUiRefresh(selectedItemId);
        public ValueTask RefreshVillageActivityIndicatorsAsync() =>
            new(owner.Dispatcher.InvokeAsync(owner.RefreshVillageActivityIndicatorsOnDashboard).Task);
        public void RaisePermanentFailureAlarm(QueueItem item, string message) =>
            owner.RaiseAlarmIfQueueItemPermanentlyFailed(item, message);
        public void Log(string message) => owner.AppendLog(message);
    }
}
