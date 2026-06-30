using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Hero operations exposed by <see cref="TravianClient"/>: adventures, revive,
/// attribute management, inventory, hide mode, and adventure-danger tuning.
/// Seam introduced ahead of extracting a dedicated hero collaborator (#7);
/// <see cref="TravianClient"/> implements it directly for now, so behavior is
/// unchanged.
/// </summary>
public interface IHeroClient
{
    Task<HeroAdventureDispatchResult> SendHeroOnAdventureAsync(CancellationToken cancellationToken = default);

    Task<int?> RefreshAdventureCountAsync(bool forceReload = true, CancellationToken cancellationToken = default);

    Task<bool> HasHeroLevelUpIndicatorOnCurrentPageAsync(CancellationToken cancellationToken = default);

    Task<bool> CheckAndReviveDeadHeroOnCurrentPageAsync(bool autoRevive, CancellationToken cancellationToken = default);

    Task<bool> IsHeroRevivingOnCurrentPageAsync(CancellationToken cancellationToken = default);

    Task<string> ManageHeroAsync(
        int minHpForAdventure,
        bool autoRevive,
        bool autoAssignPoints,
        bool autoUseOintments,
        string statPriority,
        string adventurePickOrder = "shortest",
        bool hideModeEnabled = false,
        string hideMode = "hide",
        int heroHpRegenPerDayPercent = 100,
        CancellationToken cancellationToken = default);

    Task<string> SpendHeroAttributePointsAsync(
        string statPriority,
        CancellationToken cancellationToken = default);

    Task<HeroAttributeSnapshot> ReadHeroAttributeSnapshotAsync(CancellationToken cancellationToken = default);

    Task<HeroInventoryResources> ReadHeroInventoryResourcesAsync(
        CancellationToken cancellationToken = default,
        bool suppressUiSync = false);

    Task<string> SetHeroHideModeOnlyAsync(string hideMode, CancellationToken cancellationToken = default);

    HeroInventoryResources? TryGetCachedHeroInventory();

    Task<string> IncreaseAdventuresToHardAsync(CancellationToken cancellationToken = default);

    Task<string> ReduceAdventuresTimeAsync(CancellationToken cancellationToken = default);
}
