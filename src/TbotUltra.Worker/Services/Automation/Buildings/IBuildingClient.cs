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
    Task<VillageStatus> ReadCurrentBuildingOverviewStatusAsync(CancellationToken cancellationToken = default);

    Task<WatchtowerStatus?> ReadWatchtowerStatusAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
        => Task.FromResult<WatchtowerStatus?>(null);

    Task<string> UpgradeWatchtowersToLevelAsync(
        int targetLevel,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Watchtower upgrades are not supported by this building client.");

    Task<string> DemolishBuildingToLevelAsync(
        string targetBuildingSlotOrName,
        int targetLevel,
        CancellationToken cancellationToken = default);

    Task<string> UpgradeBuildingToLevelAsync(int slotId, int targetLevel, CancellationToken cancellationToken = default, string? expectedBuildingName = null);

    Task<string> UpgradeBuildingToMaxAsync(int slotId, int maxAttempts = 30, CancellationToken cancellationToken = default, string? expectedBuildingName = null);

    Task<string> ConstructBuildingAsync(
        int slotId,
        int gid,
        string name,
        CancellationToken cancellationToken = default,
        bool allowSlotFallback = false,
        string? fallbackExcludedSlots = null);

    Task<string> RunBreweryCelebrationAsync(
        bool restartDelayEnabled,
        double restartDelayMinMinutes,
        double restartDelayMaxMinutes,
        CancellationToken cancellationToken = default);

    Task<BreweryCelebrationStatus> ReadBreweryCelebrationStatusAsync(
        IReadOnlyList<Building>? knownBuildings = null,
        CancellationToken cancellationToken = default);

    Task<string> RunTownHallCelebrationAsync(
        string mode,
        int count,
        bool restartDelayEnabled,
        double restartDelayMinMinutes,
        double restartDelayMaxMinutes,
        CancellationToken cancellationToken = default);

    Task<string> UpgradeSelectedTroopsAtSmithyAsync(
        IReadOnlyList<SmithyTroopTarget> targets,
        CancellationToken cancellationToken = default);

    Task<SmithyUpgradeStatus> ReadSmithyUpgradeStatusAsync(
        IReadOnlyList<Building>? knownBuildings = null,
        CancellationToken cancellationToken = default);

    Task<string> ReadSmithyQueueFromCurrentPageTestAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveConstruction>> ReadActiveConstructionsAsync(
        CancellationToken cancellationToken = default,
        bool allowNavigationToBuildings = true,
        ActiveConstructionReadMode readMode = ActiveConstructionReadMode.FreshForMutation);

    Task<ConstructionSlotStatus> EvaluateConstructionSlotsAsync(
        string tribe,
        bool travianPlusActive,
        CancellationToken cancellationToken = default,
        bool allowNavigationToBuildings = true);

    Task<int> WaitForConstructionSlotIfBusyAsync(
        ConstructionKind kind,
        CancellationToken cancellationToken = default);
}
