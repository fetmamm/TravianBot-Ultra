using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private Guid EnsureHeroRallyPointRepairQueued(
        QueueItem heroItem,
        HeroRallyPointRepairRequest request)
    {
        var queueItems = _botService.GetQueueItemsForDisplay();
        var plan = HeroRallyPointRepairPlanner.Plan(request, heroItem.Id, queueItems);
        if (plan.ExistingQueueItemId is Guid existingId)
        {
            AppendLog(
                $"[hero] Rally Point repair already queued for '{request.VillageName}' "
                + $"(id={existingId}); no duplicate was added.");
            return existingId;
        }

        var options = LoadBotOptions();
        ApplyConstructFasterSettingsToPayload(plan.Payload, options, plan.VillageKey, request.VillageName);
        plan.Payload[BotOptionPayloadKeys.NpcTradeEnabled] =
            IsNpcTradeEnabledForVillageKey(plan.VillageKey) ? "true" : "false";

        var maxPriority = queueItems.Select(item => item.Priority).DefaultIfEmpty(heroItem.Priority).Max();
        var priority = maxPriority == int.MaxValue ? int.MaxValue : maxPriority + 1;
        var created = _botService.Enqueue("construct_building", plan.Payload, priority, maxRetries: 3);
        AppendLog(
            $"[hero] queued Rally Point level 1 in hero home village '{request.VillageName}' "
            + $"(slot=39, id={created.Id}, priority={priority}).");
        RequestContinuousAutomationWake();
        RequestQueueUiRefresh(created.Id);
        return created.Id;
    }
}
