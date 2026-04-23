namespace TbotUltra.Worker.Domain;

public sealed record Village(string Name, string? Url);

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
    IReadOnlyList<TribeBuildingCatalogEntry> BuildingCatalog);

public sealed record TribeBuildingCatalogEntry(
    int Gid,
    string Name,
    string Category,
    bool IsSpecial,
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

public sealed record ServerBuildChoice(int Gid, string Name, bool Available, string Reason);

public sealed record ResourceStorageForecast(
    string ResourceKey,
    int? Current,
    int? Capacity,
    double? PercentOfCapacity,
    double? ProductionPerHour,
    int? SecondsToFull);

public sealed record HeroStatus(
    bool Exists = false,
    bool IsDead = false,
    int? HpPercent = null,
    int AdventuresAvailable = 0,
    int? SecondsUntilAdventureReady = null,
    int? SecondsUntilReturn = null,
    int UnassignedPoints = 0);

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
    int? WarehouseCapacity = null,
    int? GranaryCapacity = null,
    IReadOnlyList<ResourceStorageForecast>? ResourceStorageForecasts = null);

public sealed record InboxStatus(
    int UnreadMessages = 0,
    int UnreadReports = 0);
