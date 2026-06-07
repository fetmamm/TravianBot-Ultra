using TbotUltra.Core.Travian;

namespace TbotUltra.Worker.Domain;

public sealed record Village(
    string Name,
    string? Url,
    bool? IsCapital = null,
    int? CoordX = null,
    int? CoordY = null,
    int? Population = null,
    int? CropFields = null);

public sealed record AccountSnapshot(
    string Tribe,
    string ActiveVillage,
    int VillageCount,
    IReadOnlyList<Village> Villages,
    DateTimeOffset? ServerTimeUtc = null);

public sealed record AccountAnalysisSnapshot(
    int SchemaVersion,
    DateTimeOffset AnalyzedAtUtc,
    string AccountName,
    string ServerUrl,
    string Tribe,
    bool GoldClubEnabled,
    IReadOnlyList<TribeBuildingCatalogEntry> BuildingCatalog,
    bool? AutoCelebrationEnabled = null,
    IReadOnlyList<string>? AutomationLoopEnabledGroups = null,
    IReadOnlyList<string>? AutomationLoopVisibleGroups = null);

public sealed record TribeBuildingCatalogEntry(
    int Gid,
    string Name,
    string Category,
    bool IsSpecial,
    IReadOnlyList<BuildingRequirementEntry> Requirements);

public sealed record TribeBuildingCatalogFullEntry(
    int Gid,
    string Name,
    string Category,
    bool IsSpecial,
    string? RequiredTribe,
    bool MatchesPlayerTribe,
    IReadOnlyList<BuildingRequirementEntry> Requirements);

public sealed record BuildingRequirementEntry(string Name, int Level);

public sealed record ResourceField(
    int? SlotId,
    string FieldType,
    string Name,
    int? Level,
    string? Url);

public sealed record Building(
    int? SlotId,
    string Name,
    int? Level,
    string? Url,
    int? Gid = null);

public sealed record BuildQueueItem(string Text, string? TimeLeft);

public enum ConstructionKind { Resource, Building, Unknown }

public sealed record ActiveConstruction(
    ConstructionKind Kind,
    string Name,
    int? Level,
    int? TimeLeftSeconds,
    string? FinishAtText);

public sealed record ConstructionSlotStatus(
    IReadOnlyList<ActiveConstruction> Active,
    int ResourceSlotsUsed,
    int BuildingSlotsUsed,
    int ResourceSlotsMax,
    int BuildingSlotsMax,
    bool CanStartResource,
    bool CanStartBuilding,
    int? ShortestWaitSeconds);

public sealed record ResourceStorageForecast(
    string ResourceKey,
    long? Current,
    long? Capacity,
    double? PercentOfCapacity,
    double? ProductionPerHour,
    int? SecondsToFull);

public sealed record HeroStatus(
    bool Exists = false,
    bool IsDead = false,
    string State = "Unknown",
    int? HpPercent = null,
    int AdventuresAvailable = 0,
    int? SecondsUntilAdventureReady = null,
    int? SecondsUntilReturn = null,
    int? ReviveRemainingSeconds = null,
    int UnassignedPoints = 0);

public sealed record HeroAttributeSnapshot(
    bool LevelUpAvailable = false,
    int FreePoints = 0,
    int FightingStrength = 0,
    int OffenceBonus = 0,
    int DefenceBonus = 0,
    int Resources = 0,
    int? AdventureCount = null,
    string HeroState = "Unknown",
    int? ReviveRemainingSeconds = null,
    string? HideMode = null,
    // The hero's home village name, read from the attributes page ("Home village is village X").
    // Null when not found. Used by the dashboard to mark which village hosts the hero.
    string? HomeVillageName = null,
    // Whether the hero is currently away from the home village (on an adventure/attack/etc) rather than
    // standing in it. Drives the green (home) vs yellow (away) hero icon on the dashboard.
    bool HomeVillageHeroAway = false);

public sealed record HeroInventoryResources(
    int Wood = 0,
    int Clay = 0,
    int Iron = 0,
    int Crop = 0);

