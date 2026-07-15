using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Building operations exposed by <see cref="TravianClient"/>: building status,
/// upgrade/construct/demolish, smithy troop upgrades, and construction-slot
/// evaluation. Seam introduced ahead of extracting a dedicated building
/// collaborator (#7); <see cref="TravianClient"/> implements it directly for
/// now, so behavior is unchanged.
/// </summary>
public interface IBuildingClient
{
    Task<VillageStatus> ReadBuildingsStatusAsync(CancellationToken cancellationToken = default);

    Task<string> DemolishBuildingToLevelAsync(
        string targetBuildingSlotOrName,
        int targetLevel,
        CancellationToken cancellationToken = default);

    Task<string> UpgradeBuildingToLevelAsync(int slotId, int targetLevel, CancellationToken cancellationToken = default);

    Task<string> UpgradeBuildingToMaxAsync(int slotId, int maxAttempts = 30, CancellationToken cancellationToken = default);

    Task<string> ConstructBuildingAsync(
        int slotId,
        int gid,
        string name,
        CancellationToken cancellationToken = default,
        bool allowSlotFallback = false,
        string? fallbackExcludedSlots = null);

    Task<string> UpgradeSelectedTroopsAtSmithyAsync(
        IReadOnlyList<SmithyTroopTarget> targets,
        CancellationToken cancellationToken = default);

    Task<SmithyUpgradeStatus> ReadSmithyUpgradeStatusAsync(
        IReadOnlyList<Building>? knownBuildings = null,
        CancellationToken cancellationToken = default);

    Task<string> ReadSmithyQueueFromCurrentPageTestAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveConstruction>> ReadActiveConstructionsAsync(
        CancellationToken cancellationToken = default,
        bool allowNavigationToBuildings = true);

    Task<ConstructionSlotStatus> EvaluateConstructionSlotsAsync(
        string tribe,
        bool travianPlusActive,
        CancellationToken cancellationToken = default,
        bool allowNavigationToBuildings = true);

    Task<int> WaitForConstructionSlotIfBusyAsync(
        ConstructionKind kind,
        CancellationToken cancellationToken = default);
}
