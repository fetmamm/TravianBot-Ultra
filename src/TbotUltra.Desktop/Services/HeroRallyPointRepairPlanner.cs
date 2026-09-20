using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using System.Text.RegularExpressions;

namespace TbotUltra.Desktop.Services;

public sealed record HeroRallyPointRepairPlan(
    string VillageKey,
    Dictionary<string, string> Payload,
    Guid? ExistingQueueItemId);

public static class HeroRallyPointRepairPlanner
{
    private const int RallyPointSlotId = 39;
    private const int RallyPointGid = 16;

    public static HeroRallyPointRepairPlan Plan(
        HeroRallyPointRepairRequest request,
        Guid parentItemId,
        IReadOnlyList<QueueItem> queueItems)
    {
        var villageId = request.VillageId.ToString();
        var villageKey = VillageKey.FromComponents(request.CoordX, request.CoordY, villageId, request.VillageName);
        var villageUrl = $"/dorf2.php?newdid={request.VillageId}";
        var existing = queueItems.FirstOrDefault(item =>
            item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused
            && string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            && BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
            && construct?.Gid == RallyPointGid
            && IsSameVillage(item.Payload, villageKey, villageUrl));

        var payload = new BuildingConstructPayload(
            RallyPointSlotId,
            RallyPointGid,
            "Rally Point",
            TargetLevel: 1).ToDictionary();
        payload[BotOptionPayloadKeys.TargetVillageName] = request.VillageName;
        payload[BotOptionPayloadKeys.TargetVillageKey] = villageKey;
        payload[BotOptionPayloadKeys.TargetVillageUrl] = villageUrl;
        payload[BotOptionPayloadKeys.BuildingConstructAllowSlotFallback] = bool.FalseString;
        payload[BotOptionPayloadKeys.AutoAddedBy] = BotOptionPayloadKeys.AutoAddedByHeroRallyPointRepair;
        payload[BotOptionPayloadKeys.AutoAddedParentId] = parentItemId.ToString();
        payload[BotOptionPayloadKeys.AutoAddedReason] =
            "Hero adventure page confirmed that the home village has no Rally Point.";

        return new HeroRallyPointRepairPlan(villageKey, payload, existing?.Id);
    }

    private static bool IsSameVillage(
        IReadOnlyDictionary<string, string> payload,
        string villageKey,
        string villageUrl)
    {
        if (payload.TryGetValue(BotOptionPayloadKeys.TargetVillageKey, out var candidateKey)
            && string.Equals(candidateKey, villageKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!payload.TryGetValue(BotOptionPayloadKeys.TargetVillageUrl, out var candidateUrl))
        {
            return false;
        }

        var expectedDid = Regex.Match(villageUrl, @"[?&]newdid=(\d+)", RegexOptions.IgnoreCase);
        var candidateDid = Regex.Match(candidateUrl, @"[?&]newdid=(\d+)", RegexOptions.IgnoreCase);
        return expectedDid.Success
            && candidateDid.Success
            && string.Equals(expectedDid.Groups[1].Value, candidateDid.Groups[1].Value, StringComparison.Ordinal);
    }
}