public sealed record HeroAdventureDispatchResult(
    bool IsInHomeVillage,
    string? StatusText,
    int AdventureCount,
    bool Dispatched,
    int? SecondsUntilReturn,
    string Message,
    bool WasRevived = false,
    bool IsOnTheWayHome = false);

public sealed record TroopTrainingQueueStatus(
    TroopTrainingBuildingType BuildingType,
    string BuildingName,
    bool Exists,
    int? SlotId,
    IReadOnlyList<BuildQueueItem> QueueItems,
    int? RemainingSeconds,
    string RemainingText);

public sealed record BreweryCelebrationStatus(
    bool IsAvailableForTribe,
    bool? IsCapital,
    bool BreweryExists,
    int? BrewerySlotId,
    bool CelebrationRunning,
    int? RemainingSeconds,
    string RemainingText,
    string StatusText);

public sealed record SmithyUpgradeStatus(
    bool SmithyExists,
    int? SmithySlotId,
    int ActiveUpgradeCount,
    int? RemainingSeconds,
    IReadOnlyList<int> ActiveUpgradeRemainingSeconds,
    string RemainingText,
    string StatusText);

public sealed record VillageStatus(
    string ActiveVillage,
    IReadOnlyList<Village> Villages,
    IReadOnlyDictionary<string, string> Resources,
    IReadOnlyList<ResourceField> ResourceFields,
    IReadOnlyList<Building> Buildings,
    IReadOnlyList<BuildQueueItem> BuildQueue,
    string Tribe = "Unknown",
    int VillageCount = 0,
    int? Gold = null,
    int? Silver = null,
    bool IsBuildingInProgress = false,
    int ActiveBuildCount = 0,
    int? BuildQueueRemainingSeconds = null,
    string BuildQueueRemainingText = "",
    bool? IsCapital = null,
    DateTimeOffset? ServerTimeUtc = null,
    int UnreadMessages = 0,
    int UnreadReports = 0,
    long? WarehouseCapacity = null,
    long? GranaryCapacity = null,
    IReadOnlyList<ResourceStorageForecast>? ResourceStorageForecasts = null,
    IReadOnlyList<TroopTrainingQueueStatus>? TroopTrainingQueues = null,
    int? AdventureCount = null,
    IReadOnlyList<ActiveConstruction>? ActiveConstructions = null);

public sealed record InboxStatus(
    int UnreadMessages = 0,
    int UnreadReports = 0);

public sealed record PostLoginSnapshot(
    VillageStatus VillageStatus,
    InboxStatus InboxStatus,
    int? AdventureCount,
    HeroInventoryResources? HeroInventory = null);

public sealed record PageHtmlCapture(string Url, string Html);

public sealed record FarmListOverview(
    string Name,
    int ActiveFarmCount,
    int TotalFarmCount,
    int? RemainingSeconds,
    string? ListId = null);

public sealed record FarmAddResult(
    string FarmListName,
    int X,
    int Y,
    string TroopType,
    int TroopCount);

public sealed record FarmAddBatchResult(
    string FarmListName,
    int RequestedCount,
    int AttemptedCount,
    int AddedCount,
    int AlreadyInListCount,
    int FailedCount);

public sealed record ManualFarmRunResult(
    int TotalTargets,
    int AttemptedCount,
    int SentCount,
    int SkippedCount,
    int FailedCount,
    bool StoppedByNoTroopsAlarm,
    string TroopType,
    long TroopCount,
    string AttackMode);

public sealed record CatapultWaveRequest(
    int X,
    int Y,
    int WaveCount,
    bool RaidAttack,
    IReadOnlyDictionary<string, int> FirstAttackTroops,
    IReadOnlyDictionary<string, int> WaveTroops,
    string? Target1,
    string? Target2);

public sealed record CatapultWaveSetupInfo(
    IReadOnlyDictionary<string, long> AvailableTroops,
    int? RallyPointLevel);

public sealed record CatapultWaveRunResult(
    int TotalAttacks,
    int PreparedCount,
    int SentCount,
    int FailedCount,
    int X,
    int Y);
