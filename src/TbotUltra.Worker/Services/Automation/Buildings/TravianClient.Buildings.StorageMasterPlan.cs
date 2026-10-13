using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task<bool?> VerifyStorageMasterPlanAsync(CancellationToken cancellationToken)
    {
        var buildings = await ReadBuildingsAsync(cancellationToken);
        var treasury = buildings.FirstOrDefault(building => building.Gid == 27);
        if (treasury?.SlotId is not int treasurySlotId)
        {
            Notify("[storage-master-plan] Treasury was not identified on Dorf2; eligibility remains unknown.");
            return null;
        }

        await OpenSlotFromOverviewAsync(treasurySlotId, cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        if (!TravianUrls.IsBuildPageForSlot(_page.Url, treasurySlotId))
        {
            Notify($"[storage-master-plan] Treasury navigation did not reach slot {treasurySlotId}; eligibility remains unknown.");
            return null;
        }

        var result = -1;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                await ReloadOrGotoAsync(Paths.BuildBySlot(treasurySlotId), cancellationToken);
                await EnsureLoggedInAsync(cancellationToken: cancellationToken);
                if (!TravianUrls.IsBuildPageForSlot(_page.Url, treasurySlotId))
                {
                    break;
                }
            }

            var evidence = StorageMasterPlanTreasuryParser.ParseActiveVillageEffect(await _page.ContentAsync());
            result = evidence switch { true => 1, false => 0, _ => -1 };
            if (result == 1)
            {
                break;
            }
        }
        Notify($"[storage-master-plan] Treasury slot {treasurySlotId} verification: {(result == 1 ? "active" : result == 0 ? "inactive" : "unknown")}.");
        return result switch { 1 => true, 0 => false, _ => null };
    }
}
