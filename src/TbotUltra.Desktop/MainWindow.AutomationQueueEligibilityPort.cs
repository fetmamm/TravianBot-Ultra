using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutomationQueueEligibilityPort(MainWindow owner)
        : IAutomationQueueEligibilityPort
    {
        public string? GetVillageKey(QueueItem item) => owner.GetQueueItemVillageKey(item);
        public string? GetVillageName(QueueItem item) => MainWindow.GetQueueItemVillageName(item);
    }
}
