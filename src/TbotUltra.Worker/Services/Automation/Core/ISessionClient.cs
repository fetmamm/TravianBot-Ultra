using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Session and account lifecycle exposed by <see cref="TravianClient"/>: login,
/// village switching, village/account status reads, account feature signals, and
/// page refresh. Seam introduced ahead of extracting a dedicated session
/// collaborator (#7); <see cref="TravianClient"/> implements it directly for now,
/// so behavior is unchanged.
/// </summary>
public interface ISessionClient
{
    Task LoginAsync(CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);

    Task<bool> CheckLoggedInAsync(CancellationToken cancellationToken = default);

    Task SwitchToVillageAsync(
        string villageName = "",
        string? villageUrl = null,
        CancellationToken cancellationToken = default,
        bool skipFeatureRefresh = false);

    Task<IReadOnlyList<VillageStatus>> ReadAllVillageStatusesAsync(CancellationToken cancellationToken = default);

    Task<VillageStatus> ReadVillageStatusAsync(CancellationToken cancellationToken = default);

    Task<VillageStatus> ReadVillageStatusAsync(
        IReadOnlyList<Village> knownVillages,
        IReadOnlyList<Building> knownBuildings,
        CancellationToken cancellationToken = default);

    Task<AccountSnapshot> ReadAccountSnapshotAsync(
        bool forceRefreshVillages = false,
        bool preferCurrentPageVillages = false,
        bool restorePageAfterProfile = true,
        bool suppressEnsureUiSync = false,
        bool skipOverviewNavigation = false,
        CancellationToken cancellationToken = default);

    Task<AccountAnalysisSnapshot> ReadAccountAnalysisSnapshotAsync(CancellationToken cancellationToken = default);

    Task RefreshAccountFeatureSignalsAsync(CancellationToken cancellationToken = default);

    Task<bool> ReadGoldClubStatusAsync(CancellationToken cancellationToken = default);

    Task RefreshCurrentPageAsync(CancellationToken cancellationToken = default);

    Task<string> ReadTribeOnlyAsync(CancellationToken cancellationToken = default);

    Task<bool> IsTravianPlusActiveAsync(CancellationToken cancellationToken = default);
}
