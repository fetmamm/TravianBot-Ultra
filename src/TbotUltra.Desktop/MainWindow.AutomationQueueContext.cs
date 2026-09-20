using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutomationQueueContext(MainWindow owner)
    {
        public AutomationConstructionRequirementContext GetConstructionRequirementContext(QueueItem item)
        {
            var status = owner.ResolveBuildingStatusForQueueItem(item);
            var villageKey = owner.GetQueueItemVillageKey(item);
            var sameVillageFilter = owner.BuildSameVillageQueueFilter(item);
            var sameVillageItems = owner.GetActiveQueueItems()
                .Where(other => other.Id != item.Id)
                .Where(other =>
                {
                    if (villageKey is null)
                    {
                        return sameVillageFilter(other);
                    }

                    var otherKey = owner.GetQueueItemVillageKey(other);
                    return otherKey is null
                        || string.Equals(otherKey, villageKey, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            return new AutomationConstructionRequirementContext(status, sameVillageItems);
        }
    }
}
